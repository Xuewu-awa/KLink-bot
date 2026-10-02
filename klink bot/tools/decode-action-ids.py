"""把动作流里出现的所有 2 字符牌码翻译成卡名，并统计每个 cardID 的用法。"""
import json
import sys
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8")

tbl = json.load(open("docs/deck_code_ids.live.json", encoding="utf-8"))
snap = json.load(open(sys.argv[1], encoding="utf-8"))
acts = json.load(open(sys.argv[2], encoding="utf-8"))["actions"]

sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]
zone = {}
for side in ("left", "right"):
    for c in sd["starting_hand_%s" % side]:
        zone[c["card_id"]] = "%s.hand" % side
    for c in sd["deck_%s" % side]:
        zone[c["card_id"]] = "%s.deck" % side
    c = sd["location_card_%s" % side]
    zone[c["card_id"]] = "%s.hq" % side

by_id = defaultdict(list)
for x in acts:
    ad = x["action_data"]
    if not ad or x["action_type"] not in ("PC", "ML", "AC"):
        continue
    codes = [ad.get("4")] if x["action_type"] in ("PC", "ML") else [ad.get("2"), ad.get("3")]
    if x["action_type"] == "AC":
        by_id[ad.get("0")].append(("AC", ad.get("2"), x["turn_number"]))
        by_id[ad.get("1")].append(("AC-target", ad.get("3"), x["turn_number"]))
    else:
        by_id[ad.get("0")].append((x["action_type"], ad.get("4"), x["turn_number"]))

print("=== cardID 的全部用法（含它携带的牌码） ===")
for cid, uses in sorted(by_id.items(), key=lambda kv: int(kv[0]) if kv[0].isdigit() else 99999):
    codes = {(u[1] or "") for u in uses}
    names = {tbl.get(c, "???") for c in codes}
    tags = ",".join("%s@T%s" % (u[0], u[2]) for u in uses)
    zn = zone.get(int(cid)) if cid.isdigit() else None
    print("%-6s %-9s %-34s %s" % (cid, zn or "-", "/".join(sorted(names)), tags))

print()
print("=== 这些牌码对应的卡名 ===")
for c in sorted({u[1] for u in sum(by_id.values(), []) if u[1]}):
    print("  %-4s %s" % (c, tbl.get(c, "???")))
