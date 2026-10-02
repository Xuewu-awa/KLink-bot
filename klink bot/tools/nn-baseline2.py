"""
决定性问题（下半）：NN 的 77.5% 里，有多少是「读当前谁领先」，有多少是「记住卡组对位」？

⚠️ **本脚本只适用于 v0 数据集（DIM=740，卡组写死轮换 g%22）**，即 out/nn-data-v0.bin。
   编码升到 v1（925 维）之后请用 **nn-baseline3.py** —— 它一份代码同时支持 v0/v1，
   口径与本脚本一致（留一局对位多数类 / 网格读领先 / turn=2 切片），
   并已用它复现出本脚本在 v0 上的同一个 75.15%。
   保留本脚本是为了让第一轮报告 §3.2 的数字仍可逐条复现。

三个对照基线，全部只用**公开可见**信息、且不做任何搜索：

  A. 卡组对位多数类（matchup prior）—— **完全不看局面**
     数据生成时卡组是 decks[g%22] vs decks[(g+7)%22]
     （tools/NNTrain/Program.cs:100-104，MetaDecks 共 22 套，g = 写进文件的 gameId）
     ⇒ 全数据集只有 22 种对位。Deck 区（编码偏移 279..368，40 张牌的池化向量）
     本身就暴露了是哪一套卡组，所以「认出对位并背下谁赢得多」在推理时是可行的。
     统计用**留一局**（算某局时把它自己从该对位计数里剔除），避免自证。

  B. 最优线性「读领先方」—— 只在公开状态上做线性阈值
     score = w·[Δhq, Δ场上, Δ手牌, Δkredits] + b > 0 ⇒ 猜左
     权重用全数据集网格搜索 + 逻辑回归各求一次。这是「只读当前领先能到的上界」
     （含事后挑选 ⇒ 偏高）。

  C. 第一条快照切片（turn = 2）
     双方各行动一次；9739/10000 局双方 HQ 都是 20/20、场上几乎无单位。
     任何显著高于多数类（53.6%）的准确率都不可能是「读领先方」。

用法：python -X utf8 "klink bot/tools/nn-baseline2.py" [数据文件]
"""

import os
import struct
import sys

import numpy as np

DIM = 740
REC_F = DIM + 2          # float 数：740 特征 + label + gameId
MAGIC = 0x314C4B41
DECKS = 22

PATH = sys.argv[1] if len(sys.argv) > 1 else r"out\nn-data.bin"

# ---- 绝对偏移 ----
L_HQ, L_KRED, L_MAXK, L_HAND, L_DECK, L_BOARD = 0, 1, 2, 3, 4, 5
L_DECKZ, L_DECKZN = 279, 369
R_HQ, R_KRED, R_MAXK, R_HAND, R_DECK, R_BOARD = 370, 371, 372, 373, 374, 375
R_DECKZ, R_DECKZN = 649, 739


def main():
    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, hdr = struct.unpack("<ii", f.read(8))
    assert magic == MAGIC and hdr == DIM, "格式不对"
    n = (size - 8) // (REC_F * 4)
    assert (size - 8) % (REC_F * 4) == 0

    raw = np.fromfile(PATH, dtype=np.float32, offset=8, count=n * REC_F)
    rec = raw.reshape(n, REC_F)
    X = rec[:, :DIM]
    y = rec[:, DIM].astype(np.int8)                 # 1 = 左方(perspective)胜
    gid = rec[:, DIM + 1].astype(np.int64)
    del raw, rec

    dhq = np.rint((X[:, L_HQ] - X[:, R_HQ]) * 20).astype(np.int16)
    dbd = np.rint((X[:, L_BOARD] - X[:, R_BOARD]) * 10).astype(np.int16)
    dhd = np.rint((X[:, L_HAND] - X[:, R_HAND]) * 10).astype(np.int16)
    dkr = np.rint((X[:, L_KRED] - X[:, R_KRED]) * 12).astype(np.int16)
    ddc = np.rint((X[:, 278] - X[:, 648]) * 10).astype(np.int16)   # 弃牌堆张数差
    lmaxk = np.rint(X[:, L_MAXK] * 12).astype(np.int16)
    rmaxk = np.rint(X[:, R_MAXK] * 12).astype(np.int16)
    turn = (lmaxk + rmaxk).astype(np.int16)

    print("=" * 78)
    print("0. 结构自检")
    print("=" * 78)
    gs = np.unique(gid)
    print(f"  样本 {n:,}   对局 {gs.size:,}   gameId 范围 [{gs.min():.0f}, {gs.max():.0f}]")
    print(f"  gameId 恰为 0..9999 连续整数  "
          f"{'OK' if gs.size == 10000 and gs.min() == 0 and gs.max() == 9999 else 'BAD'}")
    # 卡组对位假设：对位 = (g%22, (g+7)%22)
    pair = (gs.astype(np.int64) % DECKS)
    print(f"  卡组对位（g%22）分布：{np.bincount(pair).min()}~{np.bincount(pair).max()} 局/对位")

    # ── 用**编码自身**反证对位假设：Deck 区向量（偏移 279..368 / 649..738）
    #    是「本方牌库剩余牌」的池化均值。同一套卡组的局，其牌库均值应彼此靠近，
    #    不同卡组的应彼此远离。若 g%22 的分组正好把牌库均值聚成 22 簇，
    #    则「对位 = g%22」这条从代码读出来的公式在数据上成立。
    first = np.zeros(10000, dtype=np.int64)
    gi = gid.astype(np.int64)
    first[gi[::-1]] = np.arange(n, dtype=np.int64)[::-1]   # 逆序写 ⇒ 留下最小下标
    first = first[gs]
    dl = X[first][:, L_DECKZ:L_DECKZN]
    dr = X[first][:, R_DECKZ:R_DECKZN]
    for nm, D, grp in (("左方牌库(按 g%22 分组)", dl, gs % DECKS),
                       ("右方牌库(按 (g+7)%22 分组)", dr, (gs + 7) % DECKS)):
        cen = np.stack([D[grp == p].mean(axis=0) for p in range(DECKS)])
        d = np.linalg.norm(D[:, None, :] - cen[None, :, :], axis=2)
        own = d[np.arange(gs.size), grp]
        d2 = d.copy()
        d2[np.arange(gs.size), grp] = np.inf
        nearest_other = d2.min(axis=1)
        frac = float((own < nearest_other).mean())
        print(f"  {nm}: 到本组质心平均距离 {own.mean():.4f}，"
              f"到最近他组 {nearest_other.mean():.4f}，"
              f"本组更近的比例 {100*frac:.1f}%")

    # ================= A. 卡组对位多数类 =================
    # 每局 label 恒定（自检已证）⇒ 直接按 gameId 赋值
    lab_of_game = np.zeros(10000, dtype=np.int8)
    lab_of_game[gid.astype(int)] = y
    gl = lab_of_game.astype(np.float64)
    pair_lw = np.bincount(pair, weights=gl, minlength=DECKS)         # 每对位左胜局数
    pair_n = np.bincount(pair, minlength=DECKS).astype(np.float64)

    print()
    print("=" * 78)
    print("A. 卡组对位多数类（完全不看局面，只认「哪一对卡组」）")
    print("=" * 78)
    print(f"  {'对位':>4} {'局数':>6} {'左胜':>6} {'右胜':>6} {'多数类':>9}")
    for p in range(DECKS):
        L, R = int(pair_lw[p]), int(pair_n[p] - pair_lw[p])
        print(f"  {p:>4} {int(pair_n[p]):>6} {L:>6} {R:>6} {100*max(L,R)/max(1,L+R):>8.1f}%")
    insample = (np.maximum(pair_lw, pair_n - pair_lw).sum() / pair_n.sum())
    print(f"  ⇒ 含自证（偏高）   {100*insample:.2f}%")
    # 留一局：算某局时把它自己剔除
    pg = gid.astype(int) % DECKS
    Lm = pair_lw[pg] - (y == 1)
    Rm = (pair_n[pg] - pair_lw[pg]) - (y == 0)
    predA = (Lm >= Rm).astype(np.int8)          # 平手 ⇒ 猜左
    okA = (predA == y)
    print(f"  ★ 留一局多数类    {100*okA.mean():.2f}%   ({okA.sum():,}/{n:,})")

    # ================= B. 最优线性读领先 =================
    feats = np.column_stack([
        dhq / 20.0, dbd / 10.0, dhd / 10.0, dkr / 12.0, ddc / 10.0,
    ]).astype(np.float64)
    names = ["Δhq", "Δ场上", "Δ手牌", "Δkredits", "Δ弃牌堆"]
    yl = y.astype(np.float64)

    print()
    print("=" * 78)
    print("B. 最优线性「读领先方」（只用公开状态 + 线性阈值）")
    print("=" * 78)
    # --- B1 透明网格搜索（整数权重，可手算复现）---
    grid = [0, 1, 2, 3, 4, 6]
    best = (-1, None)
    for w1 in grid:
        for w2 in grid:
            for w3 in (0, 1, 2):
                for w4 in (0, 1):
                    for w5 in (0, 1):
                        for b in (-1.0, -0.5, 0.0, 0.5, 1.0):
                            s = (w1 * feats[:, 0] + w2 * feats[:, 1] + w3 * feats[:, 2]
                                 + w4 * feats[:, 3] + w5 * feats[:, 4] + b)
                            c = int(((s > 0) == (yl > 0.5)).sum())
                            if c > best[0]:
                                best = (c, (w1, w2, w3, w4, w5, b))
    c, (w1, w2, w3, w4, w5, b) = best
    print(f"  网格最优：{w1}·Δhq + {w2}·Δ场上 + {w3}·Δ手牌 + {w4}·Δkredits "
          f"+ {w5}·Δ弃牌堆 + {b} > 0 ⇒ 猜左")
    sB = (w1 * feats[:, 0] + w2 * feats[:, 1] + w3 * feats[:, 2]
          + w4 * feats[:, 3] + w5 * feats[:, 4] + b)
    okB = ((sB > 0) == (yl > 0.5))
    print(f"  ★ 准确率 {100*okB.mean():.2f}%")
    # 不含手牌/弃牌堆（只用双方场面上的公开量）
    sPub = w1 * feats[:, 0] + w2 * feats[:, 1] + w4 * feats[:, 3] + b
    okPub = ((sPub > 0) == (yl > 0.5))
    print(f"    只用 HQ+场上+kredits（不含手牌/弃牌堆）：{100*okPub.mean():.2f}%")

    # --- B2 逻辑回归（真正的线性最优，作上界参考）---
    F = np.column_stack([np.ones(n), feats])
    w = np.zeros(F.shape[1])
    for it in range(600):
        z = F @ w
        p = 1.0 / (1.0 + np.exp(-np.clip(z, -30, 30)))
        g = F.T @ (p - yl) / n + 0.001 * np.r_[0.0, w[1:]]
        w -= 0.5 * g
    p = 1.0 / (1.0 + np.exp(-np.clip(F @ w, -30, 30)))
    okLR = ((p > 0.5) == (yl > 0.5))
    print(f"  逻辑回归（5 个公开量）：{100*okLR.mean():.2f}%   "
          f"权重 [bias {w[0]:+.2f}] " + " ".join(f"{nm} {wi:+.2f}"
                                                for nm, wi in zip(names, w[1:])))

    # ================= C. 第一条快照 =================
    m2 = turn == 2
    print()
    print("=" * 78)
    print(f"C. 第一条快照切片（turn=2，{m2.sum():,} 条 = 每局恰好 1 条）")
    print("=" * 78)
    print(f"  双方 HQ 相同            {100*(dhq[m2] == 0).mean():.1f}%")
    print(f"  左方胜率（多数类）        {100*yl[m2].mean():.2f}%")
    print(f"  卡组对位多数类 A         {100*okA[m2].mean():.2f}%")
    print(f"  线性读领先 B             {100*okB[m2].mean():.2f}%")
    print(f"  逻辑回归 B2              {100*okLR[m2].mean():.2f}%")

    # ================= 分档对照 =================
    print()
    print("=" * 78)
    print("分档准确率（同模型口径：turn = 左maxK + 右maxK）")
    print("=" * 78)
    print(f"  {'turn':>5}{'样本':>9}{'A卡组对位':>11}{'B线性读领先':>12}"
          f"{'B2逻辑回归':>11}{'多数类':>9}")
    for t in range(2, 26):
        m = turn == t
        if m.sum() < 100:
            continue
        print(f"  {t:>5}{int(m.sum()):>9,}{100*okA[m].mean():>10.1f}%"
              f"{100*okB[m].mean():>11.1f}%{100*okLR[m].mean():>10.1f}%"
              f"{100*(yl[m] > 0.5).mean():>8.1f}%")
    print()
    print(f"  全体：A {100*okA.mean():.2f}%   B {100*okB.mean():.2f}%   "
          f"B2 {100*okLR.mean():.2f}%   多数类 {100*(yl > 0.5).mean():.2f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
