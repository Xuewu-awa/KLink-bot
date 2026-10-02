"""找出每张卡蓝图里的「私有函数」——既不是 ubergraph、也不是事件 stub，却被 ubergraph 调用。

判据：
  1. assets[*].functions 里的每个 key；
  2. 排除 ExecuteUbergraph_*；
  3. 排除「事件 stub」：字节码里含 ExecuteUbergraph_ 且带 IntConst 参数的函数
     （gen-kismet-ir.py:360-372 用的就是这条判据来收集 entrypoints）；
  4. 剩下的是普通函数 —— 再看它们有没有被 ubergraph 以 FunctionName 调用过。

输出：候选私有函数 + 定义它的卡数 + 调用它的卡数 + 当前是否在 LOCAL_FUNCTIONS 里。
"""
import json, os, re, collections

ROOT = r'<repo-root>'
P = os.path.join(ROOT, r'klink bot\decompiled\cards.full.json')
LOCAL_FUNCTIONS = {"GetPlayFromHandDamage", "GetChooseSpawnCards"}

print('loading...', flush=True)
raw = json.load(open(P, encoding='utf-8'))
print('loaded', len(raw['assets']), flush=True)

CALL = {"LocalVirtualFunction", "LocalFinalFunction", "VirtualFunction", "FinalFunction",
        "LocalFinalFunctionPtr", "LocalVirtualFunctionPtr"}


def walk(node, fns):
    if isinstance(node, dict):
        if node.get('Inst') in CALL and 'FunctionName' in node:
            fns.add(node['FunctionName'])
        for v in node.values():
            walk(v, fns)
    elif isinstance(node, list):
        for v in node:
            walk(v, fns)


defined = collections.defaultdict(set)      # 私有函数名 -> 定义它的卡
called_by = collections.defaultdict(set)    # 私有函数名 -> 调用它的卡
allnames = set()
per_card = collections.defaultdict(set)

for path, a in raw['assets'].items():
    name = os.path.basename(path).replace('.uasset', '')
    fns = a.get('functions') or {}
    if not fns:
        continue
    # 事件 stub 判定
    is_stub = set()
    for fname, finfo in fns.items():
        bc = (finfo or {}).get('bytecode') or []
        s = json.dumps(bc, ensure_ascii=False)
        if 'ExecuteUbergraph' in s and '"Inst": "IntConst"' in s:
            is_stub.add(fname)
    private = [f for f in fns if not f.startswith('ExecuteUbergraph_') and f not in is_stub]
    for f in private:
        defined[f].add(name)
    # ubergraph 里调了哪些函数名
    ug = None
    for fname in fns:
        if fname.startswith('ExecuteUbergraph_'):
            ug = fname
            break
    if ug is None:
        continue
    called = set()
    walk((fns[ug] or {}).get('bytecode') or [], called)
    for c in called:
        if c in private:
            called_by[c].add(name)
            per_card[name].add(c)
        allnames.add(c)

OUT = os.path.join(ROOT, r'out\audit\private-functions.txt')
with open(OUT, 'w', encoding='utf-8') as f:
    f.write('=== 卡蓝图里的私有函数（非 ubergraph、非事件 stub），且被 ubergraph 调用过 ===\n')
    f.write('（只列定义卡数 >= 1 的；★ = 已经在 LOCAL_FUNCTIONS 里）\n\n')
    rows = sorted(called_by.items(), key=lambda x: -len(x[1]))
    for fn, cards in rows:
        mark = '★' if fn in LOCAL_FUNCTIONS else ' '
        f.write(f'{mark} {fn:44s} 被调用卡数={len(cards):4d}  定义卡数={len(defined.get(fn, ())):4d}\n')
    f.write('\n=== 定义了但一次都没被 ubergraph 调用的私有函数（可能是死代码 / 被别处调用）===\n')
    for fn in sorted(defined):
        if fn not in called_by:
            f.write(f'   {fn:44s} 定义卡数={len(defined[fn]):4d}\n')

print('private-called distinct:', len(called_by))
print('written', OUT)
print()
for fn, cards in sorted(called_by.items(), key=lambda x: -len(x[1]))[:30]:
    print(f'  {fn:44s} called_by={len(cards):4d} defined={len(defined.get(fn,())):4d}')
