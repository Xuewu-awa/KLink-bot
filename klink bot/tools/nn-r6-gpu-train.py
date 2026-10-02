"""
第六轮：**改训练配方**（优化器 / 批次 / 初始化 / 宽度），不动数据、不动编码。

背景（第五轮结论）：C# 的更新式是 `w -= (lr/used) * Σg`（Program.cs L511）——
**一个 epoch 只有一次参数更新**。150 epoch = 150 步梯度、约 47k 个参数 ⇒ 纯欠拟合。

本脚本与 `nn-r5-gpu-train.py` 的关系：
  * **损失函数一字不改**：BCE(sigmoid(z), t)，dL/dz = sigmoid(z) − t（见 loss_and_grads）；
  * **数据一字不改**：同一个 dump 文件、同一个 745 维编码、同一个 label（`--target win`）；
  * **标准化一字不改**：mean/std 在**全量 n 条**上算（fp64 累加，C# 是 fp32 累加）；
  * **切分一字不改**：逐位复刻 `new Random(12345)` + LINQ 稳定排序 + 前 15% 为验证局；
  * **改的只有**：梯度怎么用（全量 GD → mini-batch）、步长怎么定（朴素 SGD → Adam）、
    W1 初始化分布（均匀 → He）、隐藏层宽度（64 → 256）。

⚠️ 与 C# 的可比性边界：换了优化器之后**中间权重不可能与 C# 逐位相同**（不是同一个算法）。
   为了仍然能自证「没换数据、没换损失」，脚本做三件事：
   1. `--recipe control` **逐位复刻第五轮的算式**，在 --epochs 150 上必须报出 73.90%；
   2. 每次运行都打印**数据文件的 SHA256** 与**留出局清单的 SHA256**；
   3. 保存的权重可以直接用 C# `verify` 复算（见 nn-r6-pack-model.py）。

★ 固定的留出协议（第六轮新增，解决第五轮「两列留出不是同一批」）：
   留出局清单 = `new Random(12345)` 在 **10 万局的全部 gid** 上洗牌后的前 15,000 局，
   存进 `out/_r6-holdout-games.txt`（一行一个 gid），并在每个产物 JSON 里记它的 SHA256。
   **所有配置（含线性基线）都用这一份评**，所以配置之间可比。

用法：
  python -X utf8 "klink bot/tools/nn-r6-gpu-train.py" --recipe control --epochs 150 --tag ctl150
  python -X utf8 "klink bot/tools/nn-r6-gpu-train.py" --recipe mb --batch 256 --lr 1.5 --epochs 300 --tag s2
"""

import argparse
import copy
import hashlib
import json
import math
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
CHUNK = 65_536             # 全量 GD 的梯度累积分块
EVAL_CHUNK = 65_536        # 评估前向的分块（显存紧张，别开大）
LOAD_CHUNK = 65_536        # 装载分块


# ==================== .NET Random(seed) 的逐位复刻（与 nn_common 相同）====================

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
        return self._internal_sample() * (1.0 / self._INT_MAX)


def sha256_file(path: Path, prefix_bytes: int | None = None) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        left = prefix_bytes
        while True:
            want = 1 << 22 if left is None else min(1 << 22, left)
            if want <= 0:
                break
            b = f.read(want)
            if not b:
                break
            h.update(b)
            if left is not None:
                left -= len(b)
    return h.hexdigest()


# ==================== 数据 ====================

def load_meta(path: Path, max_games: int = 0):
    size = path.stat().st_size
    with open(path, "rb") as f:
        magic, dim, decks = struct.unpack("<iii", f.read(12))
    if magic != MAGIC_V2:
        raise SystemExit(f"这不是 v2 dump（magic={magic:#x}）")
    n = (size - 12) // ((dim + 3) * 4)
    return dict(path=path, size=size, dim=dim, decks=decks, n=n, ncol=dim + 3,
                max_games=max_games, file_n=n)


def load_to_gpu(meta, log=print, store="fp32"):
    """把全量 X 搬上 GPU，标签/对局号留在 CPU。返回 (Xg, y_np, gid_np)。

    `store="fp16"`：数据以 **fp16 存储**（3.22 GiB，而 fp32 要 6.44 GiB）——本机 8 GiB 卡上
    fp32 会一路顶到 7.7~8.3 GiB 并触发 WDDM 换页（实测每 epoch 0.2 s → 11 s）。
    ⚠️ 存储精度与**计算精度**是两件事：均值/方差仍用 fp64 累加、标准化后仍是 fp32、
    权重与梯度全程 fp32。fp16 只把输入量化到 ~5e-4 相对误差，
    对结果的影响在 §7 用「control 前 150 epoch 的留出 vs fp32 的 73.897%」实测过。

    `--max-games N` 时只搬 gid < N 的前 N 局 —— 因为 dump 的第 g 局只由 (seed,g) 决定，
    前 N 局在任何更大 dump 里**逐位相同**，所以「只取前 N 局」= 「一个更小的、前缀一致的数据集」，
    与小数据文件上的实验严格可比（第五轮 §4.1 已经证明过这个前缀关系）。
    """
    n, dim, ncol = meta["n"], meta["dim"], meta["ncol"]
    rec = np.memmap(meta["path"], dtype=np.float32, mode="r", offset=12, shape=(n, ncol))
    if meta["max_games"]:
        gid_all = np.asarray(rec[:, dim + 1], dtype=np.int64)
        keep = gid_all < meta["max_games"]
        n = int(keep.sum())
        rec = rec[keep]
        meta["n"] = n
        log(f"  --max-games {meta['max_games']:,} ⇒ 只用前 {n:,} 条（"
            f"{meta['file_n']:,} 中的前缀）")
    y = np.asarray(rec[:, dim], dtype=np.float32).copy()
    gid = np.asarray(rec[:, dim + 1], dtype=np.int64).copy()
    dt = torch.float16 if store == "fp16" else torch.float32
    Xg = torch.empty((n, dim), dtype=dt, device="cuda")
    t0 = time.time()
    for s in range(0, n, LOAD_CHUNK):
        e = min(n, s + LOAD_CHUNK)
        a = np.array(rec[s:e, :dim], dtype=np.float32, copy=True)   # 可写副本，避开 torch 警告
        Xg[s:e] = torch.from_numpy(a).to(dtype=dt, device="cuda", non_blocking=True)
        del a
    del rec
    torch.cuda.synchronize()
    torch.cuda.empty_cache()
    log(f"  数据上卡 {time.time()-t0:.1f}s   精度 {store}   "
        f"显存已分配 {torch.cuda.memory_allocated()/2**30:.2f} GiB / "
        f"缓存池 {torch.cuda.memory_reserved()/2**30:.2f} GiB")
    return Xg, y, gid


def split_games(gid: np.ndarray, seed: int):
    """逐位复刻 C#：`gids.Distinct().OrderBy(_ => new Random(seed).Next())` 取前 15%。"""
    uniq, seen = [], set()
    for g in gid.tolist():
        if g not in seen:
            seen.add(g)
            uniq.append(g)
    rng = DotNetRandom(seed)
    keyed = [(rng.next(), i, g) for i, g in enumerate(uniq)]
    keyed.sort(key=lambda t: t[0])                    # LINQ OrderBy 是稳定排序
    ordered = np.asarray([g for _, _, g in keyed], dtype=np.int64)
    n_val = max(1, int(len(ordered) * 0.15))
    return ordered[:n_val], ordered[n_val:]


def holdout_mask(gid: np.ndarray, val_games: np.ndarray):
    """留出掩码：必须用**集合**判定（gid 可能不连续）。"""
    is_val = np.zeros(int(gid.max()) + 1, dtype=bool)
    is_val[val_games] = True
    return is_val[gid]


# ==================== 模型 / 损失（**与 C# 逐字相同**）====================

def init_weights(dim, hidden, recipe, seed=42, log=print):
    """control：`Scale(fan)=sqrt(2/fan)*(2u−1)`（C# 原样）；
       he     ：W1 ~ U(−√(6/fan_in), +√(6/fan_in))（He-uniform，Var=2/fan_in）；
                W2 ~ U(−√(6/hidden), +√(6/hidden))（Xavier-uniform，fan_in=fan_out=hidden）。"""
    rng = DotNetRandom(seed)
    if recipe == "control":
        W1 = np.empty((hidden, dim), dtype=np.float32)
        for i in range(W1.size):
            W1.flat[i] = float(np.sqrt(2.0 / dim) * (rng.next_double() * 2 - 1))
        W2 = np.empty(hidden, dtype=np.float32)
        for j in range(hidden):
            W2[j] = float(np.sqrt(2.0 / hidden) * (rng.next_double() * 2 - 1))
        B1 = np.zeros(hidden, dtype=np.float32)
    elif recipe == "he":
        g1 = math.sqrt(6.0 / dim)
        W1 = np.empty((hidden, dim), dtype=np.float32)
        for i in range(W1.size):
            W1.flat[i] = float(-g1 + 2 * g1 * rng.next_double())
        g2 = math.sqrt(6.0 / hidden)
        W2 = np.empty(hidden, dtype=np.float32)
        for j in range(hidden):
            W2[j] = float(-g2 + 2 * g2 * rng.next_double())
        B1 = np.zeros(hidden, dtype=np.float32)
    else:
        raise SystemExit(f"不认识的 init {recipe}")
    log(f"  初始化 {recipe}: W1 std={W1.std():.5f} (理论 "
        f"{(math.sqrt(6.0/dim)/math.sqrt(3)) if recipe=='he' else math.sqrt(2.0/dim)/math.sqrt(3):.5f})"
        f"  W2 std={W2.std():.5f}  B1=0")
    return (torch.from_numpy(W1).to("cuda"), torch.from_numpy(B1).to("cuda"),
            torch.from_numpy(W2).to("cuda"))


def loss_and_grads(c, t, W1, B1, W2):
    """★ **与 C# 逐字相同的损失与反传**（Program.cs L370-L397 / L470-L477）。

        前向：h = ReLU(W1 x + B1)；z = W2·h；p = sigmoid(z)
        损失：BCE(p, t) = −t·log(p+1e-7) − (1−t)·log(1−p+1e-7)
        反传：dL/dz = p − t（**没有别的项**）；dh = (p−t)·W2 ⊙ 1[h>0]
    `c` 是**已标准化**的输入（z-score 在外面做，与 C# 一致）。
    """
    h = torch.relu(c @ W1.T + B1)
    o = h @ W2
    p = torch.sigmoid(o)
    loss = (-t * torch.log(p + 1e-7) - (1 - t) * torch.log(1 - p + 1e-7)).sum()
    d = (p - t).unsqueeze(1)                    # dL/dz，形状 (m,1)
    gW2 = (d.squeeze(1) @ h)                    # (hidden,) —— Σ_r (p_r−t_r)·h_r，与 C# 的累加等价
    dh = d * W2.unsqueeze(0) * (h > 0).float()  # C#：if (h_j == 0) continue;
    gW1 = dh.T @ c
    gB1 = dh.sum(0)
    return loss, gW1, gB1, gW2


# ==================== 评估 ====================

def predict_logits(Xg, mean, std, W1, B1, W2, rows, chunk=None, want_dead=False):
    """分块前向，返回 logits（np.float32）与（可选）死 ReLU 比例。"""
    if chunk is None:
        chunk = EVAL_CHUNK
    out = np.empty(len(rows), dtype=np.float32)
    dead = 0
    hcount = 0
    for s in range(0, len(rows), chunk):
        r = rows[s:s + chunk]
        c = (Xg[r] - mean) / std
        h = torch.relu(c @ W1.T + B1)
        out[s:s + len(r)] = (h @ W2).cpu().numpy()
        if want_dead:
            dead += int((h <= 0).sum())
            hcount += h.numel()
    return out, (dead / max(1, hcount) if want_dead else None)


def acc_of(logits, y_rows):
    return float(((logits > 0) == (y_rows > 0.5)).mean())


# ==================== 训练主循环 ====================

def run(args, meta, log):
    n, dim = meta["n"], meta["dim"]
    torch.backends.cuda.matmul.allow_tf32 = False       # 与 C# fp32 可比
    torch.manual_seed(args.torch_seed)

    Xg, y_np, gid_np = load_to_gpu(meta, log=log, store=args.store)
    yt = torch.from_numpy(y_np).to("cuda")

    # ---------- 固定留出 ----------
    ho_games_path = REPO / args.holdout_games
    natural, _ = split_games(gid_np, args.split_seed)
    natural = np.sort(natural)
    if ho_games_path.exists():
        saved = np.sort(np.loadtxt(ho_games_path, dtype=np.int64))
        if args.max_games:              # 小数据子集：取「固定清单 ∩ 本子集」
            val_games = saved[saved < args.max_games]
            extra = f"（清单 {len(saved):,} 局 ∩ 前 {args.max_games:,} 局 = {len(val_games):,} 局）"
        else:
            val_games = saved
            assert np.array_equal(saved, natural), "存下来的留出清单与 --split-seed 的自然切分不一致"
            extra = "（与 --split-seed 的自然切分逐局相同）"
        log(f"  ★ 用固定留出清单 {args.holdout_games} {extra}")
    else:
        val_games = natural
        np.savetxt(ho_games_path, val_games, fmt="%d")
        log(f"  ★ 写出固定留出局清单 {args.holdout_games}（{len(val_games):,} 局）")
    ho_mask = holdout_mask(gid_np, val_games)
    tr_mask = ~ho_mask
    ho_rows = np.flatnonzero(ho_mask)
    tr_rows = np.flatnonzero(tr_mask)
    ho_sha = sha256_file(ho_games_path)
    log(f"  留出清单 SHA256 {ho_sha}")
    log(f"  训练 {len(tr_rows):,} 条 / 留出 {len(ho_rows):,} 条（{len(val_games):,} 局）"
        f"  留出局 gid 求和 = {int(val_games.sum()):,}")

    # ---------- 标准化（**全量**上算，与 C# 一致；fp64 累加以免 2.3M 行累加掉的精度）----------
    t0 = time.time()
    s1 = torch.zeros(dim, dtype=torch.float64, device="cuda")
    s2 = torch.zeros(dim, dtype=torch.float64, device="cuda")
    for s in range(0, n, CHUNK):
        c = Xg[s:min(n, s + CHUNK)].double()
        s1 += c.sum(0)
        s2 += (c * c).sum(0)
    mean64 = s1 / n
    var = s2 / n - mean64 * mean64
    std = torch.sqrt(torch.clamp(var, min=0)) + 1e-6
    mean = mean64.float()
    std = std.float()
    log(f"  标准化统计量 {time.time()-t0:.1f}s   mean|.|平均 {mean.abs().mean():.4f}  "
        f"std 平均 {std.mean():.4f}  std 最小 {std.min():.6f}")

    # ---------- 权重 ----------
    W1, B1, W2 = init_weights(dim, args.hidden, args.init, seed=args.init_seed, log=log)
    nparam = W1.numel() + B1.numel() + W2.numel()
    log(f"  结构 {dim}→{args.hidden}→1  参数 {nparam:,}")

    # ---------- 优化器 ----------
    recipe = args.recipe
    if recipe == "control":
        opt_kind, bs = "fullgd", 0
    elif recipe == "mb":
        opt_kind, bs = "sgd", args.batch
    elif recipe == "adam":
        opt_kind, bs = "adam", args.batch
    else:
        raise SystemExit(f"不认识的 recipe {recipe}")
    n_train = len(tr_rows)
    steps_per_epoch = 1 if opt_kind == "fullgd" else math.ceil(n_train / bs)
    log(f"  配方 recipe={recipe}  优化器={opt_kind}  batch={bs or n_train}  lr={args.lr}"
        f"  ⇒ **每 epoch {steps_per_epoch:,} 次参数更新**")
    log(f"  epochs={args.epochs}  patience={args.patience}（连续这么多个 epoch 没有超过 "
        f"{args.min_delta} 点的新最好留出就停）  eval-every={args.eval_every}")
    opt_detail = (f"Adam(beta1={args.beta1}, beta2={args.beta2}, eps={args.eps})"
                  if opt_kind == "adam" else
                  "全量 GD：w -= (lr/used)·Σg（C# L511 原式）" if opt_kind == "fullgd"
                  else "朴素 SGD：w -= lr·g_batch")
    log(f"  优化器细节 {opt_detail}   梯度归一={args.grad_norm}"
        + ("（批内求和后 /m 换成批均值）" if args.grad_norm == "mean" else "（**不做归一**）"))

    tr_t = torch.from_numpy(tr_rows).to("cuda")
    ho_t = torch.from_numpy(ho_rows).to("cuda")

    hist = []
    best = -1.0
    best_ep = 0
    bad = 0
    stop_reason = "跑到 epochs 上限"
    t_start = time.time()
    train_sec = 0.0            # 累计「纯训练（不含评估）」时间
    cur_epochs = 0             # 累计 epoch 数（用于算每 epoch 纯训练耗时）
    ep = 0

    def eval_now(want_train=True):
        lg, dead = predict_logits(Xg, mean, std, W1, B1, W2, ho_rows, want_dead=True)
        a = acc_of(lg, y_np[ho_rows])
        del lg
        if not want_train:
            return a, float("nan"), dead
        tr_lg, _ = predict_logits(Xg, mean, std, W1, B1, W2, tr_rows)
        ta = acc_of(tr_lg, y_np[tr_rows])
        del tr_lg
        return a, ta, dead

    while ep < args.epochs:
        ep += 1
        t0 = time.time()
        loss_acc = 0.0
        used = 0
        gn = 0.0
        if opt_kind == "fullgd":
            gW1 = torch.zeros_like(W1)
            gB1 = torch.zeros_like(B1)
            gW2 = torch.zeros_like(W2)
            for s in range(0, n_train, CHUNK):
                r = tr_t[s:s + CHUNK]
                c = (Xg[r] - mean) / std
                l, a, b, cc = loss_and_grads(c, yt[r], W1, B1, W2)
                loss_acc += float(l)
                gW1 += a
                gB1 += b
                gW2 += cc
                used += len(r)
            sc = args.lr / max(1, used)
            gn = float(torch.sqrt((gW1**2).sum() + (gB1**2).sum() + (gW2**2).sum()))
            with torch.no_grad():
                W1 -= sc * gW1
                B1 -= sc * gB1
                W2 -= sc * gW2
            del gW1, gB1, gW2
        else:
            perm = torch.randperm(n_train, device="cuda")
            for s in range(0, n_train, bs):
                r = tr_t[perm[s:s + bs]]
                c = (Xg[r] - mean) / std
                t = yt[r]
                m = len(r)
                l, gW1, gB1, gW2 = loss_and_grads(c, t, W1, B1, W2)
                loss_acc += float(l)
                used += m
                # ⚠️ 关键：loss_and_grads 返回的是**批内求和**的梯度（与 C# 的全量累加同构）。
                #    mini-batch 里必须换成**批均值**再交给 SGD/Adam（= 标准做法），
                #    否则 lr 实际被放大 m 倍（=256）—— 第一步就把 ReLU 全推死。
                #    实测证据见 out/_r6-probe-lr.txt 的 `a-sgd0.05:sum` 一行：
                #    ep1 |g|=1.6e6 → ep2 起「死ReLU 100%」、loss 卡在 0.6931、留出 45.75%（= 全判左负）。
                if args.grad_norm == "mean":
                    gW1 = gW1 / m
                    gB1 = gB1 / m
                    gW2 = gW2 / m
                gn += float((gW1**2).sum() + (gB1**2).sum() + (gW2**2).sum())
                with torch.no_grad():
                    if opt_kind == "sgd":
                        W1 -= args.lr * gW1
                        B1 -= args.lr * gB1
                        W2 -= args.lr * gW2
                    else:                                   # Adam
                        if ep == 1 and s == 0:
                            mW1 = torch.zeros_like(W1); vW1 = torch.zeros_like(W1)
                            mB1 = torch.zeros_like(B1); vB1 = torch.zeros_like(B1)
                            mW2 = torch.zeros_like(W2); vW2 = torch.zeros_like(W2)
                            step = 0
                        step += 1
                        b1p, b2p, e = args.beta1, args.beta2, args.eps
                        for P, G, M, V in ((W1, gW1, mW1, vW1), (B1, gB1, mB1, vB1),
                                           (W2, gW2, mW2, vW2)):
                            M.mul_(b1p).add_(G, alpha=1 - b1p)
                            V.mul_(b2p).addcmul_(G, G, value=1 - b2p)
                            mh = M / (1 - b1p ** step)
                            vh = V / (1 - b2p ** step)
                            P.addcdiv_(mh, vh.sqrt().add_(e), value=-args.lr)
            gn = math.sqrt(gn / max(1, (n_train + bs - 1) // bs))
        dt = time.time() - t0
        train_sec += dt
        cur_epochs += 1
        if ep % args.eval_every == 0 or ep == args.epochs:
            want_train = (ep % args.eval_train_every == 0) or ep == args.epochs \
                or args.eval_train_every <= 1
            te0 = time.time()
            a, ta, dead = eval_now(want_train=want_train)
            eval_sec = time.time() - te0
            rec = dict(epoch=ep, loss=loss_acc / max(1, used), acc=a,
                       train_acc=(ta if want_train else None),
                       grad_norm=gn, dead_relu=dead, sec=dt + eval_sec,
                       sec_train=dt, sec_eval=eval_sec, updates=steps_per_epoch * ep,
                       elapsed=time.time() - t_start,
                       vram_gib=torch.cuda.memory_reserved() / 2**30)
            hist.append(rec)
            ta_txt = f"{ta:7.3%}" if want_train else "   (跳过)"
            log(f"  ep {ep:5d}  loss {rec['loss']:.6f}  留出 {a:7.3%}  训练 {ta_txt}"
                f"   |g| {gn:.4g}  死ReLU {dead:6.2%}  {dt:.2f}s"
                f"  [{dt:.2f}s 训 + {eval_sec:.2f}s 评]  显存 {rec['vram_gib']:.2f}G")
            if a > best + args.min_delta:
                best, best_ep, bad = a, ep, 0
            else:
                bad += 1
                if args.patience and bad >= args.patience:
                    stop_reason = (f"连续 {bad} 个 epoch 没有比 {best:.3%}（ep {best_ep}）"
                                   f"高 {args.min_delta} 点以上")
                    log(f"\n  ★ 早停：{stop_reason}")
                    break
        elif ep % 10 == 0:
            log(f"  ep {ep:5d}  loss {loss_acc/max(1,used):.6f}  ({dt:.2f}s/epoch, 只训不评)")
        # ⚠️ 显存卫生：不复用缓存块会一路涨到 7.5 GiB（8 GiB 卡）并触发 WDDM 换页，
        #    实测每 epoch 从 0.2 s 涨到 11 s（见 §7）。每轮清一次缓存池，曲线才可比。
        torch.cuda.empty_cache()
        if args.time_budget and (time.time() - t_start) > args.time_budget:
            stop_reason = f"到达时间预算 {args.time_budget}s"
            log(f"\n  ★ 停止：{stop_reason}")
            break

    total = time.time() - t_start
    peak = torch.cuda.max_memory_allocated() / 2**30
    log("")
    log(f"  训练结束：{stop_reason}")
    log(f"  最佳留出 {best:.3%} @ epoch {best_ep}   最后一点 {hist[-1]['acc']:.3%} @ ep {hist[-1]['epoch']}")
    log(f"  总时长 {total:.1f}s  （{total/max(1,ep):.2f}s/epoch）  峰值显存 {peak:.2f} GiB")

    res = dict(tag=args.tag, recipe=recipe, optimizer=opt_kind, batch=bs, lr=args.lr,
               hidden=args.hidden, init=args.init, epochs_run=ep, epochs_cap=args.epochs,
               updates_total=steps_per_epoch * ep, steps_per_epoch=steps_per_epoch,
               n_param=nparam, stop_reason=stop_reason,
               best_acc=best, best_epoch=best_ep,
               final=hist[-1], total_sec=total, peak_vram_gib=peak,
               n=meta["n"], dim=dim, n_train=n_train, n_val=len(ho_rows),
               n_val_games=len(val_games), val_games_sum=int(val_games.sum()),
               max_games=args.max_games, eval_every=args.eval_every,
               sec_train_per_epoch=train_sec / max(1, cur_epochs),
               data_file=meta["path"].name, data_size=meta["size"],
               split_seed=args.split_seed, holdout_sha256=ho_sha,
               data_sha256_prefix_64mb=args.data_sha,
               tf32=False, grad_norm=args.grad_norm, store=args.store,
               loss="BCE(sigmoid(z),t)  dL/dz=p-t（与 C# 逐字相同）",
               hist=hist)
    return res, dict(mean=mean.cpu().numpy(), std=std.cpu().numpy(),
                     W1=W1.detach().cpu().numpy(), B1=B1.detach().cpu().numpy(),
                     W2=W2.detach().cpu().numpy())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="out/nn-data-100k.bin")
    ap.add_argument("--max-games", type=int, default=0,
                    help="只用 gid < N 的前 N 局（0 = 全部）。用于快速探针，前缀一致")
    ap.add_argument("--store", default="fp32", choices=["fp32", "fp16"],
                    help="数据在显存里的存储精度（计算仍全程 fp32）")
    ap.add_argument("--recipe", default="control", choices=["control", "mb", "adam"])
    # ⚠️ 默认值 batch 4096 / lr 0.004 来自 `batch4k` 轮的同数据同超参对照
    #    （klink bot/docs/内核补全队列.md「任务 B」）：三组 4096 的最佳留出 81.548%~81.603%
    #    vs batch 256 的 81.653%，差 1~1.6 个标准误（统计上分不出来），而墙上时间 1321s → 95s。
    #    lr 取 sqrt 缩放（×4）而不是线性（×16）：16e-3 末段不稳（seed42 末点 80.699%）。
    #    只改默认值，算式一行没动。
    ap.add_argument("--batch", type=int, default=4096)
    ap.add_argument("--lr", type=float, default=0.004)
    ap.add_argument("--hidden", type=int, default=64)
    ap.add_argument("--init", default="control", choices=["control", "he"])
    ap.add_argument("--init-seed", type=int, default=42)
    ap.add_argument("--torch-seed", type=int, default=42, help="只影响 mini-batch 的洗牌顺序")
    ap.add_argument("--epochs", type=int, default=150)
    ap.add_argument("--eval-every", type=int, default=1)
    ap.add_argument("--eval-train-every", type=int, default=10,
                    help="每多少个 epoch 顺便算一次训练集准确率（留出每轮都算）；1 = 每轮都算")
    ap.add_argument("--split-seed", type=int, default=12345)
    ap.add_argument("--patience", type=int, default=0, help="0 = 不早停")
    ap.add_argument("--min-delta", type=float, default=0.0002, help="留出「新最好」的最小改进")
    ap.add_argument("--time-budget", type=float, default=0.0)
    ap.add_argument("--beta1", type=float, default=0.9)
    ap.add_argument("--beta2", type=float, default=0.999)
    ap.add_argument("--eps", type=float, default=1e-8)
    ap.add_argument("--grad-norm", default="mean", choices=["mean", "sum"],
                    help="mini-batch 梯度归一：mean（除以批大小，标准做法）/ sum（复现第一版的坑）")
    ap.add_argument("--holdout-games", default="out/_r6-holdout-games.txt")
    ap.add_argument("--tag", default="r6")
    ap.add_argument("--weights", default="", help="保存权重的路径（.npz）")
    ap.add_argument("--log", default="out/_r6-train.txt")
    ap.add_argument("--json", default="out/_r6-train.json")
    ap.add_argument("--cases", default="",
                    help="一次跑多组：tag:recipe:lr[:batch[:hidden[:init]]],... （共享同一份装载的数据）")
    a = ap.parse_args()

    lines = []

    def log(*m):
        s = " ".join(str(x) for x in m)
        print(s, flush=True)
        lines.append(s)

    meta = load_meta(REPO / a.data, max_games=a.max_games)
    log("=" * 100)
    log(f"第六轮训练  tag={a.tag}")
    log("=" * 100)
    log(f"GPU {torch.cuda.get_device_name(0)}  torch {torch.__version__}  cuda {torch.version.cuda}"
        f"  可用显存 {torch.cuda.get_device_properties(0).total_memory/2**30:.2f} GiB")
    t0 = time.time()
    sha = sha256_file(meta["path"], prefix_bytes=64 << 20)
    log(f"  数据 {meta['path'].name}  {meta['n']:,} 条 × {meta['dim']} 维  "
        f"{meta['size']/1e9:.2f} GB")
    log(f"  数据 SHA256(前 64 MiB) = {sha}      （算哈希 {time.time()-t0:.1f}s）")
    a.data_sha = sha

    if not a.cases:
        res, w = run(a, meta, log)
        allres = {a.tag: res}
        weights = {a.tag: w}
    else:
        allres, weights = {}, {}
        for spec in a.cases.split(","):
            f = spec.split(":")
            tag, recipe = f[0], f[1]
            b = copy.copy(a)
            b.tag, b.recipe = tag, recipe
            b.lr = float(f[2]) if len(f) > 2 and f[2] else a.lr
            b.batch = int(f[3]) if len(f) > 3 and f[3] else a.batch
            b.hidden = int(f[4]) if len(f) > 4 and f[4] else a.hidden
            b.init = f[5] if len(f) > 5 and f[5] else a.init
            res, w = run(b, meta, log)
            allres[tag] = res
            weights[tag] = w
            torch.cuda.empty_cache()

    (REPO / a.log).write_text("\n".join(lines), encoding="utf-8")
    (REPO / a.json).write_text(json.dumps(allres, ensure_ascii=False, indent=1), encoding="utf-8")
    wdir = REPO / (a.weights or "")
    if a.weights:
        for tag, w in weights.items():
            np.savez(str(wdir) + f".{tag}.npz" if len(allres) > 1 else wdir,
                     mean=w["mean"], std=w["std"], W1=w["W1"], B1=w["B1"], W2=w["W2"],
                     B2=np.zeros(1, dtype=np.float32))
        log(f"权重 → {a.weights}（{len(weights)} 组）")
    log(f"日志 → {a.log}    JSON → {a.json}")
    for tag, res in allres.items():
        print(f"★ {tag}: 最佳留出 {res['best_acc']:.3%} @ ep {res['best_epoch']}"
              f"  步数 {res['updates_total']:,}  用时 {res['total_sec']:.0f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
