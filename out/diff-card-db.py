"""对比「1.61 重新生成的卡表」与「当前部署的卡表」，逐字段列出差异。

当前卡表来自 1.60 的 **manifest 形态**（带 resolved 继承值）+ `76e0840` 的手工订正；
新生成的是 1.61 的 **单文件 dump 形态**。所以：
  · 差异里既有「1.61 真改了」，也有「单文件形态解析不到（占位符）」——
    后者必须保留旧值，不能当成 0/空。

用法：python diff-card-db.py [--only-changed]
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

CUR = r"E:\项目\klink-dotnet\rel\data\fyserver\BotData\cards.json"
GEN = r"E:\项目\klink-dotnet\temp\pak-20261031\cards.generated.json"

cur = json.load(open(CUR, encoding="utf-8-sig"))
gen = json.load(open(GEN, encoding="utf-8-sig"))
print("当前卡表", len(cur), "张 / 新生成", len(gen), "张")
print("当前有、新生成没有:", len(set(cur) - set(gen)))
print("新生成有、当前没有:", len(set(gen) - set(cur)), sorted(set(gen) - set(cur))[:8])

PLACEHOLDER_HINT = ("UAssetAPI", "UASSETAPI")

# 只比这些"由 CDO 直接决定"的字段；结构类（chooseOneCards/innateAbilities/usedTriggers）不比，
# 因为单文件形态解析不出来，比了只会得到噪声。
FIELDS = ["kredits", "operationcost", "attack", "defense", "range", "heavyArmor",
          "rarity", "cardSet", "faction", "type", "effectType", "cipher", "targetTax",
          "hasGuard", "title", "text", "customName1", "customName2", "selectTargetOnPlayedFromHand"]


def bad(v):
    return isinstance(v, str) and any(h in v for h in PLACEHOLDER_HINT)


all_diff = {}
for name in sorted(set(cur) & set(gen)):
    a, b = cur[name], gen[name]
    d = {}
    for f in FIELDS:
        va, vb = a.get(f), b.get(f)
        if bad(vb):
            continue
        if va != vb:
            d[f] = (va, vb)
    if d:
        all_diff[name] = d

print()
print(f"字段有差异的卡：{len(all_diff)} 张")
for name, d in all_diff.items():
    print()
    print(name)
    for f, (va, vb) in d.items():
        sa, sb = str(va), str(vb)
        if len(sa) > 80:
            sa = sa[:80] + "…"
        if len(sb) > 80:
            sb = sb[:80] + "…"
        print("    %-28s %s  ->  %s" % (f, sa, sb))
