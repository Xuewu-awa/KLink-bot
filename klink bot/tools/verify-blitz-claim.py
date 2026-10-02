"""
核验子代理最关键的一条主张：

  内核 `CardInstance.CanOperateThisTurn` 的注释里，用 310284 回放的
  「m20_scout_car / 7_schutzen / sd_kfz_10_38 出牌同回合就行动」来论证
  **「本内核故意不做召唤失调」**。

  子代理说：这三张卡 **hasBlitz 全是 True** —— 所以它们恰恰是
  「Blitz 例外」的**正面证据**，不是召唤失调不存在的证据。

这条如果成立，内核那段注释就是错的，而且召唤失调必须补上。
"""
import json

FULL = r"out\cards-full2.json"
LIVE = r"klink bot\docs\cards.live.json"
KARDSIM = r"ref\kards-sim\cards.json"

full = {c.get("id"): (c.get("raw") or {}) for c in json.load(open(FULL, encoding="utf-8"))}
live = json.load(open(LIVE, encoding="utf-8"))
ks = {c.get("id"): c for c in json.load(open(KARDSIM, encoding="utf-8"))}

SUSPECTS = ["card_unit_m20_scout_car", "card_unit_7_schutzen", "card_unit_sd_kfz_10_38",
            "card_unit_stug_iii"]

print("=" * 76)
print("内核注释用来论证「没有召唤失调」的三张卡，到底有没有 Blitz")
print("=" * 76)
for n in SUSPECTS:
    raw = full.get(n) or {}
    hb_raw = next((v for k, v in raw.items() if k.lower() == "hasblitz"), "（无此字段）")
    hb_live = (live.get(n) or {}).get("hasBlitz", "（无此字段）")
    k = ks.get(n) or {}
    kws = k.get("keywords") or k.get("Keywords") or ""
    print(f"  {n}")
    print(f"      raw CDO  hasBlitz = {hb_raw}")
    print(f"      我的卡库 hasBlitz = {hb_live}")
    print(f"      kardsim  keywords = {kws}")

print()
print("=" * 76)
print("全卡池：有多少单位有 hasBlitz（Blitz 是召唤失调的例外）")
print("=" * 76)
units = [n for n in full if n.startswith("card_unit_")]
blitz = [n for n in units if str(next((v for k, v in (full[n] or {}).items()
                                       if k.lower() == "hasblitz"), "")).lower() == "true"]
print(f"  单位总数 {len(units)}，其中 hasBlitz=True 的 {len(blitz)} 个"
      f"（{100*len(blitz)/max(1,len(units)):.0f}%）")
