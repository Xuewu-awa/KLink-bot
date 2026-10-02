"""分析 fyserver 回放数据 —— 这是内核验证的 ground truth。

数据来源：fyserver 的只读接口
    GET /replays/{id}                       开局快照（双方卡组/手牌/HQ）
    GET /replays/{id}/actions?limit=1000    全部动作（服务端已解密）

用法：
    python analyze-replay.py <回放目录或单个 replay-*.json>
"""

import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "docs" / "live-replays"


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8-sig"))


files = sorted(root.glob("replay-*.json")) if root.is_dir() else [root]
files = [f for f in files if not f.name.endswith(".actions.json")]

for f in files:
    doc = load(f)
    s = doc.get("summary", {})
    si = doc.get("starting_info", {}).get("match_and_starting_data", {})
    sd = si.get("starting_data", {})
    m = si.get("match", {})

    print("=" * 78)
    print(f"对局 {s.get('match_id')}  {s.get('match_type')}  {s.get('turns')} 回合  "
          f"{s.get('action_count')} 动作  胜方={s.get('winner_side')}")
    print(f"  {s.get('started_at')} → {s.get('completed_at')}")
    print(f"  左 {s.get('left_player_id')}  右 {s.get('right_player_id')}")
    print(f"  deck_id_left={m.get('deck_id_left')}  deck_id_right={m.get('deck_id_right')}")

    for side in ("left", "right"):
        hq = sd.get(f"location_card_{side}")
        hand = sd.get(f"starting_hand_{side}") or []
        deck = sd.get(f"deck_{side}") or []
        print(f"  [{side}] HQ={hq.get('name') if isinstance(hq, dict) else hq} "
              f"faction={hq.get('faction') if isinstance(hq, dict) else '?'}")
        print(f"         手牌 {len(hand)} 张，牌库 {len(deck)} 张")
        if hand:
            print(f"         手牌: " + ", ".join(
                f"{c.get('card_id')}:{c.get('name')}" for c in hand[:8] if isinstance(c, dict)))
        if deck:
            print(f"         牌库前 6: " + ", ".join(
                f"{c.get('card_id')}:{c.get('name')}" for c in deck[:6] if isinstance(c, dict)))

    # 动作
    af = f.with_name(f.stem + ".actions.json")
    if af.exists():
        acts = load(af)
        arr = acts.get("actions", acts if isinstance(acts, list) else [])
        print(f"  动作 {len(arr)} 条")
        types = Counter()
        for a in arr:
            if isinstance(a, dict):
                types[a.get("action_type") or a.get("ActionType")] += 1
        print("    类型: " + ", ".join(f"{k}×{v}" for k, v in types.most_common()))
        # 第一条的字段形状
        if arr and isinstance(arr[0], dict):
            print(f"    首条字段: {list(arr[0].keys())[:14]}")
    print()
