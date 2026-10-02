"""
核对 PANTHER A ZIMMERIT 的**卡面原文**：是我库里的文本抽错了，还是内核算错了。

两个来源:
  · klink bot/docs/card-effects.json 的 text  —— 我自己那套
  · out/cards-full2.json 的 raw["Text"]       —— KardsDataExtract 走 .jmap 抽的原始 CDO

用户记忆里的效果是「攻击一次之后行动花费 +1」，
而我库里写的是「After this unit operates, increase all its stats by how often it has operated.」
—— 一个越用越贵、一个越用越强，方向相反。先定死哪个是真的。
"""
import json

TARGET = "card_unit_panther_a_zimm"

live = json.load(open(r"klink bot\docs\cards.live.json", encoding="utf-8"))
eff = json.load(open(r"klink bot\docs\card-effects.json", encoding="utf-8"))
full = {c.get("id"): c for c in json.load(open(r"out\cards-full2.json", encoding="utf-8"))}

print("=" * 74)
print(f"【cards.live.json】(引擎读数值的那份)")
c = live.get(TARGET)
print(f"  {json.dumps(c, ensure_ascii=False)}" if c else "  不在")

print()
print("=" * 74)
print("【card-effects.json】(我的效果表)")
e = eff.get(TARGET)
if e:
    print(f"  title  = {e.get('title')}")
    print(f"  text   = {e.get('text')}")
    print(f"  数值   = {e.get('kredits')}费 {e.get('attack')}/{e.get('defense')}")
    print(f"  函数   = {sorted((e.get('functions') or {}).keys())}")
else:
    print("  不在")

print()
print("=" * 74)
print("【cards-full2.json 的 raw CDO】(独立抽取的原始属性)")
c2 = full.get(TARGET)
if c2:
    raw = c2.get("raw") or {}
    for k in sorted(raw):
        v = raw[k]
        s = str(v)
        if len(s) > 160:
            s = s[:160] + "…"
        print(f"  raw[{k:<26}] = {s}")
else:
    print("  不在")

print()
print("=" * 74)
print("【IR 里这张卡实现了哪些事件 + 调用了什么】")
ir = json.load(open(r"klink bot\docs\card-ir.json", encoding="utf-8")).get(TARGET)
if ir:
    print(f"  entrypoints: {ir['entrypoints']}")
    fns = {}
    for s in ir["steps"]:
        if s.get("op") == "call":
            fns[s["fn"]] = fns.get(s["fn"], 0) + 1
    for k, v in sorted(fns.items(), key=lambda x: -x[1]):
        print(f"      {k:<44} ×{v}")
else:
    print("  不在 IR")
