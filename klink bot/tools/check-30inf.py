"""核对 card_unit_30_infantry_regiment 的攻防费 —— 1 费 4/4 是否离谱。"""
import json
import os

K = "card_unit_30_infantry_regiment"

print("=" * 74)
print("① 原始 CDO 里所有和攻防费有关的字段")
print("=" * 74)
d = json.load(open(r"out/cards-full2.json", encoding="utf-8"))
if isinstance(d, list):
    d = {c.get("id"): c for c in d}
raw = (d.get(K) or {}).get("raw") or {}
for k, v in sorted(raw.items()):
    if any(s in k.lower() for s in ("attack", "defen", "kredit", "cost", "range",
                                    "title", "text", "name", "type", "faction", "flag")):
        print(f"  {k:<30} = {v}")

print()
print("=" * 74)
print("② 三个来源对比")
print("=" * 74)
for p in [r"klink bot/docs/cards.live.json", r"klink bot/docs/card-effects.json"]:
    x = json.load(open(p, encoding="utf-8"))
    if isinstance(x, list):
        x = {c.get("id") or c.get("asset", "").split("/")[-1]: c for c in x}
    c = x.get(K) or {}
    print(f"  {p}")
    print(f"      title   = {c.get('title')!r}")
    print(f"      kredits = {c.get('kredits')}   attack = {c.get('attack')}   defense = {c.get('defense')}")
    print(f"      text    = {str(c.get('text'))[:95]!r}")

print()
print("=" * 74)
print("③ kardsim 的 cards.json")
print("=" * 74)
try:
    ks = json.load(open(r"ref/kards-sim/cards.json", encoding="utf-8"))
    if isinstance(ks, list):
        ks = {c.get("id") or c.get("Id") or c.get("Name"): c for c in ks}
    c = ks.get(K) or ks.get(K.replace("card_unit_", "")) or {}
    if c:
        for k in list(c)[:16]:
            print(f"      {k:<20} = {c[k]}")
    else:
        print("      找不到这张卡")
except Exception as e:
    print("      读不到:", e)

print()
print("=" * 74)
print("④ 全卡池：1 费单位里攻防最高的几张（看 4/4 是不是孤例）")
print("=" * 74)
units = []
for i, c in d.items():
    if not i.startswith("card_unit_"):
        continue
    r = c.get("raw") or {}
    k = r.get("kredits")
    a = r.get("attack")
    df = r.get("defense")
    if isinstance(k, int) and isinstance(a, int) and isinstance(df, int) and k == 1:
        units.append((a + df, a, df, c.get("title") or i, i))
units.sort(reverse=True)
for tot, a, df, t, i in units[:12]:
    print(f"      {a}/{df}  (合计 {tot})   {t:<34} {i}")
print(f"      …1 费单位共 {len(units)} 张")
