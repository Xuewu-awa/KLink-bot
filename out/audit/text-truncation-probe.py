#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
量化「cards.live.json 的卡面文字被截断」的影响面。

背景：`card_unit_m3a3_honey_desert` 的蓝图 CDO 里 Text 是
  "When you draw a card, your HQ gains +1 defense."
而 cards.live.json 里只存了
  "your HQ gains +1 defense."
**触发条件整个丢了** —— 这正是我昨天误判「这个效果不该每回合触发」的根源。

CDO 的 Text 是 UE 的本地化引用，格式是
  Base, <pkg>, <key>, <正文>
（有时 4 段，有时更多）。所以要取**最后一段**当正文。

输出：总卡数、被截断的卡数、以及若干样例（截断掉的**前缀**）。
"""
import json
import re

assets = json.load(open('klink bot/decompiled/cards.all.json', encoding='utf-8'))['assets']
live = json.load(open('klink bot/docs/cards.live.json', encoding='utf-8'))
if isinstance(live, dict):
    live = list(live.values())
livemap = {x.get('name'): x for x in live if isinstance(x, dict) and x.get('name')}


def body(text: str) -> str:
    """
    从 `Base, <pkg>, <key>, <正文>` 里取正文。

    ⚠️ **不能用「按逗号切、取最后一段」** —— 正文自己就含逗号：
      'Base, card_britain, card_unit_m3a3_honey_desert_text, When you draw a card, your HQ gains +1 defense.'
    切完最后一段只剩 'your HQ gains +1 defense.'，**触发条件被吃掉**。
    （我第一版就是这么写的，所以量出来"0 张被截断"，是假阴性。）

    正确做法：以 `<name>_text` 这个本地化 key 当锚点，取它**之后**的全部内容。
    """
    for marker in ('_text, ', '_title, '):
        idx = text.find(marker)
        if idx >= 0:
            return text[idx + len(marker):].strip()
    parts = [p.strip() for p in text.split(',', 3)]
    return parts[3] if len(parts) > 3 else text


cdo = {}
for k, v in assets.items():
    c = v.get('cdo') or {}
    n, t = c.get('Name'), c.get('Text')
    if n and isinstance(t, str) and t:
        cdo[n] = t

print(f'CDO 有 Text 的卡 : {len(cdo)}')
print(f'cards.live 卡数  : {len(livemap)}')
print()

trunc = []
for n, t in cdo.items():
    lv = livemap.get(n)
    if not lv:
        continue
    full = body(t)
    lt = (lv.get('text') or '').strip()
    if not lt or not full:
        continue
    if full != lt and full.endswith(lt):
        trunc.append((n, full[:len(full) - len(lt)].strip(' ,'), lt, full))

print(f'被截断（CDO 全文 = 前缀 + 库里的文字）: {len(trunc)} 张')
print()
print('样例（**截断掉的前缀** ← 这就是丢失的触发条件）:')
for n, prefix, lt, full in trunc[:25]:
    print(f'  {n}')
    print(f'      丢失前缀 : {prefix}')
    print(f'      库里还剩 : {lt}')
print()
if len(trunc) > 25:
    print(f'  … 其余 {len(trunc) - 25} 张同类')

# 按丢失前缀的形态归类
from collections import Counter
shape = Counter()
for n, prefix, lt, full in trunc:
    p = prefix.lower()
    if p.startswith('when '):
        shape['when …（触发条件）'] += 1
    elif p.startswith('if '):
        shape['if …（条件）'] += 1
    elif p.startswith('deployment'):
        shape['deployment: …'] += 1
    elif p.startswith('destruction'):
        shape['destruction: …'] += 1
    else:
        shape['其它'] += 1
print()
print('丢失前缀的形态分布:')
for k, v in shape.most_common():
    print(f'  {k:28s} {v}')
