"""
量化 kardsim 卡库相对真实客户端的缺口，并区分「能上场的卡」和「UI/教学/展示卡」。

判据：`deck_code_ids.live.json` 是从客户端导出的**卡组码 → 卡名**表，
只有真正可收集、可进卡组的卡才会在里面。所以
    在   deck_code_ids 里 → 真实可用的卡，缺了就是缺口
    不在 deck_code_ids 里 → UI / 教学 / 展示 / 变体条目，缺了无所谓

这类问题只有拿真实牌局/真实资产对拍才能发现 —— kardsim 自己的审计
（「1638 张卡强制演练 0 异常」）测的是"库里的卡都能跑"，测不出"库里少了几张"。
"""
import json

mine = json.load(open(r"klink bot\docs\cards.live.json", encoding="utf-8"))
theirs = json.load(open(r"ref\kards-sim\cards.json", encoding="utf-8"))
codes = json.load(open(r"klink bot\docs\deck_code_ids.live.json", encoding="utf-8"))

m = set(mine.keys()) if isinstance(mine, dict) else {c.get("id") for c in mine}
t = {c.get("id") for c in theirs}

# 卡组码表：值可能是卡名，也可能是列表；统一成集合
playable = set()
for v in (codes.values() if isinstance(codes, dict) else codes):
    if isinstance(v, str):
        playable.add(v)
    elif isinstance(v, list):
        playable.update(x for x in v if isinstance(x, str))

missing = sorted(x for x in (m - t) if x)
real_gap = [x for x in missing if x in playable]
other = [x for x in missing if x not in playable]

print(f"我的卡库 {len(m)} 张 / kardsim {len(t)} 张 / 卡组码表覆盖 {len(playable)} 个卡名")
print(f"我有他没有: {len(missing)} 张")
print(f"  ⚠ 其中**真实可上场**的: {len(real_gap)} 张   ← 这是真缺口")
print(f"    其余(UI/教学/展示/变体): {len(other)} 张")
print()

if real_gap:
    print("=== 真实可上场但 kardsim 卡库没有的卡 ===")
    for x in real_gap:
        d = mine.get(x, {}) if isinstance(mine, dict) else {}
        title = d.get("title") or d.get("name") or ""
        cost = d.get("kredits")
        typ = d.get("type") or ""
        print(f"  {x:<42} {str(title):<24} {typ:<10} {cost}费")
print()
print("=== 其余（抽样，确认是 UI/教学/展示类）===")
print("  " + ", ".join(other[:30]))
