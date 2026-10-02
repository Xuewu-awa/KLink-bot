"""验证假设：GetPlayFromHandDamage 是不是读 BaseCardObject.effectDamage。

线索：BaseCardObject.h 里有 `int32 effectDamage;`，而英联邦的卡面是「造成 20 点伤害」。
如果它的 effectDamage = 20，那这个函数基本就是读这个字段。
"""
import json

d = json.load(open(r"out/cards-full2.json", encoding="utf-8"))
if isinstance(d, list):
    d = {c.get("id"): c for c in d}

eff = json.load(open(r"klink bot/docs/card-effects.json", encoding="utf-8"))
if isinstance(eff, list):
    eff = {c.get("id") or c.get("asset", "").split("/")[-1]: c for c in eff}

# 找出所有调用 GetPlayFromHandDamage 的卡
users = [k for k, c in eff.items() if "GetPlayFromHandDamage" in (c.get("calls") or [])]
print(f"用到 GetPlayFromHandDamage 的卡: {len(users)} 张\n")

print("=" * 78)
print("这些卡的 effectDamage 字段 vs 卡面写的伤害数字")
print("=" * 78)
import re
for k in users[:25]:
    raw = (d.get(k) or {}).get("raw") or {}
    ed = raw.get("effectDamage")
    text = (eff.get(k) or {}).get("text") or ""
    title = (eff.get(k) or {}).get("title") or k
    # 从卡面里抠出「Deal N damage」
    m = re.search(r"[Dd]eal (\d+) damage", text)
    printed = m.group(1) if m else "-"
    flag = ""
    if ed is not None and printed != "-":
        flag = "  ✅" if str(ed) == printed else f"  ⚠ 卡面 {printed}"
    print(f"  effectDamage={str(ed):<5} 卡面={printed:<4} {title[:34]:<36}{flag}")

print()
print("=" * 78)
print("统计")
print("=" * 78)
vals = []
for k in users:
    raw = (d.get(k) or {}).get("raw") or {}
    ed = raw.get("effectDamage")
    if isinstance(ed, int):
        vals.append(ed)
from collections import Counter
print(f"  有 effectDamage 整数值的: {len(vals)}/{len(users)}")
print(f"  取值分布: {dict(sorted(Counter(vals).items()))}")
