#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：统计 IR 里 `getHas*` 一族的调用点（派发表里以前一个键都没有）。

用法: python out/audit/p1-gethas-calls.py
"""
import json, collections

ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
cnt = collections.Counter()
cards = collections.defaultdict(set)
withrecv = collections.Counter()

def walk(o, card):
    if isinstance(o, dict):
        if o.get('op') == 'call' and str(o.get('fn', '')).startswith('getHas'):
            cnt[o['fn']] += 1
            cards[o['fn']].add(card)
            if o.get('recv'):
                withrecv[o['fn']] += 1
        for v in o.values():
            walk(v, card)
    elif isinstance(o, list):
        for v in o:
            walk(v, card)

for k, v in ir.items():
    walk(v, k)

print(f'{"函数":24s} {"调用点":>6s} {"卡数":>5s} {"带 recv":>8s}')
for fn, n in cnt.most_common():
    print(f'{fn:24s} {n:6d} {len(cards[fn]):5d} {withrecv[fn]:8d}')
print()
print('合计调用点', sum(cnt.values()), '  卡数', len(set().union(*cards.values()) if cards else set()))
