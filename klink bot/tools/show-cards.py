"""查若干张卡的文本/数值，用于解释回放里对不上的伤害。

用法: python tools/show-cards.py <卡名> [卡名...]
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

cards = json.load(open("docs/cards.live.json", encoding="utf-8"))

for name in sys.argv[1:]:
    c = cards.get(name)
    if not c:
        print("%-38s 未找到" % name)
        continue
    print("=" * 92)
    print("%s   type=%s faction=%s" % (name, c.get("type"), c.get("faction")))
    print("  费=%s 攻=%s 防=%s 行动=%s 稀有=%s" % (
        c.get("kredits"), c.get("attack"), c.get("defense"),
        c.get("operationcost"), c.get("rarity")))
    text = (c.get("text") or "").replace("\n", " / ")
    print("  标题: %s" % c.get("title"))
    print("  文本: %s" % text)
