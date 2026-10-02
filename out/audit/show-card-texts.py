#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打印若干张卡的卡面文字（修 localize() 前后对比用）。"""
import json
import sys

live = json.load(open('klink bot/docs/cards.live.json', encoding='utf-8'))
if isinstance(live, dict):
    live = list(live.values())
m = {x.get('name'): x for x in live if isinstance(x, dict) and x.get('name')}

names = sys.argv[1:] or [
    'card_unit_m3a3_honey_desert',
    'card_unit_7th_brigade_anzac_vet',
    'card_unit_dingo_armored_car',
    'card_unit_royal_ulster_rifles',
    'card_unit_1st_airborne',
    'card_unit_17th_infantry_brigade',
]
for n in names:
    x = m.get(n)
    if not x:
        print(f'{n}: 不在库中')
        continue
    print(f'{n}')
    print(f'    {x.get("text")}')
    print()
