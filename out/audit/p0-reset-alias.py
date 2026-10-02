"""统计：订阅 OnCardReset 的卡里，其 OnCardReset 程序是不是直接 jump 到别的入口点。

用法: python out/audit/p0-reset-alias.py [事件名]
"""
import json
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
ev = sys.argv[1] if len(sys.argv) > 1 else 'OnCardReset'
d = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
cnt = Counter()
detail = []
for name, v in d.items():
    eps = v.get('entrypoints') or {}
    if ev not in eps:
        continue
    start = eps[ev]
    starts = sorted(x for x in eps.values() if x > start)
    end = starts[0] if starts else 10 ** 9
    body = [s for s in v['steps'] if start <= s.get('i', 0) < end]
    if len(body) == 1 and body[0].get('op') == 'jump':
        tgt = body[0]['to']
        alias = [k for k, val in eps.items() if val == tgt]
        cnt['jump->' + (alias[0] if alias else str(tgt))] += 1
        detail.append((name, alias))
    else:
        cnt['real-body(%d steps)' % len(body)] += 1
for k, v in cnt.most_common(20):
    print('%-40s %d' % (k, v))
print()
for n, a in detail[:15]:
    print('  %-42s -> %s' % (n, a))
