#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 2：Alpine 链路。

1. 谁调用 GiveAlpineBonus / GiveAlpine / RemoveAlpine（在 BP_CardFunctions 内部）
2. 我们的 IR（card-ir.json）里哪些卡涉及 alpine
3. 那些卡调用 GiveAlpineBonus 时传的是什么（在场？手牌？）
"""
import json
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = r"<repo-root>"
D = json.load(open(ROOT + r"\out\bp-cardfn.json", encoding="utf-8"))
IR = json.load(open(ROOT + r"\klink bot\docs\card-ir.json", encoding="utf-8"))

TARGETS = ["GiveAlpineBonus", "GiveAlpine", "RemoveAlpine", "getHasAlpine", "GiveRandomCombatKeyword"]


def _is_call_to(s, target):
    if s.get("Inst") == "LocalVirtualFunction":
        return s.get("FunctionName") == target
    if s.get("Inst") == "Context":
        e = s.get("Expression") or {}
        return e.get("Function") == target or e.get("FunctionName") == target
    if s.get("Inst") == "FinalFunction":
        return s.get("Function") == target
    return False


def bp_callers(target):
    out = []
    for fn in D:
        body = D[fn]
        bc = body.get("bytecode") if isinstance(body, dict) else body
        if not bc:
            continue
        bc = sorted([s for s in bc if s.get("StatementIndex") is not None],
                    key=lambda s: s["StatementIndex"])
        for i, s in enumerate(bc):
            if _is_call_to(s, target):
                nxt = bc[i + 1] if i + 1 < len(bc) else None
                out.append((fn, s["StatementIndex"], nxt))
    return out


def ir_callers(target):
    hits = Counter()
    for card, body in IR.items():
        for s in body.get("steps") or []:
            if s.get("op") == "call" and s.get("fn") == target:
                hits[card] += 1
    return hits


if __name__ == "__main__":
    for t in TARGETS:
        print(f"===== BP_CardFunctions 内部谁调用 {t} =====")
        got = bp_callers(t)
        if not got:
            print("  （没有内部调用者 —— 说明它只被卡自己的蓝图 / 外部资产调用）")
        for fn, si, nxt in got:
            extra = ""
            if nxt is not None:
                extra = f"   下一条 si={nxt['StatementIndex']} {nxt.get('Inst')}"
            print(f"  {fn}  si={si}{extra}")
        print()

    print("===== card-ir.json 里调用 alpine 相关函数的卡 =====")
    for t in ("GiveAlpine", "RemoveAlpine", "GiveAlpineBonus", "getHasAlpine"):
        h = ir_callers(t)
        print(f"  {t}: {len(h)} 张卡  {sorted(h)[:40]}")
    print()

    # 含 hasAlpine 关键字（初始自带）的卡
    n = 0
    names = []
    for card, body in IR.items():
        txt = json.dumps(body, ensure_ascii=False)
        if "alpine" in txt.lower():
            n += 1
            names.append(card)
    print(f"===== IR 里出现 alpine 字样的卡：{n} 张 =====")
    for x in sorted(names):
        print("   ", x)
