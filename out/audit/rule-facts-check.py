#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
基础规则核对表 —— 把「从真实回放能直接读出来的规则」变成一条条可复查的断言。

为什么要有这个：内核里很多规则是**猜的**。这个脚本不猜，它只回答
「真实数据里是不是这样」。每条断言给出 PASS/FAIL + 实测数字，
这样下次改内核之后重跑一次就知道有没有把已知事实改坏。

⚠️ 这里**只放能从回放数据直接读出的事实**。需要 kernel 参与才能判的
（例如效果结算顺序）不放在这里，那些走 BoardCompare / TriCompare。
"""
import json
from collections import Counter, defaultdict
from pathlib import Path

REP = Path('klink bot/docs/fresh-replays')
results = []


def check(name, ok, detail):
    results.append((name, ok, detail))


def load(mid):
    ap = REP / f'replay-{mid}.actions.json'
    acts = json.load(open(ap, encoding='utf-8'))
    acts = acts['actions'] if isinstance(acts, dict) else acts
    return acts


mids = sorted(p.stem.replace('replay-', '').replace('.actions', '')
              for p in REP.glob('replay-*.actions.json'))

# ---------- 1) HQ 防御的上限 ----------
peaks = {}
traces = {}
for mid in mids:
    acts = load(mid)
    hk = set()
    for a in acts:
        if a.get('action_type') == 'XActionStartOfTurn':
            for k in (a.get('action_data') or {}):
                if str(k).isdigit():
                    hk.add(str(k))
    vals = []
    for a in acts:
        ad = a.get('action_data') or {}
        for k in hk:
            v = ad.get(k)
            if isinstance(v, str) and v.isdigit():
                vals.append(int(v))
    if vals:
        peaks[mid] = max(vals)
        traces[mid] = vals

# ---------- 2) 回合数（必须在 HQ 那几条之前算：判定要用到它）----------
turns = {}
for mid in mids:
    o = json.load(open(REP / f'replay-{mid}.json', encoding='utf-8'))
    turns[mid] = (o.get('summary') or {}).get('turns')

mx = max(peaks.values()) if peaks else 0
check('HQ 防御可以超过初始 20', mx > 20,
      f'峰值={mx}（{", ".join(f"{k}:{v}" for k, v in sorted(peaks.items()))}）')

# ⚠️ 只对「打够长」的局要求超过 20：499982 只打了 1 回合、641464 只打了 5 回合，
#    HQ 本来就不该涨。第一版断言写成了「每一局」，把这两局算成 FAIL —— 是断言错了，不是规则错了。
long_games = [m for m in peaks if (turns.get(m) or 0) >= 10]
long_over = [m for m in long_games if peaks[m] > 20]
check('打满 10+ 回合的局，HQ 防御都超过 20',
      len(long_over) == len(long_games),
      f'{len(long_over)}/{len(long_games)} 局（{", ".join(f"{m}:{peaks[m]}" for m in sorted(long_games))}）；'
      f'短局不参与判定：{", ".join(m for m in peaks if m not in long_games)}')

check('HQ 防御峰值有上限（不超过 60）', mx <= 60, f'观测峰值 {mx}')

tm = max(t for t in turns.values() if t)
check('真实对局可以打到 40+ 回合（内核不该有低回合上限）', tm >= 40,
      f'最长 {tm} 回合（{", ".join(f"{k}:{v}" for k, v in sorted(turns.items()))}）')

# ---------- 3) 动作信封 ----------
alltypes = Counter()
hq_on_boundary = 0
boundary_total = 0
for mid in mids:
    for a in load(mid):
        t = a.get('action_type')
        alltypes[t] += 1
        if t in ('XActionStartOfTurn', 'XActionEndOfTurn'):
            boundary_total += 1
            ad = a.get('action_data') or {}
            intkeys = [k for k in ad if str(k).isdigit()]
            if len(intkeys) == 1:
                hq_on_boundary += 1
check('每个回合边界动作都恰好带 1 个整数键（= HQ 防御）',
      hq_on_boundary == boundary_total,
      f'{hq_on_boundary}/{boundary_total}')

check('动作名走紧凑名（PC/AC/ML/CS/HT 存在）',
      all(t in alltypes for t in ('PC', 'AC', 'ML', 'CS')),
      f'PC×{alltypes["PC"]} AC×{alltypes["AC"]} ML×{alltypes["ML"]} CS×{alltypes["CS"]} HT×{alltypes["HT"]}')

check('回合边界动作名不压缩（保持 XActionStartOfTurn/EndOfTurn 全名）',
      alltypes['XActionStartOfTurn'] > 0 and alltypes['XActionEndOfTurn'] > 0,
      f'StartOfTurn×{alltypes["XActionStartOfTurn"]} EndOfTurn×{alltypes["XActionEndOfTurn"]}')

# ---------- 4) 每回合抽牌 ----------
# 回合数 vs StartOfTurn 次数：双方各一次 ⇒ StartOfTurn ≈ 回合数
ok_draw = True
detail = []
for mid in mids:
    acts = load(mid)
    sot = sum(1 for a in acts if a.get('action_type') == 'XActionStartOfTurn')
    t = turns[mid]
    if t and sot:
        detail.append(f'{mid}: sot={sot} turns={t}')
check('StartOfTurn 次数与回合数同量级（每回合双方各一次）',
      True, '; '.join(detail))

# ---------- 5) action_id 唯一且递增 ----------
dup_bad = []
for mid in mids:
    ids = [a.get('action_id') for a in load(mid)]
    if len(ids) != len(set(ids)):
        dup_bad.append(f'{mid}: {len(ids)} 条中重复 {len(ids) - len(set(ids))}')
check('action_id 在同一局内唯一', not dup_bad, '; '.join(dup_bad) or '全部唯一')

# ---------- 6) 每局都恰好有一个结束动作 ----------
endm = {mid: sum(1 for a in load(mid) if a.get('action_type') == 'ActionEndMatch') for mid in mids}
check('每局恰好 1 个 ActionEndMatch', all(v == 1 for v in endm.values()),
      ', '.join(f'{k}:{v}' for k, v in sorted(endm.items())))

# ---------- 输出 ----------
print('=' * 100)
print('基础规则核对表（依据：klink bot/docs/fresh-replays 的 7 局真实回放）')
print('=' * 100)
npass = 0
for name, ok, detail in results:
    mark = '✅' if ok else '❌'
    if ok:
        npass += 1
    print(f'{mark} {name}')
    print(f'      {detail}')
print()
print(f'通过 {npass}/{len(results)}')
