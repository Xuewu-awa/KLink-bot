"""
核验子代理的两条关键主张（不能只采信汇报）。

主张 A：`{self:true}` **不是接收者占位，它就是第一个实参**；
        摘掉它正是「攻击力 98.7%→87.7%」的真凶。
        → 可查：带 self 与不带 self 的同名调用，**实参个数是否相同**。
          若相同，说明 self 确实占了实参位（摘掉就会整体错位一位）→ 主张成立。
          若不同，说明它是额外附加的接收者 → 摘掉才对。

主张 B：`MatchEngine.Attack` 发的 `OnOtherCardAttacks` 程序名**不存在**，
        真名是 `OnAfterOtherCardAttacks`（23 个订阅者）。
        → 可查：数一下这两个名字各有多少张卡注册。
"""
import json
from collections import Counter, defaultdict

IR = json.load(open(r"klink bot\docs\card-ir.json", encoding="utf-8"))

print("=" * 76)
print("主张 A：{self:true} 到底占不占实参位")
print("=" * 76)

# 对每个函数，收集「带 self」和「不带 self」两种调用的实参个数
with_self = defaultdict(list)
without_self = defaultdict(list)

for card, prog in IR.items():
    for s in prog["steps"]:
        if s.get("op") != "call":
            continue
        fn = s.get("fn")
        args = s.get("args") or []
        if args and isinstance(args[0], dict) and args[0].get("self") is True:
            with_self[fn].append(len(args))
        else:
            without_self[fn].append(len(args))

both = sorted(set(with_self) & set(without_self))
print(f"两种写法都出现过的函数: {len(both)} 个\n")
print(f"{'函数':<42}{'带self的实参个数':<24}{'不带self的实参个数'}")
same_arity = 0
for fn in both[:20]:
    a = sorted(set(with_self[fn]))
    b = sorted(set(without_self[fn]))
    mark = ""
    if a == b:
        same_arity += 1
        mark = "  ← 个数相同"
    print(f"  {fn:<40}{str(a):<24}{str(b)}{mark}")

# 全量统计
total_both = 0
for fn in both:
    if sorted(set(with_self[fn])) == sorted(set(without_self[fn])):
        total_both += 1
print(f"\n★ {len(both)} 个混用函数里，有 {total_both} 个「两种写法实参个数完全相同」")
print("   → 个数相同说明 self 占了实参位（摘掉会整体错位）")
print("   → 若个数差 1，说明 self 是额外附加的接收者（摘掉才对）")

print()
print("=" * 76)
print("主张 B：OnOtherCardAttacks 这个名字存在吗")
print("=" * 76)
for name in ["OnOtherCardAttacks", "OnAfterOtherCardAttacks",
             "OnOtherCardPlayedFromHand", "OnEndOfTurn", "OnCardReset",
             "OnOtherCardReset", "OnAfterOtherCardOperactionCostBuffsReset"]:
    n = sum(1 for p in IR.values() if name in p["entrypoints"])
    print(f"  {name:<44} {n:4} 张卡注册")
