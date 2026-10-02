#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 3：打印 GiveAlpineBonus 各调用点的上下文（前后语句）。"""
import importlib.util
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
spec = importlib.util.spec_from_file_location("cc", r"<repo-root>\out\audit\ccbb-decode.py")
m = importlib.util.module_from_spec(spec)
sys.argv = ["x", "none"]
spec.loader.exec_module(m)

SITES = [
    ("SpawnCardToBoard", 872),
    ("SpawnMultipleCardsOnBattlefield", 2968),
    ("PlayCardFromHand", 2207),
    ("PlayCardDirectlyFromHand", 3053),
    ("AfterWaitCardPlayFromHand", 974),
]

for fn, si in SITES:
    bc = sorted(m.D[fn]["bytecode"], key=lambda x: x["StatementIndex"])
    idx = [i for i, s in enumerate(bc) if s["StatementIndex"] == si][0]
    print(f"##### {fn}  调用点 si={si}  (上下文 {bc[max(0,idx-6)]['StatementIndex']}..{bc[min(len(bc)-1,idx+6)]['StatementIndex']})")
    for s in bc[max(0, idx - 6): idx + 7]:
        mark = " <<<" if s["StatementIndex"] == si else ""
        print(f"  si={s['StatementIndex']:<6} {m.brief(s)}{mark}")
    print()
