#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P2：把 BP_CardFunctions 里某几个函数的语句级细节全量打印（含嵌套 Parameters）。"""
import json
import sys

FULL = 'klink bot/decompiled/cards.full.json'


def main():
    assets = json.load(open(FULL, encoding='utf-8'))['assets']
    for want in sys.argv[1:]:
        for asset, body in assets.items():
            if 'BP_CardFunctions' not in asset:
                continue
            fns = body.get('functions') or {}
            if want not in fns:
                continue
            bc = fns[want].get('bytecode', [])
            print('=' * 96)
            print(f'{want}   {len(bc)} 条语句')
            print('=' * 96)
            for i, ins in enumerate(bc):
                print(f'--- [{i}] {ins.get("Inst")} ---')
                print(json.dumps(ins, ensure_ascii=False, indent=1))


if __name__ == '__main__':
    main()
