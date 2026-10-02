#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从真实回放里提取「基础规则」的硬事实。

为什么要这个：内核里很多规则是**猜的**（回合上限、疲劳公式、HQ 上限…），
而真实回放是现成的判据。这个脚本把能直接读出来的事实全部摊开：

  1. 每局怎么结束的（HQ 摧毁？疲劳？投降？）→ 决定「有没有回合上限」要往哪查
  2. 回合数分布 → 真实对局能打到多少回合
  3. action_data 里出现了哪些**数值型键**及其轨迹 → HQ 防御/费用这类量到底怎么变
  4. action_type 分布 → 哪些动作真实会发生（内核该覆盖哪些）
"""
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

REP = Path('klink bot/docs/fresh-replays')

INT_KEYS = re.compile(r'^\d+$')


def load(p):
    o = json.load(open(p, encoding='utf-8'))
    acts = o['actions'] if isinstance(o, dict) and 'actions' in o else o
    return o, acts


def main():
    files = sorted(REP.glob('replay-*.json'))
    files = [f for f in files if not f.name.endswith('.actions.json')]

    print('=' * 96)
    print('每局的结束情况 / 回合数')
    print('=' * 96)
    for f in files:
        o = json.load(open(f, encoding='utf-8'))
        s = o.get('summary') or {}
        mid = f.stem.replace('replay-', '')
        print(f'  {mid:9s} turns={str(s.get("turns")):4s} actions={str(s.get("action_count")):5s} '
              f'winner={str(s.get("winner_side")):6s} status={s.get("status")}')
        # summary 里有没有"结束原因"字段
        extra = {k: v for k, v in s.items()
                 if k not in ('match_id', 'match_type', 'status', 'left_player_id',
                              'right_player_id', 'turns', 'action_count', 'winner_side',
                              'local_subactions')}
        if extra:
            print(f'            其它 summary 字段: {json.dumps(extra, ensure_ascii=False)[:160]}')

    print()
    print('=' * 96)
    print('动作类型分布（全部回放合计）')
    print('=' * 96)
    types = Counter()
    for f in files:
        _, acts = load(REP / f'{f.stem}.actions.json') if (REP / f'{f.stem}.actions.json').exists() else (None, [])
        for a in acts:
            types[a.get('action_type')] += 1
    for t, n in types.most_common():
        print(f'  {str(t):32s} {n}')

    print()
    print('=' * 96)
    print('action_data 的整数键 —— 每个键的取值集合与轨迹（找 HQ 防御 / 费用这类量）')
    print('=' * 96)
    per_key = defaultdict(list)      # key -> [(match, action_id, type, value)]
    for f in files:
        ap = REP / f'{f.stem}.actions.json'
        if not ap.exists():
            continue
        _, acts = load(ap)
        mid = f.stem.replace('replay-', '')
        for a in acts:
            ad = a.get('action_data') or {}
            if not isinstance(ad, dict):
                continue
            for k, v in ad.items():
                if INT_KEYS.match(str(k)) and isinstance(v, str) and v.isdigit():
                    per_key[str(k)].append((mid, a.get('action_id'), a.get('action_type'), int(v)))

    for k in sorted(per_key, key=lambda x: int(x)):
        rows = per_key[k]
        vals = [r[3] for r in rows]
        print(f'  键 [{k}]  出现 {len(rows)} 次   取值 {min(vals)}..{max(vals)}   不同值 {len(set(vals))}')
        # 打印一局的轨迹
        for mid in sorted({r[0] for r in rows})[:2]:
            tr = [(r[1], r[2], r[3]) for r in rows if r[0] == mid]
            if len(tr) > 40:
                tr = tr[:20] + [('...', '', '')] + tr[-10:]
            print(f'      {mid}: ' + '  '.join(f'{v}' if t == '' else f'{v}' for _, t, v in tr))


if __name__ == '__main__':
    main()
