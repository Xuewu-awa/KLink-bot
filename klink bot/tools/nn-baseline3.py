"""
卡组对位随机化**前后**的同一套平凡基线（一份代码跑两个版本，便于逐项对照）。

背景（见 klink bot/docs/NN训练诊断.md §3.2 / §5）：
  v0 的数据是 `decks[g%22]` vs `decks[(g+7)%22]` 写死轮换 ⇒ 10,000 局只有 22 种对位，
  且很多对位一边倒（对位 12 是 454:0）。卡组身份在编码里完全可见（Deck 区池化向量），
  于是「认出这是哪一对卡组、背下谁赢得多」＝ 75.15%（留一局口径），
  而 740→64→1 的 MLP 留出只有 76.52% —— 模型的绝大部分能力来自这条与打法无关的捷径。
  v1 改成每局随机抽两个不同卡组 + 左右随机互换（tools/NNTrain/Program.cs 的 dump）。

本脚本自动识别编码版本（看文件头里的 dim）：
  · dim == 740 → v0 布局，对位 = gid % 22（写死的轮换公式）
  · dim == 925 → v1 布局，对位来自 dump 同时写出的对位清单 <数据文件>.decks.json
  其余统计口径完全一致，所以两个版本的数字可以直接比。

用法：
  python -X utf8 "klink bot/tools/nn-baseline3.py" out\\nn-data-v0.bin        # 修复前
  python -X utf8 "klink bot/tools/nn-baseline3.py" out\\nn-data.bin           # 修复后
"""

import json
import os
import struct
import sys

import numpy as np

MAGIC = 0x314C4B41
DECKS = 22
TURNS = 40

PATH = sys.argv[1] if len(sys.argv) > 1 else r"out\nn-data.bin"
MANIFEST = sys.argv[2] if len(sys.argv) > 2 else PATH + ".decks.json"
VERIFYLOG = sys.argv[3] if len(sys.argv) > 3 else None   # NNTrain verify 的输出（可选，加一列模型）


def model_buckets(path):
    """从 `NNTrain verify` 的输出里解析【全体样本】分档准确率。"""
    import re
    out = {}
    lines = open(path, encoding="utf-8").read().splitlines()
    for i, ln in enumerate(lines):
        if "【全体样本】" not in ln:
            continue
        for ln2 in lines[i + 1:]:
            m = re.match(r"\s*(\d+)\s+([\d,]+)\s+([\d.]+)%", ln2)
            if m:
                out[int(m.group(1))] = float(m.group(3))
            elif "合计" in ln2:
                break
        break
    return out


def fit_logistic(F, y, l2=1e-3, iters=1200, lr=0.8):
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = F.shape[0]
    for _ in range(iters):
        p = 1.0 / (1.0 + np.exp(-np.clip(F @ w, -30, 30)))
        g = F.T @ (p - y) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return w


class Layout:
    """按版本给出需要的偏移。字段顺序的出处见 nn-check-layout.py / check-leader-baseline.py。"""

    def __init__(self, dim):
        self.dim = dim
        if dim == 740:                     # v0
            self.ver = 0
            (self.L_HQ, self.L_KRED, self.L_MAXK,
             self.L_HAND, self.L_DECK, self.L_BOARD) = 0, 1, 2, 3, 4, 5
            (self.R_HQ, self.R_KRED, self.R_MAXK,
             self.R_HAND, self.R_DECK, self.R_BOARD) = 370, 371, 372, 373, 374, 375
            self.L_DISC_N, self.R_DISC_N = 278, 648
        elif dim == 925:                   # v1
            self.ver = 1
            (self.L_HQ, self.L_KRED, self.L_MAXK,
             self.L_HAND, self.L_DECK, self.L_BOARD) = 3, 4, 5, 6, 7, 8
            (self.R_HQ, self.R_KRED, self.R_MAXK,
             self.R_HAND, self.R_DECK, self.R_BOARD) = 464, 465, 466, 467, 468, 469
            self.L_DISC_N, self.R_DISC_N = 372, 833
        else:
            raise SystemExit(f"不认识的维度 {dim}")

    def turn(self, X):
        if self.ver == 0:
            # v0 没有 Turn 字段，只能用 maxKredits 代理（触顶 12 后饱和）
            return (np.rint(X[:, 2] * 12) + np.rint(X[:, 372] * 12)).astype(np.int64)
        return np.rint(X[:, 0] * 30).astype(np.int64)


def main():
    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, dim = struct.unpack("<ii", f.read(8))
    assert magic == MAGIC, "格式不对"
    L = Layout(dim)
    REC_F = dim + 2
    assert (size - 8) % (REC_F * 4) == 0
    n = (size - 8) // (REC_F * 4)

    raw = np.fromfile(PATH, dtype=np.float32, offset=8, count=n * REC_F)
    rec = raw.reshape(n, REC_F)
    X = rec[:, :dim]
    y = rec[:, dim].astype(np.int8)                 # 1 = 左方(perspective)胜
    gid = rec[:, dim + 1].astype(np.int64)
    del raw, rec

    print("=" * 92)
    print(f"数据 {PATH}   编码 v{L.ver}（dim={dim}）")
    print("=" * 92)
    gs = np.unique(gid)
    lab_of_game = np.zeros(int(gs.max()) + 1, dtype=np.int8)
    # label 在同一局内恒定（已在 nn-check-layout.py 里反证过）⇒ 直接写，同值覆盖无副作用
    lab_of_game[gid.astype(int)] = y
    print(f"  样本 {n:,}   对局 {gs.size:,}   左胜率 {100*y.mean():.2f}%")

    # ---------------- 对位（每局一对卡组）----------------
    pair_of_game = np.full(int(gs.max()) + 1, -1, dtype=np.int64)
    if L.ver == 0:
        pair_of_game[gs.astype(int)] = gs % DECKS
        print("  对位来源：写死的轮换公式 gid%22（v0 数据集）")
        pair_desc = "对位 = gid%22"
    else:
        with open(MANIFEST, encoding="utf-8") as f:
            man = json.load(f)
        for g, li, ri in man["pairs"]:
            pair_of_game[g] = li * DECKS + ri
        print(f"  对位来源：dump 同时写出的清单 {MANIFEST}（seed={man['seed']}, games={man['games']}）")
        pair_desc = "对位 = 清单里的 (左卡组, 右卡组) 有序对"

    pg = pair_of_game[gid.astype(int)]
    assert (pg >= 0).all(), "有样本的对局在清单里找不到"
    npair = DECKS * DECKS
    gpg = pair_of_game[gs.astype(int)]           # 每局的对位（注意：不是每样本）
    cnt = np.bincount(gpg, minlength=npair)      # ⇒ 每对位的**局数**
    used = cnt[cnt > 0]
    print(f"  有序对位 {int((cnt>0).sum())} 种，每对位 {used.min()}~{used.max()} 局"
          f"（均值 {used.mean():.1f}，p50 {int(np.median(used))}）")

    # 卡组身份**是否仍可从编码里认出来**？（捷径还在不在，只是还值不值钱）
    # 用每局第一条快照的 Deck 区池化向量，看它能否按真实卡组聚成 22 簇。
    first_i = {}
    for i in range(n):
        g = int(gid[i])
        if g not in first_i:
            first_i[g] = i
    gss = np.array(sorted(first_i))
    fi = np.array([first_i[int(g)] for g in gss])
    if L.ver == 0:
        zL, zR = 279, 649
        li = gss % DECKS
        ri = (gss + 7) % DECKS
    else:
        zL, zR = 373, 834
        li = pair_of_game[gss] // DECKS
        ri = pair_of_game[gss] % DECKS
    for nm, off, grp in (("左方牌库区（按**左卡组真实身份**分组）", zL, li),
                         ("右方牌库区（按**右卡组真实身份**分组）", zR, ri)):
        D = X[fi][:, off:off + 90]
        cen = np.stack([D[grp == p].mean(axis=0) for p in range(DECKS)])
        d = np.linalg.norm(D[:, None, :] - cen[None, :, :], axis=2)
        own = d[np.arange(len(grp)), grp]
        d2 = d.copy()
        d2[np.arange(len(grp)), grp] = np.inf
        frac = float((own < d2.min(axis=1)).mean())
        print(f"  {nm}: 本组质心更近的比例 {100*frac:.1f}%  "
              f"（→ 卡组身份{'仍完全可见' if frac > 0.9 else '已不易辨认'}）")

    # ================= A. 卡组对位多数类（留一局）=================
    lab_g = lab_of_game[gs.astype(int)].astype(np.float64)
    gp = pair_of_game[gs.astype(int)]
    pair_lw = np.bincount(gp, weights=lab_g, minlength=npair)     # 每对位左胜局数
    pair_n = np.bincount(gp, minlength=npair).astype(np.float64)

    print()
    print("=" * 92)
    print(f"A. 卡组对位多数类（完全不看局面，只认「哪一对卡组」）   {pair_desc}")
    print("=" * 92)
    order = np.argsort(-pair_n)
    print(f"  {'对位(左,右)':>12} {'局数':>6} {'左胜':>6} {'右胜':>6} {'多数类':>9}")
    for p in order[:8]:
        lw, tot = int(pair_lw[p]), int(pair_n[p])
        print(f"  {p//DECKS:>5},{p%DECKS:<6} {tot:>6} {lw:>6} {tot-lw:>6} "
              f"{100*max(lw,tot-lw)/max(1,tot):>8.1f}%")
    print("  …（只列局数最多的 8 个）")
    Lm = pair_lw[pg] - (y == 1)
    Rm = (pair_n[pg] - pair_lw[pg]) - (y == 0)
    okA = (Lm >= Rm) == (y == 1)                      # 平手 ⇒ 猜左
    print(f"  ⇒ 含自证（偏高）   {100*np.maximum(pair_lw, pair_n-pair_lw).sum()/pair_n.sum():.2f}%")
    print(f"  ★ 留一局多数类    {100*okA.mean():.2f}%   ({okA.sum():,}/{n:,})")

    # ================= A2. 只认「左方是哪套卡组」（不含右方）=================
    pl = gp // DECKS
    pl_lw = np.bincount(pl, weights=lab_g, minlength=DECKS)
    pl_n = np.bincount(pl, minlength=DECKS).astype(np.float64)
    plg = pl[gid.astype(int)]
    okA2 = ((pl_lw[plg] - (y == 1)) >= ((pl_n[plg] - pl_lw[plg]) - (y == 0))) == (y == 1)
    pr = gp % DECKS
    pr_lw = np.bincount(pr, weights=lab_g, minlength=DECKS)
    pr_n = np.bincount(pr, minlength=DECKS).astype(np.float64)
    prg = pr[gid.astype(int)]
    okA3 = ((pr_lw[prg] - (y == 1)) >= ((pr_n[prg] - pr_lw[prg]) - (y == 0))) == (y == 1)
    print()
    print("A2. 只看单边卡组的多数类（留一局）")
    print(f"  只认「左方是哪套卡组」(22 组)   {100*okA2.mean():.2f}%")
    print(f"  只认「右方是哪套卡组」(22 组)   {100*okA3.mean():.2f}%")

    # ================= B. 最优线性读领先 =================
    dhq = np.rint((X[:, L.L_HQ] - X[:, L.R_HQ]) * 20).astype(np.int16)
    dbd = np.rint((X[:, L.L_BOARD] - X[:, L.R_BOARD]) * 10).astype(np.int16)
    dhd = np.rint((X[:, L.L_HAND] - X[:, L.R_HAND]) * 10).astype(np.int16)
    dkr = np.rint((X[:, L.L_KRED] - X[:, L.R_KRED]) * 12).astype(np.int16)
    ddc = np.rint((X[:, L.L_DISC_N] - X[:, L.R_DISC_N]) * 10).astype(np.int16)
    turn = L.turn(X)
    feats = np.column_stack([dhq / 20.0, dbd / 10.0, dhd / 10.0, dkr / 12.0,
                             ddc / 10.0]).astype(np.float64)
    yl = y.astype(np.float64)

    print()
    print("=" * 92)
    print("B. 最优线性「读领先方」（只用公开状态 + 线性阈值）")
    print("=" * 92)
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
    okB = (sB > 0) == (yl > 0.5)
    print(f"  ★ 准确率 {100*okB.mean():.2f}%")
    F = np.column_stack([np.ones(n), feats])
    w = np.zeros(F.shape[1])
    for _ in range(600):
        z = F @ w
        p = 1.0 / (1.0 + np.exp(-np.clip(z, -30, 30)))
        g = F.T @ (p - yl) / n + 0.001 * np.r_[0.0, w[1:]]
        w -= 0.5 * g
    p = 1.0 / (1.0 + np.exp(-np.clip(F @ w, -30, 30)))
    okLR = ((p > 0.5) == (yl > 0.5))
    print(f"  逻辑回归（5 个公开量）：{100*okLR.mean():.2f}%")

    # ================= C. 第一条快照切片 =================
    m2 = turn == 2
    print()
    print("=" * 92)
    print(f"C. 第一条快照切片（turn=2，{int(m2.sum()):,} 条）")
    print("=" * 92)
    print(f"  双方 HQ 相同            {100*(dhq[m2] == 0).mean():.1f}%")
    print(f"  左方胜率（多数类）        {100*yl[m2].mean():.2f}%")
    print(f"  卡组对位多数类 A         {100*okA[m2].mean():.2f}%")
    print(f"  只看左卡组 A2            {100*okA2[m2].mean():.2f}%")
    print(f"  线性读领先 B             {100*okB[m2].mean():.2f}%")
    print(f"  逻辑回归 B2              {100*okLR[m2].mean():.2f}%")

    # ================= D. 线性天花板 F1/F2（按局留出）=================
    # F1 = 公开局面；F2p = F1 + 有序对位 one-hot(462)；F2d = F1 + 左/右卡组 one-hot(44)
    holdout = (gid % 5) == 0
    F1 = np.column_stack([np.ones(n, dtype=np.float32)] + [feats[:, k].astype(np.float32)
                                                           for k in range(5)])
    ohp = np.zeros((n, npair), dtype=np.float32)
    ohp[np.arange(n), pg] = 1.0
    ohd = np.zeros((n, 2 * DECKS), dtype=np.float32)
    ohd[np.arange(n), plg] = 1.0
    ohd[np.arange(n), DECKS + prg] = 1.0
    F2p = np.column_stack([F1, ohp])
    F2d = np.column_stack([F1, ohd])
    oht = np.zeros((n, TURNS), dtype=np.float32)
    oht[np.arange(n), np.clip(turn, 0, TURNS - 1)] = 1.0
    F3 = np.column_stack([F2d, oht])

    print()
    print("=" * 92)
    print("D. 平凡特征的线性天花板（逻辑回归；按局留出 = gid%5==0 的 2000 局）")
    print("=" * 92)
    print(f"  {'特征层':<30}{'参数':>6}{'全数据拟合/评估':>18}{'按局留出':>14}")
    rows = [("F0 多数类（永远猜左）", None, 1),
            ("F1 公开局面（读领先）", F1, F1.shape[1]),
            ("F2d F1+左/右卡组 one-hot", F2d, F2d.shape[1]),
            ("F2p F1+有序对位 one-hot", F2p, F2p.shape[1]),
            ("F3  F2d+回合 one-hot", F3, F3.shape[1])]
    a_maj = 100 * max(y.mean(), 1 - y.mean())
    a_maj_ho = 100 * max(y[holdout].mean(), 1 - y[holdout].mean())
    print(f"  {'F0 多数类（永远猜左）':<30}{1:>6}{a_maj:>17.2f}%{a_maj_ho:>13.2f}%")
    fitted = {}
    for name, Fm, npar in rows[1:]:
        w_all = fit_logistic(Fm, y)
        a_all = 100 * float(((Fm @ w_all > 0) == (y > 0.5)).mean())
        w_ho = fit_logistic(Fm[~holdout], y[~holdout])
        a_ho = 100 * float(((Fm[holdout] @ w_ho > 0) == (y[holdout] > 0.5)).mean())
        fitted[name] = (Fm, w_all, w_ho)
        print(f"  {name:<30}{npar:>6}{a_all:>17.2f}%{a_ho:>13.2f}%")

    # ================= 分档对照 =================
    print()
    print("=" * 92)
    print("分档准确率（同模型口径；v1 的 turn 就是编码字段 0 = State.Turn）")
    print("=" * 92)
    okF1 = (F1 @ fitted["F1 公开局面（读领先）"][1] > 0) == (y > 0.5)
    okF2p = (F2p @ fitted["F2p F1+有序对位 one-hot"][1] > 0) == (y > 0.5)
    mb = model_buckets(VERIFYLOG) if VERIFYLOG else {}
    if mb:
        print(f"（模型列来自 {VERIFYLOG} 的『全体样本』分档 = 含训练样本口径，只用于看形状）")
    hdr = f"  {'turn':>5}{'样本':>9}" + (f"{'模型':>8}" if mb else "") + \
          f"{'A对位':>9}{'A2左卡组':>10}{'B读领先':>9}{'F1读领先':>10}{'F2p对位':>9}{'多数类':>9}"
    print(hdr)
    for t in range(int(turn.min()), int(turn.max()) + 1):
        m = turn == t
        if m.sum() < 100:
            continue
        cells = f"  {t:>5}{int(m.sum()):>9,}"
        if mb:
            cells += f"{mb.get(t, float('nan')):>7.1f}%"
        cells += (f"{100*okA[m].mean():>8.1f}%{100*okA2[m].mean():>9.1f}%"
                  f"{100*okB[m].mean():>8.1f}%{100*okF1[m].mean():>9.1f}%"
                  f"{100*okF2p[m].mean():>8.1f}%{100*max(yl[m].mean(),1-yl[m].mean()):>8.1f}%")
        print(cells)
    print()
    print(f"  全体：A {100*okA.mean():.2f}%   A2 {100*okA2.mean():.2f}%   "
          f"B {100*okB.mean():.2f}%   F1 {100*okF1.mean():.2f}%   "
          f"F2p {100*okF2p.mean():.2f}%   多数类 {100*max(yl.mean(),1-yl.mean()):.2f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
