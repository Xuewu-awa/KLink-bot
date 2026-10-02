"""在 card-effects.json（覆盖面比 card-ir.json 广，含我 IR 缺的卡）里
找「能对敌方 HQ 造成伤害」的卡。

用 external_calls 的出现组合判断，不依赖 IR 步骤。
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

snap = json.load(open(sys.argv[1], encoding="utf-8"))
sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]

pool = set()
for side in ("left", "right"):
    for field in ("starting_hand_%s" % side, "deck_%s" % side):
        pool |= {c["name"] for c in sd[field]}

eff = json.load(open("docs/card-effects.json", encoding="utf-8"))

print("=== 卡池里带 DamageCard 的卡，及其全部外部调用 ===")
n = 0
for name in sorted(pool):
    e = eff.get(name)
    if not e:
        continue
    calls = set(e.get("external_calls") or [])
    if "DamageCard" not in calls and "DamageMultipleCards" not in calls:
        continue
    n += 1
    flags = []
    if "GetLocationCardBySide" in calls:
        flags.append("★可能打HQ")
    if "GetOppositeSide" in calls:
        flags.append("有OppositeSide")
    if "GetTargetedCard" in calls:
        flags.append("打目标")
    print("  %-40s %s" % (name, " ".join(flags)))
    print("      calls: %s" % ", ".join(sorted(calls)))
    print("      ints : %s" % e.get("ints"))
print("  共 %d 张带伤害调用" % n)

print()
print("=== 卡池里完全不在 card-effects.json 的卡 ===")
miss = sorted(n for n in pool if n not in eff)
print("  %s" % (miss or "（无）"))
