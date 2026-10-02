#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1b：内置卡组里含哪些「伤害修正链 / Alpine」的卡。
卡组码解码照抄 klink bot/tools/analyze-decks.py 的做法（2 字符码 -> 卡名）。
用法: python out/audit/p1b-deckchain.py
"""
import json, re
from pathlib import Path

ROOT = Path('.')
ids = json.load(open('klink bot/docs/deck_code_ids.json', encoding='utf-8'))

DECKS = [
    ("德芬车", "%%19|303NE1gVwCxr;2Z3f3X4AoUrKwE;E4nlxoxpza;ux"),
    ("德澳老兵", "%%2a|0m19DJDSgvoosQsTtXw2y7yJyO;0leFqYwbyc;1VDTy9yd;DR"),
    ("英苏中立", "%%24|0mDDgvooq0sQtVtXyEyJyU;DQE0qYxmyGyZzB;DRzC;tTyH"),
    ("米色团", "%%25|0m19oow7;bqEbohsDtVw0wa;vSwbyw;DRpUsU"),
    ("日波炸槽", "%%38|5CjQp2pezq;5B5Z6w6xtFztzw;p1p6zpzs;pczi"),
    ("日澳快攻", "%%3a|5C6B6yhHrktGwXznzqzvzx;DJE7tFy2;7ltKy9zs;7axX"),
    ("苏英爆破", "%%42|8C9bfrjWppqTt3tmvewqwwyUyXz2z5;DQE0tVxmyZz0z1;DRzC;yH"),
    ("苏澳中速", "%%4a|8C9bfrjWppqTt3tmvewqwwxYyXz5;8NDJE0xWxXz0z1zx;xmy9zC;"),
    ("自残苏", "%%48|7M8Cwy;9mthu5;8I8UE5gbxmzC;7Hz0zi"),
    ("美澳跳", "%%5a|bCbEbibmcPDBDCdkfGmPtYv6vYy7ycyv;DJv7zx;bPy9yd;bKgg"),
    ("美英跳", "%%52|bCbEbmcPDBDCdkfr4rctYu8v6v7vUvXvYw2yv;qYw8;bKbPlgyH;DR"),
    ("美澳极限快", "%%5a|bBDBtYv6vYy3;bqDHjyoh;bOxcyw;DGglvSxX"),
    ("德美", "%%15|2L2Q2Z31343b3j3m3N3O3YbPgggUlUnDnsoTtdtgu0xrz6;j1nwofoPvh;bKmI;"),
    ("英日", "%%23|0m0z195NgvoosTwbyEyJ;qYw8;0d1V78oxp3tTyH;5B"),
    ("英美2", "%%25|0j0m0w0z191xgFgvjzkplroosQsTw2w7w8yJ;08bPj1mIofqYwb;mU;bK"),
    ("日美", "%%35|5C5R5x636B6Y6Z7b7s7xlfnPrk;5z666CbPjOoeohp4p9x3;bKgL;"),
    ("日法", "%%36|5C636Bzn;2h2l5z6XEagJnNnQp9tKwh;7a7eg3;2p"),
    ("英芬", "%%29|0B0D0d0meFgvjen8ngoopUq0rKsQw7;0l1VnjqYrLrPsOsRtWw8wbwc;;"),
]

ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
chain = set()
for card, node in ir.items():
    loc = (node or {}).get('locals') or {}
    if 'OnCardDealDamage_ModifyDamageDealt' in loc or 'OnOtherCardDealDamageAddDamage' in loc:
        chain.add(card)

alpine = set()
for card, node in ir.items():
    pass
import re as _re
src = open('src/KLink.Bot/Cards/CardInnateTable.cs', encoding='utf-8').read()
for m in _re.finditer(r'\["([^"]+)"\]\s*=\s*\(\[([^\]]*)\]', src):
    if 'Alpine' in m.group(2):
        alpine.add(m.group(1))

print('伤害修正链卡数(IR locals):', len(chain))
print('Alpine 卡数:', len(alpine))
print()
for name, code in DECKS:
    body = code.split('|', 1)[1]
    groups = body.split(';')
    cards = []
    for gi, g in enumerate(groups):
        n = 1
        m = _re.match(r'^(\d+)', g)
        if m:
            n = int(m.group(1))
            g = g[m.end():]
        for i in range(0, len(g) - 1, 2):
            cid = ids.get(g[i:i + 2])
            if cid:
                cards.extend([cid] * n)
    hit_chain = sorted({c for c in cards if c in chain})
    hit_alp = sorted({c for c in cards if c in alpine})
    if hit_chain or hit_alp:
        print('%-10s 链卡 %2d 张: %s' % (name, len(hit_chain), ','.join(hit_chain)))
        print('%-10s 山地 %2d 张: %s' % ('', len(hit_alp), ','.join(hit_alp)))
