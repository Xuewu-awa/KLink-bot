"""
读 fyserver（KLink 私服）自己的卡表 —— `_bal` 变体的真实费用在这里。

线索链：
  · 三份客户端卡库（我的 2021 / card-effects 2053 / kardsim 2023）里 `_bal` **一条都没有**
  · 客户端抽取产物里也只有 `card_unit_m2a4` 和 `card_event_kettenkrad_home` 基础资产
  · 但回放（服务端）里出现的是 `card_unit_m2a4_bal` / `card_event_kettenkrad_home_bal`
  → 所以 `_bal` 是**服务端侧的卡**，解包客户端 pak 读不到，得看 fyserver 的卡表。
"""
import json
import os

P = r"src\KLink.App\bin\Release\net10.0-windows\data\fyserver\cards-from-fmodel.json"
if not os.path.exists(P):
    P = r"src\KLink.App\bin\Debug\net10.0-windows\data\fyserver\cards-from-fmodel.json"

d = json.load(open(P, encoding="utf-8"))
print(f"路径: {P}")
print(f"fyserver 卡表条数: {len(d)}")

bal = sorted(k for k in d if k.endswith("_bal"))
print(f"其中 _bal 结尾: {len(bal)} 张")

print()
print("=== replay-989040 T14 那三张（上限 7，实际花了 11）===")
for n in ["card_unit_queens_own", "card_unit_m2a4", "card_unit_m2a4_bal",
          "card_event_kettenkrad_home", "card_event_kettenkrad_home_bal"]:
    c = d.get(n)
    if not c:
        print(f"  {n:<38} 不在表里")
        continue
    print(f"  {n:<38} kredits={c.get('kredits')}  atk={c.get('attack')}  "
          f"def={c.get('defense')}  type={c.get('type')}")

print()
print("=== _bal 与其基础卡的 kredits 对比（前 25 组）===")
shown = 0
for n in bal:
    base = n[:-4]
    cb, cn = d.get(base), d.get(n)
    if not cb or not cn:
        continue
    same = cb.get("kredits") == cn.get("kredits")
    mark = "" if same else "   ← 费用不同！"
    print(f"  {base:<40} {str(cb.get('kredits')):>4} → {n:<44} {str(cn.get('kredits')):>4}{mark}")
    shown += 1
    if shown >= 25:
        break
if shown == 0:
    print("  （没有基础卡同名的 _bal，说明 _bal 是独立命名）")
