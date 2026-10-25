"""RULE-427: replay captured receive-queue AST and automatic-picker prefix.

Offline only. Not a full picker, native VM, network protocol, or PvP proof.
Usage: python tools/audit-hand-selection-queues.py --evidence PATH
"""
import argparse
import copy
import gzip
import hashlib
import importlib.util
import itertools
import json
import struct
from pathlib import Path


def require(ok, message):
    if not ok:
        raise ValueError(message)


def replay(rows, local, instance, client_turn=False, stop=None):
    """Only the syntax used by the receive function and picker prefix <=699."""
    local = copy.deepcopy(local)
    instance = copy.deepcopy(instance)
    positions = {r['StatementIndex']: i for i, r in enumerate(rows)}
    stack, visited = [], []

    def assign(node, value):
        kind = node['Inst']
        if kind in ('LocalVariable', 'LocalOutVariable'):
            local[node['Variable Name']] = value
        elif kind == 'Context':
            obj = expr(node['Context'])
            if obj is not None:
                obj[node['Expression']['Variable Name']] = value
        else:
            raise ValueError(('unsupported assignment', node))

    def expr(node, context=None):
        kind = node['Inst']
        if kind in ('LocalVariable', 'LocalOutVariable'):
            return local[node['Variable Name']]
        if kind == 'InstanceVariable':
            return (instance if context is None else context)[node['Variable Name']]
        if kind == 'Self':
            return instance
        if kind == 'ObjectConst':
            return node['Object']
        if kind in ('IntConst', 'StringConst'):
            return node['Value']
        if kind in ('True', 'False'):
            return kind == 'True'
        if kind == 'StructMemberContext':
            return expr(node['StructExpression'])[node['Property Name']]
        if kind == 'Context':
            obj = expr(node['Context'])
            return None if obj is None else expr(node['Expression'], obj)
        if kind == 'SwitchValue':
            value = expr(node['Expression'])
            return next((expr(c['CaseResult']) for c in node['Cases']
                         if expr(c['CaseValue']) == value), None)
        if kind in ('CallMath', 'FinalFunction', 'LocalVirtualFunction'):
            name = node.get('Function', node.get('FunctionName'))
            p = node.get('Parameters', [])
            if name == 'GetBoard':
                assign(p[1], instance['board'])
            elif name == 'IsLocalClientTurn':
                assign(p[0], client_turn)
            elif name == 'IsValid':
                # Controlled external predicate, not native UObject validity.
                return expr(p[0]) is not None
            elif name in ('Less_IntInt', 'Add_IntInt', 'NotEqual_StriStri'):
                a, b = map(expr, p)
                if name == 'Less_IntInt':
                    return a < b
                if name == 'Add_IntInt':
                    return a + b
                # Test strings are ASCII; no non-ASCII case-fold claim.
                return a.lower() != b.lower()
            elif name == 'Array_Length':
                return len(expr(p[0]))
            elif name == 'Array_IsNotEmpty':
                return bool(expr(p[0]))
            elif name == 'Array_Get':
                assign(p[2], copy.deepcopy(expr(p[0])[expr(p[1])]))
            elif name == 'Array_Add':
                arr = expr(p[0])
                arr.append(copy.deepcopy(expr(p[1])))
                return len(arr) - 1
            elif name == 'Array_Clear':
                expr(p[0]).clear()
            else:
                raise ValueError(('unsupported call', name))
            return None
        raise ValueError(('unsupported expression', node))

    pc = 0
    for _ in range(10000):
        row = rows[pc]
        at = row['StatementIndex']
        if at == stop:
            return local, instance, visited, at
        pc += 1
        visited.append(at)
        kind = row['Inst']
        if kind in ('Let', 'LetBool'):
            # Blueprint array assignment copies the value; Array_Clear takes a ref.
            value = expr(row['Expression'])
            assign(row['Variable'], copy.deepcopy(value) if isinstance(value, list) else value)
        elif kind == 'PushExecutionFlow':
            stack.append(row['Offset'])
        elif kind == 'Jump':
            pc = positions[row['Offset']]
        elif kind == 'JumpIfNot':
            if not expr(row['Condition']):
                pc = positions.get(row['Offset'], -1)
                if pc == -1:
                    return local, instance, visited, row['Offset']
        elif kind == 'PopExecutionFlow' or kind == 'PopExecutionFlowIfNot' and not expr(row['Condition']):
            target = stack.pop()
            if target not in positions:
                return local, instance, visited, target
            pc = positions[target]
        elif kind == 'PopExecutionFlowIfNot':
            pass
        elif kind == 'Return':
            return local, instance, visited, at
        else:
            expr(row)
    raise ValueError('AST step limit exceeded')


def verify(root):
    capture = json.loads((root / 'rule427-hand-queue-functions-native-20261026.json').read_text())
    callers = json.loads((root / 'rule427-hand-queue-callers-native-20261026.json').read_text())
    for key in ('pid', 'game_module', 'exe_sha256'):
        require(capture[key] == callers[key], 'mixed capture identity')
    spec = importlib.util.spec_from_file_location('decoder427', root.parent.parent / 'runtime-kismet-subset-297.py')
    decoder = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(decoder)
    symbols = {(x['kind'], int(x['address'], 16)): x['name'] for x in capture['symbols']}
    fnames = capture['fnames']
    functions = {}
    redecoded = 0
    for f in capture['functions']:
        raw = bytes.fromhex(f['script_hex'])
        require(len(raw) == f['script_size'], 'script length mismatch')
        require(hashlib.sha256(raw).hexdigest() == f['script_sha256'], 'script hash mismatch')
        if not raw:
            continue  # Native declaration only; no native implementation claim.
        ast = decoder.Decoder(raw, lambda k, a: symbols[k, a], lambda i, n: fnames[f'{i}:{n}']).decode()
        require(f['decode_complete'] and ast == f['decoded'], 'independent decode mismatch')
        redecoded += 1
        functions[f['owner'], f['name']] = f
    require(redecoded == len(functions) == 7, 'scripted function count changed')
    receive = functions['BP_OnlineMatch_C', 'ReceiveActionSelectHandTargetPending']['decoded']
    picker = functions['BP_Logic_C', 'autoPickHandTargets']['decoded']
    select = next(x for x in picker if x['StatementIndex'] == 239)['Expression']
    require(select['Inst'] == 'SwitchValue', 'queue selector changed')
    require([c['CaseResult']['Expression']['Variable Name'] for c in select['Cases']] ==
            ['pendingCardsSelectingHandTargets', 'enemyPendingCardsSelectingHandTargets'], 'queue order changed')
    datasets = [[], [('unrelated', 9)], [('cardBeingPlayed', 60)], [('CARDBEINGPLAYED', -1)],
                [('cardBeingPlayed', 60), ('cardBeingPlayed', 81)],
                [('unrelated', 9), ('cardBeingPlayed', 0)],
                [('cardBeingPlayed', 60), ('unrelated', 9)]]
    receive_cases = []
    for turn, initial, data in itertools.product((False, True), (0, 77, -1), datasets):
        initial_state = dict(pendingCardsSelectingHandTargets=[11], enemyPendingCardsSelectingHandTargets=[22])
        local, state, visited, _ = replay(receive, dict(cardBeingPlayed=initial,
            ActionData=[dict(Name=k, Value=v) for k, v in data]), initial_state, turn)
        parsed = next((v for k, v in reversed(data) if k.lower() == 'cardbeingplayed'), initial)
        expected = copy.deepcopy(initial_state)
        expected['pendingCardsSelectingHandTargets' if turn else 'enemyPendingCardsSelectingHandTargets'].append(parsed)
        require(state == expected and local['cardBeingPlayed'] == parsed, 'receive queue model mismatch')
        receive_cases.append(dict(client_turn=turn, initial=initial, data=data, state=state, visited=visited))
    picker_cases = []
    prefix = [x for x in picker if x['StatementIndex'] <= 699]
    for ending, widget_valid, pending_kill, queues in itertools.product(
            (False, True), (False, True), (False, True), (([], []), ([60, 60], [81]), ([], [81]), ([60], []))):
        online = dict(pendingCardsSelectingHandTargets=queues[0], enemyPendingCardsSelectingHandTargets=queues[1])
        board = dict(selectHandTargetWidget=dict(pendingKill=pending_kill) if widget_valid else None,
                     isSelectingHandTarget=True, chooseOneActive=True)
        local, state, visited, exit_at = replay(prefix, {}, dict(onlineMatch=online,
            board=board, endingTurnForOpponent=ending), stop=699)
        if widget_valid and not pending_kill:
            require(exit_at == 1805 and state['onlineMatch'] == online, 'widget path incorrectly cleared queues')
        else:
            expected = queues[1 if ending else 0]
            require(local['_cardsPendingSelectingHandTarget'] == expected, 'picker queue copy mismatch')
            if expected:
                require(exit_at == 699 and not any(state['onlineMatch'].values()), 'both queues not cleared')
                require(not state['board']['isSelectingHandTarget'] and not state['board']['chooseOneActive'], 'flags not cleared')
            else:
                require(exit_at == 2594 and state['onlineMatch'] == online, 'empty selected queue should return without clearing')
        picker_cases.append(dict(ending=ending, widget_valid=widget_valid, pending_kill=pending_kill,
                                 queues=queues, exit_at=exit_at, visited=visited))
    online = next(f for f in callers['functions'] if f['owner'] == 'BP_OnlineMatch_C')
    require(online['decode_complete'], 'OnlineMatch graph incomplete')
    patterns = set()
    for f in capture['functions']:
        if f['name'] in ('autoPickHandTargets', 'AddSubActionSelectHandTargetPending', 'NotifySelectHandTargetPending'):
            patterns.update((bytes.fromhex(f['header_hex'])[0x18:0x20], struct.pack('<Q', int(f['address'], 16))))
    require({p.hex() for p in patterns} == set(callers['patterns']), 'caller patterns differ')
    inventory = json.loads((root / 'rule419-loaded-json-array-callers-native-20261025.json').read_text())
    candidates = {int(f['address'], 16): f for f in callers['functions']}
    seen = set()
    with gzip.open(root / 'rule419-loaded-script-corpus-20261025.bin.gz', 'rb') as stream:
        require(stream.read(8) == b'KR419S1\0', 'corpus header mismatch')
        for entry in inventory['inventory']:
            at, ptr, size = struct.unpack('<QQI', stream.read(20))
            header, raw = stream.read(0xe0), stream.read(size)
            require(at == int(entry['address'], 16) and size == entry['script_size'] and
                    hashlib.sha256(raw).hexdigest() == entry['sha256'], 'corpus record mismatch')
            if any(p in raw for p in patterns):
                require(at in candidates, 'missing caller candidate')
                f = candidates[at]
                require(raw.hex() == f['script_hex'] and header.hex() == f['header_hex'], 'caller corpus bytes differ')
                seen.add(at)
        require(not stream.read(1), 'trailing corpus data')
    require(seen == set(candidates), 'extra caller candidate')
    caller_symbols = {(x['kind'], int(x['address'], 16)): x['name'] for x in callers['symbols']}
    caller_fnames = callers['fnames']
    for f in callers['functions']:
        raw = bytes.fromhex(f['script_hex'])
        require(len(raw) == f['script_size'] and hashlib.sha256(raw).hexdigest() == f['sha256'], 'caller bytes mismatch')
        if f['decode_complete']:
            ast = decoder.Decoder(raw, lambda k, a: caller_symbols[k, a],
                                  lambda i, n: caller_fnames[f'{i}:{n}']).decode()
            require(ast == f['decoded'], 'complete caller decode mismatch')
    row = next(x for x in online['decoded'] if x['StatementIndex'] == 11770)
    require(row['FunctionName'] == 'AddSubActionSelectHandTargetPending' and
            row['Parameters'] == [{'Inst': 'LocalVariable', 'Variable Name': 'K2Node_Event_CardBeingPlayed_1'}], 'forwarding changed')
    return dict(rule='RULE-427', scripted_functions_redecoded=len(functions), caller_candidates=len(seen),
                incomplete_caller_graphs=[f['owner'] for f in callers['functions'] if not f['decode_complete']],
                receive_ast_cases=len(receive_cases),
                picker_prefix_ast_cases=len(picker_cases), receive_cases=receive_cases, picker_cases=picker_cases,
                boundary='Controlled IsLocalClientTurn/GetBoard/IsValid, seeded local initial values, ASCII names; picker stops at 699 or branch frontier. No full picker, native helpers, live match, or cross-side PvP reachability certification.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.evidence), ensure_ascii=False, indent=2))
