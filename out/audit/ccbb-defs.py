#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 5：
 (a) bp-cardfn.json 里所有 RValuePropertyName 的 casing 统计（证明 location/Location 同一属性）
 (b) 全仓搜 CanCardBeBuffed 的定义处（假设 2：同名不同义）
 (c) 卡数据库里 hasAlpine 的卡有多少张
"""
import json
import os
import re
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = r"<repo-root>"

print("===== (a) bp-cardfn.json RValuePropertyName casing =====")
d = json.load(open(ROOT + r"\out\bp-cardfn.json", encoding="utf-8"))
c = Counter()
for fn, body in d.items():
    for s in (body.get("bytecode") or []):
        t = json.dumps(s)
        for mm in re.finditer(r'"RValuePropertyName": "([^"]+)"', t):
            c[mm.group(1)] += 1
for k, v in c.most_common(60):
    print(f"  {v:6d}  {k}")

print()
print("===== (b) CanCardBeBuffed 出现过的文件 =====")
NEEDLE = b"CanCardBeBuffed"
for base, dirs, files in os.walk(ROOT):
    if any(x in base for x in ("\\.git", "\\obj\\", "\\bin\\", "\\node_modules")):
        continue
    for f in files:
        p = os.path.join(base, f)
        try:
            if os.path.getsize(p) > 200 * 1024 * 1024:
                continue
            with open(p, "rb") as fh:
                b = fh.read()
        except Exception:
            continue
        n = b.count(NEEDLE)
        if n:
            print(f"  {n:5d}  {os.path.relpath(p, ROOT)}")

print()
print("===== (c) 卡数据里 hasAlpine 的卡 =====")
for cand in ("out/cards-full.json", "out/cards-full2.json", "klink bot/docs/cards.json",
             "out/bp-cardscheck.json"):
    p = os.path.join(ROOT, cand.replace("/", os.sep))
    if not os.path.exists(p):
        continue
    txt = open(p, encoding="utf-8", errors="replace").read()
    n = txt.count("hasAlpine")
    print(f"  {cand}: hasAlpine 出现 {n} 次")
    if n and n < 200:
        try:
            j = json.loads(txt)
        except Exception:
            continue
        names = []

        def walk(o):
            if isinstance(o, dict):
                if o.get("hasAlpine") in (True, "True", 1):
                    names.append(o.get("name") or o.get("cardName") or o.get("id") or "?")
                for v in o.values():
                    walk(v)
            elif isinstance(o, list):
                for v in o:
                    walk(v)

        walk(j)
        print(f"     hasAlpine=true 的条目 {len(names)} 个: {sorted(set(names))[:40]}")
