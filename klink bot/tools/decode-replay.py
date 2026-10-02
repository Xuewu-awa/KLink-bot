"""把 fyserver 回放解码成完全可读的形式 —— 内核验证的主要素材。

复用开局快照里的 cardID → 卡名映射，把每个动作里的 cardID 翻成卡名，
并把 action_data 按**每种动作各自的键位表**还原成语义字段。

用法：
    python decode-replay.py <match_id>          # 从 docs/live-replays/ 读
    python decode-replay.py --all               # 全部对局
"""

import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPLAY_DIR = ROOT / "docs" / "live-replays"

# action_data 的键位表（依据实测样本推断，见 docs/对局协议参考.md §8.2）
KEY_LAYOUT = {
    "PC": {"0": "cardID", "1": "槽位", "2": "目标ID", "3": "?", "4": "卡组码"},
    "AC": {"0": "攻击者ID", "1": "防御者ID", "2": "攻击者码", "3": "防御者码"},
    "ML": {"0": "cardID", "1": "目标槽位", "2": "卡组码"},
    "XActionStartOfTurn": {"side": "阵营"},
    "XActionEndOfTurn": {"side": "阵营", "reason": "原因"},
}


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8-sig"))


def build_card_index(starting_data):
    """cardID → (卡名, 归属)"""
    idx = {}
    for side in ("left", "right"):
        hq = starting_data.get(f"location_card_{side}")
        if isinstance(hq, dict) and hq.get("card_id"):
            idx[hq["card_id"]] = (hq.get("name"), side, "HQ")
        for field, where in ((f"starting_hand_{side}", "手牌"), (f"deck_{side}", "牌库")):
            for c in starting_data.get(field) or []:
                if isinstance(c, dict) and c.get("card_id"):
                    idx[c["card_id"]] = (c.get("name"), side, where)
    return idx


def decode(match_id):
    rp = REPLAY_DIR / f"replay-{match_id}.json"
    ap = REPLAY_DIR / f"replay-{match_id}.actions.json"
    if not rp.exists() or not ap.exists():
        print(f"找不到 {match_id} 的回放文件")
        return

    doc = load(rp)
    sd = doc["starting_info"]["match_and_starting_data"]["starting_data"]
    cards = build_card_index(sd)
    acts = load(ap).get("actions", [])

    print("=" * 90)
    s = doc["summary"]
    print(f"对局 {match_id}  {s['turns']} 回合  {len(acts)} 动作  胜方={s['winner_side']}")
    print(f"左 {s['left_player_id']} (HQ {sd.get('location_card_left',{}).get('name')})   "
          f"右 {s['right_player_id']}")
    print("=" * 90)

    # 观察 84 的变化
    v84_by_player = defaultdict(list)

    for a in acts:
        t = a.get("action_type") or "?"
        ad = a.get("action_data") or {}
        pid = a.get("player_id")
        layout = KEY_LAYOUT.get(t, {})

        if pid is not None and "84" in ad:
            v84_by_player[pid].append((a.get("turn_number"), a.get("action_id"), ad["84"]))

        # 还原语义
        fields = []
        for k, v in sorted(ad.items(), key=lambda kv: (len(kv[0]), kv[0])):
            label = layout.get(k, f"键{k}")
            if label in ("cardID", "目标ID", "攻击者ID", "防御者ID") and v not in (None, "", "0"):
                name = cards.get(int(v), ("?", "?", "?"))[0] if str(v).isdigit() else "?"
                fields.append(f"{label}={v}({name})")
            elif label.endswith("码") and v not in (None, "", "0"):
                fields.append(f"{label}={v}")
            else:
                fields.append(f"{label}={v}")

        print(f"[{a.get('action_id'):>3}] T{a.get('turn_number'):<3} p{pid} {t:<20} " + "  ".join(fields))

    print()
    print("---- action_data['84'] 的取值轨迹 ----")
    for pid, seq in v84_by_player.items():
        vals = [v for _, _, v in seq]
        changes = [(t, aid, v) for t, aid, v in seq if v != vals[0]]
        print(f"  玩家 {pid}: 初值 {vals[0]}，取值集合 {sorted(set(vals), key=lambda x: int(x))}，"
              f"变化 {len(changes)} 次")
        for t, aid, v in changes[:8]:
            print(f"      T{t} action {aid} → {v}")


if __name__ == "__main__":
    if "--all" in sys.argv:
        for f in sorted(REPLAY_DIR.glob("replay-*.json")):
            if f.name.endswith(".actions.json"):
                continue
            mid = f.stem.split("-", 1)[1]
            decode(mid)
            print()
    else:
        decode(sys.argv[1])
