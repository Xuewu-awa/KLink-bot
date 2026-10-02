#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：从 klink bot/decompiled/cards.full.json 里按子串找资产并列出函数。"""
import json, sys

d = json.load(open('klink bot/decompiled/cards.full.json', encoding='utf-8'))
a = d['assets']
needle = sys.argv[1]
want = sys.argv[2] if len(sys.argv) > 2 else None
for k, v in a.items():
    if needle not in k:
        continue
    print('===', k, 'ok=', v.get('ok'), 'has_bytecode=', v.get('has_bytecode'))
    fns = v.get('functions')
    if isinstance(fns, dict):
        for fn, body in fns.items():
            if want and want.lower() not in fn.lower():
                continue
            n = len(body.get('bytecode', [])) if isinstance(body, dict) else len(body)
            print(f'   fn {fn}  stmts={n}')
    elif isinstance(fns, list):
        for body in fns:
            fn = body.get('name') if isinstance(body, dict) else '?'
            if want and want.lower() not in str(fn).lower():
                continue
            n = len(body.get('bytecode', [])) if isinstance(body, dict) else len(body)
            print(f'   fn {fn}  stmts={n}')
