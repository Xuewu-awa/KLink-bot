#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：统计 IR 里哪些卡的哪些程序读了给定的一组裸变量名。
用法: python out/audit/p1b-whoreads.py <var> [var...]
"""
import json, sys, collections

ir_path = 'klink bot/docs/card-ir.json'
if '--ir' in sys.argv:
    i = sys.argv.index('--ir'); ir_path = sys.argv[i + 1]; del sys.argv[i:i + 2]

names = sys.argv[1:]
ir = json.load(open(ir_path, encoding='utf-8'))
hits = collections.defaultdict(set)
for card, node in ir.items():
    for bucket in ('entrypoints', 'locals'):
        for prog, body in ((node or {}).get(bucket) or {}).items():
            steps = body.get('steps') if isinstance(body, dict) else body
            s = json.dumps(steps, ensure_ascii=False)
            for n in names:
                if '"var": "%s"' % n in s:
                    hits[n].add((card, prog))
for n in names:
    print(f'== {n}: {len(hits[n])} 处')
    for card, prog in sorted(hits[n])[:40]:
        print(f'   {card} :: {prog}')
