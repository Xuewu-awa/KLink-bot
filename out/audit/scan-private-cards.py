"""只保留「定义在 card_* 卡资产里」的私有函数 —— 排除 UI/Widget 资产。"""
import json, os, collections

ROOT = r'<repo-root>'
P = os.path.join(ROOT, r'klink bot\decompiled\cards.full.json')
LOCAL_FUNCTIONS = {"GetPlayFromHandDamage", "GetChooseSpawnCards"}
CALL = {"LocalVirtualFunction", "LocalFinalFunction", "VirtualFunction", "FinalFunction",
        "LocalFinalFunctionPtr", "LocalVirtualFunctionPtr"}

raw = json.load(open(P, encoding='utf-8'))


def walk(node, out):
    if isinstance(node, dict):
        if node.get('Inst') in CALL and 'FunctionName' in node:
            out.add(node['FunctionName'])
        for v in node.values():
            walk(v, out)
    elif isinstance(node, list):
        for v in node:
            walk(v, out)


defined = collections.defaultdict(set)
called_by = collections.defaultdict(set)
for path, a in raw['assets'].items():
    name = os.path.basename(path).replace('.uasset', '')
    if not name.startswith('card_'):
        continue
    fns = a.get('functions') or {}
    if not fns:
        continue
    stub = set()
    for fname, finfo in fns.items():
        s = json.dumps((finfo or {}).get('bytecode') or [], ensure_ascii=False)
        if 'ExecuteUbergraph' in s and '"Inst": "IntConst"' in s:
            stub.add(fname)
    priv = [f for f in fns if not f.startswith('ExecuteUbergraph_') and f not in stub]
    for f in priv:
        defined[f].add(name)
    ug = next((f for f in fns if f.startswith('ExecuteUbergraph_')), None)
    if ug is None:
        continue
    called = set()
    walk((fns[ug] or {}).get('bytecode') or [], called)
    for c in called:
        if c in priv:
            called_by[c].add(name)

# 内核派发表
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
import re
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))

OUT = os.path.join(ROOT, r'out\audit\private-fns-cards-only.txt')
with open(OUT, 'w', encoding='utf-8') as f:
    f.write('=== 定义在 card_* 卡资产里、被 ubergraph 调用的私有函数 ===\n')
    f.write('状态：★=已在 LOCAL_FUNCTIONS / 派发表有 / 派发表无（=IR 里既没函数体、VM 也不认，调用被记 Unimplemented）\n\n')
    rows = sorted(called_by.items(), key=lambda x: -len(x[1]))
    n_none = 0
    for fn, cards in rows:
        if fn in LOCAL_FUNCTIONS:
            st = '★LOCAL_FUNCTIONS'
        elif fn in dispatched:
            st = '派发表有(手写C#)'
        else:
            st = '**派发表无**'
            n_none += 1
        f.write(f'{fn:42s} 卡数={len(cards):4d}  {st}\n')
    f.write(f'\n合计 {len(rows)} 个，其中派发表无 = {n_none}\n')
    f.write('\n=== 明细（哪些卡定义了它）===\n')
    for fn, cards in rows:
        if fn in LOCAL_FUNCTIONS or fn in dispatched:
            continue
        f.write(f'{fn}: {sorted(cards)}\n')

print('private fns in card_ assets, called:', len(rows), ' not-in-dispatch:', n_none)
print('written', OUT)
