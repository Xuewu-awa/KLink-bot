#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 11：证明 uasset 名字表里的 Location / location 是同一个 FName
（UE 的 FName 比较哈希大小写无关，只有 display 哈希不同）。"""
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
p = r"<repo-root>\klink bot\live\Cards\kards\Content\Blueprints\Cards\BP_CardFunctions.uasset"
b = open(p, "rb").read()

for m in re.finditer(rb"Location\x00|\x00location\x00", b):
    s = max(0, m.start() - 12)
    e = min(len(b), m.end() + 12)
    print(f"pos={m.start():>7}  {b[s:e]!r}")

print()
print("--- 判读 ---")
for m in re.finditer(rb"\x09\x00\x00\x00(Location|location)\x00", b):
    tail = b[m.end():m.end() + 4]
    print(f"  len=9  name={m.group(1).decode():9s}  紧随 4 字节 = {tail.hex(' ')}")
