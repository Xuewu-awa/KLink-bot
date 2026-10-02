#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
调查：采集快照与 fyserver 回放的 cardID 空间到底是不是同一个。

背景：BoardCompare 报「卡名 99.7%」，但同时区域只有 ~60%，且区域错配里
所有卡名**都是对的**。这两个事实只有在「ID 能对上、但区域归属不同」时才自洽。
可是逐张查又发现同一张卡在两边编号不同。必须把这件事查死。

输出四个数：
  A 同 id 同名命中          —— DetectFlip 现在数的就是这个
  B 翻转 id 同名命中
  C 「同 id 同名」里 名字也一致 的比例（A 会不会是假阳性）
  D 按名字对齐（完全忽略 id）的命中 —— 用名字当桥，看区域是否真的不同
"""
import json
import sys
from collections import defaultdict

REP = sys.argv[1] if len(sys.argv) > 1 else 'klink bot/docs/fresh-replays/replay-989040.json'
SNAP = sys.argv[2] if len(sys.argv) > 2 else 'out/_handoff/klink-bot/klink bot/docs/capture/snapshot-match28.jsonl'

rep = json.load(open(REP, encoding='utf-8'))
sd = rep['starting_info']['match_and_starting_data']['starting_data']

smap = {}
rep_pool = {}
for f in ('starting_hand_left', 'deck_left', 'starting_hand_right', 'deck_right'):
    for c in (sd.get(f) or []):
        rep_pool[c['card_id']] = (c['name'], f)
for k in ('location_card_left', 'location_card_right'):
    c = sd.get(k)
    if c:
        rep_pool[c['card_id']] = (c['name'], k)

s0 = json.loads(open(SNAP, encoding='utf-8').readline())
for c in s0['cards']:
    cid = int(c['cardID'])
    smap[cid] = (c['Name'], c['location'], c['locationNumber'])

LOC = {'1': '左库', '2': '右库', '3': '左手', '4': '右手',
       '5': '左半场', '6': '右半场', '7': '前线', '8': '弃牌', '9': '牌库'}


def flip(i):
    return i + 40 if 0 < i <= 40 else (i - 40 if 40 < i <= 80 else i)


A = B = 0
for i, (n, _) in rep_pool.items():
    if i in smap and smap[i][0] == n:
        A += 1
    if flip(i) in smap and smap[flip(i)][0] == n:
        B += 1

print(f'replay 池 = {len(rep_pool)} 张，快照 = {len(smap)} 张')
print(f'A 同 id 同名    : {A}')
print(f'B 翻转 id 同名  : {B}')
print()

# 按名字对齐：同一张卡在两个空间里的 id
by_name_rep = defaultdict(list)
for i, (n, _) in rep_pool.items():
    by_name_rep[n].append(i)
by_name_snap = defaultdict(list)
for i, (n, _, _) in smap.items():
    by_name_snap[n].append(i)

rep_names = set(by_name_rep)
snap_names = set(by_name_snap)
print(f'名字集合：replay {len(rep_names)}，snapshot {len(snap_names)}，交集 {len(rep_names & snap_names)}')
print(f'只在 replay  : {len(rep_names - snap_names)} 张（例：{sorted(rep_names - snap_names)[:4]}）')
print(f'只在 snapshot: {len(snap_names - rep_names)} 张（例：{sorted(snap_names - rep_names)[:4]}）')
print()

# 「同 id 同名」的样本，逐个列出两边的名字，看是不是真的一致
print('=== 同 id 同名 的 12 个样本 ===')
shown = 0
for i, (n, f) in sorted(rep_pool.items()):
    if i in smap and smap[i][0] == n:
        print(f'  #{i:<4} replay={n:<38} [{f}]   snap={smap[i][1]} locNum={smap[i][2]}')
        shown += 1
        if shown >= 12:
            break

print()
print('=== 同 id 但名字不同 的 12 个样本（假匹配的证据）===')
shown = 0
for i, (n, f) in sorted(rep_pool.items()):
    if i in smap and smap[i][0] != n:
        print(f'  #{i:<4} replay={n:<38} [{f}]')
        print(f'        snap={smap[i][0]:<38} loc={LOC.get(smap[i][1], smap[i][1])}')
        shown += 1
        if shown >= 12:
            break
