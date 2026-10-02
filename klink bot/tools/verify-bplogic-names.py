"""核验：我在 §①.7 里列的规则函数名，哪些真存在、哪些是我猜的。"""
import re

raw = open(r"out\bp-logic.json", encoding="utf-8").read()
names = set(re.findall(r'"(?:name|Name|FunctionName)"\s*:\s*"([^"]+)"', raw))
print(f"BP_Logic 里实际出现的名字 {len(names)} 个\n")

MINE = [
    "CreateStartingHandCards", "CanSideDrawCards", "FirstTurn", "CanSideGainKreditSlots",
    "CreateAction_StartOfTurn_Start", "CreateAction_StartOfTurn_End", "ExecuteStartOfTurnEvents",
    "DrawTopCardFromDeck", "DoApplyMulligan", "HandleMulliganResponse", "BothPlayersDoneWithMulligan",
    "CanPlayCardFromHand", "KreditCheckAndAutoBanIfNeeded", "GetTurnNumber",
]
ok = bad = 0
for n in MINE:
    if n in names:
        ok += 1
        print(f"  {n:<38} ✓ 存在")
    else:
        bad += 1
        print(f"  {n:<38} ✗ 不存在（我猜的）")
print(f"\n  存在 {ok} 个 / 我猜错 {bad} 个")

# 顺便看看有没有名字相近的真函数
print("\n=== 含有 Turn / Draw / Kredit 的真实函数名（供纠正）===")
for n in sorted(names):
    if re.search(r"turn|draw|kredit", n, re.I):
        print(f"    {n}")
