#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
看人类玩家自己发的 `PC`/`ML`/`AC` 的 `action_data` 是什么形状 ——
**这是客户端接受的权威格式**，bot 产出的动作必须与之逐槽对齐。
"""
import json
import sys
from collections import Counter
from pathlib import Path

mid = sys.argv[1] if len(sys.argv) > 1 else '989040'
p = Path('klink bot/docs/fresh-replays') / f'replay-{mid}.actions.json'
o = json.load(open(p, encoding='utf-8'))
acts = o['actions'] if isinstance(o, dict) else o

print('=== 全部 PC / ML / AC 的槽位形状（去重计数）===')
shapes = Counter()
for a in acts:
    t = a.get('action_type')
    if t in ('PC', 'ML', 'AC'):
        keys = tuple(sorted((a.get('action_data') or {}).keys(), key=lambda x: (len(x), x)))
        shapes[(t, keys)] += 1
for (t, keys), n in sorted(shapes.items()):
    print(f'  {t:3s}  keys={list(keys)}   ×{n}')

print()
print('=== 逐条样例（每个类型前 4 条）===')
for want in ('PC', 'ML', 'AC'):
    shown = 0
    for a in acts:
        if a.get('action_type') != want or shown >= 4:
            continue
        print(f"  #{a.get('action_id'):<4} {want} turn={a.get('turn_number')} "
              f"pid={a.get('player_id')} {json.dumps(a.get('action_data'), ensure_ascii=False)}")
        shown += 1
    print()

print('=== 有没有 sub_actions / local_subactions ===')
sub = Counter()
for a in acts:
    sub[(a.get('action_type'), bool(a.get('sub_actions')), a.get('local_subactions'))] += 1
for k, v in sorted(sub.items(), key=lambda x: -x[1])[:8]:
    print(f'  {k}  ×{v}')
