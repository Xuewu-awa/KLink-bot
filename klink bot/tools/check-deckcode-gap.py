"""查回放里失败的卡组码指向什么、卡库有没有。"""
import json

tbl = json.load(open(r"klink bot\docs\deck_code_ids.json", encoding="utf-8"))
print("=== 表里这些码指向什么 ===")
for c in ["Cx", "CB", "G8", "st", "3Y"]:
    v = tbl.get(c)
    print(f"  {c}: " + (json.dumps(v, ensure_ascii=False)[:200] if v else "（表里没有）"))

print()
cards = json.load(open(r"klink bot\docs\cards.json", encoding="utf-8"))
if isinstance(cards, list):
    cards = {c.get("id") or c.get("name"): c for c in cards}
eff = json.load(open(r"klink bot\docs\card-effects.json", encoding="utf-8"))
if isinstance(eff, list):
    eff = {c.get("id") or c.get("asset", "").split("/")[-1]: c for c in eff}
print(f"=== 卡库: cards.json {len(cards)} 条 / card-effects.json {len(eff)} 条 ===")


def norm(v):
    if isinstance(v, dict):
        for k in ("card", "name", "title", "id", "card_name"):
            if k in v:
                return v[k]
    return v


for c in ["Cx", "CB", "st", "3Y", "Al", "oj"]:
    v = tbl.get(c)
    name = norm(v)
    print(f"  {c} -> {name!r}")
    if isinstance(name, str) and name:
        cands = [name, "card_unit_" + name, "card_event_" + name]
        hit_c = [k for k in cands if k in cards]
        hit_e = [k for k in cands if k in eff]
        print(f"       cards.json 命中: {hit_c}")
        print(f"       card-effects 命中: {hit_e}")
        if not hit_c and not hit_e:
            tail = name.split("_")[-1][:8]
            fuzzy = [k for k in list(cards) + list(eff) if tail and tail in k]
            print(f"       模糊({tail}): {fuzzy[:6]}")
