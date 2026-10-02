import json
import sys

d = json.load(open(r"klink bot\docs\card-effects.json", encoding="utf-8"))
names = sys.argv[1:] or [
    "card_unit_arado_ar_196", "card_unit_panzer_i_a", "card_unit_85_pioneer_company",
    "card_unit_sd_kfz_10_38", "card_unit_3_panzergrenadier",
    "card_unit_m2a4_bal", "card_unit_panther_a_zimm",
]
for n in names:
    c = d.get(n)
    if not c:
        print("  %-34s 不在卡库" % n)
        continue
    print("  %-34s %s费 %s/%s op=%s [%s]" % (
        n, c.get("kredits"), c.get("attack"), c.get("defense"),
        c.get("operationcost"), c.get("type")))
    print("        %s" % (c.get("text") or ""))
