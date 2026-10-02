"""
在卡面文本里找「指令减费」类的卡 —— 用户描述的那张光环卡：
    部署后持续生效，每回合第一张指令 -1 费；
    表现为「没打出指令前所有指令都显示 -1，打出一张后恢复」。

这类卡在数据上的特征是：文本里同时出现 order/Orders 和 cost/less 之类的词，
而且效果实现方式多半是「给手牌里所有指令 ChangeKreditCost(-1)」+「打出指令后还原」。

⚠️ 这类**光环**对模拟器特别麻烦：
  · 它不是一次性结算，而是持续改变别人（手牌里的指令）的费用
  · 触发点是 OnDeployment + OnPlayedFromHand（打出指令时）
  · 只读卡面文本会漏掉它改变的是**别的卡**的费用
"""
import json
import re

EFFECTS = r"klink bot\docs\card-effects.json"
d = json.load(open(EFFECTS, encoding="utf-8"))

PAT = re.compile(
    r"(order|orders|指令)[^.]{0,60}(cost|costs|less|cheaper|reduce)|"
    r"(cost|costs|less|cheaper|reduce)[^.]{0,60}(order|orders)",
    re.I,
)

print("=== 卡面提到「指令费用」的卡 ===")
hits = []
for name, c in d.items():
    t = c.get("text") or ""
    if PAT.search(t):
        hits.append((name, c.get("kredits"), c.get("type"), c.get("title"), t))

hits.sort(key=lambda x: (x[2] or "", x[0]))
for name, k, typ, title, t in hits:
    print(f"  {name:<44} {str(k):>4}费 {str(typ):<10} {str(title)[:22]:<24} {t}")

print(f"\n共 {len(hits)} 张")

# 再看这些卡用了哪些调用 —— 减费应该出现 ChangeKreditCost
print()
print("=== 其中用到 ChangeKreditCost 的 ===")
for name, k, typ, title, t in hits:
    calls = set()
    for v in (d[name].get("functions") or {}).values():
        calls.update(v)
    if "ChangeKreditCost" in calls:
        fn = sorted(d[name].get("functions", {}).keys())
        print(f"  {name:<44} 函数={fn}")
