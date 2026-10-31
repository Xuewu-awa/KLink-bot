"""按 `CardPoolTable` 的规则，在**当前（1.61）卡表**上复算候选池规模。

规则来源（`src/KLink.Bot/Cards/CardPoolTable.cs`）：
  `IsSetInPool(cardSet, includeNotAttainable)`：
     1/8/9/10/12/13/15/16/17/18/19/20 → 无条件保留
     21（OceaniaStorm）               → 仅当 OceaniaStormActive（离线看固定开关）
     2..7                             → 只有 includeNotAttainable 为真才保留
     0/11/其余                        → 无条件剔除
然后 `includeReserved=false` 时再剔除 `CardPoolTable.Reserved` 里的卡。

`Reserved` 直接从 C# 源码里解析，避免手抄 563 个名字出错。
"""
import json
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = r"E:\项目\klink-dotnet"
CARDS = ROOT + r"\rel\data\fyserver\BotData\cards.json"
POOL_TABLE = ROOT + r"\friend-main\src\KLink.Bot\Cards\CardPoolTable.cs"

CARD_SET_ID = {
    "NotAvailable": 0, "Basic": 1, "Special": 2, "OnlySpawnable": 3, "Candidate": 4,
    "Placeholder": 5, "Expansion1": 6, "Expansion2": 7, "Expansion2019": 8,
    "Campaign2020": 9, "Breakthrough": 10, "Wildcards": 11, "Legions": 12,
    "WorldAtWar": 13, "Core": 14, "BrothersInArms": 15, "WinterWar": 16,
    "CurrentExpansion": 17, "BloodAndIron": 18, "NavalWarfare": 19,
    "Homefront": 20, "OceaniaStorm": 21,
}
ALWAYS = {1, 8, 9, 10, 12, 13, 15, 16, 17, 18, 19, 20}
GATED = {2, 3, 4, 5, 6, 7}


def in_pool(card_set, include_not_attainable, oceania_active):
    cid = CARD_SET_ID.get(card_set, 0)
    if cid in ALWAYS:
        return True
    if cid == 21:
        return oceania_active
    if cid in GATED:
        return include_not_attainable
    return False


src = open(POOL_TABLE, encoding="utf-8-sig").read()
m = re.search(r"public static readonly HashSet<string> Reserved = new\(StringComparer\.Ordinal\)\s*\{(.*?)\};",
              src, re.S)
reserved = set(re.findall(r'"([^"]+)"', m.group(1))) if m else set()

cards = json.load(open(CARDS, encoding="utf-8-sig"))
print(f"当前卡表 {len(cards)} 张；CardPoolTable.Reserved {len(reserved)} 张")

for oceania in (True, False):
    for not_attainable in (False, True):
        kept = [n for n, c in cards.items()
                if in_pool(c.get("cardSet"), not_attainable, oceania)]
        after_res = [n for n in kept if n not in reserved]
        print(f"  OceaniaActive={str(oceania):5}  includeNotAttainable={str(not_attainable):5}"
              f"  → 卡集保留 {len(kept):5}  再剔预备后 {len(after_res):5}")

# 默认口径（离线：OceaniaStormActive 按 CardPoolTable 的常量；见源码）
print()
print("按卡集分布（当前卡表）：")
from collections import Counter
for k, v in Counter(c.get("cardSet") for c in cards.values()).most_common():
    print(f"   {str(k):18} {v:5}   id={CARD_SET_ID.get(k, 0)}")
