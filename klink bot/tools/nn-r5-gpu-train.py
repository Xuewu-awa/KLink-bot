"""
用 GPU（RTX 5060）**逐字复算** C# NNTrain 的同一份训练算式，把每 epoch 从 ~90 s 压到秒级。

⚠️ 这不是「换优化器/调参」，是**同一个算式换个执行器**：一个数据一个字节不差地喂进去，
   用数学上完全相同的更新式（全量梯度下降，lr/n 缩放），只在 GPU 上算。
   正确性**必须**用 C# 的参考曲线验证（见 verify_against_csharp）。

与 tools/NNTrain/Program.cs 的对应关系（逐条对齐）：
  * 数据        ：magic 'AKL2'，每条 float v[745] + outcome + gid + pairId
  * 标准化      ：mean/std 在**全量 n 条**上算（float32），x → (x-mean)/std
  * 切分        ：gids.Distinct() 保持首次出现序 → new Random(12345) 逐次 Next() → OrderBy(key)
                  （LINQ OrderBy 是**稳定**排序）→ 取前 15%
  * 初始化      ：new Random(42)；Range = sqrt(2/fan)，Seq = 2*NextDouble()-1（减法发生器）
  * 前向        ：h = ReLU(W1 x + B1)，z = W2·h（无 B2）
  * 损失/反传   ：BCE(sigmoid(z), t)；dL/dz = sigmoid(z) − t
  * 更新        ：**每 epoch 累加全部训练样本的梯度，再一次性** w -= (lr/used) * g
                  ← 这是 C# 的 `float sc = lr / Math.Max(1, used)`（Program.cs L511）
  * 报数        ：每 epoch 报 loss（/used）与留出准确率；留出 = 切分出的验证局

用法：
  python -X utf8 "klink bot/tools/nn-r5-gpu-train.py" --data out/_r5-prefix-10k-of-100k.bin \
      --epochs 150 --eval-every 1 --out out/_r5-gpu-10k.bin --log out/_r5-gpu-10k.txt
"""

import argparse
import json
import struct
import sys
import time
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

MAGIC_V2 = 0x324C4B41
CHUNK = 262144          # 每次搬多少条到 GPU（262144×745×4B = 781 MB）


# ==================== .NET Random(seed) 的逐位复刻 ====================
# 与 nn_common.DotNetRandom 相同；这里复制一份以便权重初始化也能复刻（NextDouble）。

class DotNetRandom:
    _INT_MAX = 0x7FFFFFFF

    def __init__(self, seed: int):
        imax = self._INT_MAX
        subtraction = imax if seed == -2**31 else abs(seed)
        mj = 161803398 - subtraction
        sa = [0] * 56
        sa[55] = mj
        mk = 1
        ii = 0
        for _ in range(1, 55):
            ii += 21
            if ii >= 55:
                ii -= 55
            sa[ii] = mk
            mk = mj - mk
            if mk < 0:
                mk += imax
            mj = sa[ii]
        for _ in range(1, 5):
            for i in range(1, 56):
                n = i + 30
                if n >= 55:
                    n -= 55
                sa[i] -= sa[1 + n]
                if sa[i] < 0:
                    sa[i] += imax
        self._seed_array = sa
        self._inext = 0
        self._inextp = 21

    def _internal_sample(self) -> int:
        sa = self._seed_array
        inext = self._inext + 1
        if inext >= 56:
            inext = 1
        inextp = self._inextp + 1
        if inextp >= 56:
            inextp = 1
        ret = sa[inext] - sa[inextp]
        if ret == self._INT_MAX:
            ret -= 1
        if ret < 0:
            ret += self._INT_MAX
        sa[inext] = ret
        self._inext = inext
        self._inextp = inextp
        return ret

    def next(self) -> int:
        return self._internal_sample()

    def next_double(self) -> float:
        """`Random.NextDouble()` = InternalSample() * (1.0 / int.MaxValue)。"""
        return self._internal_sample() * (1.0 / self._INT_MAX)


# ==================== 数据 & 切分 ====================

def load(path, max_games=None, log=print):
    size = Path(path).stat().st_size
    with open(path, "rb") as f:
        magic, dim, decks = struct.unpack("<iii", f.read(12))
    if magic != MAGIC_V2:
        raise SystemExit(f"这不是 v2 dump（magic={magic:#x}）")
    ncol = dim + 3
    n_total = (size - 12) // (ncol * 4)
    log(f"  数据 {Path(path).name}: {n_total:,} 条 × {dim} 维（{size/1e9:.2f} GB）")
    rec = np.memmap(path, dtype=np.float32, mode="r", offset=12, shape=(n_total, ncol))
    y = np.asarray(rec[:, dim], dtype=np.float32)
    gid = np.asarray(rec[:, dim + 1], dtype=np.int64)
    if max_games is not None:
        keep = gid < max_games
        y, gid = y[keep], gid[keep]
        X = np.asarray(rec[keep, :dim], dtype=np.float32)
    else:
        X = np.asarray(rec[:, :dim], dtype=np.float32)
    del rec
    return X, y, gid, dim, decks


def split(gid, split_seed=12345):
    """逐位复刻：Distinct() 保序 → Random(seed).Next() 做键 → 稳定排序 → 前 15% 为验证局。"""
    uniq = []
    seen = set()
    for g in gid.tolist():
        if g not in seen:
            seen.add(g)
            uniq.append(g)
    rng = DotNetRandom(split_seed)
    keyed = [(rng.next(), i, g) for i, g in enumerate(uniq)]
    keyed.sort(key=lambda t: t[0])
    ordered = [g for _, _, g in keyed]
    n_val = max(1, int(len(ordered) * 0.15))
    val_games = np.asarray(ordered[:n_val], dtype=np.int64)
    is_val = np.zeros(int(gid.max()) + 1, dtype=bool)
    is_val[val_games] = True
    val_mask = is_val[gid]
    return val_mask, val_games


def init_weights(dim, hidden, seed=42):
    """复刻 C#：Scale(fan) = sqrt(2/fan) * (2*NextDouble() − 1)。"""
    rng = DotNetRandom(seed)
    W1 = np.empty((hidden, dim), dtype=np.float32)
    for i in range(W1.size):
        W1.flat[i] = float(np.sqrt(2.0 / dim) * (rng.next_double() * 2 - 1))
    W2 = np.empty(hidden, dtype=np.float32)
    for j in range(hidden):
        W2[j] = float(np.sqrt(2.0 / hidden) * (rng.next_double() * 2 - 1))
    B1 = np.zeros(hidden, dtype=np.float32)
    return W1, B1, W2


# ==================== 训练 ====================

def run(X, y, gid, args, log=print):
    dev = "cuda"
    n, dim = X.shape
    hidden = args.hidden

    val_mask, val_games = split(gid, args.split_seed)
    tr_idx = np.flatnonzero(~val_mask)
    va_idx = np.flatnonzero(val_mask)
    n_train = len(tr_idx)
    log(f"  训练 {n_train:,} / 验证 {len(va_idx):,}  （按对局切分，验证集 {len(val_games):,} 局）")
    log(f"  验证局前 5 个: {list(val_games[:5])}   验证局 gid 求和 = {int(val_games.sum()):,}")

    # 标准化：与 C# 一样在**全量**上算（float32；GPU 上做，避免 CPU 端再占一份大数组）
    t0 = time.time()
    Xt = torch.from_numpy(np.ascontiguousarray(X))    # CPU（可写副本，避免 torch 警告）
    s1 = torch.zeros(dim, dtype=torch.float64, device=dev)
    s2 = torch.zeros(dim, dtype=torch.float64, device=dev)
    for s in range(0, n, CHUNK):
        c = Xt[s:s + CHUNK].to(dev, non_blocking=False).double()
        s1 += c.sum(0)
        s2 += (c * c).sum(0)
    mean = (s1 / n)
    var = (s2 / n) - mean * mean
    std = torch.sqrt(torch.clamp(var, min=0)) + 1e-6
    mean = mean.float()
    std = std.float()
    log(f"  标准化统计量用时 {time.time()-t0:.1f}s")

    W1, B1, W2 = init_weights(dim, hidden)
    W1t = torch.from_numpy(W1).to(dev)
    B1t = torch.from_numpy(B1).to(dev)
    W2t = torch.from_numpy(W2).to(dev)

    tr_cpu = torch.from_numpy(tr_idx)             # CPU 索引：用来从 CPU 上的 Xt 里 gather
    va_cpu = torch.from_numpy(va_idx)
    tr = tr_cpu.to(dev)                           # GPU 索引：用来索引已搬到 GPU 的 y
    va = va_cpu.to(dev)
    yt = torch.from_numpy(np.ascontiguousarray(y)).to(dev)

    def forward_chunk(idx_cpu, idx_dev):
        """返回 (loss_sum, grad_sum...)；idx_cpu 用于取数据，idx_dev 用于取标签。"""
        c = Xt[idx_cpu].to(dev, non_blocking=True)
        z0 = (c - mean) / std
        h = torch.relu(z0 @ W1t.T + B1t)
        o = h @ W2t
        p = torch.sigmoid(o)
        t = yt[idx_dev]
        loss = (-t * torch.log(p + 1e-7) - (1 - t) * torch.log(1 - p + 1e-7)).sum()
        d = (p - t).unsqueeze(1)                      # dL/dz
        gW2 = (d.squeeze(1).unsqueeze(0) @ h).squeeze(0)
        dh = d * W2t.unsqueeze(0)
        dh = dh * (h > 0).float()
        gW1 = dh.T @ z0
        gB1 = dh.sum(0)
        return loss, gW1, gB1, gW2

    hist = []
    log("")
    log("  epoch      loss        留出      训练")
    for ep in range(1, args.epochs + 1):
        t0 = time.time()
        gW1 = torch.zeros_like(W1t)
        gB1 = torch.zeros_like(B1t)
        gW2 = torch.zeros_like(W2t)
        loss_sum = 0.0
        used = 0
        for s in range(0, n_train, CHUNK):
            l, a, b, c2 = forward_chunk(tr_cpu[s:s + CHUNK], tr[s:s + CHUNK])
            loss_sum += float(l)
            gW1 += a
            gB1 += b
            gW2 += c2
            used += min(CHUNK, n_train - s)
        sc = args.lr / max(1, used)
        with torch.no_grad():
            W1t -= sc * gW1
            B1t -= sc * gB1
            W2t -= sc * gW2
        t_train = time.time() - t0

        if ep % args.eval_every == 0 or ep == args.epochs:
            correct = tr_correct = 0
            with torch.no_grad():
                for arr_cpu, arr_dev, want_tr in ((va_cpu, va, False), (tr_cpu, tr, True)):
                    hit = 0
                    for s in range(0, len(arr_cpu), CHUNK):
                        idx_cpu = arr_cpu[s:s + CHUNK]
                        c = Xt[idx_cpu].to(dev)
                        z0 = (c - mean) / std
                        h = torch.relu(z0 @ W1t.T + B1t)
                        o = h @ W2t
                        hit += int(((o > 0) == (yt[arr_dev[s:s + CHUNK]] > 0.5)).sum())
                    if want_tr:
                        tr_correct = hit
                    else:
                        correct = hit
            acc = correct / len(va)
            tracc = tr_correct / n_train
            hist.append(dict(epoch=ep, loss=loss_sum / max(1, used), acc=acc,
                             train_acc=tracc, sec=t_train))
            log(f"  {ep:5d}  {loss_sum/max(1,used):.6f}  {acc:7.2%}  {tracc:7.2%}"
                f"   ({t_train:.1f}s/epoch)")

    best = max(hist, key=lambda h: h["acc"])
    log("")
    log(f"  最佳验证准确率 {best['acc']:.2%} @ epoch {best['epoch']}"
        f"   最后一点 {hist[-1]['acc']:.2%} @ epoch {hist[-1]['epoch']}")
    return dict(hist=hist, mean=mean.cpu().numpy(), std=std.cpu().numpy(),
                W1=W1t.cpu().numpy(), B1=B1t.cpu().numpy(), W2=W2t.cpu().numpy(),
                n_train=n_train, n_val=len(va_idx), val_games=len(val_games),
                dim=dim, hidden=hidden)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="out/nn-data-100k.bin")
    ap.add_argument("--epochs", type=int, default=150)
    ap.add_argument("--hidden", type=int, default=64)
    ap.add_argument("--lr", type=float, default=0.05)
    ap.add_argument("--split-seed", type=int, default=12345)
    ap.add_argument("--eval-every", type=int, default=1)
    ap.add_argument("--out", default="out/_r5-gpu-model.bin")
    ap.add_argument("--log", default="out/_r5-gpu.txt")
    a = ap.parse_args()

    lines = []

    def log(*m):
        s = " ".join(str(x) for x in m)
        print(s, flush=True)
        lines.append(s)

    log(f"GPU: {torch.cuda.get_device_name(0)}   torch {torch.__version__}  cuda {torch.version.cuda}")
    log(f"目标: win（BCE）  结构 {a.hidden} 隐藏层  lr {a.lr}  epochs {a.epochs}"
        f"  split-seed {a.split_seed}")
    X, y, gid, dim, decks = load(REPO / a.data, log=log)
    log(f"  样本 {len(y):,}  维度 {dim}  对局 {int(gid.max())+1:,}  卡组 {decks}")
    res = run(X, y, gid, a, log=log)

    json.dump({k: v for k, v in res.items() if k != "hist"} | {"hist": res["hist"]},
              open(REPO / (a.log + ".json"), "w", encoding="utf-8"),
              ensure_ascii=False, indent=1, default=lambda o: o.tolist())
    (REPO / a.log).write_text("\n".join(lines), encoding="utf-8")
    log(f"\n→ {a.log}")


if __name__ == "__main__":
    main()
