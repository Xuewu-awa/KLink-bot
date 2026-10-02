#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P2：列出用到某个调用的卡，并打上类型/数值/卡面文字 —— 用来挑"端到端探针卡"。"""
import json
import sys

eff = json.load(open('klink bot/docs/card-effects.json', encoding='utf-8'))
cards = json.load(open('klink bot/docs/cards.live.json', encoding='utf-8'))
if isinstance(cards, dict):
    cards = list(cards.values())
byname = {c.get('name'): c for c in cards if isinstance(c, dict)}


def main():
    pat = sys.argv[1] if len(sys.argv) > 1 else 'AddAttackUntilEndOfTurn'
    for n, c in sorted(eff.items()):
        calls = set(c.get('calls') or [])
        for fn, lst in (c.get('functions') or {}).items():
            calls.update(lst or [])
        if pat not in calls:
            continue
        d = byname.get(n) or {}
        t = d.get('type', '')
        a, dd = d.get('attack'), d.get('defense')
        txt = (d.get('text') or '').replace('\n', ' ')
        print(f'{n:44s} {t:10s} {a}/{dd}')
        print(f'     {txt[:170]}')


if __name__ == '__main__':
    main()
