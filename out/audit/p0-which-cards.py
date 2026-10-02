"""列出订阅了某个事件入口点的卡，并打印该入口点程序里用到的调用名。

用法: python out/audit/p0-which-cards.py <事件名> [最多打印几张]
"""
import json
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
ev = sys.argv[1]
limit = int(sys.argv[2]) if len(sys.argv) > 2 else 8

d = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
cards = d.get('cards') or d
hits = []
for name, entry in cards.items():
    eps = entry.get('entrypoints') or {}
    if ev in eps:
        hits.append((name, entry))

print('订阅 %s 的卡: %d 张' % (ev, len(hits)))
calls = Counter()
for name, entry in hits:
    # 找到该入口点对应的程序，收集调用名
    for prog in (entry.get('programs') or []):
        if prog.get('name') == ev or prog.get('entry') == ev:
            for c in (prog.get('calls') or []):
                calls[c] += 1
    for k in ('callsByEntry',):
        pass
for name, _ in hits[:limit]:
    print('  ', name)
if calls:
    print('该事件程序里出现的调用名 TOP:')
    for k, v in calls.most_common(25):
        print('   %-42s %d' % (k, v))
else:
    print('(没有 programs[].calls 结构，打印第一个卡的结构键)')
    if hits:
        print(list(hits[0][1].keys()))
