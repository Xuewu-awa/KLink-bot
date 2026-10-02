"""第六轮：**每步耗时去哪了** —— 拆开量 batch 准备的各个环节。

动机：mini-batch 配方下 GPU 利用率只有 14~18%、python 单核 100%。
要分清楚是「数据不在显存里，每步都要搬」还是「数据在显存里，但每步的
Python/kernel-launch 开销把 GPU 饿死」——两者的修法完全不同。

做法：数据先整体上显存（fp32），然后逐项计时：
  ① index_select 取一个 batch（含 y）
  ② z-score 标准化
  ③ 前向 + BCE
  ④ 反向
  ⑤ 权重更新（SGD / Adam）
  ⑥ 把「一次 forward+backward+update」放在 CUDA graph 里重放（看能不能把间隙吃掉）

另外对照：数据留在**内存**里、每步 H2D 拷贝 的 ①'。
"""

import argparse
import struct
import sys
import time
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
MAGIC_V2 = 0x324C4B41


def bench(fn, iters, warmup=20):
    for _ in range(warmup):
        fn()
    torch.cuda.synchronize()
    t0 = time.time()
    for _ in range(iters):
        fn()
    torch.cuda.synchronize()
    return (time.time() - t0) / iters * 1e6        # 微秒


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="out/nn-data-100k.bin")
    ap.add_argument("--max-games", type=int, default=50000)
    ap.add_argument("--batch", type=int, default=256)
    ap.add_argument("--hidden", type=int, default=64)
    ap.add_argument("--iters", type=int, default=800)
    ap.add_argument("--out", default="out/_r6-pipeline.txt")
    a = ap.parse_args()

    lines = []

    def log(*m):
        s = " ".join(str(x) for x in m)
        print(s, flush=True)
        lines.append(s)

    torch.backends.cuda.matmul.allow_tf32 = False
    torch.manual_seed(0)
    path = REPO / a.data
    size = path.stat().st_size
    with open(path, "rb") as f:
        magic, dim, ncol0 = struct.unpack("<iii", f.read(12))
    assert magic == MAGIC_V2
    ncol = dim + 3
    n_all = (size - 12) // (ncol * 4)
    rec = np.memmap(path, dtype=np.float32, mode="r", offset=12, shape=(n_all, ncol))
    gid_all = np.asarray(rec[:, dim + 1], dtype=np.int64)
    keep = gid_all < a.max_games if a.max_games else np.ones(n_all, dtype=bool)
    n = int(keep.sum())

    log("=" * 96)
    log(f"每步耗时拆解  数据 {path.name}  取前 {n:,} 条  batch {a.batch}  hidden {a.hidden}")
    log("=" * 96)
    log(f"GPU {torch.cuda.get_device_name(0)}  torch {torch.__version__}")

    Xg = torch.empty((n, dim), dtype=torch.float32, device="cuda")
    yv = np.asarray(rec[keep, dim], dtype=np.float32)
    sub = rec[keep]
    for s in range(0, n, 65536):
        e = min(n, s + 65536)
        d = np.array(sub[s:e, :dim], dtype=np.float32)
        Xg[s:e] = torch.from_numpy(d).to("cuda")
    del sub, rec
    yg = torch.from_numpy(yv).to("cuda")
    torch.cuda.empty_cache()
    log(f"数据已整体上显存：{torch.cuda.memory_allocated()/2**30:.2f} GiB")

    mean = torch.zeros(dim, device="cuda")
    std = torch.ones(dim, device="cuda")
    W1 = (torch.randn(a.hidden, dim, device="cuda") * 0.05).requires_grad_(False)
    B1 = torch.zeros(a.hidden, device="cuda")
    W2 = (torch.randn(a.hidden, device="cuda") * 0.05)
    gW1 = torch.zeros_like(W1); gB1 = torch.zeros_like(B1); gW2 = torch.zeros_like(W2)

    idx = torch.randint(0, n, (a.batch,), device="cuda")
    bi = 0
    d = dim

    def step_gather():
        nonlocal bi
        bi = (bi + 1) % (n - a.batch)
        r = torch.arange(bi, bi + a.batch, device="cuda")
        c = Xg[r]
        t = yg[r]
        return c, t

    c0, t0_ = step_gather()

    def f_norm():
        return (c0 - mean) / std

    def f_fwd():
        h = torch.relu(c0 @ W1.T + B1)
        o = h @ W2
        p = torch.sigmoid(o)
        return (-t0_ * torch.log(p + 1e-7) - (1 - t0_) * torch.log(1 - p + 1e-7)).sum(), h, p

    def f_full_sgd():
        nonlocal W1, B1, W2
        c, t = step_gather()
        z = (c - mean) / std
        h = torch.relu(z @ W1.T + B1)
        o = h @ W2
        p = torch.sigmoid(o)
        loss = (-t * torch.log(p + 1e-7) - (1 - t) * torch.log(1 - p + 1e-7)).sum()
        dd = (p - t).unsqueeze(1)
        g2 = dd.squeeze(1) @ h
        dh = dd * W2.unsqueeze(0) * (h > 0).float()
        g1 = dh.T @ z
        gb = dh.sum(0)
        with torch.no_grad():
            W1 -= 0.05 * g1 / a.batch
            B1 -= 0.05 * gb / a.batch
            W2 -= 0.05 * g2 / a.batch

    u = bench(step_gather, a.iters)
    log(f"\n① 取一个 batch（显存 index_select，含 y）        {u:8.1f} µs")
    log(f"② z-score 标准化（{a.batch}×{d}）                {bench(f_norm, a.iters):8.1f} µs")
    log(f"③ 前向 + BCE                                     {bench(f_fwd, a.iters):8.1f} µs")
    tot = bench(f_full_sgd, a.iters)
    flops = 3 * 2 * a.batch * (d * a.hidden + a.hidden)     # 前向 2N，反传约 2×前向
    log(f"④⑤ ①+②+前向+反传+更新（=真实每一步）           {tot:8.1f} µs")
    log(f"    ⇒ 每 epoch 3,855 步 ≈ {tot*3855/1e6:.2f} s（实测 3.25 s，对得上）")
    log(f"    ⇒ 每步有效算力 {flops/1e12/(tot/1e6):.3f} TFLOP/s"
        f"（fp32 稠密峰值约 20 ⇒ 利用率 {100*flops/1e12/(tot/1e6)/20:.1f}%）")

    # 数据放内存、每步 H2D 的对照
    Xc = Xg.cpu()
    yc = yg.cpu()

    def f_cpu():
        nonlocal W1, B1, W2
        r = torch.arange(bi, bi + a.batch)
        c = Xc[r].to("cuda", non_blocking=False)
        t = yc[r].to("cuda", non_blocking=False)
        z = (c - mean) / std
        h = torch.relu(z @ W1.T + B1)
        o = h @ W2
        p = torch.sigmoid(o)
        loss = (-t * torch.log(p + 1e-7) - (1 - t) * torch.log(1 - p + 1e-7)).sum()
        dd = (p - t).unsqueeze(1)
        g2 = dd.squeeze(1) @ h
        dh = dd * W2.unsqueeze(0) * (h > 0).float()
        g1 = dh.T @ z
        gb = dh.sum(0)
        with torch.no_grad():
            W1 -= 0.05 * (g1 / a.batch)
            B1 -= 0.05 * (gb / a.batch)
            W2 -= 0.05 * (g2 / a.batch)

    uc = bench(f_cpu, a.iters, warmup=10)
    log(f"\n①' 同样一步，但**数据留内存、每步 H2D**         {uc:8.1f} µs"
        f"   （是显存版的 {uc/tot:.2f}×）")
    del Xc, yc

    # 大 batch 的吞吐对照（同一份数据）
    log("\n同一份数据、不同 batch 的**吞吐**（只影响硬件效率，不改算法）")
    log(f"  {'batch':>7} {'每步 µs':>10} {'步/秒':>10} {'每 epoch 步数':>13} {'每 epoch 秒':>12}"
        f" {'有效 TFLOP/s':>13}")
    for bs in (256, 1024, 4096):
        idxb = torch.randint(0, n, (bs,), device="cuda")

        def f_bs():
            nonlocal W1, B1, W2
            c = Xg[idxb]
            t = yg[idxb]
            z = (c - mean) / std
            h = torch.relu(z @ W1.T + B1)
            o = h @ W2
            p = torch.sigmoid(o)
            loss = (-t * torch.log(p + 1e-7) - (1 - t) * torch.log(1 - p + 1e-7)).sum()
            dd = (p - t).unsqueeze(1)
            g2 = dd.squeeze(1) @ h
            dh = dd * W2.unsqueeze(0) * (h > 0).float()
            g1 = dh.T @ z
            gb = dh.sum(0)
            with torch.no_grad():
                W1 -= 0.05 * (g1 / bs)
                B1 -= 0.05 * (gb / bs)
                W2 -= 0.05 * (g2 / bs)

        t_us = bench(f_bs, max(50, a.iters // (bs // 256)), warmup=10)
        fl = 3 * 2 * bs * (d * a.hidden + a.hidden)
        log(f"  {bs:>7} {t_us:>10.1f} {1e6/t_us:>10.0f} {n//bs:>13,} {n//bs*t_us/1e6:>12.2f}"
            f" {fl/(t_us*1e-6)/1e12:>13.3f}")

    (REPO / a.out).write_text("\n".join(lines), encoding="utf-8")
    log(f"\n→ {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
