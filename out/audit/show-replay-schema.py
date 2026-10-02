#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打印真实回放的完整字段形状 —— 用来告诉对方「导出时必须要哪些字段」。"""
import json
import sys
from pathlib import Path

mid = sys.argv[1] if len(sys.argv) > 1 else '989040'
base = Path('klink bot/docs/fresh-replays')

o = json.load(open(base / f'replay-{mid}.json', encoding='utf-8'))
print('=== replay-<id>.json 顶层 ===')
print('  ', list(o.keys()))

si = o.get('starting_info') or {}
print()
print('=== starting_info 顶层 ===')
print('  ', list(si.keys()))

msd = si.get('match_and_starting_data') or {}
print()
print('=== match_and_starting_data ===')
print('  ', list(msd.keys()))

sd = msd.get('starting_data') or {}
print()
print(f'=== starting_data（{len(sd)} 个键）===')
for k, v in sd.items():
    if isinstance(v, list):
        print(f'  {k:24s} list[{len(v)}]')
    elif isinstance(v, dict):
        print(f'  {k:24s} obj {list(v.keys())}')
    else:
        print(f'  {k:24s} {type(v).__name__} = {str(v)[:40]}')

print()
print('=== 关键：单张卡的字段（这决定了能不能用）===')
for k in ('location_card_left', 'starting_hand_left'):
    v = sd.get(k)
    if isinstance(v, list) and v:
        print(f'  {k}[0] = {json.dumps(v[0], ensure_ascii=False)}')
    elif isinstance(v, dict):
        print(f'  {k}    = {json.dumps(v, ensure_ascii=False)}')
if sd.get('deck_left'):
    print(f'  deck_left[0] = {json.dumps(sd["deck_left"][0], ensure_ascii=False)}')

print()
print('=== 动作流 ===')
acts = json.load(open(base / f'replay-{mid}.actions.json', encoding='utf-8'))
acts = acts['actions'] if isinstance(acts, dict) else acts
print(f'  {len(acts)} 条；第一条 = {json.dumps(acts[0], ensure_ascii=False)}')
print(f'  含 sub_actions 的条数: {sum(1 for a in acts if a.get("sub_actions"))}')
print(f'  含 local_subactions 的条数: {sum(1 for a in acts if a.get("local_subactions") is not None)}')

print()
print('=== summary ===')
print(' ', json.dumps(o.get('summary'), ensure_ascii=False))
