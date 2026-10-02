"""
量化 cards.live.json 里 operationcost 缺了多少张，并和权威源交叉验证。

发现过程（按用户方案：解包 → 对照效果）：
  1. 自对弈跑出 PANTHER A ZIMMERIT 对 HQ 打 11 点（卡面是 2 攻）
  2. 它的卡面：「After this unit operates, increase all its stats by how often it has operated.」
  3. 查它的数值键 → **没有 operationcost** → 内核当 0 → 行动免费 → 无限 operate → 属性无限叠

这里要回答的是：这只是那一张卡的偶然，还是一整批卡的系统性缺口？
交叉验证源用 KardsDataExtract 抽的 raw CDO（走 trumank 的 .jmap，字段更全）。
"""
import json

LIVE = r"klink bot\docs\cards.live.json"
FULL = r"out\cards-full2.json"

live = json.load(open(LIVE, encoding="utf-8"))
full = {c.get("id"): c for c in json.load(open(FULL, encoding="utf-8"))}

print(f"cards.live.json: {len(live)} 张")
print(f"cards-full2.json: {len(full)} 张")

# ---- 1) 有多少张缺 operationcost ----
no_op = sorted(k for k, c in live.items() if "operationcost" not in c)
print(f"\n=== 缺 operationcost 的: {len(no_op)} 张（占 {100*len(no_op)/len(live):.1f}%）===")
for k in no_op[:25]:
    t = (live[k].get("type") or "?")
    print(f"    {k:<44} [{t}] {live[k].get('text') or ''}"[:130])
if len(no_op) > 25:
    print(f"    … 还有 {len(no_op)-25} 张")

# ---- 2) 缺 operationcost 的是不是全是单位？----
by_type = {}
for k in no_op:
    t = live[k].get("type") or "?"
    by_type[t] = by_type.get(t, 0) + 1
print(f"\n    按类型: {by_type}")

# ---- 3) 权威源（raw CDO）里这些卡有没有 operationCost ----
print("\n=== 交叉验证：raw CDO 里有没有 operationCost ===")
recoverable = []
still_missing = []
for k in no_op:
    c = full.get(k)
    raw = (c or {}).get("raw") or {}
    has = any(kk.lower() == "operationcost" for kk in raw)
    (recoverable if has else still_missing).append(k)

print(f"  raw 里有（说明是 cards.live.json 抽漏了）: {len(recoverable)} 张")
for k in recoverable[:10]:
    raw = full[k]["raw"]
    v = next(v for kk, v in raw.items() if kk.lower() == "operationcost")
    print(f"      {k:<44} raw operationCost = {v}")
print(f"  raw 里也没有（可能是真的 0）: {len(still_missing)} 张")
for k in still_missing[:10]:
    print(f"      {k}")
