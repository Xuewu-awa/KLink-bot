"""
查「每条线几格」的权威值。

我上一轮从快照看到 location=7 的槽位只到 3（同时最多 4 张），就下了「前线 4 格」的结论。
但那只说明**没观测到 5 张**，不是证明。用户指出前线应是 5 格。

权威来源优先：
  1. BP_Logic 反编译产物（out/bp-logic.json）里的行容量相关函数/常量
  2. 快照的槽位分布（自己半场 loc 5/6 出现到槽位 4 → 5 个位置，HQ 占 0）
  3. kardsim 的 Rules 常量（它注释说取自 BP_Logic CDO）
"""
import json
import re
from collections import Counter
import os

print("=" * 74)
print("1) BP_Logic 里和「行容量 / 数量上限」有关的东西")
print("=" * 74)
raw = open(r"out\bp-logic.json", encoding="utf-8").read()
names = sorted(set(re.findall(r'"(?:name|Name|FunctionName)"\s*:\s*"([^"]+)"', raw)))
PAT = re.compile(r"max|row|line|capacity|slot|board|full|count|limit", re.I)
hit = [n for n in names if PAT.search(n)]
print(f"  命中 {len(hit)} 个：")
for n in hit[:40]:
    print(f"    {n}")

print()
print("=" * 74)
print("2) 真实快照的槽位分布（每侧、每个 location）")
print("=" * 74)
CAP = r"<user-home>\AppData\Local\Temp\klink-capture"
FILES = ["snapshot-match51.jsonl", "snapshot-match28.jsonl", "snapshot-match40.jsonl",
         "snapshot-match19.jsonl", "snapshot-match64.jsonl", "snapshot-match82.jsonl"]
slot = {}
simul = {}          # location -> 同时最多几张
for fn in FILES:
    p = os.path.join(CAP, fn)
    if not os.path.exists(p):
        continue
    for line in open(p, encoding="utf-8"):
        if not line.strip():
            continue
        try:
            r = json.loads(line)
        except json.JSONDecodeError:
            continue
        per_loc = Counter()
        for c in r["cards"]:
            loc = c.get("location")
            ln = c.get("locationNumber")
            if loc is None or ln is None or not str(ln).isdigit():
                continue
            slot.setdefault(loc, Counter())[ln] += 1
            per_loc[loc] += 1
        for loc, n in per_loc.items():
            if n > simul.get(loc, 0):
                simul[loc] = n

NAME = {"1": "左牌库", "2": "右牌库", "3": "左手", "4": "右手",
        "5": "左半场(HQ+单位)", "6": "右半场(HQ+单位)", "7": "前线(共享)", "8": "弃牌堆"}
for loc in sorted(slot):
    slots = sorted(slot[loc].items(), key=lambda x: int(x[0]))
    nums = [int(k) for k, _ in slots]
    print(f"  loc={loc:<2} {NAME.get(loc,'?'):<16} 槽位 {min(nums)}..{max(nums)}"
          f"（共 {len(nums)} 个不同槽位）  同时最多 {simul.get(loc,0)} 张")
    print(f"        分布 {dict(slots)}")
