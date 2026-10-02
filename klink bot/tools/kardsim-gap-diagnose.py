"""
诊断 kardsim 卡库缺口到底是「真缺」还是「命名不一样」。

两条判据：
  1. 名称归一化之后能否配对（我的 card unit_royal_sussex vs 他的 ..._regiment）
  2. 按**资产目录**统计覆盖率 —— 如果某个目录（比如 OceaniaStorm）在他的卡库里
     一张都没有，那就是整套没被抽取，而不是零散漏卡

结论决定修法：
  · 命名差异 → 加一张别名表就行，游戏逻辑本来就在
  · 整套缺失 → 要按 sim 的思路重新抽取 + 转译那一批资产
"""
import json
import os
import re
import collections

mine = json.load(open(r"klink bot\docs\cards.live.json", encoding="utf-8"))
theirs = json.load(open(r"ref\kards-sim\cards.json", encoding="utf-8"))
effects = json.load(open(r"klink bot\docs\card-effects.json", encoding="utf-8"))


def norm(s):
    return re.sub(r"[^a-z0-9]", "", (s or "").lower())


m = set(mine.keys()) if isinstance(mine, dict) else {c.get("id") for c in mine}
t = {c.get("id") for c in theirs}
missing = sorted(x for x in (m - t) if x)

# ---- 1. 归一化配对 ----
t_by_norm = collections.defaultdict(list)
for x in t:
    t_by_norm[norm(x)].append(x)

alias, genuine = [], []
for x in missing:
    nx = norm(x)
    cand = t_by_norm.get(nx)
    if not cand:
        # 去掉常见后缀再试
        for suf in ("regiment", "battalion", "company", "squadron", "bal", "vet"):
            if nx.endswith(suf) and t_by_norm.get(nx[: -len(suf)]):
                cand = t_by_norm[nx[: -len(suf)]]
                break
    if cand:
        alias.append((x, cand[0]))
    else:
        genuine.append(x)

print(f"我的卡库 {len(m)} / kardsim {len(t)}")
print(f"我有他没有 {len(missing)} 张：")
print(f"  · 归一化后能在他的库里找到对应: {len(alias)} 张  → 只是**命名不一样**")
print(f"  · 确实找不到:                   {len(genuine)} 张")
if alias:
    print("\n  命名差异抽样:")
    for a, b in alias[:15]:
        print(f"    {a:<44} ↔ {b}")

# ---- 2. 按资产目录统计覆盖率 ----
print("\n=== 按资产目录看覆盖率（只列我这边有、他那边一张都没有的目录）===")
folder_all = collections.Counter()
folder_missing = collections.Counter()
for name in m:
    e = effects.get(name) or {}
    asset = e.get("asset") or ""
    if not asset:
        continue
    folder = os.path.dirname(asset)
    folder_all[folder] += 1
    if name not in t:
        folder_missing[folder] += 1

fully_missing = [(f, folder_all[f]) for f in folder_all
                 if folder_missing[f] == folder_all[f] and folder_all[f] >= 2]
fully_missing.sort(key=lambda x: -x[1])
print(f"  我这边有卡、且**整目录**都没进他卡库的目录数: {len(fully_missing)}")
for f, n in fully_missing[:20]:
    print(f"    {n:3} 张  {f}")

partial = [(f, folder_missing[f], folder_all[f]) for f in folder_all
           if 0 < folder_missing[f] < folder_all[f]]
partial.sort(key=lambda x: -x[1])
print(f"\n  部分缺失的目录 TOP10（缺/总）:")
for f, miss, tot in partial[:10]:
    print(f"    {miss:3}/{tot:<3}  {f}")

# ---- 3. 那 4 张实机打出的卡属于哪 ----
print("\n=== 玩家实机打出过的 4 张 ===")
for name in ["card_event_pams", "card_event_repel_the_attack",
             "card_event_the_rock_of_gibraltar", "card_unit_2nd_west_africa"]:
    e = effects.get(name) or {}
    print(f"  {name:<38} asset={e.get('asset')}")
