#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：打印若干卡的属性（type/attack/defense/range/cost/op/flags/text）。
用法: python out/audit/p1b-cards.py <cardID> [cardID...]
"""
import json, sys

d = json.load(open('out/cards-full2.json', encoding='utf-8'))
byid = {r['id']: r for r in d}
for n in sys.argv[1:]:
    r = byid.get(n)
    if not r:
        print(n, 'MISSING')
        continue
    raw = r['raw']
    print('%-46s type=%-10s atk=%s def=%s range=%s cost=%s op=%s' % (
        n, raw.get('Type'), raw.get('attack'), raw.get('defense'),
        raw.get('range'), raw.get('kredits'), raw.get('operationCost')))
    print('     flags=%s' % r['flags'])
    print('     text=%s' % r['text'])
