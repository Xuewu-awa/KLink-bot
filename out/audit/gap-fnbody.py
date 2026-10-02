#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
P2：转储 BP_CardFunctions（或任意资产）里某个函数的**函数体**，供照抄语义。

用法：
  python -X utf8 out/audit/gap-fnbody.py AddAttackUntilEndOfTurn
  python -X utf8 out/audit/gap-fnbody.py --asset BP_CardFunctions DamageMultipleCards MakeCardRetreat
"""
import json
import sys

FULL = 'klink bot/decompiled/cards.full.json'


def main():
    args = sys.argv[1:]
    asset_filter = 'BP_CardFunctions'
    if args and args[0] == '--asset':
        asset_filter = args[1]
        args = args[2:]

    assets = json.load(open(FULL, encoding='utf-8'))['assets']

    for want in args:
        print('=' * 100)
        print(f'### {want}')
        print('=' * 100)
        found = False
        for asset, body in assets.items():
            stem = asset.split('/')[-1]
            if asset_filter and asset_filter.lower() not in stem.lower():
                continue
            fns = body.get('functions')
            if not isinstance(fns, dict) or want not in fns:
                continue
            found = True
            fb = fns[want]
            bc = fb.get('bytecode', []) if isinstance(fb, dict) else fb
            print(f'资产 {stem}  函数 {want}  {len(bc)} 条语句')
            if isinstance(fb, dict):
                for k in ('params', 'inputs', 'outputs', 'flags', 'signature'):
                    if k in fb:
                        print(f'  {k}: {json.dumps(fb[k], ensure_ascii=False)[:400]}')
            print('-' * 100)
            for i, ins in enumerate(bc):
                if isinstance(ins, dict):
                    s = json.dumps(ins, ensure_ascii=False)
                    print(f'{i:4d}  {s[:400]}')
                else:
                    print(f'{i:4d}  {ins}')
        if not found:
            print(f'  ⚠ 在 {asset_filter} 里找不到 {want}')
        print()


if __name__ == '__main__':
    main()
