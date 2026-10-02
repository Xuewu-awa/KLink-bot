"""把指定卡**所有** DamageCard / 治疗 / 改 HQ 的调用摊开，看目标表达式和数值。

比 find-hq-damage-effects.py 更宽：不预设「打 HQ」的表达式形状，
而是把所有 DamageCard 的目标实参原样打出来，人工判断哪个能落到 HQ 上。
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

IR = json.load(open("docs/card-ir.json", encoding="utf-8"))


def walk(expr, out):
    if isinstance(expr, dict):
        if "call" in expr:
            out.append(expr)
        for v in expr.values():
            walk(v, out)
    elif isinstance(expr, list):
        for v in expr:
            walk(v, out)


INTERESTING = ("DamageCard", "DamageMultipleCards", "DestroyCard", "HealCard",
               "ChangeDefense", "SetDefense", "DamageLocationCard", "DealDamage")


def dump(name):
    card = IR.get(name)
    if not card:
        print("%-42s 不在 IR 里" % name)
        return
    eps = card.get("entrypoints", {})
    print("=" * 96)
    print("%s   入口: %s" % (name, json.dumps(eps, ensure_ascii=False)))
    for s in card["steps"]:
        calls = []
        walk(s, calls)
        for c in calls:
            fn = c.get("call")
            if fn not in INTERESTING:
                continue
            args = c.get("args") or []
            desc = []
            for a in args:
                desc.append(json.dumps(a, ensure_ascii=False))
            print("    i=%-5s %-18s %s" % (s.get("i"), fn, " , ".join(desc)))


for n in sys.argv[1:]:
    dump(n)
