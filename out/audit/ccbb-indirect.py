#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 8：BP_CardFunctions 之外的间接调用点
（哪些外部资产调用了那 17 个「内部带 CanCardBeBuffed 守位」的函数）。"""
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = r"<repo-root>"
GUARDED = {
    "ChangeAttack", "ChangeDefense", "ChangeKreditCost", "CustomAbilityAdd",
    "GiveAlpine", "GiveAlpineBonus", "GiveAmbush", "GiveBlitz", "GiveBond",
    "GiveFury", "GiveGuard", "GiveImmune", "GiveMobilize", "GiveRandomCombatKeyword",
    "GiveSalvage", "GiveShock", "GiveSmokescreen",
}

def walk_calls(o, out):
    """递归找出所有 LocalVirtualFunction / FinalFunction 调用名。"""
    if isinstance(o, dict):
        inst = o.get("Inst")
        if inst == "LocalVirtualFunction" and isinstance(o.get("FunctionName"), str):
            out.append(o["FunctionName"])
        elif inst in ("FinalFunction", "Function") and isinstance(o.get("Function"), str):
            out.append(o["Function"])
        for v in o.values():
            walk_calls(v, out)
    elif isinstance(o, list):
        for v in o:
            walk_calls(v, out)


for f in ("bp-onlinematch.json", "bp-notifier.json", "bp-logic.json", "bp-gamestate.json",
          "bp-matchcontroller.json", "bp-cardscheck.json"):
    p = os.path.join(ROOT, "out", f)
    if not os.path.exists(p):
        continue
    d = json.load(open(p, encoding="utf-8"))
    print(f"===== {f} =====")
    found = 0
    for fn, body in d.items():
        bc = body.get("bytecode") if isinstance(body, dict) else None
        if not isinstance(bc, list):
            continue
        for s in bc:
            names = []
            walk_calls(s, names)
            for nm in names:
                if nm in GUARDED:
                    print(f"  {fn}  si={s.get('StatementIndex')}  -> {nm}")
                    found += 1
    if not found:
        print("  （无）")
