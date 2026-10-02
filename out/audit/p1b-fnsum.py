#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：打印 out/bp-*.json 里若干函数的语句数 / 最大 StatementIndex。
用法: python out/audit/p1b-fnsum.py <fn> [fn...] [--file out/bp-cardfn.json]
"""
import json, sys

args = sys.argv[1:]
path = 'out/bp-cardfn.json'
if '--file' in args:
    i = args.index('--file'); path = args[i + 1]; del args[i:i + 2]

d = json.load(open(path, encoding='utf-8'))
for k in args:
    b = d.get(k)
    if b is None:
        print(k + ': MISSING')
        continue
    bc = b['bytecode'] if isinstance(b, dict) else b
    mx = max((s.get('StatementIndex', -1) for s in bc), default=-1)
    fl = b.get('function_flags') if isinstance(b, dict) else None
    print('%s: stmts=%d maxsi=%d flags=%s' % (k, len(bc), mx, fl))
