"""第六轮：用 **Python 侧独立复算** 校验 `nn-r6-gpu-train.py` 报出的留出准确率。

训练器报的 `acc` 是训练过程中最后一轮（或最好一轮）的读数；这个脚本从存下来的 npz
**重新读入权重**，在**同一份固定留出清单**上重算一遍 —— 这是「报出的数字不是记账错误」的证据。
（C# `verify` 的独立复算见 nn-r6-pack-model.py + NNTrain verify，是第三重。）

用法：
  python -X utf8 "klink bot/tools/nn-r6-verify-npz.py" --npz out/_r6-weights.npz --tag S3-adam \
      --data out/nn-data-100k.bin --max-games 50000
"""

import argparse
import json
import struct
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
MAGIC_V2 = 0x324C4B41


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--npz", required=True, help="形如 out/_r6-w50k.npz.<tag>.npz 或单个 .npz")
    ap.add_argument("--tag", default="")
    ap.add_argument("--data", default="out/nn-data-100k.bin")
    ap.add_argument("--max-games", type=int, default=0)
    ap.add_argument("--holdout", default="out/_r6-holdout-games.txt")
    ap.add_argument("--result", default="", help="训练器 JSON（用来对照它报的数字）")
    ap.add_argument("--out", default="out/_r6-verify-npz.txt")
    a = ap.parse_args()

    npz = Path(a.npz)
    if a.tag and not str(npz).endswith(f".{a.tag}.npz"):
        cand = Path(str(npz) + f".{a.tag}.npz")
        if cand.exists():
            npz = cand
    z = np.load(npz)
    W1, B1, W2, mean, std = z["W1"], z["B1"], z["W2"], z["mean"], z["std"]
    hidden, dim = W1.shape
    val_games = np.sort(np.loadtxt(REPO / a.holdout, dtype=np.int64))

    with open(REPO / a.data, "rb") as f:
        magic, d2, decks = struct.unpack("<iii", f.read(12))
    assert magic == MAGIC_V2 and d2 == dim, (magic, d2, dim)
    ncol = dim + 3
    n = (Path(REPO / a.data).stat().st_size - 12) // (ncol * 4)
    rec = np.memmap(REPO / a.data, dtype=np.float32, mode="r", offset=12, shape=(n, ncol))
    if a.max_games:
        gid_all = np.asarray(rec[:, dim + 1], dtype=np.int64)
        keep = gid_all < a.max_games
        rec = rec[keep]
        n = int(keep.sum())
        val_games = val_games[val_games < a.max_games]
    y = np.asarray(rec[:, dim], dtype=np.float32)
    gid = np.asarray(rec[:, dim + 1], dtype=np.int64)
    is_val = np.zeros(int(gid.max()) + 1, dtype=bool)
    is_val[val_games] = True
    ho = is_val[gid]
    w = min(hidden, 64)

    correct = 0
    tot = 0
    tr_correct = 0
    tr_tot = 0
    for s in range(0, n, 65536):
        e = min(n, s + 65536)
        c = np.asarray(rec[s:e, :dim], dtype=np.float32)
        zz = (c - mean) / std
        h = np.maximum(zz @ W1.T + B1, 0.0)
        o = h @ W2
        ok = (o > 0) == (y[s:e] > 0.5)
        m = ho[s:e]
        correct += int(ok[m].sum())
        tot += int(m.sum())
        tr_correct += int(ok[~m].sum())
        tr_tot += int((~m).sum())
        del c, zz, h, o, ok, m
    acc = correct / tot
    tracc = tr_correct / tr_tot

    lines = []
    lines.append("=" * 92)
    lines.append(f"第六轮 Python 独立复算  {npz.name}")
    lines.append("=" * 92)
    lines.append(f"  结构 {dim}→{hidden}→1   数据 {Path(a.data).name}"
                 + (f"（前 {a.max_games:,} 局）" if a.max_games else "（全量）"))
    lines.append(f"  留出清单 {a.holdout}   {len(val_games):,} 局 / {tot:,} 条")
    lines.append(f"  → 留出准确率 {acc:.4%}（{correct:,}/{tot:,}）   训练 {tracc:.4%}")
    reported = None
    if a.result:
        d = json.loads((REPO / a.result).read_text(encoding="utf-8"))
        r = d.get(a.tag, d) if a.tag else d
        if isinstance(r, dict) and "hist" in r:
            fin = r["hist"][-1]
            reported = (fin["acc"], r["best_acc"])
            lines.append(f"  训练器报的：最后一点 {fin['acc']:.4%}（ep {fin['epoch']}）"
                         f"   峰值 {r['best_acc']:.4%} @ep {r['best_epoch']}")
            lines.append(f"  ★ 复算 vs 最后一点：{100*(acc-fin['acc']):+.4f} 点")
    (REPO / a.out).write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main())
