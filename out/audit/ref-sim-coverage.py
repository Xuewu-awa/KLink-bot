# -*- coding: utf-8 -*-
"""
把参考模拟器 ref/kards-sim/KardsSim/Bridge/EngineHost.cs 的原语表抽出来，
与我们的「缺失键」求交 —— 交集就是**语义已知、可照抄**的缺口（最有价值的一批）。

用法：
  python out/audit/ref-sim-coverage.py            # 概览
  python out/audit/ref-sim-coverage.py <键名>      # 打印该键在参考实现里的整段代码
"""
import json, re, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
REF = 'ref/kards-sim/KardsSim/Bridge/EngineHost.cs'
src = open(REF, encoding='utf-8', errors='replace').read()

# 原语表条目形如： ["Name"] = (h, a) => ...
ENTRY = re.compile(r'^[ \t]*\["([^"]+)"\]\s*=', re.M)
marks = [(m.start(), m.group(1)) for m in ENTRY.finditer(src)]
entries = {}
lines = src.split('\n')


def line_of(pos):
    return src[:pos].count('\n') + 1


for idx, (pos, name) in enumerate(marks):
    end = marks[idx + 1][0] if idx + 1 < len(marks) else len(src)
    entries[name] = (line_of(pos), src[pos:end].rstrip())

rows = json.load(open('out/audit/missing-keys-classify2.json', encoding='utf-8'))
missing = {r['fn']: r for r in rows}

if len(sys.argv) > 1:
    for k in sys.argv[1:]:
        if k in entries:
            ln, body = entries[k]
            print(f'===== {k}  @ {REF}:{ln} =====')
            print(body)
            print()
        else:
            print(f'!! {k} 不在参考实现的原语表里')
    sys.exit(0)

inter = [(missing[k]['calls'], missing[k]['cards'], k, entries[k][0]) for k in missing if k in entries]
inter.sort(key=lambda x: -x[0])
print(f'参考模拟器 EngineHost.cs 的原语数：{len(entries)}')
print(f'我们缺失 {len(missing)} 种，其中参考实现**有**的：{len(inter)} 种')
print(f'  这些合计 {sum(x[0] for x in inter)} 个调用点 / '
      f'{len(set().union(*[set(missing[x[2]]["cardList"]) for x in inter])) if inter else 0} 张卡')
print()
for calls, cards, k, ln in inter:
    print(f'  {k:44s} {calls:4d}调用 {cards:3d}卡   EngineHost.cs:{ln}')
json.dump([{'fn': k, 'calls': c, 'cards': n, 'refLine': ln} for c, n, k, ln in inter],
          open('out/audit/ref-sim-coverage.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
