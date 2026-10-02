import json
import sys

d = json.load(open(r"klink bot\docs\card-effects.json", encoding="utf-8"))
names = sys.argv[1:] or [
    "card_event_war_bonds", "card_event_mi_5", "card_unit_m2a4_bal",
    "card_unit_queens_own", "card_unit_hampshire_regiment",
    "card_event_kettenkrad_home_bal", "card_event_the_war_machine",
]
for n in names:
    c = d.get(n)
    if not c:
        print(f"{n}: 不在卡库")
        continue
    print(n)
    print(f"   费用={c.get('kredits')}  {c.get('title')}  [{c.get('type')}]")
    print(f"   卡面: {c.get('text')}")
    calls = set()
    for v in c.get("functions", {}).values():
        calls.update(v)
    kr = [x for x in sorted(calls) if "redit" in x]
    print(f"   kredit 相关调用: {kr}")
