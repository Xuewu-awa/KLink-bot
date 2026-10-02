"""在某一局双方的卡池里找「能对敌方 HQ 造成固定伤害」的卡，并列出伤害值。

用来回答：310284 的 A35 之后左方 HQ 凭空 -3，这个 3 会不会来自
**某张已经打出的卡**的延迟/条件效果。

用法: python tools/hq-damage-in-match.py <快照.json>
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

snap = json.load(open(sys.argv[1], encoding="utf-8"))
sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]

pool = {"left": [], "right": []}
for side in ("left", "right"):
    for field in ("starting_hand_%s" % side, "deck_%s" % side):
        pool[side] += [c["name"] for c in sd[field]]
    pool[side].append(sd["location_card_%s" % side]["name"])

ir = json.load(open("docs/card-ir.json", encoding="utf-8"))


def walk(expr, out):
    if isinstance(expr, dict):
        if "call" in expr:
            out.append(expr)
        for v in expr.values():
            walk(v, out)
    elif isinstance(expr, list):
        for v in expr:
            walk(v, out)


def hq_damage(name):
    """返回 [(所在步, 伤害值)]，只认「目标来自 GetLocationCardBySide」的 DamageCard。"""
    card = ir.get(name)
    if not card:
        return None
    out = []
    for s in card["steps"]:
        calls = []
        walk(s, calls)
        fns = {c.get("call") for c in calls}
        if not {"GetOppositeSide", "GetLocationCardBySide", "DamageCard"} <= fns:
            continue
        for c in calls:
            if c.get("call") != "DamageCard":
                continue
            args = c.get("args") or []
            tgt = json.dumps(args[0], ensure_ascii=False) if args else ""
            if "GetLocationCardBySide" not in tgt:
                continue
            a1 = args[1] if len(args) > 1 else {}
            out.append((s.get("i"), a1.get("int") if isinstance(a1, dict) else str(a1)))
    return out


print("=== 双方卡池里「打敌方 HQ 固定伤害」的卡 ===")
for side in ("left", "right"):
    print("\n--- %s 方 ---" % side)
    hits = 0
    for name in sorted(set(pool[side])):
        r = hq_damage(name)
        if r:
            print("  %-42s %s" % (name, r))
            hits += 1
    if not hits:
        print("  （没有）")

print()
print("=== 整个卡池里有没有卡名缺失 IR 的 ===")
missing = sorted({n for n in pool["left"] + pool["right"] if n not in ir})
print("  共 %d 张不在我的 IR 里：" % len(missing))
for n in missing:
    print("   ", n)
