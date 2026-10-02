"""找出「能对敌方 HQ 造成伤害」的效果程序，并列出伤害值与所在触发器。

思路：310284 的 A35 之后左方 HQ 凭空 -3，而两个独立引擎都算不出来。
所以先别猜机制，先把**在场所有卡里所有能打敌方 HQ 的程序**摊开，
看有没有哪个的伤害正好是 3、而且触发时机能落在 A34/A35 上。

判据：一段程序里同时出现
  GetOppositeSide  ->  GetLocationCardBySide  ->  DamageCard(那张卡, N, ...)
这种链就是「打敌方 HQ N 点」。

用法: python tools/find-hq-damage-effects.py [卡名...]
      不给卡名就扫全部卡
"""
import json
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")

IR = json.load(open("docs/card-ir.json", encoding="utf-8"))


def walk(expr, out):
    """递归收集表达式树里出现的所有调用。"""
    if isinstance(expr, dict):
        if "call" in expr:
            out.append(expr)
        for v in expr.values():
            walk(v, out)
    elif isinstance(expr, list):
        for v in expr:
            walk(v, out)


def hq_damage_amounts(steps):
    """返回这段程序里「打敌方 HQ」的伤害值列表。"""
    found = []
    for s in steps:
        calls = []
        walk(s, calls)
        fns = [c.get("call") for c in calls]
        if "GetOppositeSide" not in fns:
            continue
        if "GetLocationCardBySide" not in fns:
            continue
        for c in calls:
            if c.get("call") != "DamageCard":
                continue
            args = c.get("args") or []
            target = json.dumps(args[0], ensure_ascii=False) if args else ""
            # 目标是 GetLocationCardBySide 的 out 槽 -> 就是 HQ
            if "GetLocationCardBySide" not in target:
                continue
            amount = None
            if len(args) > 1:
                a1 = args[1]
                if isinstance(a1, dict) and "int" in a1:
                    amount = a1["int"]
                else:
                    amount = json.dumps(a1, ensure_ascii=False)
            found.append(amount)
    return found


def scan(name):
    card = IR.get(name)
    if not card:
        return None
    res = {}
    for entry, idx in card.get("entrypoints", {}).items():
        # 取该入口开始的步（简单起见：用全部步，误差可接受，只用来找候选）
        amounts = hq_damage_amounts(card["steps"])
        if amounts:
            res[entry] = amounts
    return res or None


targets = sys.argv[1:]
if targets:
    for name in targets:
        r = scan(name)
        print("%-40s %s" % (name, r if r else "（没有打敌方 HQ 的程序）"))
    sys.exit(0)

# 全扫
hits = {}
for name in IR:
    r = scan(name)
    if r:
        hits[name] = r

print("=== 全库里「能打敌方 HQ」的卡（共 %d 张）===" % len(hits))
amount_counter = Counter()
for name, r in sorted(hits.items()):
    flat = [a for v in r.values() for a in v]
    for a in flat:
        amount_counter[str(a)] += 1

print("\n伤害值分布（出现次数）:")
for amt, cnt in amount_counter.most_common(20):
    print("  %-10s %d" % (amt, cnt))

print("\n伤害值 = 3 的卡:")
n = 0
for name, r in sorted(hits.items()):
    flat = [a for v in r.values() for a in v]
    if 3 in flat:
        print("  %-44s %s" % (name, json.dumps(r, ensure_ascii=False)))
        n += 1
print("  共 %d 张" % n)
