"""两个判定性检查：
  1) 快照的 deck_* 里有没有重复的 card_id（若有 -> card_id 是"牌定义 id"而非"实例 id"）
  2) 动作流里那些"完全相同"的连续动作，原始 JSON 到底差在哪
"""
import json
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")

snap = json.load(open(sys.argv[1], encoding="utf-8"))
acts = json.load(open(sys.argv[2], encoding="utf-8"))["actions"]
sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]

print("=== 1) 快照里的 card_id 重复情况 ===")
for side in ("left", "right"):
    for field in ("starting_hand_%s" % side, "deck_%s" % side):
        ids = [c["card_id"] for c in sd[field]]
        dup = {k: v for k, v in Counter(ids).items() if v > 1}
        names = {c["card_id"]: c["name"] for c in sd[field]}
        print("  %-22s n=%-3d unique=%-3d dups=%s" % (field, len(ids), len(set(ids)), dup))
        for k in dup:
            print("      id %d x%d -> %s" % (k, dup[k], names[k]))

print()
print("=== 2) 连续重复动作的原始 JSON ===")
for i, x in enumerate(acts):
    if 68 <= i <= 78:
        print(json.dumps(x, ensure_ascii=False))

print()
print("=== 3) 全表检查：同一 card_id 是否对应多个牌码 ===")
by_id_codes = {}
bad = 0
for x in acts:
    ad = x["action_data"]
    if not ad or x["action_type"] not in ("PC", "ML"):
        continue
    cid, code = ad.get("0"), ad.get("4")
    if cid in by_id_codes and by_id_codes[cid] != code:
        print("  冲突 id=%s: %s vs %s (action %s)" % (cid, by_id_codes[cid], code, x["action_id"]))
        bad += 1
    by_id_codes[cid] = code
print("  冲突数 =", bad)
