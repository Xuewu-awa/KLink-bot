#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把真实回放 action_data 里的数值键**认出来** —— 逐键对齐到语义。

做法：`XActionEndOfTurn` / `XActionStartOfTurn` 这两个动作的 action_data 结构最简单，
把它打出来就能看出「哪个键是哪个玩家的 HQ 防御」。

顺带回答两个基础规则问题：
  Q1 真实对局里 HQ 防御会不会超过 20？（决定内核的 +1/抽牌 累积是不是错的）
  Q2 真实对局的回合数上限是多少？（决定内核要不要加回合上限）
"""
import json
from collections import defaultdict
from pathlib import Path

REP = Path('klink bot/docs/fresh-replays')


def main():
    files = sorted(p for p in REP.glob('replay-*.json') if not p.name.endswith('.actions.json'))

    print('=' * 100)
    print('XActionEndOfTurn / StartOfTurn 的 action_data（这两种动作的键最少，最好认）')
    print('=' * 100)
    for f in files:
        ap = REP / f'{f.stem}.actions.json'
        if not ap.exists():
            continue
        acts = json.load(open(ap, encoding='utf-8'))
        acts = acts['actions'] if isinstance(acts, dict) else acts
        mid = f.stem.replace('replay-', '')
        shown = 0
        for a in acts:
            t = a.get('action_type')
            if t in ('XActionEndOfTurn', 'XActionStartOfTurn') and shown < 3:
                print(f'  {mid:9s} {t:22s} {json.dumps(a.get("action_data"), ensure_ascii=False)}')
                shown += 1

    print()
    print('=' * 100)
    print('用 name→card_id 反查：哪个键的取值落在 card_id 集合里')
    print('=' * 100)
    for f in files:
        ap = REP / f'{f.stem}.actions.json'
        jp = REP / f'{f.stem}.json'
        if not (ap.exists() and jp.exists()):
            continue
        o = json.load(open(jp, encoding='utf-8'))
        sd = ((o.get('starting_info') or {}).get('match_and_starting_data') or {}).get('starting_data') or {}
        ids = set()
        for k in ('starting_hand_left', 'deck_left', 'starting_hand_right', 'deck_right'):
            for c in (sd.get(k) or []):
                ids.add(c.get('card_id'))
        for k in ('location_card_left', 'location_card_right'):
            c = sd.get(k)
            if c:
                ids.add(c.get('card_id'))
        acts = json.load(open(ap, encoding='utf-8'))
        acts = acts['actions'] if isinstance(acts, dict) else acts
        by_key = defaultdict(set)
        for a in acts:
            ad = a.get('action_data') or {}
            if not isinstance(ad, dict):
                continue
            for kk, vv in ad.items():
                if str(kk).isdigit() and isinstance(vv, str) and vv.isdigit():
                    by_key[str(kk)].add(int(vv))
        print(f'  {f.stem.replace("replay-", ""):9s} card_id 池 {min(ids)}..{max(ids)} (n={len(ids)})')
        for kk in sorted(by_key, key=lambda x: int(x)):
            vs = by_key[kk]
            hit = len(vs & ids)
            if hit:
                print(f'      键[{kk:>3s}] {len(vs):3d} 个值，其中 {hit:3d} 个命中 card_id 池')

    print()
    print('=' * 100)
    print('Q1: 真实对局里各键的最大值（看 HQ 防御类键能涨到多少）')
    print('=' * 100)
    agg = defaultdict(list)
    for f in files:
        ap = REP / f'{f.stem}.actions.json'
        if not ap.exists():
            continue
        acts = json.load(open(ap, encoding='utf-8'))
        acts = acts['actions'] if isinstance(acts, dict) else acts
        for a in acts:
            ad = a.get('action_data') or {}
            if not isinstance(ad, dict):
                continue
            for kk, vv in ad.items():
                if str(kk).isdigit() and isinstance(vv, str) and vv.isdigit():
                    agg[str(kk)].append(int(vv))
    for kk in sorted(agg, key=lambda x: int(x)):
        vs = agg[kk]
        # HQ 防御类的键：取值集中在 14..60 之间
        lo, hi = min(vs), max(vs)
        flag = '  ← 疑似 HQ 防御类' if 14 <= lo and hi <= 60 else ''
        print(f'  键[{kk:>3s}]  n={len(vs):4d}  {lo}..{hi}{flag}')

    print()
    print('=' * 100)
    print('Q2: 回合数与结束方式')
    print('=' * 100)
    for f in files:
        o = json.load(open(f, encoding='utf-8'))
        s = o.get('summary') or {}
        print(f'  {f.stem.replace("replay-", ""):9s} turns={s.get("turns")}')


if __name__ == '__main__':
    main()
