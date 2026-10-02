#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 4：SpawnCardToBoard 里 location 是什么时候被设成"在场"的。"""
import importlib.util
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
spec = importlib.util.spec_from_file_location("cc", r"<repo-root>\out\audit\ccbb-decode.py")
m = importlib.util.module_from_spec(spec)
_ARGV = list(sys.argv[1:])
sys.argv = ["x", "none"]
spec.loader.exec_module(m)

ARGS = [a for a in _ARGV if a != "none"]
fn = ARGS[0] if ARGS else "SpawnCardToBoard"
lo = int(ARGS[1]) if len(ARGS) > 1 else 0
hi = int(ARGS[2]) if len(ARGS) > 2 else 10 ** 9
for s in sorted(m.D[fn]["bytecode"], key=lambda x: x["StatementIndex"]):
    if lo <= s["StatementIndex"] <= hi:
        print(f"  si={s['StatementIndex']:<6} {m.brief(s)}")
