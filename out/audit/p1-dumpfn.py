#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1 通用：按 StatementIndex 打印 bp-cardfn.json 里某个函数的语句。
用法: python out/audit/p1-dumpfn.py <fn> [lo] [hi] [--file out/bp-cardfn.json]
"""
import json, sys, os

def main():
    args = sys.argv[1:]
    path = 'out/bp-cardfn.json'
    if '--file' in args:
        i = args.index('--file'); path = args[i+1]; del args[i:i+2]
    fn = args[0]
    lo = int(args[1]) if len(args) > 1 else -10**9
    hi = int(args[2]) if len(args) > 2 else 10**9
    d = json.load(open(path, encoding='utf-8'))
    if fn not in d:
        cand = [k for k in d if fn.lower() in k.lower()]
        print('没有这个函数。相近的:', cand[:20]); return
    body = d[fn]
    bc = body['bytecode'] if isinstance(body, dict) else body
    print(f'### {fn}  expression_count={body.get("expression_count") if isinstance(body,dict) else "?"}  stmts={len(bc)}')
    for st in bc:
        si = st.get('StatementIndex')
        if si is None or si < lo or si > hi: continue
        print(f'--- si={si} off={st.get("Offset")}')
        print('    ' + json.dumps(st, ensure_ascii=False))

main()
