#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：原样打印 decompiled/cards.full.json 里某个资产某个函数的语句。"""
import json, sys
needle = sys.argv[1]; want = sys.argv[2]
lo = int(sys.argv[3]) if len(sys.argv) > 3 else -10**9
hi = int(sys.argv[4]) if len(sys.argv) > 4 else 10**9
d = json.load(open('klink bot/decompiled/cards.full.json', encoding='utf-8'))
for k, v in d['assets'].items():
    if needle not in k: continue
    for fn, body in (v.get('functions') or {}).items():
        if want != fn: continue
        bc = body.get('bytecode', []) if isinstance(body, dict) else body
        for st in bc:
            si = st.get('StatementIndex')
            if si is None or si < lo or si > hi: continue
            print(f'--- si={si}')
            print('   ', json.dumps(st, ensure_ascii=False))
