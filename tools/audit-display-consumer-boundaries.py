"""Check historical decoded display-consumer boundaries, never grant admission.

The decoded AST is extraction evidence, not authenticated by script_hex alone.
No game process is read or invoked. Dynamic targets remain explicitly unresolved.
"""
import argparse
import hashlib
import json
from pathlib import Path

DEFAULT_REVIEW = Path('D:/Codex Workshop/kards-install-review')
SNAPSHOT = 'runtime-snapshots/registry-enum-20261018-170111/rule500-text-chain-20261028.json'
PARSED = 'parsed/kards/Content/Blueprints/Cards/BP_CardFunctions.functions.json'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def walk(value, path='$', contexts=()):
    if isinstance(value, dict):
        yield path, value, contexts
        for key, child in value.items():
            nested = contexts + (value['Context'],) if value.get('Inst') == 'Context' and key == 'Expression' else contexts
            yield from walk(child, path + '.' + key, nested)
    elif isinstance(value, list):
        for i, child in enumerate(value):
            yield from walk(child, path + f'[{i}]', contexts)


def calls(statements):
    return [dict(statement=s['StatementIndex'], path=path, instruction=n['Inst'],
                 function=n.get('Function', n.get('FunctionName')),
                 parameters=n.get('Parameters', []), contexts=list(contexts))
            for s in statements for path, n, contexts in walk(s)
            if n.get('Inst') in {'CallMath', 'LocalVirtualFunction', 'VirtualFunction',
                'LocalFinalFunction', 'FinalFunction', 'CallMulticastDelegate'}]


def variable(name):
    return {'Inst': 'LocalVariable', 'Variable Name': name}


def one_call(statements, name, statement):
    found = [c for c in calls(statements) if c['function'] == name and c['statement'] == statement]
    require(len(found) == 1, f'Expected one {name} at {statement}')
    return found[0]


def references(statements, name):
    return [(s['StatementIndex'], path) for s in statements for path, n, _ in walk(s)
            if n.get('Variable Name') == name]


def check_text_forwarding(statements, name, statement, callee, argument):
    call = one_call(statements, callee, statement)
    refs = references(statements, name)
    expected = [(statement, call['path'] + f'.Parameters[{argument}]')]
    require(refs == expected, f'{name}: unexpected direct use: {refs}')
    require(call['parameters'][argument].get('Inst') == 'LocalVariable', 'Changed text argument kind')
    return dict(source_variable=name, statement=statement, function=callee, argument=argument,
                path=expected[0][1], contexts=call['contexts'])


def check(parsed, snapshot):
    by_id = {}
    for f in snapshot['functions']:
        key = (f['owner'], f['name'])
        require(key not in by_id, f'Duplicate function {key}')
        by_id[key] = f

    selected = []
    def get(owner, name):
        f = by_id[(owner, name)]
        raw = bytes.fromhex(f['script_hex'])
        require(f['decode_complete'] and len(raw) == f['script_size'] and len(raw) > 0,
                f'Incomplete script: {owner}.{name}')
        require(hashlib.sha256(raw).hexdigest() == f['script_sha256'], 'Script hash mismatch')
        require(f['decoded'] and f['decoded'][-1]['Inst'] == 'EndOfScript', 'Missing decoded terminator')
        selected.append(dict(owner=owner, name=name, bytes=len(raw),
            script_sha256=f['script_sha256'], decoded_sha256=hashlib.sha256(
                json.dumps(f['decoded'], sort_keys=True, ensure_ascii=False).encode()).hexdigest()))
        return f['decoded']

    forwarding = [
        check_text_forwarding(parsed['SpawnCardinHandbySide']['bytecode'], 'newCardText', 333, 'CreateCard', 7),
        check_text_forwarding(parsed['CreateCard']['bytecode'], 'newCardText', 2880, 'NotifyDrawCardFromDeck', 9)]
    add = get('BP_CardFunctions_C', 'AddNumberToText')
    notify = get('U_CardFunctionsNotifier_C', 'NotifyAppendNumberToCardText')
    handler = get('BP_OnlineMatch_C', 'HandleAppendNumberToCardText')
    graph = get('BP_OnlineMatch_C', 'ExecuteUbergraph_BP_OnlineMatch')
    builder = get('BP_OnlineMatch_C', 'AddSubActionAppendNumberToCardText')
    queue = get('BP_OnlineMatch_C', 'CreateAction_AddSubAction')
    c = one_call(add, 'NotifyAppendNumberToCardText', 1558)
    require(c['contexts'] == [{'Inst': 'InstanceVariable', 'Variable Name': 'CardFunctionsNotifier'}],
            'Changed notifier receiver')
    require(c['parameters'] == [variable(x) for x in ['CardID', 'CallFunc_Conv_TextToString_ReturnValue', 'CardID', 'Color']],
            'Changed notification payload')
    c = one_call(notify, 'HandleAppendNumberToCardText', 0)
    require(c['contexts'] == [{'Inst': 'InterfaceContext', 'InterfaceValue': {
        'Inst': 'InstanceVariable', 'Variable Name': 'OnlineMatchReceiver'}}], 'Changed interface receiver')
    require(c['parameters'] == [variable(x) for x in ['CardID', 'newCardText', 'instigatorID', 'Color']],
            'Changed interface payload')
    expected_fields = ['K2Node_Event_CardID_22', 'K2Node_Event_NewCardText_1',
                       'K2Node_Event_InstigatorID_18', 'K2Node_Event_Color']
    persistent = [s for s in handler if s['Inst'] == 'LetValueOnPersistentFrame']
    require([(s['Property Name'], s['Expression']) for s in persistent] == list(zip(expected_fields,
        [variable(x) for x in ['CardID', 'newCardText', 'instigatorID', 'Color']])), 'Changed event frame mapping')
    c = one_call(handler, 'ExecuteUbergraph_BP_OnlineMatch', 72)
    require(c['parameters'] == [{'Inst': 'IntConst', 'Value': 12721}], 'Changed callback entry')
    window = [s for s in graph if 12721 <= s['StatementIndex'] <= 12771]
    require(len(window) == 2 and window[-1]['Inst'] == 'PopExecutionFlow', 'Changed callback continuation')
    c = one_call(window, 'AddSubActionAppendNumberToCardText', 12721)
    require(c['parameters'] == [variable(x) for x in expected_fields], 'Changed callback payload')
    forwarding.append(check_text_forwarding(builder, 'newCardText', 154, 'ActionValueString', 1))
    c = one_call(builder, 'CreateAction_AddSubAction', 241)
    require(c['parameters'] == [{'Inst': 'StringConst', 'Value': 'ZActionAppendNumberToCardText'},
                              variable('K2Node_MakeArray_Array')], 'Changed subaction envelope')
    stub = by_id[('CardFunctionsStub', 'AddNumberToText')]
    require(stub['script_size'] == 0, 'Native declaration changed; re-review dispatch')
    return dict(forwarding=forwarding, callback_entry=12721, callback_window=window,
        functions=selected, add_number_calls=calls(add), notifier_calls=calls(notify),
        handler_calls=calls(handler), builder_calls=calls(builder), queue_calls=calls(queue),
        native_declaration=dict(owner='CardFunctionsStub', name='AddNumberToText', script_bytes=0,
            implementation_resolved=False),
        admission_exempt=False, global_noninterference_proven=False,
        open_boundaries=['Actual dynamic CardFunctions/OnlineMatchReceiver class and dispatch binding',
            'Native text helpers and PrintString implementation; SDK declarations alone are insufficient',
            'Other notification recipients, downstream scheduling, reentrancy and full state consumers',
            'Historical decoded AST provenance/version; script hash does not authenticate decoding',
            'Runtime kismet-program-json/v1 hash + asset/export/entry/instruction/expression-path binding'])


def build(review):
    sources = [review / PARSED, review / SNAPSHOT]
    parsed, snapshot = [json.loads(p.read_text(encoding='utf-8-sig')) for p in sources]
    result = check(parsed, snapshot)
    result.update(schema='display-consumer-boundaries/v1', authority='historical pak parse and memory extraction',
        captured_exe_sha256=snapshot['exe_sha256'], current_client_equivalence_claimed=False,
        tool_bindings={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in [
            Path(__file__), Path(__file__).with_name('test-display-consumer-boundaries.py')]},
        sources=[dict(path=str(p), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sources])
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--review', type=Path, default=DEFAULT_REVIEW)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = build(args.review)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(functions=len(result['functions']), callback_entry=result['callback_entry'],
                          forwarding=len(result['forwarding']), admission_exempt=False)))
