"""Inventory candidate card references; deliberately does not decide PvP legality."""
import argparse
import csv
import hashlib
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

CONTRACTS = {'CreateCard': 1, 'SpawnCardToBoard': 0, 'ConvertCard': 2,
             'SpawnCardInDeckBySide': 1, 'SpawnCardInHandBySide': 1, 'SpawnNextToCard': 0}
FIELDS = ('spawnCardName', 'chooseOneCards', 'customName1', 'customName2')
CALLS = {'LocalVirtualFunction', 'VirtualFunction', 'FinalFunction', 'LocalFinalFunction', 'CallMath'}

def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))

def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()

def walk(value, path='$'):
    if isinstance(value, dict):
        yield path, value
        for k, v in value.items():
            yield from walk(v, path + '/' + str(k))
    elif isinstance(value, list):
        for i, v in enumerate(value):
            yield from walk(v, path + '/' + str(i))

def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, (list, dict)):
        for v in (value.values() if isinstance(value, dict) else value):
            yield from strings(v)

def aliases(cards):
    index = defaultdict(set)
    for c in cards:
        asset = c['asset']; package = '/Game/' + asset.split('Content/', 1)[-1].removesuffix('.uasset')
        name = c['name']; cls = c['class']
        for key in (name, cls, asset, package, package + '.' + name, package + '.' + cls):
            index[key].add(name)
    return index

def scan(cards, root):
    index = aliases(cards); edges = []; calls = []; inventory = []; files = []
    for c in cards:
        name = c['name']; rel = c.get('functions_file'); p = root / rel.replace('\\', '/') if rel else None
        exists = p is not None and p.is_file()
        funcs = read(p) if exists else {}
        declared = len(c.get('functions', []))
        inventory.append(dict(card=name, asset=c['asset'], parent=c.get('parent'),
                              card_set_tag=c.get('explicit_overrides', {}).get('cardSet'),
                              declared_functions=declared, parsed_functions=len(funcs),
                              export_state='present' if exists else ('missing_expected' if declared else 'data_only_no_own_functions'),
                              scope_decision='unreviewed', exclusion_evidence=None))
        if exists:
            files.append(dict(path=str(p), sha256=sha(p)))
        def reference(raw, origin, function, path, symbol=None):
            targets = sorted(index.get(raw, []))
            for target in targets:
                edges.append(dict(source=name, target=target, origin=origin, function=function,
                                  path=path, literal=raw, symbol=symbol,
                                  status='candidate_reference_not_proven_reachable'))
            return targets
        for field in FIELDS:
            for raw in strings(c.get('explicit_overrides', {}).get(field)):
                # Delimited strings are candidate names only, not an interpreted selector.
                for part in raw.split(';'):
                    reference(part, 'explicit_cdo_field', '', field)
        for function, body in funcs.items():
            for path, node in walk(body.get('bytecode', [])):
                kind = node.get('Inst')
                if kind in {'NameConst', 'StringConst', 'ObjectConst'}:
                    for raw in strings(node.get('Value', node.get('Object'))):
                        reference(raw, 'ast_literal', function, path)
                if kind not in CALLS:
                    continue
                symbol = node.get('FunctionName', node.get('Function', ''))
                if not isinstance(symbol, str):
                    symbol = json.dumps(symbol, sort_keys=True)
                if symbol not in CONTRACTS and not re.search('spawn|convert|transform|choose', symbol, re.I):
                    continue
                params = node.get('Parameters', []); slot = CONTRACTS.get(symbol)
                arg = params[slot] if slot is not None and slot < len(params) else None
                raw = arg.get('Value') if isinstance(arg, dict) and arg.get('Inst') in {'NameConst', 'StringConst'} else None
                targets = reference(raw, 'contract_name_slot', function, path + '/Parameters/' + str(slot), symbol) if isinstance(raw, str) else []
                calls.append(dict(source=name, function=function, path=path, symbol=symbol, name_slot=slot,
                                  argument=arg, candidate_targets=targets,
                                  status=('unmapped_symbol' if slot is None else
                                          'literal_resolved_receiver_unverified' if targets else
                                          'literal_unresolved' if isinstance(raw, str) else 'dynamic_or_missing_argument'),
                                  receiver_and_reachability_verified=False))
    return inventory, edges, calls, files

def write_tsv(p, rows, fields):
    with p.open('w', encoding='utf8', newline='') as f:
        w = csv.DictWriter(f, fieldnames=fields, delimiter='\t', extrasaction='ignore', lineterminator='\n'); w.writeheader()
        for row in rows:
            w.writerow({k: json.dumps(v, ensure_ascii=False) if isinstance(v, (dict, list)) else v for k, v in row.items() if k in fields})

def build(args):
    root=Path(args.evidence_root); out=Path(args.output); out.mkdir(parents=True, exist_ok=True)
    definitions=root/'cards.definitions.json'; cards=read(definitions)
    sdk=Path(args.sdk)/'BP_CardFunctions.hpp'; enums=Path(args.sdk)/'KardsCore_enums.hpp'
    enum_text=enums.read_text(encoding='utf-8-sig')
    assert re.search(r'OnlySpawnable\s*=\s*3\b',enum_text) and re.search(r'Campaign2020\s*=\s*9\b',enum_text)
    text=sdk.read_text(encoding='utf-8-sig'); contracts=[]
    for name, slot in CONTRACTS.items():
        matches=re.findall(r'void ' + re.escape(name) + r'\(([^\n]+)\);', text)
        assert len(matches)==1, (name, matches)
        # Known signatures have no comma inside their template types.
        assert matches[0].split(',')[slot].strip().startswith('FName '), (name, slot)
        contracts.append(dict(symbol=name, name_slot=slot, signature=matches[0], status='sdk_name_slot_only_receiver_unverified'))
    inventory, edges, calls, files=scan(cards,root)
    capture=Path(args.capture); runtime=read(capture); bodies=[]
    for f in runtime['functions']:
        raw=bytes.fromhex(f['script_hex']); assert len(raw)==f['script_size']
        if raw: assert hashlib.sha256(raw).hexdigest()==f['script_sha256']
        status=('empty_declaration' if not raw else 'interface_return_body' if f['owner']=='I_BP_Logic_Receiver_C' else 'body_captured_semantics_not_all_reviewed')
        bodies.append(dict(owner=f['owner'],name=f['name'],bytes=len(raw),status=status,sha256=hashlib.sha256(raw).hexdigest()))
    counts=dict(assets=len(inventory), parsed_functions=sum(r['parsed_functions'] for r in inventory),
                export_states=dict(Counter(r['export_state'] for r in inventory)), candidate_edges=len(edges),
                unique_card_pairs=len({(e['source'],e['target']) for e in edges}),
                creation_related_calls=len(calls), call_states=dict(Counter(r['status'] for r in calls)),
                cards_without_observed_incoming_reference=len({c['name'] for c in cards}-{e['target'] for e in edges}),
                runtime_functions=len(bodies), runtime_body_states=dict(Counter(r['status'] for r in bodies)))
    result=dict(schema_version=1, status='partial_scope_characterization', counts=counts,
                pvp_denominator=None, remaining_percent=None, certified_complete_cards=0,
                generator_sha256=sha(Path(__file__)),
                captured_client_exe_sha256=runtime.get('exe_sha256'),
                inputs=[dict(path=str(p),sha256=sha(p)) for p in [definitions,sdk,enums,capture]],
                parsed_input_bindings=files, name_slot_contracts=contracts,
                boundaries=['Export version is not certified equal to current client.',
                  'Effect candidate pool is not deck legality. All assets retain unreviewed scope.',
                  'No observed incoming reference does not justify exclusion.',
                  'Exact literal candidates do not prove branch reachability, receiver, or actual creation.',
                  'Dynamic arguments, ID-based conversion, inheritance, native dependencies and implicit roots remain open.',
                  'Current mode policies, blacklist/reserve state, selector callbacks and array ordering are not certified.'])
    (out/'manifest.json').write_bytes((json.dumps(result,ensure_ascii=False,indent=2)+'\n').encode('utf8'))
    asset_fields=[k for k in inventory[0] if k!='scope_decision']+['scope_decision']
    for name, rows, fields in [('assets',inventory,asset_fields), ('candidate_references',edges,['source','target','origin','function','path','literal','symbol','status']), ('creation_calls',calls,['source','function','path','symbol','name_slot','argument','candidate_targets','status','receiver_and_reachability_verified']), ('runtime_functions',bodies,['owner','name','bytes','status','sha256'])]:
        write_tsv(out/(name+'.tsv'),rows,fields)
    (out/'README.md').write_bytes(('# PvP 范围证据：SCOPE-001\n\n这是候选引用清单，不是已经闭合的 PvP 分母。全部资产保留为 unreviewed。\n\n'
       'manifest.json 保存统计、输入指纹和边界；assets.tsv 区分有函数导出、数据对象与缺失导出；candidate_references.tsv 保存精确名称引用；creation_calls.tsv 保留动态或未映射调用。\n\n'
       '运行时效果池函数的离线模型验证独立存于 evidence-summary.json。卡集 9 可经过普通效果池，卡集 3 可在 includeNotAttainable 开启时经过；不能按战役名称或不可构筑标签直接删除。\n\n'
       '合法牌组、特殊模式、运行时策略、原生实现及递归可达性仍需核对。因此本清单不提供完成百分比，也不认证任何卡牌已完整还原。\n').encode('utf8'))
    print(json.dumps(counts,ensure_ascii=False))

if __name__=='__main__':
    p=argparse.ArgumentParser(); p.add_argument('--evidence-root',required=True); p.add_argument('--sdk',required=True); p.add_argument('--capture',required=True); p.add_argument('--output',default='out/audit/pvp-scope'); build(p.parse_args())
