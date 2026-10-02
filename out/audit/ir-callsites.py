# -*- coding: utf-8 -*-
"""打印指定 IR 函数的全部调用点（recv + args 原始 JSON），用于核对参数形状。"""
import json, os, sys, collections

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))

for fn in sys.argv[1:]:
    print('=' * 100)
    print(f'### {fn}')
    n = 0
    shapes = collections.Counter()
    for card, v in ir.items():
        if not isinstance(v, dict):
            continue
        for s in v.get('steps', []):
            if s.get('fn') != fn:
                continue
            n += 1
            if n <= 4:
                print(f'  [{card}] i={s.get("i")}')
                print(f'     recv = {json.dumps(s.get("recv"), ensure_ascii=False)}')
                for k, a in enumerate(s.get('args', [])):
                    print(f'     a[{k}] = {json.dumps(a, ensure_ascii=False)}')
                print(f'     outs = {json.dumps(s.get("outs"), ensure_ascii=False)}')
            shapes[tuple(json.dumps(a, ensure_ascii=False) for a in s.get('args', []))] += 1
    print(f'  --- 共 {n} 个调用点，形状 {len(shapes)} 种 ---')
    for sh, c in shapes.most_common(6):
        print(f'    ×{c}: {list(sh)}')
