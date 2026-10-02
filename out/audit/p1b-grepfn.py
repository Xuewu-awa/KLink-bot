#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：在某个函数体里按子串过滤语句（打印 si/inst/offset/函数名/JSON）。
用法: python out/audit/p1b-grepfn.py <fn> <substr> [substr...]
"""
import json, sys

fn = sys.argv[1]
needles = sys.argv[2:]
d = json.load(open('out/bp-cardfn.json', encoding='utf-8'))
bc = d[fn]['bytecode']
for s in bc:
    j = json.dumps(s, ensure_ascii=False)
    if not all(n in j for n in needles):
        continue
    si = s.get('StatementIndex')
    inst = s.get('Inst')
    off = s.get('Offset')
    name = s.get('FunctionName') or s.get('Function') or ''
    arrow = ('-> %s' % off) if off is not None else ''
    print('%5d %-22s %-9s %s' % (si, inst, arrow, name))
    print('      ' + j[:600])
