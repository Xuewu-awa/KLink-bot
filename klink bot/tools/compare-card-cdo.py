"""
把这局用到的每张卡的**原始 CDO 字段**摊开，和 card-effects.json 里的值对照。

目的：找出「模拟器算错」到底是
  a) 解包阶段就丢了字段（比如 85_pioneer_company 的 attack 是 None）
  b) 字段在，但内核读错/没读

数据来源：
  out/cards-full2.json   —— KardsDataExtract 用 trumank 的 .jmap 抽的**全量**卡表，
                            每条带 `raw`（客户端 CDO 的原始属性字典）
  klink bot/docs/card-effects.json —— 我自己那套（反编译调用表）
"""
import json
import sys

FULL = r"out\cards-full2.json"
EFFECTS = r"klink bot\docs\card-effects.json"

full = {c.get("id"): c for c in json.load(open(FULL, encoding="utf-8"))}
eff = json.load(open(EFFECTS, encoding="utf-8"))

NAMES = sys.argv[1:] or [
    "card_unit_arado_ar_196", "card_unit_panzer_i_a", "card_unit_85_pioneer_company",
    "card_unit_sd_kfz_10_38", "card_unit_3_panzergrenadier", "card_unit_panther_a_zimm",
]

# 先看 raw 里到底有哪些键（用第一张有 raw 的卡）
sample = next((c for c in full.values() if c.get("raw")), None)
if sample:
    print("=== raw 里的键（样例：%s）===" % sample.get("id"))
    print("   " + ", ".join(sorted(sample["raw"].keys())))
    print()

for n in NAMES:
    c = full.get(n)
    e = eff.get(n)
    print("=" * 78)
    print(n)
    if c is None:
        print("   ✗ 不在全量卡表里")
        continue
    raw = c.get("raw") or {}
    print("   [KardsDataExtract]  type=%s faction=%s rarity=%s" %
          (c.get("type"), c.get("faction"), c.get("rarity")))
    # 只打数值相关的键
    KEYS = [k for k in raw if any(t in k.lower() for t in
            ("kredit", "attack", "defense", "range", "operation", "cost", "armor", "health"))]
    if KEYS:
        for k in sorted(KEYS):
            print("      raw[%-28s] = %s" % (k, raw[k]))
    else:
        print("      （raw 里没有数值字段）")

    if e:
        print("   [card-effects]     %s费  attack=%s defense=%s range=%s op=%s" %
              (e.get("kredits"), e.get("attack"), e.get("defense"),
               e.get("range"), e.get("operationcost")))
        print("      卡面: %s" % (e.get("text") or ""))
    else:
        print("   [card-effects]     ✗ 不在")
