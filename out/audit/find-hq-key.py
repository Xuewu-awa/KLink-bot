#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
自动识别「HQ 防御」那个键。

思路：HQ 防御的特征很明确 ——
  · 取值落在一个合理血量区间（≤ 60；卡面初始 20）
  · **只在 `side` 明确**的动作上变化，且 `XActionStartOfTurn`/`XActionEndOfTurn` 都带它
  · 同一局里它是**单调不增**的（HQ 只会被打，除了少数加血效果）

先把每个整数键的 (出现次数, 取值区间, 是否出现在 EndOfTurn 上) 列出来，
再逐键打印它在某局里的完整轨迹。
"""
import json
import sys
from collections import defaultdict
from pathlib import Path

REP = Path('klink bot/docs/fresh-replays')


def load(mid):
    ap = REP / f'replay-{mid}.actions.json'
    acts = json.load(open(ap, encoding='utf-8'))
    return acts['actions'] if isinstance(acts, dict) else acts


def main():
    files = sorted(p.stem.replace('replay-', '').replace('.actions', '')
                    for p in REP.glob('replay-*.actions.json'))

    print('=' * 100)
    print('每个整数键的统计（重点看：出现在 EndOfTurn 上的那些）')
    print('=' * 100)
    for mid in files:
        acts = load(mid)
        stats = defaultdict(lambda: {'n': 0, 'vals': set(), 'on_eot': 0, 'on_sot': 0})
        for a in acts:
            ad = a.get('action_data') or {}
            if not isinstance(ad, dict):
                continue
            t = a.get('action_type')
            for k, v in ad.items():
                if str(k).isdigit() and isinstance(v, str) and v.isdigit():
                    s = stats[str(k)]
                    s['n'] += 1
                    s['vals'].add(int(v))
                    if t == 'XActionEndOfTurn':
                        s['on_eot'] += 1
                    if t == 'XActionStartOfTurn':
                        s['on_sot'] += 1
        cands = [(k, s) for k, s in stats.items()
                 if s['on_eot'] or s['on_sot']]
        print(f'  replay-{mid}:')
        for k, s in sorted(cands, key=lambda x: int(x[0])):
            vs = sorted(s['vals'])
            rng = f'{vs[0]}..{vs[-1]}'
            mark = ''
            if 14 <= vs[0] and vs[-1] <= 60:
                mark = '   ← 候选(HQ 血量区间)'
            print(f'      键[{k:>4s}] n={s["n"]:3d} eot={s["on_eot"]:3d} sot={s["on_sot"]:3d} '
                  f'区间={rng:10s} 不同值={len(vs):3d}{mark}')

    print()
    print('=' * 100)
    print('候选键的完整轨迹（找单调不增那一条 = HQ 防御）')
    print('=' * 100)
    for mid in files:
        acts = load(mid)
        stats = defaultdict(set)
        for a in acts:
            ad = a.get('action_data') or {}
            if not isinstance(ad, dict):
                continue
            if a.get('action_type') not in ('XActionStartOfTurn', 'XActionEndOfTurn'):
                continue
            for k, v in ad.items():
                if str(k).isdigit() and isinstance(v, str) and v.isdigit():
                    stats[str(k)].add(int(v))
        for k, vs in sorted(stats.items(), key=lambda x: int(x[0])):
            v = sorted(vs)
            if not (14 <= v[0] and v[-1] <= 60):
                continue
            tr = []
            last = None
            for a in acts:
                ad = a.get('action_data') or {}
                if isinstance(ad, dict) and k in ad:
                    x = ad[k]
                    if isinstance(x, str) and x.isdigit() and int(x) != last:
                        tr.append((a.get('action_id'), a.get('action_type'), int(x)))
                        last = int(x)
            print(f'  replay-{mid} 键[{k}]  轨迹: ' +
                  '  '.join(f'{v}' for _, _, v in tr))
            ups = [tr[i] for i in range(1, len(tr)) if tr[i][2] > tr[i - 1][2]]
            if ups:
                print(f'        ↑ 上升点: ' +
                      ', '.join(f'act={a}({t}) {tr[i-1][2]}->{v}' for i, (a, t, v) in enumerate(tr) if v > tr[i-1][2]))


if __name__ == '__main__':
    main()
