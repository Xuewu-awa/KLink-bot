# -*- coding: utf-8 -*-
"""
★ 关键修正：区分「真缺口」与「由 locals 兜底已处理」。

`card-ir.json` 每张卡可以带一个 `locals` 字典（卡自己蓝图里的私有函数体）。
`KismetVm.ExecuteCall` 在派发表查不到时会**回退到卡自己的 locals 函数体**执行
（`tools/BotSim/SelfTest.cs` 的 `LocalFunctionActuallyRuns` 就是守这条的）。

⇒ 一个 IR 调用名「不在派发表里」**并不等于**它没被处理：
   如果调用它的那张卡自己在 `locals` 里定义了这个函数，那它已经被执行了。

本脚本把 608 个缺失键重新按「**按调用点**」分成：
  · 兜底已处理：该调用点所在卡的 locals 里有这个函数
  · 真缺口：该调用点所在卡的 locals 里没有，且派发表里也没有
    （按调用点分类后，键级取"只要有一个调用点是真缺口"就算真缺口）
"""
import json, os, re
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
disp = open('src/KLink.Bot/Effects/CardApiDispatch.cs', encoding='utf-8').read()
keys = set(re.findall(r'^\s*\["([^"]+)"\]\s*=', disp, re.M))

calls = Counter()                 # fn -> 总调用点
cards = defaultdict(set)
local_ok = Counter()              # fn -> 由 locals 兜底处理的调用点
local_cards = defaultdict(set)
real = Counter()                  # fn -> 真缺口调用点
real_cards = defaultdict(set)

for card, v in ir.items():
    if not isinstance(v, dict) or not isinstance(v.get('steps'), list):
        continue
    locals_ = v.get('locals') or {}
    for s in v['steps']:
        if s.get('op') != 'call' or not s.get('fn'):
            continue
        fn = s['fn']
        if fn in keys:
            continue
        calls[fn] += 1
        cards[fn].add(card)
        if fn in locals_:
            local_ok[fn] += 1
            local_cards[fn].add(card)
        else:
            real[fn] += 1
            real_cards[fn].add(card)

allmissing = set(calls)
pure_local = sorted(k for k in allmissing if real[k] == 0)
mixed = sorted((k for k in allmissing if real[k] > 0 and local_ok[k] > 0), key=lambda k: -real[k])
pure_real = sorted((k for k in allmissing if local_ok[k] == 0), key=lambda k: -real[k])

out = []
out.append(f'缺失键 {len(allmissing)} 种（调用点 {sum(calls.values())} 个）')
out.append(f'  A. **纯靠 locals 兜底**（没有真缺口调用点）：{len(pure_local)} 种 / '
           f'{sum(local_ok[k] for k in pure_local)} 调用点  ← 不是缺口')
out.append(f'  B. 混合（部分调用点有 locals、部分没有）：{len(mixed)} 种 / '
           f'{sum(real[k] for k in mixed)} 个真缺口调用点')
out.append(f'  C. **纯真缺口**（一个 locals 都没有）：{len(pure_real)} 种 / '
           f'{sum(real[k] for k in pure_real)} 个真缺口调用点')
out.append(f'⇒ **真缺口键合计 {len(mixed) + len(pure_real)} 种 / '
           f'{sum(real.values())} 个调用点 / '
           f'{len(set().union(*[real_cards[k] for k in real])) if real else 0} 张卡**')
out.append('')
out.append('=== 真缺口（按真缺口调用点数排序，前 80）===')
for k in sorted(real, key=lambda k: -real[k])[:80]:
    tag = 'mixed' if local_ok[k] else 'pure'
    out.append(f'  {k:44s} 真缺口 {real[k]:4d} / 总 {calls[k]:4d}  {len(real_cards[k]):3d}卡 [{tag}]')

open('out/audit/missing-keys-real-gap.txt', 'w', encoding='utf-8').write('\n'.join(out))
json.dump({'pureLocal': pure_local, 'mixed': mixed, 'pureReal': pure_real,
           'realCalls': dict(real), 'totalCalls': dict(calls),
           'realCards': {k: sorted(v) for k, v in real_cards.items()}},
          open('out/audit/missing-keys-real-gap.json', 'w', encoding='utf-8'),
          ensure_ascii=False, indent=1)
print('\n'.join(out[:8]))
