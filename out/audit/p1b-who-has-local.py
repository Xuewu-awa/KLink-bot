#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：列出 IR 里带某个 local 程序名的卡。
用法: python out/audit/p1b-who-has-local.py <progName> [--ir klink bot/docs/card-ir.json]
"""
import json, sys

args = sys.argv[1:]
ir_path = 'klink bot/docs/card-ir.json'
if '--ir' in args:
    i = args.index('--ir'); ir_path = args[i + 1]; del args[i:i + 2]

prog = args[0]
ir = json.load(open(ir_path, encoding='utf-8'))
hits = []
for card, node in ir.items():
    loc = (node or {}).get('locals') or {}
    if prog in loc:
        body = loc[prog]
        steps = body.get('steps') if isinstance(body, dict) else body
        hits.append((card, len(steps or [])))
print(f'{prog}: {len(hits)} 张卡')
for card, n in sorted(hits):
    print(f'  {card}  steps={n}')
