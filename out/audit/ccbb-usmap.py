#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 10：usmap 里 BaseCardObject.Location 的类型。"""
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = r"<repo-root>"
p = os.path.join(ROOT, r"klink bot\kards1.60_No_UE4SS.jmap")

try:
    j = json.load(open(p, encoding="utf-8"))
except Exception as ex:
    print("json 解析失败:", ex)
    txt = open(p, encoding="utf-8", errors="replace").read()
    for key in ('"name": "BaseCardObject"',):
        i = txt.find(key)
        print(txt[i:i + 1500] if i >= 0 else "not found")
    sys.exit()

print("top-level keys:", list(j.keys())[:10] if isinstance(j, dict) else type(j))


def find(o, path=""):
    if isinstance(o, dict):
        nm = o.get("name")
        if nm == "Location":
            print("HIT", path, json.dumps(o, ensure_ascii=False)[:400])
        for k, v in o.items():
            find(v, path + "/" + str(k))
    elif isinstance(o, list):
        for i, v in enumerate(o):
            find(v, path + f"[{i}]")


find(j)
