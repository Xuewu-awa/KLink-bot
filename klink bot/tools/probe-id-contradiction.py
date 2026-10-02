"""
决定性检查：`card_event_colossus` 在「快照」和「回放」里各是什么编号、各在哪个位置。

矛盾现场（206428，act=2 = 全场第一个出牌动作）：
    快照说左手是 {19,9,22,31,12}（开局断言已验证身份正确）
    回放说左手打出 id=24 card_event_colossus
同一时刻两个来源不可能都对。把这张卡在两边的 (cardID, location) 摆出来，
就能一次看清是**配对错**还是**编号映射错**。
"""
import json
import os
import re

CAP = r"<user-home>\AppData\Local\Temp\klink-capture\snapshot-match51.jsonl"
REP = r"klink bot\docs\fresh-replays\replay-206428.json"
TARGET = "card_event_colossus"


def scan(obj, path, out):
    """收集所有「同时有名字和数字 id」的条目，记录它们的 JSON 路径。"""
    if isinstance(obj, dict):
        flat = {k: v for k, v in obj.items() if isinstance(v, (str, int))}
        txt = json.dumps(flat, ensure_ascii=False)
        if TARGET in txt and re.search(r'"(card_?id|id|ID)"', txt):
            out.append((path, flat))
        for k, v in obj.items():
            scan(v, f"{path}.{k}", out)
    elif isinstance(obj, list):
        for i, v in enumerate(obj[:400]):
            scan(v, f"{path}[{i}]", out)


print("=" * 70)
print("【快照】act=1 里的", TARGET)
print("=" * 70)
row = None
for line in open(CAP, encoding="utf-8"):
    if line.strip():
        r = json.loads(line)
        if r["act"] == 1:
            row = r
            break
for c in row["cards"]:
    if c.get("Name") == TARGET:
        print(f"  cardID={c.get('cardID'):<4} location={c.get('location'):<3} "
              f"locationNumber={c.get('locationNumber'):<3}")
print(f"  （快照 act=1 共 {row['count']} 张卡）")

print()
print("=" * 70)
print("【回放】起始数据里的", TARGET)
print("=" * 70)
rep = json.load(open(REP, encoding="utf-8"))
hits = []
scan(rep, "$", hits)
seen = set()
for path, flat in hits:
    key = json.dumps(flat, ensure_ascii=False, sort_keys=True)
    if key in seen:
        continue
    seen.add(key)
    print(f"  {path}")
    print(f"      {key[:200]}")

if not hits:
    print("  没找到 —— 打印根结构键名以便定位：")
    print("   ", list(rep.keys()))
    for k in rep:
        v = rep[k]
        if isinstance(v, dict):
            print(f"    {k}: {list(v.keys())}")
