#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""查 HQ 防御在动作流里的那个键，以及它的轨迹（用来判断 58/82 那种值是不是真存在）。"""
import json
import sys

p = sys.argv[1] if len(sys.argv) > 1 else 'klink bot/docs/fresh-replays/replay-989040.actions.json'
o = json.load(open(p, encoding='utf-8'))
acts = o['actions'] if isinstance(o, dict) else o

print(f'动作数 {len(acts)}')

for a in acts:
    if 'EndOfTurn' in str(a.get('action_type', '')):
        print('样例 EndOfTurn:')
        print('  action_type =', a.get('action_type'))
        print('  action_data =', json.dumps(a.get('action_data'), ensure_ascii=False))
        break

# 所有出现在 action_data 里的键
keys = {}
for a in acts:
    ad = a.get('action_data') or {}
    if isinstance(ad, dict):
        for k, v in ad.items():
            keys.setdefault(k, []).append(v)
print()
print('action_data 出现过的键：')
for k in sorted(keys, key=lambda x: (len(x), x)):
    vs = keys[k]
    print(f'  [{k}] x{len(vs)}  例={vs[:8]}')
