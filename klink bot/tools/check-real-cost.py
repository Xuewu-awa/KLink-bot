"""
直接读**真实快照**里这几张卡的 `kredits` 字段 —— 对拍里被拒的那三张到底多少钱。

背景（replay-989040，T14 右侧，上限 7）：
    实际打出：queens_own(卡库写 7) + m2a4_bal(卡库写 2) + kettenkrad_home_bal(卡库写 2) = 11
    但上限只有 7 → 内核把后两张判成「kredit 不足」拒掉。

三种可能：
  a) 这两张的真实费用本来就比卡库低（`_bal` 是变体，费用和基础卡不同）
  b) 它们被减费效果改成 0 了（比如 PAMS：「Develop a British order costing 4 or less.
     Add it to your deck with a cost of 0.」—— 这局右侧 T2 确实打过 pams）
  c) 右侧拿了额外 kredit 槽位

`kredits` 是**实时值**还是**卡面基础值**需要当场验：如果它在局中变过，就是实时值。
所以这里把这几张卡**每个快照的 kredits 都打出来**，看它变不变。
"""
import json
import os

CAP = r"<user-home>\AppData\Local\Temp\klink-capture"
FILE = "snapshot-match40.jsonl"          # 配对 replay-989040
WATCH = ["card_unit_queens_own", "card_unit_m2a4_bal",
         "card_event_kettenkrad_home_bal", "card_event_pams"]

rows = [json.loads(l) for l in open(os.path.join(CAP, FILE), encoding="utf-8") if l.strip()]
rows.sort(key=lambda r: r["act"])

print(f"{FILE}: {len(rows)} 条快照，act {rows[0]['act']}..{rows[-1]['act']}")
print()

# 先看这些卡在不在
present = {}
for r in rows:
    for c in r["cards"]:
        n = c.get("Name")
        if n in WATCH:
            present.setdefault(n, []).append(c)

for n in WATCH:
    hits = present.get(n, [])
    if not hits:
        print(f"  {n:<34} 快照里没出现")
        continue
    costs = sorted({h.get("kredits") for h in hits})
    print(f"  {n:<34} 出现 {len(hits)} 次   kredits 取值={costs}")

print()
print("=== 逐快照看 queens_own / m2a4_bal / kettenkrad_home_bal 的费用与位置 ===")
for r in rows:
    line = []
    for c in r["cards"]:
        n = c.get("Name")
        if n in ("card_unit_queens_own", "card_unit_m2a4_bal", "card_event_kettenkrad_home_bal"):
            line.append(f"{n.replace('card_unit_','').replace('card_event_','')}"
                        f"(id={c.get('cardID')} k={c.get('kredits')} loc={c.get('location')})")
    if line:
        print(f"  act={r['act']:<4} " + "  ".join(sorted(line)))
