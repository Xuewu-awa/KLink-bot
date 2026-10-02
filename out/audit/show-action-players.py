#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""看一局真实回放里每条动作的 player_id / side，用来挑「轮到 bot 的时刻」。"""
import json
import sys
from collections import Counter
from pathlib import Path

mid = sys.argv[1] if len(sys.argv) > 1 else '989040'
p = Path('klink bot/docs/fresh-replays') / f'replay-{mid}.actions.json'
o = json.load(open(p, encoding='utf-8'))
acts = o['actions'] if isinstance(o, dict) else o

sp = Path('klink bot/docs/fresh-replays') / f'replay-{mid}.json'
snap = json.load(open(sp, encoding='utf-8'))
sd = snap['starting_info']['match_and_starting_data']['starting_data']
print(f'左玩家 id={sd.get("player_id_left")}  右玩家 id={sd.get("player_id_right")}')
print(f'左玩家名={sd.get("left_player_name")}  右玩家名={sd.get("right_player_name")}')
print()

print('player_id 分布：')
for k, v in Counter(a.get('player_id') for a in acts).most_common():
    print(f'   {k}: {v} 条')
print()

print('前 30 条：')
for a in acts[:30]:
    ad = a.get('action_data') or {}
    print(f"   #{a.get('action_id'):<4} {str(a.get('action_type')):22s} "
          f"pid={str(a.get('player_id')):8s} turn={a.get('turn_number')} side={ad.get('side', '')}")
