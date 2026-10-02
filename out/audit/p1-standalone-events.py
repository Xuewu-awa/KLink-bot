#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：列出「带出参的事件处理函数」——它们是独立 export 的函数图，
函数体里没有 `ExecuteUbergraph`，所以旧 IR 生成器（只认 stub→ubergraph）全部漏掉。

用法: python out/audit/p1-standalone-events.py [--file <cards.full.json>]
"""
import json, sys, collections

path = 'klink bot/decompiled/cards.full.json'
if '--file' in sys.argv:
    path = sys.argv[sys.argv.index('--file') + 1]

d = json.load(open(path, encoding='utf-8'))
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))

tot = collections.Counter()
miss = collections.Counter()
cards_only = collections.Counter()
ex = collections.defaultdict(list)

for k, v in d['assets'].items():
    name = k.rsplit('/', 1)[-1].replace('.uasset', '')
    fns = v.get('functions') or {}
    eps = set(((ir.get(name) or {}).get('entrypoints') or {}).keys())
    loc = set(((ir.get(name) or {}).get('locals') or {}).keys())
    for fn, body in fns.items():
        if not fn.startswith('On') or fn.endswith('__DelegateSignature'):
            continue
        if 'ExecuteUbergraph' in json.dumps(body, ensure_ascii=False):
            continue
        tot[fn] += 1
        if not name.startswith('card_'):
            continue
        cards_only[fn] += 1
        if fn not in eps and fn not in loc:
            miss[fn] += 1
            if len(ex[fn]) < 3:
                ex[fn].append(name)

print(f'{"函数":48s} {"总数":>5s} {"card_*":>7s} {"IR 里仍缺":>9s}  例子')
for fn, c in tot.most_common():
    if not cards_only[fn]:
        continue
    print(f'{fn:48s} {c:5d} {cards_only[fn]:7d} {miss[fn]:9d}  {ex[fn]}')
print()
print('card_* 上的独立事件函数合计:', sum(cards_only.values()),
      '  仍缺 IR 的:', sum(miss.values()))
