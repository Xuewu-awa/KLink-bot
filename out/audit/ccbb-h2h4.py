#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 7：假设 2（同名不同义）与假设 4（服务端/客户端分工）。

(a) 两个 usmap（1.60 的 .jmap / 5.6.1 的 .jmap）里 CanCardBeBuffed 挂在哪个类上
(b) BP_CardFunctions 里有没有 ZAction*/Notify* 与 alpine 相关
(c) CardInnateTable 里 Alpine 卡有多少张
"""
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = r"<repo-root>"

print("===== (a) usmap 里 CanCardBeBuffed 的宿主 =====")
for jm in (r"klink bot\kards1.60_No_UE4SS.jmap",
           r"klink bot\kards-5.6.1-0+++UE5+Release-5.6-Fork-unknown.jmap"):
    p = os.path.join(ROOT, jm)
    if not os.path.exists(p):
        print(f"  {jm}: 不存在")
        continue
    txt = open(p, encoding="utf-8", errors="replace").read()
    print(f"  --- {os.path.basename(jm)} (size={len(txt)}) ---")
    for m in re.finditer(r"CanCardBeBuffed", txt):
        s = txt.rfind('"', 0, m.start() - 1)
        # 往前找最近的类名
        head = txt[:m.start()]
        cls = None
        for cm in re.finditer(r'"name":\s*"([A-Za-z0-9_]+)"', head):
            cls = cm.group(1)
        print(f"     pos={m.start():>9}  最近的 name= {cls}   上下文={txt[m.start()-60:m.end()+60]!r}")

print()
print("===== (b) BP_CardFunctions 里 alpine 相关的 ZAction*/Notify* =====")
d = json.load(open(ROOT + r"\out\bp-cardfn.json", encoding="utf-8"))
names = set()
for fn, body in d.items():
    for s in (body.get("bytecode") or []):
        for k in ("FunctionName", "Function"):
            v = s.get(k)
            if isinstance(v, str):
                names.add(v)
        e = s.get("Expression")
        if isinstance(e, dict) and isinstance(e.get("Function"), str):
            names.add(e["Function"])
z = sorted(n for n in names if n.startswith("ZAction") or n.startswith("Notify"))
print(f"  ZAction*/Notify* 共 {len(z)} 个：")
for n in z:
    print("   ", n)
print("  含 alpine 的:", [n for n in z if "lpine" in n] or "（无）")

print()
print("===== (c) 内核 CardInnateTable 里的 Alpine 卡 =====")
p = os.path.join(ROOT, r"src\KLink.Bot\Cards\CardInnateTable.cs")
txt = open(p, encoding="utf-8").read()
cards = re.findall(r'\["([^"]+)"\]\s*=\s*\(\[([^\]]*)\]', txt)
alp = [(c, k) for c, k in cards if "Alpine" in k]
print(f"  自带 Alpine 的卡：{len(alp)} 张")
for c, k in alp:
    print(f"   {c:44s} {k}")
