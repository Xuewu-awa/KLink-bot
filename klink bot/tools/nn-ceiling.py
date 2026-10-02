"""
第三层：把「77.5% 的来源」拆开 —— 平凡特征的**天花板**是多少？

⚠️ **本脚本只适用于 v0 数据集（DIM=740，卡组写死轮换 g%22）**，即 out/nn-data-v0.bin。
   v1（925 维 + 随机对位）请用 **nn-baseline3.py** 的 D 段（F0/F1/F2d/F2p/F3，同样的按局留出口径）。

思路：用**线性逻辑回归**（平凡特征、参数量个位数~几百）逐层加特征，看每加一层
能拿到多少准确率。如果线性模型用「对局对位 + 当前领先」就能到 77.5% 以上，
那 NN 的 77.5% 就没有「局面判断」成分。

特征层：
  F0  多数类（永远猜左）
  F1  公开局面量：Δhq / Δ场上 / Δ手牌 / Δkredits / Δ弃牌堆      （= 读当前领先）
  F2  F1 + 卡组对位 one-hot(22)
  F3  F2 + turn one-hot(26)
  F4  F3 + 对位×turn 交互(22×26)                              （= 背「对位+回合」胜率表）

两种口径：
  · 全数据集拟合/评估（= 模型 verify 的『全体准确率』口径，含自证）
  · **按对局留出**：用 gid%5!=0 的 8000 局拟合，在 gid%5==0 的 2000 局上评估
    （= 模型 verify 的『验证集准确率』口径）

用法：python -X utf8 "klink bot/tools/nn-ceiling.py" [数据文件]
"""

import os
import struct
import sys

import numpy as np

DIM = 740
REC_F = DIM + 2
MAGIC = 0x314C4B41
DECKS = 22
TURNS = 26

PATH = sys.argv[1] if len(sys.argv) > 1 else r"out\nn-data.bin"

L_HQ, L_KRED, L_MAXK, L_HAND, L_DECK, L_BOARD = 0, 1, 2, 3, 4, 5
R_HQ, R_KRED, R_MAXK, R_HAND, R_DECK, R_BOARD = 370, 371, 372, 373, 374, 375


def fit_logistic(F, y, l2=1e-3, iters=1200, lr=0.8):
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = F.shape[0]
    for _ in range(iters):
        p = 1.0 / (1.0 + np.exp(-np.clip(F @ w, -30, 30)))
        g = F.T @ (p - y) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return w


def acc(F, y, w):
    return float(((F @ w > 0) == (y > 0.5)).mean())


def build(X, pair, turn, level):
    """按 level 拼设计矩阵（含截距列），float32。"""
    cols = [np.ones(len(pair), dtype=np.float32)]
    if level >= 1:
        cols += [X[:, L_HQ] - X[:, R_HQ], X[:, L_BOARD] - X[:, R_BOARD],
                 X[:, L_HAND] - X[:, R_HAND], X[:, L_KRED] - X[:, R_KRED],
                 X[:, 278] - X[:, 648]]
    if level >= 2:
        oh = np.zeros((len(pair), DECKS), dtype=np.float32)
        oh[np.arange(len(pair)), pair] = 1.0
        cols.append(oh)
    if level >= 3:
        oh = np.zeros((len(pair), TURNS), dtype=np.float32)
        oh[np.arange(len(pair)), turn] = 1.0
        cols.append(oh)
    if level >= 4:
        oh = np.zeros((len(pair), DECKS * TURNS), dtype=np.float32)
        oh[np.arange(len(pair)), pair * TURNS + turn] = 1.0
        cols.append(oh)
    return np.column_stack(cols)


def main():
    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, hdr = struct.unpack("<ii", f.read(8))
    assert magic == MAGIC and hdr == DIM
    n = (size - 8) // (REC_F * 4)
    raw = np.fromfile(PATH, dtype=np.float32, offset=8, count=n * REC_F)
    rec = raw.reshape(n, REC_F)
    X = np.ascontiguousarray(rec[:, :DIM])
    y = rec[:, DIM].astype(np.float32)
    gid = rec[:, DIM + 1].astype(np.int64)
    del raw, rec

    pair = (gid % DECKS).astype(int)
    turn = np.clip((np.rint(X[:, L_MAXK] * 12) + np.rint(X[:, R_MAXK] * 12))
                   .astype(int), 0, TURNS - 1)
    holdout = (gid % 5) == 0

    print(f"样本 {n:,}   对局 10,000   留出 {len(np.unique(gid[holdout])):,} 局"
          f"（gid%5==0）/ {int(holdout.sum()):,} 样本")
    print()
    print("=" * 92)
    print("平凡特征线性天花板（逻辑回归，纯线性、无隐藏层、无局面结构）")
    print("=" * 92)
    print(f"  {'特征层':<24}{'参数':>6}{'全数据拟合/评估':>18}"
          f"{'按局留出(8000局→2000局)':>26}")
    a_maj = 100 * max(y.mean(), 1 - y.mean())
    a_maj_ho = 100 * max(y[holdout].mean(), 1 - y[holdout].mean())
    print(f"  {'F0 多数类（永远猜左）':<24}{1:>6}{a_maj:>17.2f}%{a_maj_ho:>25.2f}%")
    names = {1: "F1 公开局面（读领先）", 2: "F2 F1+对位 one-hot",
             3: "F3 F2+回合 one-hot", 4: "F4 F3+对位×回合交互"}
    for lv in (1, 2, 3, 4):
        F = build(X, pair, turn, lv)
        w = fit_logistic(F, y)
        a_all = 100 * acc(F, y, w)
        w2 = fit_logistic(F[~holdout], y[~holdout])
        a_ho = 100 * acc(F[holdout], y[holdout], w2)
        print(f"  {names[lv]:<24}{F.shape[1]:>6}{a_all:>17.2f}%{a_ho:>25.2f}%")
        if lv in (1, 2, 4):
            if lv == 1:
                F1m, w1m = F, w
            elif lv == 2:
                F2m, w2m = F, w
            else:
                F4m, w4m = F, w
        del F
    del X

    print()
    print("=" * 92)
    print("分档对照（F1/F2/F4 用**全数据拟合**的权重，与模型『全体准确率』口径一致）")
    print("=" * 92)
    print(f"  {'turn':>5}{'样本':>9}{'F1读领先':>11}{'F2+对位':>11}"
          f"{'F4+对位×回合':>14}{'多数类':>9}")
    lo = (turn * 0 + int(turn.min()))
    for t in range(int(turn.min()), TURNS):
        m = turn == t
        if m.sum() < 100:
            continue
        print(f"  {t:>5}{int(m.sum()):>9,}"
              f"{100*acc(F1m[m], y[m], w1m):>10.1f}%"
              f"{100*acc(F2m[m], y[m], w2m):>10.1f}%"
              f"{100*acc(F4m[m], y[m], w4m):>13.1f}%"
              f"{100*max(y[m].mean(), 1-y[m].mean()):>8.1f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
