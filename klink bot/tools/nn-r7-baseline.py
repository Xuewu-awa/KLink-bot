"""
第七轮（NN 收敛后下场）：**同一份 10 万局数据上的 GreedyBot 对 GreedyBot 基准**。

为什么要重算而不是沿用第三轮的表：
  * 第三轮的卡池表（`out/_effects-deck-compare.txt`）是 **10,000 局 / v1 数据 / 引擎 cardID bug 修复之前**跑的；
  * 本轮的基准应当落在**与它同一份语料**（`out/nn-data-100k.bin`，10 万局，v2，修复之后）上，
    这样「NN 打 Greedy 的胜率」才有一个同一分布、同一内核的对照。

产出（`--out`）：
  1. 22 套卡组的**边际**胜率（随机对手 + 随机左右；与第二轮/第三轮同口径）；
  2. 本轮关心的 4 个**有序对位**的头对头胜率（左方 = 该行左边那套牌）；
  3. 顺带：左方总胜率（多数类基线）。

只读 `nn-data-100k.bin` 的 `outcome / gid / pairId` 三列（分块 memmap，6.9 GB 顺序读一遍）。

用法：
  python -X utf8 "klink bot/tools/nn-r7-baseline.py" --data out/nn-data-100k.bin `
    --out out/_r7-baseline-100k.txt
"""

import argparse
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="out/nn-data-100k.bin")
    ap.add_argument("--out", default="out/_r7-baseline-100k.txt")
    ap.add_argument("--all-pairs", default="", help="把全部有序对位写成一张表（给「选均势对位」用）")
    ap.add_argument("--chunk", type=int, default=200_000)
    a = ap.parse_args()

    import numpy as np
    from nn_common import open_data2

    data = REPO / a.data
    man = json.load(open(str(data) + ".decks.json", encoding="utf-8"))
    names = man["decks"]
    n_decks = len(names)
    gl = {g: l for g, l, _r in man["pairs"]}
    gr = {g: r for g, _l, r in man["pairs"]}

    rec, dim, n, decks = open_data2(str(data))
    print(f"数据 {data}  样本 {n:,}  维度 {dim}  卡组 {decks}  （分块读 outcome/gid 两列）")
    t0 = time.time()

    # ---- 逐样本读 (outcome, gid)：同局所有样本同标签，取每局最后一个即可 ----
    lab = {}
    for s in range(0, n, a.chunk):
        e = min(n, s + a.chunk)
        blk = np.asarray(rec[s:e, [dim, dim + 1]], dtype=np.float32)
        for g, y in zip(blk[:, 1].astype(np.int64).tolist(), blk[:, 0].tolist()):
            lab[g] = y
        print(f"  …{e:,}/{n:,}  ({time.time() - t0:.0f}s)")
    del rec
    print(f"读到 {len(lab):,} 局标签，用时 {time.time() - t0:.0f}s")

    # ---- 边际胜率（每套牌作为「任一方」时的胜率）----
    win = [0] * n_decks
    tot = [0] * n_decks
    for g, y in lab.items():
        l, r = gl.get(g), gr.get(g)
        if l is None:
            continue
        wl = 1 if y >= 0.5 else 0
        win[l] += wl
        tot[l] += 1
        win[r] += 1 - wl
        tot[r] += 1
    left_wins = sum(1 for y in lab.values() if y >= 0.5)

    # ---- 有序对位 ----
    h2h = {}
    for g, y in lab.items():
        l, r = gl.get(g), gr.get(g)
        if l is None:
            continue
        k = (l, r)
        w, t = h2h.get(k, (0, 0))
        h2h[k] = (w + (1 if y >= 0.5 else 0), t + 1)

    idx = {nm: i for i, nm in enumerate(names)}
    focus = [("德芬车", "德澳老兵"), ("德澳老兵", "德芬车"),
             ("日澳快攻", "美英跳"), ("美英跳", "日澳快攻")]

    lines = []
    p = lines.append
    p("=" * 100)
    p("① 10 万局语料（GreedyBot vs GreedyBot、带效果内核、引擎 cardID 修复之后）上的卡池基准")
    p("=" * 100)
    p(f"数据      : {a.data}（{len(lab):,} 局 / {n:,} 样本 / {dim} 维 / v2）")
    p(f"左方总胜率: {100.0 * left_wins / len(lab):.2f}%（多数类基线；卡池随机左右 ⇒ 理论 50%）")
    p("")
    p("边际胜率（随机对手 + 随机左右）：")
    order = sorted(range(n_decks), key=lambda i: -win[i] / max(1, tot[i]))
    for i in order:
        p(f"  {names[i]:<12} {win[i]:>6}/{tot[i]:<6} {100.0 * win[i] / max(1, tot[i]):>6.1f}%")
    ws = np.array([100.0 * win[i] / max(1, tot[i]) for i in range(n_decks)])
    p(f"  跨度 {ws.min():.1f}% ~ {ws.max():.1f}%   标准差 σ = {ws.std(ddof=0):.1f} 点")
    p("")
    p("本轮的 4 个有序对位（头对头；行 = 左方视角胜率）：")
    for a_, b_ in focus:
        w, t = h2h.get((idx[a_], idx[b_]), (0, 0))
        p(f"  {(a_ + ' → ' + b_):<24} {w:>5}/{t:<6} {100.0 * w / max(1, t):>6.1f}%")
    p("")
    txt = "\n".join(lines)
    print()
    print(txt)
    (REPO / a.out).write_text(txt, encoding="utf-8")
    print(f"\n已写入 {a.out}")

    if a.all_pairs:
        pl = ["=" * 100,
              "全部有序对位（左方视角胜率；n = 该方向局数；10 万局 Greedy vs Greedy）",
              "=" * 100,
              f"{'左卡组':<14}{'右卡组':<14}{'胜/总':>14}{'左胜率':>9}"]
        for (l, r), (w, t) in sorted(h2h.items(), key=lambda kv: (kv[0][0], kv[0][1])):
            pl.append(f"{names[l]:<14}{names[r]:<14}{f'{w}/{t}':>14}{100.0 * w / t:>8.1f}%")
        pl.append("")
        pl.append("最接近 50% 的对位（两个方向都 ≥150 局、都落在 45~55%）：")
        pl.append(f"{'卡组A':<14}{'卡组B':<14}{'A→B':>10}{'n':>6}{'B→A':>10}{'n':>6}{'|A→B-50|':>11}")
        cand = []
        for i in range(n_decks):
            for j in range(i + 1, n_decks):
                w1, t1 = h2h.get((i, j), (0, 0))
                w2, t2 = h2h.get((j, i), (0, 0))
                if t1 >= 150 and t2 >= 150:
                    r1, r2 = 100.0 * w1 / t1, 100.0 * w2 / t2
                    if 45 <= r1 <= 55 and 45 <= r2 <= 55:
                        cand.append((abs(r1 - 50) + abs(r2 - 50), i, j, r1, t1, r2, t2))
        for _s, i, j, r1, t1, r2, t2 in sorted(cand)[:25]:
            pl.append(f"{names[i]:<14}{names[j]:<14}{r1:>9.1f}%{t1:>6}{r2:>9.1f}%{t2:>6}{abs(r1 - 50):>10.1f}")
        ptxt = "\n".join(pl)
        print()
        print(ptxt)
        (REPO / a.all_pairs).write_text(ptxt, encoding="utf-8")
        print(f"\n已写入 {a.all_pairs}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
