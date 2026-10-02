"""
决定性检查：cards.live.json 里 operationCost 这个键的**大小写**。

背景：CardDatabase 的反序列化设置是
    PropertyNameCaseInsensitive = false
而 FmodelCard 上写的是
    [JsonPropertyName("operationcost")]        ← 全小写
如果数据里的键是 `operationCost`（驼峰），那这一项**永远解析不出来 → OperationCost 恒为 0**
→ 单位行动不要钱 → 机器人可以无限 operate
→ 「每次 operate 后全属性 +1」这类卡无限叠（实测 PANTHER A ZIMMERIT 打出 11 点伤害）
→ 对局 7 回合就结束

同一份代码里其它字段（kredits / attack / defense / range）如果大小写正好对得上，
就只有 operationCost 一个人坏掉 —— 这种"只坏一个字段"的 bug 最难发现。
"""
import json

P = r"klink bot\docs\cards.live.json"
d = json.load(open(P, encoding="utf-8"))
print(f"{P}: {len(d)} 条")

PROBE = ["card_unit_arado_ar_196", "card_unit_85_pioneer_company",
         "card_unit_panther_a_zimm", "card_unit_3_panzergrenadier"]

# 1) 键的大小写全貌
allkeys = set()
for c in list(d.values())[:400]:
    allkeys |= set(c.keys())
print("\n=== 数据里所有键（看大小写风格）===")
for k in sorted(allkeys):
    print(f"    {k}")

# 2) 和解析器期望的名字对照
EXPECTED = ["kredits", "operationcost", "attack", "defense", "range",
            "cardSet", "cardset", "title", "text", "type", "faction", "rarity"]
print("\n=== 解析器期望的键名 vs 数据里到底有没有 ===")
for e in EXPECTED:
    print(f"    {e:<18} {'✓ 有' if e in allkeys else '✗ 没有'}")

# 3) 那几张卡的实际取值
print("\n=== 对局里用到的卡：operationCost 附近的所有键 ===")
for n in PROBE:
    c = d.get(n)
    if not c:
        print(f"  {n}: 不在")
        continue
    cand = {k: v for k, v in c.items() if "operat" in k.lower() or "cost" in k.lower()}
    print(f"  {n}")
    for k, v in sorted(cand.items()):
        print(f"      {k:<24} = {v}")
    # 顺便看它有哪些数值键
    nums = {k: v for k, v in c.items() if isinstance(v, (int, float))}
    print(f"      数值键: {nums}")
