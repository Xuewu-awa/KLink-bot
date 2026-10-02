#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 6：CanCardBeBuffed 的影响面。

凡是调用「内部带 CanCardBeBuffed 守位」那 17 个函数的卡，都受影响。
"""
import json
import re
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = r"<repo-root>"
IR = json.load(open(ROOT + r"\klink bot\docs\card-ir.json", encoding="utf-8"))

GUARDED = [
    "ChangeAttack", "ChangeDefense", "ChangeKreditCost", "CustomAbilityAdd",
    "GiveAlpine", "GiveAlpineBonus", "GiveAmbush", "GiveBlitz", "GiveBond",
    "GiveFury", "GiveGuard", "GiveImmune", "GiveMobilize", "GiveRandomCombatKeyword",
    "GiveSalvage", "GiveShock", "GiveSmokescreen",
]

print("===== 每个守位函数的调用卡数 =====")
hit_cards = Counter()
per_fn = {}
for fn in GUARDED:
    cards = set()
    for card, body in IR.items():
        for s in body.get("steps") or []:
            if s.get("op") == "call" and s.get("fn") == fn:
                cards.add(card)
    per_fn[fn] = cards
    for c in cards:
        hit_cards[c] += 1
    print(f"  {fn:26s} {len(cards):4d} 张卡")

print()
print(f"===== 受影响卡合计：{len(hit_cards)} 张 =====")
for c, n in sorted(hit_cards.items(), key=lambda kv: -kv[1]):
    print(f"  {n:3d}  {c}")

print()
print("===== 内核实现情况 =====")
DISP = open(ROOT + r"\src\KLink.Bot\Effects\CardApiDispatch.cs", encoding="utf-8").read()
KEYS = set(re.findall(r'\["([A-Za-z0-9_ .:]+)"\]\s*=', DISP))
for fn in GUARDED:
    print(f"  {fn:26s} 派发表里{'有' if fn in KEYS else '**没有**'}")
