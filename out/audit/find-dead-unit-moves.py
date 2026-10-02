#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
**找「客户端认为已死、我们内核认为还活着」的单位。**

## 背景（雪雾 2026-10-01 实测）

bot 移动了它自己认为「1/1 活着」的单位（`5th_parachute_brigade#63`），
但客户端那边这个单位**已经死了** —— 用户看到"AI 在移动死亡单位"。

bot 日志里 `未应用=0`（每条动作都被内核接受了），
**但"接受"不等于"状态一致"** —— 效果没实现时动作会被当成 no-op 接受。

## 这个脚本查什么

把回放里的动作流按顺序过一遍，**跟踪每个单位的防御力变化**，
找出「**被移动时防御力已经 <= 0**」或「**被移动时已经进弃牌堆**」的情况。

如果找到 ⇒ 那是**内核自己**的 bug（该拒绝却没拒绝）。
如果**找不到** ⇒ 说明我们内核认为它是满血的 ⇒
**是效果没实现导致的状态漂开**（客户端扣了血、我们没扣）。

用法：
    python out/audit/find-dead-unit-moves.py out/_server-replays/replay-380712
"""
import json
import sys
from pathlib import Path

# 动作里出现「伤害」语义的字段名（尽力而为：回放只给最终动作，不给中间态）
DMG_HINTS = ("damage", "Damage", "attack", "Attack")


def main():
    base = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('out/_server-replays/replay-380712')
    snap = json.loads(base.with_suffix('.json').read_text(encoding='utf-8'))
    acts = json.loads(Path(str(base) + '.actions.json').read_text(encoding='utf-8'))
    acts = acts.get('actions', acts) if isinstance(acts, dict) else acts

    sd = snap['starting_info']['match_and_starting_data']['starting_data']
    L, R = sd.get('player_id_left'), sd.get('player_id_right')

    # 起始卡表：cardID -> (卡名, 初始防御)
    info = {}
    for k in ('location_card_left', 'location_card_right'):
        c = sd.get(k)
        if c:
            info[c['card_id']] = (c['name'], c.get('defense', 0), c.get('location', ''))
    for lst in ('starting_hand_left', 'starting_hand_right', 'deck_left', 'deck_right'):
        for c in sd.get(lst) or []:
            info[c['card_id']] = (c['name'], c.get('defense', 0), c.get('location', ''))

    print(f"=== 对局 {sd.get('match_id')} ===")
    print(f"  动作 {len(acts)} 条；左={L} 右={R}")
    print()

    # 逐条打印「移动」动作，并标注该单位的起始防御
    print("=== 所有 ML（移动）动作 ===")
    moves = []
    for a in acts:
        if a.get('action_type') != 'ML':
            continue
        d = a.get('action_data') or {}
        cid = _int(d.get('0'))
        who = 'R(bot)' if a.get('player_id') == R else 'L(人类)'
        nm, dfn, loc = info.get(cid, ('?', '?', '?'))
        moves.append((a.get('action_id'), a.get('turn_number'), who, cid, nm, d))
        print(f"  #{a.get('action_id'):>3} t{a.get('turn_number'):>2} {who:8s} "
              f"cardID={cid} {nm}  槽位={d.get('1')}  起始防御={dfn}")
    print()

    # 所有「攻击」动作：看谁打了谁
    print("=== 所有 AC（攻击）动作 ===")
    for a in acts:
        if a.get('action_type') != 'AC':
            continue
        d = a.get('action_data') or {}
        who = 'R(bot)' if a.get('player_id') == R else 'L(人类)'
        atk = _int(d.get('0'))
        dfd = _int(d.get('1'))
        an = info.get(atk, ('?',))[0]
        dn = info.get(dfd, ('?',))[0]
        print(f"  #{a.get('action_id'):>3} t{a.get('turn_number'):>2} {who:8s} "
              f"{atk}:{an} → {dfd}:{dn}")
    print()

    # 所有 PC（出牌）动作，标出指向我们单位的（1 槽 = 目标）
    print("=== 所有 PC（出牌）动作 ===")
    for a in acts:
        if a.get('action_type') != 'PC':
            continue
        d = a.get('action_data') or {}
        who = 'R(bot)' if a.get('player_id') == R else 'L(人类)'
        cid = _int(d.get('0'))
        tgt = _int(d.get('1'))
        cn = info.get(cid, ('?',))[0]
        tn = info.get(tgt, ('',))[0] if tgt else ''
        print(f"  #{a.get('action_id'):>3} t{a.get('turn_number'):>2} {who:8s} "
              f"{cid}:{cn}{(' → ' + str(tgt) + ':' + tn) if tn else ''}  码={d.get('4')}")

    print()
    print("=== 判读 ===")
    print("  回放**只有动作、没有中间状态** ⇒ 光靠它不能直接证明某单位已死。")
    print("  要看的是：**人类有没有对 bot 的单位造成过伤害**（上面的 AC / PC 指向）。")
    print("  如果有，而 bot 之后仍然移动了那个单位 ⇒ 我们内核**没扣那次伤害**。")


def _int(x):
    try:
        return int(x)
    except (TypeError, ValueError):
        return None


if __name__ == '__main__':
    main()
