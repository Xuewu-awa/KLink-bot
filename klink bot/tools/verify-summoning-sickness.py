"""严格检验「召唤失调 + Blitz 例外」这条规则。

做法：在 5 局真实回放里找出所有「同一回合内、同一 cardID 先 PC 后 ML/AC」的情形，
然后逐张查它的 hasBlitz 标志。

- 如果**全部**都有 Blitz → 支持「有召唤失调，Blitz 是例外」
- 只要有一张**没有** Blitz → 就说明根本没有召唤失调（我之前的判断才是对的）

卡名从开局快照的 cardID→name 拿；hasBlitz 从 kards-sim 的 cards.json 拿
（它是从 CDO 的 has* 布尔字段抽的，权威）。
"""
import glob
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")

REPLAY_DIR = "docs/live-replays"
their = {c["id"]: c for c in json.load(open(r"..\ref\kards-sim\cards.json", encoding="utf-8"))}


def has_blitz(name):
    c = their.get(name)
    if not c:
        return None
    raw = c.get("raw") or {}
    return bool(raw.get("hasBlitz"))


rows = []
for path in sorted(glob.glob(os.path.join(REPLAY_DIR, "replay-*.json"))):
    if ".actions." in path:
        continue
    match = os.path.basename(path)[len("replay-"):-len(".json")]
    snap = json.load(open(path, encoding="utf-8"))
    sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]

    names = {}
    for side in ("left", "right"):
        for field in ("starting_hand_%s" % side, "deck_%s" % side):
            for c in sd[field]:
                names[c["card_id"]] = c["name"]
        c = sd["location_card_%s" % side]
        names[c["card_id"]] = c["name"]

    acts_path = os.path.join(REPLAY_DIR, "replay-%s.actions.json" % match)
    acts = json.load(open(acts_path, encoding="utf-8"))["actions"]

    # 记录每个 cardID 在每一回合的首次 PC
    first_pc = {}
    for a in acts:
        ad = a.get("action_data") or {}
        if a["action_type"] != "PC" or "0" not in ad:
            continue
        try:
            cid = int(ad["0"])
        except ValueError:
            continue
        first_pc.setdefault((cid, a["turn_number"]), a["action_id"])

    for a in acts:
        ad = a.get("action_data") or {}
        if a["action_type"] not in ("ML", "AC") or "0" not in ad:
            continue
        try:
            cid = int(ad["0"])
        except ValueError:
            continue
        pc_id = first_pc.get((cid, a["turn_number"]))
        if pc_id is None or pc_id >= a["action_id"]:
            continue
        rows.append((match, a["turn_number"], a["action_id"], a["action_type"],
                     cid, names.get(cid, "?"), has_blitz(names.get(cid, ""))))

print("=== 5 局回放里「同回合 PC 后立刻 ML/AC」的全部情形 ===")
print("%-8s %-4s %-6s %-4s %-6s %-38s %s" % ("对局", "回合", "动作", "类型", "cardID", "卡名", "hasBlitz"))
for r in rows:
    print("%-8s T%-3s %-6s %-4s %-6s %-38s %s" % r)

print()
no_blitz = [r for r in rows if r[6] is False]
unknown = [r for r in rows if r[6] is None]
print("总计 %d 例；有 Blitz %d，无 Blitz %d，查不到 %d"
      % (len(rows), len([r for r in rows if r[6] is True]), len(no_blitz), len(unknown)))

if no_blitz:
    print()
    print("⚠️ 以下情形**没有** Blitz —— 说明不存在召唤失调：")
    for r in no_blitz:
        print("   ", r)
else:
    print()
    print("✅ 全部带 Blitz —— 支持「有召唤失调，Blitz 是例外」")

# 反向检查：没有 Blitz 的卡，出牌后是不是下一回合才行动
print()
print("=== 反向检查：没有 Blitz 的单位，出牌后是否「本回合不动」 ===")
for path in sorted(glob.glob(os.path.join(REPLAY_DIR, "replay-*.json"))):
    if ".actions." in path:
        continue
    match = os.path.basename(path)[len("replay-"):-len(".json")]
    snap = json.load(open(path, encoding="utf-8"))
    sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]
    names = {}
    for side in ("left", "right"):
        for field in ("starting_hand_%s" % side, "deck_%s" % side):
            for c in sd[field]:
                names[c["card_id"]] = c["name"]
        c = sd["location_card_%s" % side]
        names[c["card_id"]] = c["name"]
    acts = json.load(open(os.path.join(REPLAY_DIR, "replay-%s.actions.json" % match),
                          encoding="utf-8"))["actions"]

    pc_turn = {}
    for a in acts:
        ad = a.get("action_data") or {}
        if a["action_type"] == "PC" and "0" in ad:
            try:
                pc_turn.setdefault(int(ad["0"]), a["turn_number"])
            except ValueError:
                pass

    for a in acts:
        ad = a.get("action_data") or {}
        if a["action_type"] not in ("ML", "AC") or "0" not in ad:
            continue
        try:
            cid = int(ad["0"])
        except ValueError:
            continue
        if cid not in pc_turn:
            continue
        if has_blitz(names.get(cid, "")) is False:
            gap = a["turn_number"] - pc_turn[cid]
            if gap >= 0:
                print("  %s  %-34s  T%s 出牌 → T%s %s  间隔 %d 回合"
                      % (match, names.get(cid), pc_turn[cid], a["turn_number"],
                         a["action_type"], gap))
