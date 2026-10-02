#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""转储 `out/bp-cardfn.json` 里某个函数的语句 + 谁是它的调用者。"""
import json
import sys

O = json.load(open('out/bp-cardfn.json', encoding='utf-8'))


def dump(name, prefix=''):
    v = O.get(name)
    if not v:
        print(f'（没有函数 {name}）')
        return
    bc = v.get('bytecode') or []
    print('=' * 100)
    print(f'{name}   {len(bc)} 条   flags={v.get("function_flags")}')
    print('=' * 100)
    for i, ins in enumerate(bc):
        s = json.dumps(ins, ensure_ascii=False)
        print(f'{i:4d}  {s[:280]}')
    print()


def callers(name):
    print(f'=== 谁调用 {name} ===')
    for k, v in sorted(O.items()):
        blob = json.dumps(v.get('bytecode') or [], ensure_ascii=False)
        if name in blob:
            n = len(v.get('bytecode') or [])
            print(f'  {k:46s} {n:4d} 条')
    print()


if __name__ == '__main__':
    for a in sys.argv[1:]:
        if a.startswith('@'):
            callers(a[1:])
        else:
            dump(a)
