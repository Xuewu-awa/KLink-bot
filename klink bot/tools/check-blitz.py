"""核对"召唤失调"这条规则：回放里出现「同回合 PC 后立刻 ML/AC」的卡，
到底有没有 Blitz 关键字。

背景：kards-sim 的引擎写的是
    c.SummonedThisTurn = true;
    if (!c.Has(Kw.Blitz)) c.AttackedThisTurn = true;   // 非 Blitz 当回合不能打
而我按 310284 的回放把这条门禁**去掉**了。两者必须有一个人错。
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

their = {c["id"]: c for c in json.load(open(r"..\ref\kards-sim\cards.json", encoding="utf-8"))}
mine = json.load(open("docs/cards.live.json", encoding="utf-8"))

TARGETS = [
    # 回放里「同回合 出牌 → 移动/攻击」的四张卡
    ("card_unit_7_schutzen", "310284 A34 PC → A35 AC（同 T10）"),
    ("card_unit_m20_scout_car", "310284 A24 PC → A25 ML → A26 AC（同 T8）"),
    ("card_unit_sd_kfz_10_38", "310284 A70 PC → A71 ML（同 T18）"),
    ("card_unit_stug_iii", "310284 A9 T4 PC → A15 T6 ML（跨回合，不算证据）"),
]

print("=== 关键字对照 ===")
for name, note in TARGETS:
    t = their.get(name)
    print("\n### %s   （%s）" % (name, note))
    if not t:
        print("    kards-sim 没有这张卡")
    else:
        raw = t.get("raw") or {}
        kws = {k: v for k, v in raw.items()
               if k.lower().startswith("has") and isinstance(v, bool) and v}
        print("    kards-sim flags:", t.get("flags"))
        print("    kards-sim raw 里的 has* 为真的:", kws or "（无）")
        print("    kards-sim type/kredits/attack/defense:", raw.get("Type"),
              raw.get("kredits"), raw.get("attack"), raw.get("defense"))

    m = mine.get(name)
    if m:
        kws2 = {k: v for k, v in m.items()
                if k.lower().startswith("has") and isinstance(v, bool) and v}
        print("    我这边的 has* 为真的:", kws2 or "（无）")

print()
print("=== 直接找 hasBlitz 字段是否存在 ===")
for name, _ in TARGETS:
    t = their.get(name) or {}
    raw = t.get("raw") or {}
    hits = [k for k in raw if "blitz" in k.lower()]
    print("  %-28s raw 里含 blitz 的键: %s" % (name, hits or "（无）"))
