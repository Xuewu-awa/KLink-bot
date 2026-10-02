"""
查 `CS` 到底是什么动作。

线索：
  · 全 6 局里出现 21 次，**从没映射过**（一直当"未处理"跳过）
  · 989040 在**第一次 HQ 分歧之前**，它是唯一没应用的动作
  · 206428 的分歧点完全一样（T5 左方回合开始，右方 HQ 少 1 点）

所以 CS 很可能是个**有后果的真实动作**，跳过它就漏掉了效果（比如对 HQ 的伤害）。

这里把全部 CS 摊开看模式：
  1. action_data 的键位长什么样
  2. 它前面一条动作是什么（是不是总跟在某个 PC 后面）
  3. 它引用的 cardID 能不能解析出卡名
  4. 和别的动作类型对比键位
"""
import json
import glob
import os
from collections import Counter

REP = r"klink bot\docs\fresh-replays"

rows = []
for f in sorted(glob.glob(os.path.join(REP, "replay-*.actions.json"))):
    if f.endswith(".json") and "actions" not in f:
        continue
    rid = os.path.basename(f).replace("replay-", "").replace(".actions.json", "")
    raw = json.load(open(f, encoding="utf-8"))
    # 结构可能是 list，也可能是 {"actions": [...]}
    acts = raw if isinstance(raw, list) else (raw.get("actions") or raw.get("Actions") or [])
    if not acts:
        print(f"  ⚠ {rid}: 读不出动作列表，顶层键 = {list(raw.keys()) if isinstance(raw, dict) else type(raw)}")
        continue
    for i, a in enumerate(acts):
        rows.append((rid, i, a, acts[i - 1] if i > 0 else None))

cs = [(r, i, a, p) for (r, i, a, p) in rows if a.get("action_type") == "CS"]
print(f"全部动作 {len(rows)} 条，其中 CS {len(cs)} 条\n")

print("=" * 78)
print("① 所有 CS 的 action_data")
print("=" * 78)
for rid, i, a, prev in cs:
    d = a.get("action_data") or {}
    keys = sorted(d.keys(), key=lambda x: (len(x), x))
    kv = " ".join(f"{k}={d[k]}" for k in keys)
    print(f"  {rid} #{a['action_id']:<4} T{a.get('turn_number')}  {kv}")

print()
print("=" * 78)
print("② CS 前面一条是什么动作")
print("=" * 78)
prevtypes = Counter(p.get("action_type") if p else "（无）" for _, _, _, p in cs)
for t, n in prevtypes.most_common():
    print(f"  CS 前一条 = {t:<24} ×{n}")

print()
print("=" * 78)
print("③ CS 的 action_data 键位集合 vs 其他动作")
print("=" * 78)
bykey = {}
for rid, i, a, p in rows:
    t = a.get("action_type")
    d = a.get("action_data") or {}
    bykey.setdefault(t, Counter()).update(d.keys())
for t in sorted(bykey):
    ks = sorted(bykey[t].keys(), key=lambda x: (len(x), x))
    print(f"  {t:<24} 键 {ks}")

print()
print("=" * 78)
print("④ CS 引用的 cardID 能不能解出卡名（用该局回放的起始数据）")
print("=" * 78)
for rid in sorted(set(r for r, *_ in cs)):
    p = os.path.join(REP, f"replay-{rid}.json")
    try:
        rep = json.load(open(p, encoding="utf-8"))
        sd = rep["starting_info"]["match_and_starting_data"]["starting_data"]
    except Exception:
        continue
    id2name = {}
    for key in ("starting_hand_left", "starting_hand_right", "deck_left", "deck_right"):
        for c in sd.get(key) or []:
            id2name[c.get("card_id")] = c.get("name")
    mine = [(a, prev) for r, i, a, prev in cs if r == rid]
    for a, prev in mine[:6]:
        d = a.get("action_data") or {}
        cid = d.get("0")
        try:
            cid = int(cid)
        except (TypeError, ValueError):
            pass
        nm = id2name.get(cid, "（不在起始数据里——局中生成的卡？）")
        pt = (prev or {}).get("action_type")
        pd = (prev or {}).get("action_data") or {}
        print(f"  {rid} #{a['action_id']:<4} CS cardID={cid} → {nm}")
        print(f"        前一条: {pt} {pd}")
