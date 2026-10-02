#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
读**服务端真实回放**，把 bot（右侧）的每一步摊开看。

## 为什么要看回放而不是 bot 日志

bot 日志只记「内核重建出的局面 + 模型给的分」，**看不到客户端实际认不认这些动作、
也看不到 bot 的动作和人类动作在格式上有没有差别**。
回放是服务端记录的**真实动作流**，能回答：

  · bot 到底发了哪些动作（紧凑名 / 槽位 / 值）
  · 人类的动作长什么样（对照格式）
  · 有没有"开发选牌"这种需要**客户端回一个选择**的交互，而 bot 从没回过
  · 有没有动作被客户端拒绝（后续局面因此漂开）

用法：
    python out/audit/show-server-replay.py out/_server-replays/replay-630801
"""
import json
import sys
from collections import Counter
from pathlib import Path


def main():
    base = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('out/_server-replays/replay-630801')
    snap = json.loads(base.with_suffix('.json').read_text(encoding='utf-8'))
    acts = json.loads(Path(str(base) + '.actions.json').read_text(encoding='utf-8'))
    acts = acts.get('actions', acts) if isinstance(acts, dict) else acts

    si = snap.get('starting_info') or snap
    msd = si.get('match_and_starting_data') or {}
    sd = msd.get('starting_data') or {}
    match = msd.get('match') or {}

    L = sd.get('player_id_left')
    R = sd.get('player_id_right')
    print(f"=== 对局 {match.get('match_id')} ===")
    print(f"  左 = {L} ({sd.get('left_player_name')})   右 = {R} ({sd.get('right_player_name')})")
    print(f"  动作 {len(acts)} 条")
    print()

    # ---- 谁发了什么 ----
    byActor = Counter()
    byType = Counter()
    for a in acts:
        who = 'right(bot)' if a.get('player_id') == R else ('left(人类)' if a.get('player_id') == L else f"?{a.get('player_id')}")
        byActor[who] += 1
        byType[a.get('action_type')] += 1
    print("=== 按行动方 ===")
    for k, v in byActor.most_common():
        print(f"  {k:14s} {v}")
    print()
    print("=== 按动作类型 ===")
    for k, v in byType.most_common():
        print(f"  {k:32s} {v}")
    print()

    # ---- 完整动作流 ----
    print("=== 动作流 ===")
    for a in acts:
        pid = a.get('player_id')
        who = 'R' if pid == R else ('L' if pid == L else '?')
        d = a.get('action_data') or {}
        ds = ' '.join(f"{k}={v}" for k, v in d.items())
        subs = a.get('sub_actions') or []
        sub_s = f"  sub={len(subs)}" if subs else ''
        print(f"  #{a.get('action_id'):>3} t{a.get('turn_number'):>2} {who} "
              f"{a.get('action_type'):<28} {ds}{sub_s}")

    # ---- 找「需要客户端回选择」的交互 ----
    print()
    print("=== 可能的交互/选择类动作 ===")
    interesting = [a for a in acts
                   if any(k in (a.get('action_type') or '')
                          for k in ('Generate', 'Develop', 'Select', 'Choose', 'Pick', 'Draw'))]
    if interesting:
        for a in interesting:
            print(f"  #{a.get('action_id')} {a.get('action_type')} "
                  f"pid={a.get('player_id')} data={json.dumps(a.get('action_data'), ensure_ascii=False)}")
    else:
        print("  （没有）")

    # ---- `action` / `value` 字段非空的（可能是选择回执）----
    print()
    print("=== action/value 字段非空的动作 ===")
    n = 0
    for a in acts:
        if a.get('action') or a.get('value') is not None:
            n += 1
            print(f"  #{a.get('action_id')} {a.get('action_type')} "
                  f"action={a.get('action')!r} value={a.get('value')!r}")
    if n == 0:
        print("  （没有 —— 这一局里没有任何'选择回执'）")

    # ---- bot 的每次出牌：卡名 + 目标 ----
    print()
    print("=== bot（右）的动作明细 ===")
    cards = {}
    for k in ('location_card_left', 'location_card_right'):
        c = sd.get(k)
        if c:
            cards[c['card_id']] = c['name']
    for lst in ('starting_hand_left', 'starting_hand_right', 'deck_left', 'deck_right'):
        for c in sd.get(lst) or []:
            cards[c['card_id']] = c['name']

    for a in acts:
        if a.get('player_id') != R:
            continue
        d = a.get('action_data') or {}
        t = a.get('action_type')
        if t in ('XActionStartOfTurn', 'XActionEndOfTurn'):
            continue
        cid = d.get('0')
        tgt = d.get('1')
        cn = cards.get(_int(cid), f"#{cid}")
        tn = cards.get(_int(tgt), '') if tgt not in (None, 0, '0') else ''
        extra = ' '.join(f"{k}={v}" for k, v in d.items() if k not in ('0', '1'))
        print(f"  #{a.get('action_id'):>3} t{a.get('turn_number'):>2} {t:<6} "
              f"{cn}{(' → ' + tn) if tn else ''}   {extra}")


def _int(x):
    try:
        return int(x)
    except (TypeError, ValueError):
        return None


if __name__ == '__main__':
    main()
