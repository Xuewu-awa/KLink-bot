"""把 fyserver 的原始回放（/matches/v2/<id> 的响应）转成 ReplayData.Load 期望的包装格式。

期望结构（见 src/KLink.Bot/Replay/ReplayData.cs:107-109）：
    {
      "summary":       { match_id, left_player_id, right_player_id, turns, winner_side, ... },
      "starting_info": { "match_and_starting_data": { "starting_data": {...} } }
    }
原始结构（本文件）：
    { starting_data, match, end_of_match, mulligan_left, mulligan_right,
      local_subactions, actions }
"""
import json
import sys

src = sys.argv[1]
dst_head = sys.argv[2]      # 写 replay-<id>.json
dst_acts = sys.argv[3]      # 写 replay-<id>.actions.json

d = json.load(open(src, encoding="utf-8"))
m = d.get("match", {}) or {}
sd = d.get("starting_data", {}) or {}

summary = {
    "match_id": m.get("match_id"),
    "match_type": m.get("match_type"),
    "status": m.get("status"),
    "left_player_id": m.get("player_id_left"),
    "right_player_id": m.get("player_id_right"),
    "turns": m.get("current_turn"),
    "action_count": m.get("current_action_id"),
    "winner_side": m.get("winner_side"),
    "local_subactions": d.get("local_subactions"),
}

head = {
    "summary": summary,
    "starting_info": {
        "local_subactions": d.get("local_subactions"),
        "match_and_starting_data": {
            "starting_data": sd,
            "match": m,
            # 调度信息也带上，回放驱动可能要看
            "mulligan_left": d.get("mulligan_left"),
            "mulligan_right": d.get("mulligan_right"),
        },
    },
}

acts = d.get("actions", [])

# ★ 关键：我们内核的回放用**紧凑名**（PC/AC/ML/CS/HT），而 fyserver 原始响应用**全名**。
#   映射来自 src/KLink.Bot/Engine/WireAction.cs 的 CompactToFull（反过来用）。
#   XActionStartOfTurn / XActionEndOfTurn / XActionCheat / ActionEndMatch 内核本来就按全名认，不动。
FULL_TO_COMPACT = {
    "XActionPlayCardFromHand": "PC",
    "XActionMoveCardToLine": "ML",
    "XActionAttackCard": "AC",
    "XActionCardToDrawSelected": "CS",
    "XActionHandTargetSelected": "HT",
}
converted = 0
for a in acts:
    if isinstance(a, dict):
        t = a.get("action_type")
        if t in FULL_TO_COMPACT:
            a["action_type"] = FULL_TO_COMPACT[t]
            converted += 1

json.dump(head, open(dst_head, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
json.dump({"actions": acts}, open(dst_acts, "w", encoding="utf-8"), ensure_ascii=False)
print(f"写出 {dst_head}")
print(f"  summary = {json.dumps(summary, ensure_ascii=False)}")
print(f"写出 {dst_acts}（{len(acts)} 条动作，其中 {converted} 条全名→紧凑名）")
from collections import Counter
print("  动作类型:", dict(Counter(a.get("action_type") for a in acts if isinstance(a, dict))))
