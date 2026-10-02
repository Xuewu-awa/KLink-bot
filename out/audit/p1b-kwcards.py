#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：列出带某个关键字的卡 + 卡面文本。
用法: python out/audit/p1b-kwcards.py <Keyword>
"""
import json, re, sys, glob

kw = sys.argv[1]
src = open('src/KLink.Bot/Cards/CardInnateTable.cs', encoding='utf-8').read()

# 形如  ["card_unit_x"] = (["Alpine","Blitz"], ...)
cards = []
for m in re.finditer(r'\["([^"]+)"\]\s*=\s*\(\[([^\]]*)\]', src):
    flags = re.findall(r'"([^"]+)"', m.group(2))
    if kw in flags:
        cards.append(m.group(1))

texts = {}
for path in ('out/cards-full2.json', 'out/cards-full.json'):
    try:
        d = json.load(open(path, encoding='utf-8'))
    except Exception:
        continue
    if isinstance(d, list):
        for row in d:
            n = row.get('cardID') or row.get('name') or row.get('id')
            if n:
                texts.setdefault(n, row)
    break

print(f'{kw}: {len(cards)} 张')
for c in sorted(cards):
    row = texts.get(c)
    t = ''
    if isinstance(row, dict):
        for k in ('text', 'Text', 'description', 'cardText', 'Text_EN'):
            if isinstance(row.get(k), str) and row[k].strip():
                t = row[k].strip().replace('\n', ' ')
                break
    print(f'  {c:46s} {t[:110]}')
