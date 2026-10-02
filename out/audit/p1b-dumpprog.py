#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：打印 card-ir.json 里某张卡的某个程序（locals 或 entrypoints）的步骤。
用法: python out/audit/p1b-dumpprog.py <card> <prog> [--ir path] [--limit N]
"""
import json, sys

args = sys.argv[1:]
ir_path = 'klink bot/docs/card-ir.json'
lim = 400
if '--ir' in args:
    i = args.index('--ir'); ir_path = args[i + 1]; del args[i:i + 2]
if '--limit' in args:
    i = args.index('--limit'); lim = int(args[i + 1]); del args[i:i + 2]

card, prog = args[0], args[1]
ir = json.load(open(ir_path, encoding='utf-8'))
node = ir.get(card)
if node is None:
    cand = [k for k in ir if card in k]
    print('没有这张卡。相近:', cand[:10]); sys.exit(1)
for bucket in ('locals', 'entrypoints'):
    body = (node.get(bucket) or {}).get(prog)
    if body is not None:
        steps = body.get('steps') if isinstance(body, dict) else body
        print(f'### {card} :: {bucket} :: {prog}  steps={len(steps)}')
        for n, st in enumerate(steps[:lim]):
            print(f'{n:4d}  {json.dumps(st, ensure_ascii=False)}')
        sys.exit(0)
print(f'{card} 里没有 {prog}。可用 locals:', list((node.get('locals') or {}).keys()))
print('可用 entrypoints:', list((node.get('entrypoints') or {}).keys()))
