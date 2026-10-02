"""核实「ApplyTheBuff 有替身，但卡调的是 ApplyBuff」这条。

对每个"卡内私有函数"名字：
  - IR 里有多少张卡**真的调用**它（call fn=<name>）
  - 派发表里有没有同名键（从 CardApiDispatch.cs 的 ["Name"] = 里 grep）
"""
import json
import pathlib
import re
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = pathlib.Path(r"<repo-root>")
IR = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))
DISPATCH = (ROOT / "src" / "KLink.Bot" / "Effects" / "CardApiDispatch.cs").read_text(encoding="utf-8")
KEYS = set(re.findall(r'\["([A-Za-z0-9_ .:]+)"\]\s*=', DISPATCH))

NAMES = [
    "ApplyTheBuff", "RemoveTheBuff", "Apply The Buff", "Remove the Buff",
    "ApplyBuff", "RemoveBuff", "ApplyAttackBuff", "RemoveAttackBuff",
    "CheckAndApplyKreditBuff", "ApplyAndCorrectBuff", "_checkAndSetBuff", "UpdateBuff",
    "updateBuffs", "clearBuffs", "checkAndUpdateBuffOnCard", "checkAndUpdateBuffOnAllCards",
    "updateCustomJsonIfNeeded", "anyOrderPlayedThisTurn", "getTwoCardsFromPossibleCards",
    "getPossibleCardsFromStaticCards", "_isBigRedOne",
    "didPlayBritishInfantryLastTurn", "isSecondOrderThisTurn", "hasPlayedOrderThisTurn",
    "isPattonPlayedThisTurn", "Random Card", "RandomChooseCards", "PickRandomCard",
    "hasGuardAdjacentUnit", "get_adjacent_unit_count", "AddPinnedOverride", "RemovePinnedOverride",
]

print(f"派发表键数: {len(KEYS)}")
print(f"{'函数名':34s} {'调用卡数':>6s} {'调用点':>6s}  派发表")
for name in NAMES:
    cards = set()
    calls = 0
    for card, body in IR.items():
        for s in body.get("steps") or []:
            if s.get("op") == "call" and s.get("fn") == name:
                cards.add(card)
                calls += 1
    where = "有" if name in KEYS else "**没有**"
    print(f"{name:34s} {len(cards):6d} {calls:6d}  {where}")
