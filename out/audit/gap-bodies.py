# -*- coding: utf-8 -*-
"""
从反编译直译产物 out/Generated-gap/_deps/*.g.cs 里抽出指定函数的完整函数体。

用法：
  python out/audit/gap-bodies.py SpawnCardInFrontline          # 打印函数体
  python out/audit/gap-bodies.py --list BP_CardFunctions       # 列出该蓝图的全部函数
  python out/audit/gap-bodies.py --wrapper                     # 分析"只差注册"的包装函数

--wrapper：对 608 个缺失键，看它的直译函数体里调用了哪些 **已在派发表里** 的原语。
若函数体只做「读参数 → 调 1~2 个已注册原语」，它就是一个**包装函数**，
说明实现已经在我们的派发表里了，缺的只是一层名字/参数适配 —— 这正是 (B) 的机械判据。
"""
import json, re, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
DEPS = 'out/Generated-gap/_deps'


def load_bodies():
    bodies = {}
    for f in os.listdir(DEPS):
        if not f.endswith('.g.cs'):
            continue
        cls = f[:-5]
        t = open(os.path.join(DEPS, f), encoding='utf-8', errors='replace').read()
        marks = [(m.start(), m.group(1)) for m in re.finditer(r'^// ---- (\w+) ----$', t, re.M)]
        for idx, (pos, fn) in enumerate(marks):
            end = marks[idx + 1][0] if idx + 1 < len(marks) else len(t)
            bodies.setdefault(fn, []).append((cls, t[pos:end]))
    return bodies


BODIES = load_bodies()
DISPATCH = set(re.findall(r'^\s*\["([^"]+)"\]\s*=',
                          open('src/KLink.Bot/Effects/CardApiDispatch.cs', encoding='utf-8').read(), re.M))


def show(fn):
    if fn not in BODIES:
        print(f'!! {fn} 在直译产物里找不到定义')
        return
    for cls, body in BODIES[fn]:
        print('=' * 100)
        print(f'### {fn}  @ {cls}')
        print(body.rstrip())


def wrapper_report():
    rows = json.load(open('out/audit/missing-keys-classify2.json', encoding='utf-8'))
    out = []
    for r in sorted(rows, key=lambda r: -r['calls']):
        fn = r['fn']
        if fn not in BODIES:
            continue
        cls, body = BODIES[fn][0]
        called = re.findall(r'H\.Call\("([^"]+)"', body)
        registered = [c for c in called if c in DISPATCH]
        unreg = [c for c in called if c not in DISPATCH]
        ncall = len(called)
        # 包装函数：调用点少、且全部/绝大多数落到已注册原语
        if ncall and len(registered) == ncall and ncall <= 3:
            out.append((r['calls'], r['cards'], fn, cls, ncall, sorted(set(registered)), r['class']))
    out.sort(key=lambda x: -x[0])
    print(f'=== 「函数体只调已注册原语」的缺失键：{len(out)} 种 ===')
    for calls, cards, fn, cls, ncall, reg, cl in out:
        print(f'  {fn:44s} {calls:4d}调用 {cards:3d}卡  [{cl}] @{cls}  → {reg}')
    json.dump([{'fn': o[2], 'class': o[6], 'calls': o[0], 'cards': o[1], 'definedIn': o[3],
                'callsRegistered': o[5]} for o in out],
              open('out/audit/missing-keys-wrappers.json', 'w', encoding='utf-8'),
              ensure_ascii=False, indent=1)


if __name__ == '__main__':
    a = sys.argv[1:]
    if not a:
        print(__doc__)
    elif a[0] == '--list':
        t = open(os.path.join(DEPS, a[1] + '.g.cs'), encoding='utf-8', errors='replace').read()
        for m in re.finditer(r'^// ---- (\w+) ----$', t, re.M):
            print(m.group(1))
    elif a[0] == '--wrapper':
        wrapper_report()
    else:
        for fn in a:
            show(fn)
