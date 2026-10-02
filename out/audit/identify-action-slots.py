#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
**认出 PC / ML / AC 的每个槽位** —— 这是接服务器最关键的一步：
槽位填错了客户端会触发同步错误（`isValidatingActionSent` / `syncErrorCheckCards`），
而且不会报错、只会静默不同步。

做法：拿 `action_data` 的每个槽去和**已知事实**对账：
  · 槽 0 是不是打出的那张牌？（对 `starting_hand_*` / 牌库的 card_id）
  · 槽 4 是 2 字符卡组码吗？它和槽 0 指向的卡**是不是同一张**？
  · 槽 40 是不是对手 HQ 防御？（对动作流里 StartOfTurn/EndOfTurn 上那个 HQ 键）
  · 槽 1 / 2 / 3 在什么情况下非 0？

输出：每个槽的「命中率」+ 反例，够不够下结论一眼可见。
"""
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

REP = Path('klink bot/docs/fresh-replays')


def load(mid):
    o = json.load(open(REP / f'replay-{mid}.actions.json', encoding='utf-8'))
    return o['actions'] if isinstance(o, dict) else o


def starting(mid):
    o = json.load(open(REP / f'replay-{mid}.json', encoding='utf-8'))
    return o['starting_info']['match_and_starting_data']['starting_data']


def deckcode_table():
    """2 字符码 → 卡名。"""
    for p in ('klink bot/docs/deck_code_ids.live.json', 'klink bot/docs/deck_code_ids.json'):
        try:
            d = json.load(open(p, encoding='utf-8'))
        except OSError:
            continue
        out = {}
        if isinstance(d, dict):
            for k, v in d.items():
                if isinstance(v, str):
                    out[k] = v
                elif isinstance(v, dict):
                    out[k] = v.get('card') or v.get('name') or ''
        return out
    return {}


def main():
    mids = sorted(p.stem.replace('replay-', '').replace('.actions', '')
                  for p in REP.glob('replay-*.actions.json'))
    table = deckcode_table()
    print(f'卡组码表 {len(table)} 条')
    print()

    for mid in mids:
        acts = load(mid)
        sd = starting(mid)
        # card_id → name（开局全部卡）
        id2name = {}
        for f in ('starting_hand_left', 'deck_left', 'starting_hand_right', 'deck_right'):
            for c in (sd.get(f) or []):
                id2name[c['card_id']] = c['name']
        for f in ('location_card_left', 'location_card_right'):
            c = sd.get(f)
            if c:
                id2name[c['card_id']] = c['name']

        # 本局的 HQ 键（StartOfTurn 上那个整数键）
        hqkeys = set()
        for a in acts:
            if a.get('action_type') == 'XActionStartOfTurn':
                for k in (a.get('action_data') or {}):
                    if str(k).isdigit():
                        hqkeys.add(str(k))

        # 每个 StartOfTurn/EndOfTurn 上的 HQ 值，按 action_id 建索引
        hq_by_act = {}
        for a in acts:
            ad = a.get('action_data') or {}
            for k in hqkeys:
                v = ad.get(k)
                if isinstance(v, str) and v.isdigit():
                    hq_by_act[a.get('action_id')] = int(v)

        stat = defaultdict(lambda: Counter())
        for a in acts:
            t = a.get('action_type')
            if t not in ('PC', 'ML', 'AC'):
                continue
            ad = a.get('action_data') or {}
            name0 = id2name.get(int(ad.get('0', '0')), '?')

            # 槽 4：2 字符码，查表得到的卡名应等于槽 0 的卡名
            code4 = ad.get('4')
            if code4:
                stat['槽4 是卡组码'][bool(table.get(code4) == name0)] += 1

            # 槽 40：是不是某个 HQ 值
            v40 = ad.get('40')
            if v40 is not None:
                stat['槽40 落在 HQ 值域'][14 <= int(v40) <= 60 if str(v40).isdigit() else False] += 1

            # 槽 1/2/3 的非零情况
            for slot in ('1', '2', '3'):
                v = ad.get(slot, '0')
                stat[f'槽{slot} 非零'][v != '0'] += 1

        print(f'--- replay-{mid}（PC/ML/AC 共 '
              f'{sum(1 for a in acts if a.get("action_type") in ("PC","ML","AC"))} 条）---')
        for k in sorted(stat):
            c = stat[k]
            yes, no = c.get(True, 0), c.get(False, 0)
            print(f'    {k:26s} 是={yes:4d}  否={no:4d}')
        print()


if __name__ == '__main__':
    main()
