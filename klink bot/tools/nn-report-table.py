"""
把「模型分档」和「各平凡基线分档」合并成一张表（报告 §4 用）。

模型的分档数字**不重新训练**，直接从 NNTrain verify 的输出文件里解析
（默认 out/_verify-strat.txt，即 `verify --model out/nn-model.bin --split-seed 12345`
的『全体样本』表）；基线在这一脚本里用 numpy 重算（口径相同：全 209,016 样本）。

用法：
  dotnet tools/NNTrain/bin/Release/net10.0/NNTrain.dll verify \
      --data out/nn-data.bin --model out/nn-model.bin --split-seed 12345 \
      > out/_verify-strat.txt
  python -X utf8 "klink bot/tools/nn-report-table.py"
"""

import os
import re
import struct
import sys

import numpy as np

DIM = 740
REC_F = DIM + 2
DECKS = 22
TURNS = 26

PATH = r"out/nn-data.bin"
MODEL_LOG = r"out/_verify-strat.txt"


def fit_logistic(F, y, l2=1e-3, iters=1200, lr=0.8):
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = F.shape[0]
    for _ in range(iters):
        p = 1.0 / (1.0 + np.exp(-np.clip(F @ w, -30, 30)))
        g = F.T @ (p - y) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return w


def main():
    # ---- 解析模型分档 ----
    model = {}
    with open(MODEL_LOG, encoding="utf-8") as f:
        lines = f.readlines()
    for i, ln in enumerate(lines):
        if "【全体样本】" in ln:
            for ln2 in lines[i + 1:]:
                m = re.match(r"\s*(\d+)\s+([\d,]+)\s+([\d.]+)%", ln2)
                if m:
                    model[int(m.group(1))] = float(m.group(3))
                elif "合计" in ln2:
                    break
            break
    if not model:
        print(f"没能从 {MODEL_LOG} 解析出模型分档 —— 先跑 verify（见文件头注释）")
        return 1
    print(f"从 {MODEL_LOG} 解析到模型分档 {len(model)} 档")

    # ---- 数据 ----
    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, hdr = struct.unpack("<ii", f.read(8))
    n = (size - 8) // (REC_F * 4)
    raw = np.fromfile(PATH, dtype=np.float32, offset=8, count=n * REC_F)
    rec = raw.reshape(n, REC_F)
    X = np.ascontiguousarray(rec[:, :DIM])
    y = rec[:, DIM].astype(np.float32)
    gid = rec[:, DIM + 1].astype(np.int64)
    del raw, rec

    dhq = X[:, 0] - X[:, 370]
    dbd = X[:, 5] - X[:, 375]
    dhd = X[:, 3] - X[:, 373]
    dkr = X[:, 1] - X[:, 371]
    ddc = X[:, 278] - X[:, 648]
    turn = np.clip((np.rint(X[:, 2] * 12) + np.rint(X[:, 372] * 12)).astype(int),
                   0, TURNS - 1)
    pair = (gid % DECKS).astype(int)

    # A：卡组对位多数类（留一局）
    lab_g = np.zeros(10000, dtype=np.float32)
    lab_g[gid.astype(int)] = y
    gs = np.unique(gid)
    gpair = (gs.astype(np.int64) % DECKS)
    pair_lw = np.bincount(gpair, weights=lab_g[gs], minlength=DECKS)
    pair_n = np.bincount(gpair, minlength=DECKS).astype(float)
    pg = gid.astype(int) % DECKS
    predA = ((pair_lw[pg] - (y == 1)) >= ((pair_n[pg] - pair_lw[pg]) - (y == 0))).astype(np.float32)
    okA = predA == y

    # B：线性读领先（网格最优，见 nn-baseline2.py）
    sB = 2 * dhq + 6 * dbd + 2 * dhd + 0.5
    okB = (sB > 0) == (y > 0.5)

    # F1 / F2：线性天花板
    F1 = np.column_stack([np.ones(n, dtype=np.float32), dhq, dbd, dhd, dkr, ddc])
    oh = np.zeros((n, DECKS), dtype=np.float32)
    oh[np.arange(n), pair] = 1.0
    F2 = np.column_stack([F1, oh])
    w1 = fit_logistic(F1, y)
    w2 = fit_logistic(F2, y)
    okF1 = (F1 @ w1 > 0) == (y > 0.5)
    okF2 = (F2 @ w2 > 0) == (y > 0.5)

    series = {
        "模型 MLP740→64→1": None,
        "A 卡组对位(留一局)": okA,
        "B 线性读领先(6参)": okB,
        "F1 线性读领先(6参)": okF1,
        "F2 读领先+对位(28参)": okF2,
        "多数类(猜左)": None,
    }

    def cell(name, m):
        if name.startswith("模型"):
            return model.get(int(turn[m][0]), float("nan")) if m.sum() else float("nan")
        if name.startswith("多数类"):
            return 100 * max(y[m].mean(), 1 - y[m].mean())
        return 100 * series[name][m].mean()

    print()
    print("=" * 104)
    print("全 209,016 样本 · 分档准确率（模型 = verify 的『全体样本』口径）")
    print("=" * 104)
    hdr = f"  {'turn':>5}{'样本':>9}" + "".join(f"{k.split()[0]:>13}" for k in series)
    print(hdr)
    for t in sorted(model):
        m = turn == t
        if m.sum() == 0:
            continue
        print(f"  {t:>5}{int(m.sum()):>9,}"
              + "".join(f"{cell(k, m):>12.1f}%" for k in series))

    print()
    print("=" * 104)
    print("按阶段合并")
    print("=" * 104)
    groups = [("早局 T2–T4", (2, 4)), ("中局 T5–T12", (5, 12)),
              ("后局 T13–T23", (13, 23)), ("终局 T24–T25", (24, 25))]
    print(f"  {'阶段':<14}{'样本':>9}" + "".join(f"{k.split()[0]:>13}" for k in series))
    for nm, (a, b) in groups:
        m = (turn >= a) & (turn <= b)
        vals = []
        for k in series:
            if k.startswith("模型"):
                tot = sum(model.get(int(t), 0) * int((turn == t).sum())
                          for t in range(a, b + 1))
                cnt = sum(int((turn == t).sum()) for t in range(a, b + 1))
                vals.append(tot / cnt if cnt else float("nan"))
            elif k.startswith("多数类"):
                # 分档多数类的加权平均
                tot = sum(max(y[turn == t].mean(), 1 - y[turn == t].mean())
                          * int((turn == t).sum()) for t in range(a, b + 1))
                cnt = sum(int((turn == t).sum()) for t in range(a, b + 1))
                vals.append(100 * tot / cnt)
            else:
                vals.append(100 * series[k][m].mean())
        print(f"  {nm:<14}{int(m.sum()):>9,}" + "".join(f"{v:>12.1f}%" for v in vals))
    print()
    print("注：模型列来自 verify 输出（out/_verify-strat.txt）；A/B/F1/F2 由本脚本用"
          "同一批样本重算。")
    print("    F1/F2 为**全数据拟合**（含自证），A 为留一局（无自证）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
