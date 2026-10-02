#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把真实回放里的 **HQ 防御轨迹** 抽出来 —— 这是唯一逐动作可读的状态量。

用途：基础规则核对。内核算出的 HQ 轨迹与真实对不上时，
第一条对不上的位置就是规则理解出错的地方。

输出：逐动作的 (action_id, type, side, 真实HQ值)，以及**变化点**（哪几个动作让它变了）。
"""
import json
import sys
from pathlib import Path

REP = Path('klink bot/docs/fresh-replays')


def main():
    mid = sys.argv[1] if len(sys.argv) > 1 else '15'
    ap = REP / f'replay-{mid}.actions.json'
    jp = REP / f'replay-{mid}.json'
    acts = json.load(open(ap, encoding='utf-8'))
    acts = acts['actions'] if isinstance(acts, dict) else acts

    o = json.load(open(jp, encoding='utf-8'))
    sd = ((o.get('starting_info') or {}).get('match_and_starting_data') or {}).get('starting_data') or {}
    lc = sd.get('location_card_left') or {}
    rc = sd.get('location_card_right') or {}
    print(f'=== replay-{mid} ===')
    print(f'左 HQ = #{lc.get("card_id")} {lc.get("name")}')
    print(f'右 HQ = #{rc.get("card_id")} {rc.get("name")}')
    print()

    # 找出两方 HQ 的 cardID（action_data 里的数值键）
    keys = set()
    for a in acts:
        ad = a.get('action_data') or {}
        if isinstance(ad, dict):
            for k, v in ad.items():
                if str(k).isdigit() and isinstance(v, str) and v.isdigit():
                    keys.add(str(k))
    hqkeys = [k for k in keys if k in (str(lc.get('card_id')), str(rc.get('card_id')))]
    print(f'action_data 里的 HQ 键: {sorted(hqkeys, key=int)}')
    print()

    print(f'{"act":>4} {"type":22s} {"side":6s} ' +
          '  '.join(f'HQ#{k}' for k in sorted(hqkeys, key=int)))
    print('-' * 72)
    prev = {}
    changes = []
    for a in acts:
        ad = a.get('action_data') or {}
        if not isinstance(ad, dict):
            continue
        vals = {k: ad.get(k) for k in sorted(hqkeys, key=int)}
        if not any(v is not None for v in vals.values()):
            continue
        cur = {k: (int(v) if v is not None and str(v).isdigit() else None) for k, v in vals.items()}
        for k, v in cur.items():
            if v is not None and prev.get(k) is not None and v != prev[k]:
                changes.append((a.get('action_id'), a.get('action_type'),
                                ad.get('side'), k, prev[k], v))
        prev.update({k: v for k, v in cur.items() if v is not None})
        vals_txt = '  '.join(f'{cur[k] if cur[k] is not None else "-":>5}' for k in sorted(hqkeys, key=int))
        print(f'{str(a.get("action_id")):>4} {str(a.get("action_type")):22s} '
              f'{str(ad.get("side") or ""):6s} {vals_txt}')

    print()
    print('=== HQ 值的变化点（这是核对规则的关键位置）===')
    for aid, t, side, k, a0, a1 in changes:
        print(f'  act={aid:<5} {str(t):22s} side={str(side):6s} HQ#{k}: {a0} -> {a1}  (Δ{a1 - a0:+d})')


if __name__ == '__main__':
    main()
