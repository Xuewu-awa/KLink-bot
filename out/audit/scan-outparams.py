"""从 cards.full.json（87MB 原始字节码）抽出每个被调函数的：
   - 出参槽名（CallFunc_<fn>_<param>）
   - 出参的 PinCategory / PinSubCategoryObject
   - 入参个数（出现过的形状）
用来找「返回类型和蓝图对不上」的同形 bug。
"""
import json, collections, os, re

ROOT = r'<repo-root>'
P = os.path.join(ROOT, r'klink bot\decompiled\cards.full.json')
print('loading...', flush=True)
raw = json.load(open(P, encoding='utf-8'))
print('loaded', len(raw['assets']), flush=True)

outslots = collections.defaultdict(collections.Counter)      # fn -> slot name -> n
slottype = collections.defaultdict(collections.Counter)      # (fn, slot) -> "PinCat|PinSub"
argshapes = collections.defaultdict(collections.Counter)     # fn -> arity
callers = collections.defaultdict(set)

CALL = {"LocalVirtualFunction", "LocalFinalFunction", "VirtualFunction", "FinalFunction",
        "LocalFinalFunctionPtr", "LocalVirtualFunctionPtr"}


def walk(node, asset, depth=0):
    if isinstance(node, dict):
        if node.get('Inst') in CALL and 'FunctionName' in node:
            fn = node['FunctionName']
            params = node.get('Parameters') or []
            argshapes[fn][len(params)] += 1
            callers[fn].add(asset)
            for p in params:
                if isinstance(p, dict) and p.get('Inst') == 'LocalVariable':
                    name = p.get('Variable Name') or ''
                    m = re.match(r'CallFunc_' + re.escape(fn) + r'_(.+)$', name)
                    if m:
                        outer = p.get('Variable Outer')
                        if isinstance(outer, dict):
                            cat = outer.get('PinCategory')
                            sub = outer.get('PinSubCategoryObject') or outer.get('PinSubCategory')
                            outslots[fn][m.group(1)] += 1
                            slottype[(fn, m.group(1))][f'{cat}|{sub}'] += 1
                        else:
                            outslots[fn][m.group(1)] += 1
        for v in node.values():
            walk(v, asset, depth + 1)
    elif isinstance(node, list):
        for v in node:
            walk(v, asset, depth + 1)


for path, a in raw['assets'].items():
    name = os.path.basename(path).replace('.uasset', '')
    walk(a.get('functions') or {}, name)

OUT = os.path.join(ROOT, r'out\audit\outparams.txt')
with open(OUT, 'w', encoding='utf-8') as f:
    f.write('=== 每个函数的出参槽名与类型（来自 cards.full.json 的原始字节码）===\n')
    for fn in sorted(outslots):
        shapes = ','.join(str(k) for k in sorted(argshapes[fn]))
        f.write(f'\n{fn}   cards={len(callers[fn])} arity={shapes}\n')
        for slot, n in outslots[fn].most_common(8):
            types = ' / '.join(f'{t}x{c}' for t, c in slottype[(fn, slot)].most_common(3))
            f.write(f'    {slot:55s} x{n:<5d} {types}\n')

json.dump({fn: dict(outslots[fn]) for fn in outslots},
          open(os.path.join(ROOT, r'out\audit\outparams.json'), 'w', encoding='utf-8'),
          ensure_ascii=False, indent=1)
print('functions with out slots:', len(outslots))
print('written', OUT)
