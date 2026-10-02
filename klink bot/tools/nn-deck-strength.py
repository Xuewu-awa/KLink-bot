"""
「A 对位基线为什么没掉下去」的最后一环：**这套卡池本身就不平衡**。

随机化之后每个有序对位只剩 ~21 局，但仍然能靠「认出是哪一对卡组」拿到 75% ——
因为 22 套卡组的实测胜率跨度是 17% ~ 88.5%（随机对手 + 随机左右）。
本脚本检查一个具体假设：**弱卡组之所以弱，是因为它们的卡在这个内核里没实现完**
（未实现的调用会被 <c>CardApi</c> 记进 UnimplementedCalls，卡就退化成白板）。
若假设成立，则「对位先验」本质上是「哪套牌的卡没实现」这个**仿真器缺陷**的代理，
不是真实 KARDS 的强弱关系 —— 这一点必须写清楚。

做法（**静态**，不需要跑对局）：
  1. 从 src/KLink.Bot/Cards/MetaDecks.cs 读 22 套卡组码（顺序 = 数据里的卡组下标）
  2. 用 analyze-decks.py 同一套解析逻辑展开成卡名
  3. 每张卡的 external_calls（card-effects.json）逐个判「内核实现了吗」：
     判据 = 该调用名是否作为**字符串字面量**出现在 src/KLink.Bot/**.cs 里
     （⚠️ 这是个**代理判据**，不是运行时真值；会低估「靠通用路径实现」的调用）
  4. 与实测胜率对照

用法：python -X utf8 "klink bot/tools/nn-deck-strength.py"
"""

import json
import re
import struct
import sys
from collections import Counter
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                       # klink bot/
REPO = ROOT.parent                       # 仓库根

DATA = REPO / "out/nn-data.bin"
MANIFEST = REPO / "out/nn-data.bin.decks.json"
METADECKS = REPO / "src/KLink.Bot/Cards/MetaDecks.cs"
SRC = REPO / "src/KLink.Bot"
DECK_IDS = REPO / "src/KLink.Server/Assets/kards-server/deck_code_ids.json"
EFFECTS = ROOT / "docs/card-effects.json"

MULTIPLIERS = (1, 2, 3, 4)
MAGIC = 0x314C4B41


def parse_deck_code(code):
    body = code.strip()[2:]
    country, cards = body.split("|")[0], body.split("|")[1]
    if "~" in cards:
        cards = cards[:cards.index("~")]
    counts = Counter()
    for gi, group in enumerate(cards.split(";")):
        if gi >= len(MULTIPLIERS):
            break
        for j in range(0, len(group) - 1, 2):
            counts[group[j:j + 2]] += MULTIPLIERS[gi]
    return counts


def main():
    # ---- 1. 卡组码（顺序 = 数据里的卡组下标）----
    txt = METADECKS.read_text(encoding="utf-8")
    decks = re.findall(r'\("([^"]+)",\s*"([^"]+)"\)', txt)
    assert len(decks) == 22, f"MetaDecks 里读到 {len(decks)} 套，期望 22"

    # ---- 2. 实现的调用名集合（代理判据：源码里的字符串字面量）----
    literals = set()
    src_bytes = 0
    for p in SRC.rglob("*.cs"):
        s = p.read_text(encoding="utf-8", errors="ignore")
        src_bytes += len(s)
        literals.update(re.findall(r'"([^"\\\n]{2,80})"', s))
    print(f"扫描 {SRC}：{len(literals):,} 个字符串字面量，{src_bytes/1024:.0f} KB 源码")

    ids = json.loads(DECK_IDS.read_text(encoding="utf-8-sig"))
    effects = json.loads(EFFECTS.read_text(encoding="utf-8"))

    # ---- 3. 实测每套卡组的胜率（从数据 + 清单重算）----
    size = DATA.stat().st_size
    with open(DATA, "rb") as f:
        magic, dim = struct.unpack("<ii", f.read(8))
    assert magic == MAGIC
    REC_F = dim + 2
    n = (size - 8) // (REC_F * 4)
    rec = np.fromfile(DATA, dtype=np.float32, offset=8, count=n * REC_F).reshape(n, REC_F)
    y = rec[:, dim].astype(np.int8)
    gid = rec[:, dim + 1].astype(np.int64)
    man = json.load(open(MANIFEST, encoding="utf-8"))
    lab = np.zeros(10001, dtype=np.int8)
    lab[gid.astype(int)] = y
    GL = np.zeros(10000, dtype=np.int64)
    GR = np.zeros(10000, dtype=np.int64)
    for g, l, r in man["pairs"]:
        GL[g] = l
        GR[g] = r
    GY = lab[:10000].astype(np.float64)

    # ---- 4. 逐卡组：静态覆盖率 ----
    print()
    print("=" * 100)
    print("卡组强度 vs 内核实现覆盖率（静态代理判据）")
    print("=" * 100)
    print(f"  {'卡组':<13}{'实测胜率':>9}{'张数':>5}{'完全可跑的牌':>13}"
          f"{'缺调用(去重)':>13}   缺得最多的调用")
    rows = []
    for d, (name, code) in enumerate(decks):
        counts = parse_deck_code(code)
        total = 0
        ok_copies = 0
        missing = Counter()          # 调用 -> 涉及几张牌
        for iid, cnt in counts.items():
            entry = ids.get(iid)
            card = entry.get("card") if isinstance(entry, dict) else None
            if not card:
                total += cnt
                continue
            cl = (effects.get(card) or {}).get("external_calls", [])
            total += cnt
            bad = [c for c in cl if c not in literals]
            for c in bad:
                missing[c] += cnt
            if not bad:
                ok_copies += cnt
        win = float(GY[GL == d].sum() + (1 - GY)[GR == d].sum())
        tot = int((GL == d).sum() + (GR == d).sum())
        wr = win / tot
        rows.append((name, wr, total, ok_copies, len(missing),
                     missing.most_common(3)))
        top = "  ".join(f"{c}×{k}" for c, k in missing.most_common(3))
        print(f"  {name:<13}{100*wr:>8.1f}%{total:>5}{ok_copies:>8}/{total:<4}"
              f"{len(missing):>13}   {top}")

    wr = np.array([r[1] for r in rows])
    cover = np.array([r[3] / r[2] for r in rows])
    ncall = np.array([r[4] for r in rows], dtype=float)
    print()
    print(f"  实测胜率 vs 「完全可跑的牌」占比：Pearson r = {np.corrcoef(wr, cover)[0,1]:+.3f}"
          f"   Spearman 近似（对秩）r = {np.corrcoef(np.argsort(np.argsort(wr)), np.argsort(np.argsort(cover)))[0,1]:+.3f}")
    print(f"  实测胜率 vs 「缺调用个数」    ：Pearson r = {np.corrcoef(wr, ncall)[0,1]:+.3f}")
    print()
    lo = wr <= np.median(wr)
    print(f"  弱的一半（胜率 ≤ 中位 {100*np.median(wr):.1f}%）平均可跑比例 {100*cover[lo].mean():.1f}%，"
          f"平均胜率 {100*wr[lo].mean():.1f}%")
    print(f"  强的一半                              平均可跑比例 {100*cover[~lo].mean():.1f}%，"
          f"平均胜率 {100*wr[~lo].mean():.1f}%")

    # ---- 5. 换一个假设：强弱是不是「贪心策略能不能用」的代理？----
    # GreedyBot 只做三件事：按费用从高到低出牌（单位优先）→ 能杀就杀、否则打 HQ → 从不移动。
    # 于是「便宜、身材好、单位多」的牌组天然占优；靠指令（order）/combo / 后期大招的牌组打不出来。
    cv = json.loads((ROOT / "docs/card-vectors.json").read_text(encoding="utf-8"))["cards"]
    print()
    print("=" * 100)
    print("换一个假设：强弱 = 「这套牌的计划能不能被贪心策略执行」")
    print("=" * 100)
    print(f"  {'卡组':<13}{'胜率':>7}{'唯一卡':>7}{'均费':>7}{'均攻':>7}{'均防':>7}{'单位占比':>9}")
    stats = []
    for d, (name, code) in enumerate(decks):
        counts = parse_deck_code(code)
        tot = sum(counts.values())
        cost = atk = dfn = unit = 0.0
        for iid, cnt in counts.items():
            entry = ids.get(iid)
            card = entry.get("card") if isinstance(entry, dict) else None
            if not card or card not in cv:
                continue
            s = cv[card]["s"]
            cost += s[0] * 12 * cnt
            atk += s[1] * 20 * cnt
            dfn += s[2] * 20 * cnt
            unit += cnt if card.startswith("card_unit_") else 0
        stats.append((cost / tot, atk / tot, dfn / tot, unit / tot, len(counts)))
        print(f"  {name:<13}{100*rows[d][1]:>6.1f}%{len(counts):>7}{cost/tot:>7.2f}"
              f"{atk/tot:>7.2f}{dfn/tot:>7.2f}{100*unit/tot:>8.1f}%")
    st = np.array(stats)
    for i, nm in enumerate(["均费", "均攻", "均防", "单位占比", "唯一卡数"]):
        print(f"  实测胜率 vs {nm:<6} Pearson r = {np.corrcoef(wr, st[:, i])[0,1]:+.3f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
