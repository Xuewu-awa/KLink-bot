"""RULE-428 offline syntax/call-prefix checks, not whole-graph execution.

Requires retained local captures and the bounded RULE-297 decoder chain.
"""
import argparse
import copy
import hashlib
import importlib.util
import itertools
import json
import struct
import sys
from pathlib import Path


def require(value, message):
    if not value:
        raise ValueError(message)


def walk(value):
    if isinstance(value, dict):
        yield value
        for item in value.values():
            yield from walk(item)
    elif isinstance(value, list):
        for item in value:
            yield from walk(item)


def make_decoder(base):
    class Decoder(base.Decoder):
        def expr(self):
            op = self.raw[self.at]
            if op == 0x13:
                self.take(1)
                return dict(Inst='MetaCast', Class=self.ptr('object'), Expression=self.expr())
            if op == 0x62:
                self.take(1)
                return dict(Inst='OpaqueTwoOperandOp62', Left=self.expr(), Right=self.expr(),
                            SemanticStatus='syntax-only-two-steps-observed-in-native-handler')
            if op == 0x61:
                words = struct.unpack_from('<III', self.raw, self.at + 1)
                if words[1:] != (0, 0):
                    self.take(13)
                    return dict(Inst='BindDelegateOpaqueName', NameWords=list(words),
                                BaseName=self.fname(words[0], 0), Delegate=self.expr(), Object=self.expr(),
                                SemanticStatus='name-extra-words-unresolved')
            return super().expr()
    return Decoder


def run_prefix(rows, start, frontiers, state, is_my_turn=False):
    """External calls are controlled; autoPickCardToDraw has no modelled effects."""
    state = copy.deepcopy(state)
    local, stack, calls, visited = {}, [], [], []
    indices = {row['StatementIndex']: i for i, row in enumerate(rows)}

    def assign(node, value):
        if node['Inst'] == 'LocalVariable':
            local[node['Variable Name']] = value
        elif node['Inst'] == 'InstanceVariable':
            state[node['Variable Name']] = value
        else:
            raise ValueError(('unsupported assignment', node))

    def expr(node, context=None):
        kind = node['Inst']
        if kind == 'LocalVariable':
            return local[node['Variable Name']]
        if kind == 'InstanceVariable':
            return (state if context is None else context)[node['Variable Name']]
        if kind == 'ObjectConst':
            return node['Object']
        if kind == 'Self':
            return state
        if kind in ('True', 'False'):
            return kind == 'True'
        if kind == 'ByteConst':
            return node['Value']
        if kind == 'Context':
            obj = expr(node['Context'])
            return None if obj is None else expr(node['Expression'], obj)
        if kind in ('LocalVirtualFunction', 'FinalFunction', 'CallMath'):
            name = node.get('FunctionName', node.get('Function'))
            p = node.get('Parameters', [])
            if name == 'GetBoard':
                assign(p[1], state['board'])
            elif name == 'GetLogic':
                assign(p[1], state['logic'])
            elif name == 'IsMyTurn':
                return is_my_turn
            elif name == 'EqualEqual_ByteByte':
                return expr(p[0]) == expr(p[1])
            elif name in ('autoPickHandTargets', 'autoPickCardToDraw'):
                calls.append(name)
            else:
                raise ValueError(('unsupported prefix call', name))
            return None
        raise ValueError(('unsupported prefix expression', node))

    pc = indices[start]
    for _ in range(100):
        row = rows[pc]
        at = row['StatementIndex']
        if at in frontiers:
            return dict(state=state, calls=calls, visited=visited, frontier=at)
        pc += 1
        visited.append(at)
        kind = row['Inst']
        if kind == 'LetBool':
            assign(row['Variable'], expr(row['Expression']))
        elif kind == 'PushExecutionFlow':
            stack.append(row['Offset'])
        elif kind == 'Jump':
            pc = indices[row['Offset']]
        elif kind == 'JumpIfNot':
            if not expr(row['Condition']):
                pc = indices[row['Offset']]
        elif kind == 'PopExecutionFlow' or kind == 'PopExecutionFlowIfNot' and not expr(row['Condition']):
            pc = indices[stack.pop()]
        elif kind == 'PopExecutionFlowIfNot':
            pass
        else:
            expr(row)
    raise ValueError('prefix step limit')


def verify(root):
    capture = json.loads((root / 'rule428-hand-queue-callers-final-native-20261026.json').read_text())
    wrappers = json.loads((root / 'rule428-event-wrappers-native-20261026.json').read_text())
    spec = importlib.util.spec_from_file_location('base297', root.parent.parent / 'runtime-kismet-subset-297.py')
    base = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(base)
    decoder = make_decoder(base)
    sys.path.insert(0, str(root.parent.parent / 'python-tools'))
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    native_windows = {}
    for opcode in (0x61, 0x62):
        window = json.loads((root / f'rule428-op{opcode:02x}-vm-window-native-20261026.json').read_text())
        for key in ('pid', 'exe_sha256', 'game_module'):
            require(window[key] == capture[key], 'mixed native-window capture')
        target = int(window['target'], 16)
        raw = bytes.fromhex(window['raw_hex'])
        require(len(raw) == 768, 'native-window size differs')
        decoded = [dict(address=hex(i.address), mnemonic=i.mnemonic, op_str=i.op_str)
                   for i in disassembler.disasm(raw, target)]
        require(decoded == window['disassembly'], 'native disassembly replay mismatch')
        prefix = []
        for instruction in decoded:
            prefix.append(instruction)
            if instruction['mnemonic'] == 'ret':
                break
        dispatches = [i for i in prefix if i['mnemonic'] == 'call' and i['op_str'] == 'qword ptr [rbp + rax*8]']
        require(len(dispatches) == 2, 'two expression dispatches not found in native prefix')
        if opcode == 0x61:
            require(any(i['mnemonic'] == 'lea' and i['op_str'] == 'r8, [rax + 0xc]' for i in prefix), '12 byte delegate-name read missing')
        native_windows[f'{opcode:02x}'] = dict(target=window['target'], expression_dispatches=len(dispatches))
    count = 0
    identities = {}
    for bundle in (capture, wrappers):
        for key in ('pid', 'exe_sha256', 'game_module'):
            require(bundle[key] == capture[key], 'mixed capture identities')
        symbols = {(s['kind'], int(s['address'], 16)): s['name'] for s in bundle['symbols']}
        names = bundle['fnames']
        for f in bundle['functions']:
            raw = bytes.fromhex(f['script_hex'])
            require(len(raw) == f['script_size'] and hashlib.sha256(raw).hexdigest() == f['sha256'], 'script bytes mismatch')
            require(f['decode_complete'], 'incomplete syntax parse')
            ast = decoder(raw, lambda k, a: symbols[k, a], lambda i, n: names[f'{i}:{n}']).decode()
            require(ast == f['decoded'], 'syntax replay mismatch')
            identity = (f['owner'], f['name'], f['sha256'])
            require(f['address'] not in identities or identities[f['address']] == identity, 'duplicate function identity differs')
            identities[f['address']] = identity
            count += 1
    graphs = {f['owner']: f['decoded'] for f in capture['functions'] if f['name'].startswith('ExecuteUbergraph')}
    logic, controller = graphs['BP_Logic_C'], graphs['MatchController_C']
    callsites = [(owner, row['StatementIndex']) for owner, rows in graphs.items() for row in rows
                 if any(n.get('FunctionName') == 'autoPickHandTargets' for n in walk(row))]
    require(callsites == [('BP_Logic_C', 43361), ('BP_Logic_C', 44593), ('MatchController_C', 2686)], 'callsite set changed')
    expected_entries = {('BP_Logic_C', 'EndTurnFromLogic'): 42163,
                        ('BP_Logic_C', 'EndTurnForOpponent'): 44526,
                        ('MatchController_C', 'readyToSetBoardAfterReconnect'): 15983}
    for (owner, name), entry in expected_entries.items():
        f = next(f for f in wrappers['functions'] if (f['owner'], f['name']) == (owner, name))
        invocations = [n for row in f['decoded'] for n in walk(row)
                       if n.get('Inst') == 'LocalFinalFunction' and n.get('Function', '').startswith('ExecuteUbergraph')]
        require(len(invocations) == 1 and invocations[0]['Parameters'] == [{'Inst': 'IntConst', 'Value': entry}], 'wrapper entry changed')
    ordinary, opponent, reconnect = [], [], []
    for choose, ending, play_state, reason, side in itertools.product((False, True), (False, True), range(5), ('manual', 'forced'), (0, 1)):
        state = dict(board=dict(chooseOneActive=choose), endingTurnForOpponent=ending,
                     PlayState=play_state, reason=reason, mySide=side)
        result = run_prefix(logic, 42163, {42188}, state)
        require(result['calls'] == ['autoPickCardToDraw'] + (['autoPickHandTargets'] if choose else []), 'end-turn call ordering changed')
        require(result['state'] == state, 'end-turn prefix changed input state')
        ordinary.append(result)
    for play_state, ending, ignore, side in itertools.product(range(5), (False, True), (False, True), (0, 1)):
        state = dict(PlayState=play_state, endingTurnForOpponent=ending, bIgnoreKreditValidation=ignore, mySide=side)
        result = run_prefix(logic, 44526, {44607, 44908}, state)
        expected = copy.deepcopy(state)
        if play_state == 4:
            expected.update(endingTurnForOpponent=True, bIgnoreKreditValidation=True)
        require(result['state'] == expected and result['calls'] == (['autoPickHandTargets'] if play_state == 4 else []), 'opponent prefix mismatch')
        opponent.append(result)
    for turn, valid_logic, ending in itertools.product((False, True), (False, True), (False, True)):
        state = dict(logic=dict(endingTurnForOpponent=ending) if valid_logic else None)
        result = run_prefix(controller, 2606, {1603, 2722}, state, turn)
        require(result['calls'] == (['autoPickHandTargets'] if turn and valid_logic else []), 'controller prefix mismatch')
        reconnect.append(result)
    return dict(rule='RULE-428', function_records_redecoded=count, unique_functions_redecoded=len(identities),
                captured_script_bytes=sum(f['script_size'] for f in capture['functions']),
                native_syntax_windows=native_windows,
                callsites=callsites, ordinary_prefix_cases=len(ordinary), opponent_prefix_cases=len(opponent),
                controller_prefix_cases=len(reconnect), ordinary=ordinary, opponent=opponent, controller=reconnect,
                boundary='Only selected call prefixes execute in this offline model. External calls controlled; no effects for autoPickCardToDraw. Opaque delegate operands are syntax only. No whole-card, full reconnect, native-helper, or cross-side PvP reachability proof.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    print(json.dumps(verify(parser.parse_args().evidence), ensure_ascii=False, indent=2))
