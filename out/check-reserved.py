"""核对 1.61 pak 里 `isReserved = true` 的卡，与内核冻结的 `CardPoolTable.Reserved` 是否同一批。

内核的 `Reserved` 是一张**冻结的 563 个卡名**表（来自更早的 pak）。
如果 1.61 换了预备卡名单，内核的候选池第二层过滤就会**静默错**：
该排的没排（池子变大），或者排了不该排的（池子变小）。
两种都会让「同一条随机流位置 → 抽到不同的卡」。
"""
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = r"E:\项目\klink-dotnet"
DUMP = os.path.join(ROOT, r"temp\pak-20261031\cards.all.json")
POOL_TABLE = os.path.join(ROOT, r"friend-main\src\KLink.Bot\Cards\CardPoolTable.cs")

assets = json.load(open(DUMP, encoding="utf-8-sig"))["assets"]
pak = {os.path.basename(k)[: -len(".uasset")]
       for k, v in assets.items()
       if str((v.get("cdo") or {}).get("isReserved")).lower() == "true"}

src = open(POOL_TABLE, encoding="utf-8-sig").read()
m = re.search(
    r"public static readonly HashSet<string> Reserved = new\(StringComparer\.Ordinal\)\s*\{(.*?)\};",
    src, re.S)
frozen = set(re.findall(r'"([^"]+)"', m.group(1))) if m else set()

print(f"1.61 pak 的 isReserved=True : {len(pak)} 张")
print(f"内核冻结的 Reserved 集合     : {len(frozen)} 张")
print(f"  交集 {len(pak & frozen)}   只在 pak {len(pak - frozen)}   只在内核 {len(frozen - pak)}")
if pak - frozen:
    print("  只在 pak（内核没排，池子会偏大）:", sorted(pak - frozen)[:12])
if frozen - pak:
    print("  只在内核（1.61 已不是预备卡，池子会偏小）:", sorted(frozen - pak)[:12])
