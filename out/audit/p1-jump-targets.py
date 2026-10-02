#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：验证「Jump/JumpIfNot 的 Offset 就是目标的 StatementIndex」这个假设。

对 bp-cardfn.json 的每个函数，收集所有 StatementIndex 集合 S 与所有跳转目标集合 J，
统计 J \ S（落在 S 之外的目标）有多少。
"""
import json, sys, collections

path = sys.argv[1] if len(sys.argv) > 1 else 'out/bp-cardfn.json'
d = json.load(open(path, encoding='utf-8'))
tot_j = 0
miss = collections.Counter()
missing_examples = collections.defaultdict(list)
for fn, body in d.items():
    bc = body['bytecode'] if isinstance(body, dict) else body
    S = {s.get('StatementIndex') for s in bc if s.get('StatementIndex') is not None}
    for s in bc:
        if s.get('Inst') in ('Jump', 'JumpIfNot') and s.get('Offset') is not None:
            tot_j += 1
            if s['Offset'] not in S:
                miss[fn] += 1
                if len(missing_examples[fn]) < 4:
                    missing_examples[fn].append((s['StatementIndex'], s['Offset']))

print(f'总跳转 {tot_j}，落在语句集之外 {sum(miss.values())}（{sum(miss.values())/max(tot_j,1)*100:.2f}%）')
print('有落空目标的函数（前 25）:')
for fn, n in miss.most_common(25):
    print(f'  {fn:40s} {n:4d}  例(si->off): {missing_examples[fn]}')
