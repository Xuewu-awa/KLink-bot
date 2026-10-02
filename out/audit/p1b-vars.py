#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：统计某个 local 程序体里读到的「裸变量名」（即需要宿主喂的入参/局部）。
用法: python out/audit/p1b-vars.py <progName> [--ir path]
"""
import json, sys, collections, re

args = sys.argv[1:]
ir_path = 'klink bot/docs/card-ir.json'
if '--ir' in args:
    i = args.index('--ir'); ir_path = args[i + 1]; del args[i:i + 2]

prog = args[0]
ir = json.load(open(ir_path, encoding='utf-8'))
cnt = collections.Counter()
cards = collections.defaultdict(list)
for card, node in ir.items():
    loc = (node or {}).get('locals') or {}
    if prog not in loc:
        continue
    body = loc[prog]
    steps = body.get('steps') if isinstance(body, dict) else body
    seen = set()
    def walk(t):
        if isinstance(t, dict):
            if 'var' in t and isinstance(t['var'], str):
                seen.add(t['var'])
            for v in t.values():
                walk(v)
        elif isinstance(t, list):
            for v in t:
                walk(v)
    walk(steps)
    for v in seen:
        cnt[v] += 1
        cards[v].append(card)
print(f'{prog}: 卡数={sum(1 for c,n in ir.items() if prog in ((n or {}).get("locals") or {}))}')
for v, c in cnt.most_common():
    print(f'  {v:60s} {c:4d}   e.g. {cards[v][:2]}')
