"""核对：动作流里被"打出/攻击/移动"的 cardID，在开局快照里究竟位于哪个区。

用来回答两个问题：
  1) action_data 的 "0" 到底是不是全局 cardID（能否与快照对上）
  2) 打出的牌是不是都在手牌里（即快照的 hand/deck 划分是否正确）
"""
import json
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")

snap = json.load(open(sys.argv[1], encoding="utf-8"))
acts = json.load(open(sys.argv[2], encoding="utf-8"))["actions"]

sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]
left_id = sd["player_id_left"]
right_id = sd["player_id_right"]

zone = {}
name = {}
for side in ("left", "right"):
    for c in sd["starting_hand_%s" % side]:
        zone[c["card_id"]] = ("%s.hand" % side, c["location_number"])
        name[c["card_id"]] = c["name"]
    for c in sd["deck_%s" % side]:
        zone[c["card_id"]] = ("%s.deck" % side, c["location_number"])
        name[c["card_id"]] = c["name"]
    c = sd["location_card_%s" % side]
    zone[c["card_id"]] = ("%s.hq" % side, c["location_number"])
    name[c["card_id"]] = c["name"]

side_of = {left_id: "left", right_id: "right"}

print("=== 每个玩家打出的牌：快照中的位置 ===")
for x in acts:
    if x["action_type"] not in ("PC", "AC", "ML") or not x["action_data"]:
        continue
    ad = x["action_data"]
    who = side_of.get(x["player_id"], "?")
    refs = [(k, ad[k]) for k in ("0", "1", "2", "3") if k in ad]
    parts = []
    for k, v in refs:
        try:
            cid = int(v)
        except ValueError:
            parts.append("%s=%s?" % (k, v))
            continue
        z = zone.get(cid)
        zn = "%s#%d" % (z[0], z[1]) if z else "??"
        parts.append("%s=%d[%s]" % (k, cid, zn))
    print("%3d T%-3s %-5s %-3s %s" % (
        x["action_id"], x["turn_number"], who, x["action_type"], " ".join(parts)))

print()
print("=== 汇总：被当作'打出的牌'的 cardID 落在哪个区 ===")
cnt = Counter()
for x in acts:
    if x["action_type"] != "PC" or not x["action_data"]:
        continue
    ad = x["action_data"]
    try:
        cid = int(ad.get("0", ""))
    except ValueError:
        continue
    z = zone.get(cid)
    own = side_of.get(x["player_id"], "?")
    zn = z[0] if z else "??"
    cnt["%s -> %s" % (own, zn)] += 1
for k, v in sorted(cnt.items()):
    print("  %-20s %d" % (k, v))
