#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 9：17 个调用点的实参 + 守位分支。"""
import importlib.util
import json
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
spec = importlib.util.spec_from_file_location("cc", r"<repo-root>\out\audit\ccbb-decode.py")
m = importlib.util.module_from_spec(spec)
sys.argv = ["x", "none"]
spec.loader.exec_module(m)


def argnames(s):
    out = []
    for p in s.get("Parameters") or []:
        if isinstance(p, dict):
            out.append(p.get("Variable Name") or p.get("Inst"))
    return out


rows = []
for fn, s, n1, n2 in m.callers("CanCardBeBuffed"):
    rows.append((fn, s["StatementIndex"], argnames(s), n1, n2))

for fn, si, args, n1, n2 in rows:
    tgt = args[0] if args else "?"
    out = args[1] if len(args) > 1 else "?"
    nxt = f"si={n1['StatementIndex']} {m.brief(n1)}" if n1 else "-"
    print(f"{fn:28s} si={si:<5} CanCardBeBuffed({tgt} -> {out})   守位: {nxt}")
print()
print("总调用点:", len(rows))
