"""第六轮：训练器设计基准 —— 先量出「数据放哪、每 epoch 多快」，再决定实验预算。

量四件事：
  1. fp32 与 fp16 的**全量训练矩阵**能不能放进 8.5 GB 显存（RTX 5060，可用约 8.15 GB）；
  2. 每 epoch 的两种取批方式哪个快：`index_select`（随机索引 gather）vs 顺序切片；
  3. batch=256 时**真的一个 full pass**（约 7,700 次更新）要多久 —— 这是实验预算的依据；
  4. `allow_tf32` 打开 / 关闭对**每 epoch 耗时**的影响（关掉 TF32 是为了与 C# fp32 逐位可比）。
"""

import struct
import sys
import time
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
MAGIC_V2 = 0x324C4B41


def main():
    data = REPO / "out/nn-data-100k.bin"
    size = data.stat().st_size
    with open(data, "rb") as f:
        magic, dim, decks = struct.unpack("<iii", f.read(12))
    assert magic == MAGIC_V2
    ncol = dim + 3
    n = (size - 12) // (ncol * 4)
    print(f"数据 {n:,} 条 × {dim} 维  {size/1e9:.2f} GB  卡组 {decks}")
    print(f"全量 fp32 需要 {n*dim*4/2**30:.2f} GiB   训练侧(0.85n) {int(n*0.85)*dim*4/2**30:.2f} GiB")
    print(f"显存总计 {torch.cuda.get_device_properties(0).total_memory/2**30:.2f} GiB")
    torch.cuda.reset_peak_memory_stats()

    # ---------- 加载为 GPU 上的连续训练矩阵（先按 fp32）----------
    rec = np.memmap(data, dtype=np.float32, mode="r", offset=12, shape=(n, ncol))
    t0 = time.time()
    Xg = torch.empty((n, dim), dtype=torch.float32, device="cuda")
    CH = 200_000
    for s in range(0, n, CH):
        e = min(n, s + CH)
        Xg[s:e] = torch.from_numpy(np.asarray(rec[s:e, :dim], dtype=np.float32)).to("cuda")
    del rec
    torch.cuda.synchronize()
    print(f"\n加载到显存 {time.time()-t0:.1f}s   显存已用 {torch.cuda.memory_allocated()/2**30:.2f} GiB"
          f"  峰值 {torch.cuda.max_memory_allocated()/2**30:.2f} GiB")

    for hidden in (64, 256):
        for batch in (256,):
            B = 8192
            idx = torch.randint(0, n, (B, batch), device="cuda")
            W1 = torch.randn(hidden, dim, device="cuda") * 0.05
            B1 = torch.zeros(hidden, device="cuda")
            W2 = torch.randn(hidden, device="cuda") * 0.05
            y = (torch.rand(B, batch, device="cuda") > 0.5).float()

            def epoch(use_index, tf32):
                torch.backends.cuda.matmul.allow_tf32 = tf32
                for b in range(B):
                    c = Xg[idx[b]] if use_index else Xg[(b * batch) % (n - batch):(b * batch) % (n - batch) + batch]
                    h = torch.relu(c @ W1.T + B1)
                    o = h @ W2
                    p = torch.sigmoid(o)
                    d = (p - y[b]).unsqueeze(1)
                    dh = d * W2.unsqueeze(0) * (h > 0).float()
                    W2 -= 0.01 * (dh.T @ h).sum(0)
                    W1 -= 0.01 * (dh.T @ c)
                    B1 -= 0.01 * dh.sum(0)
                torch.cuda.synchronize()

            for tag, use_index, tf32 in (("index_select+tf32on", True, True),
                                         ("index_select+fp32math", True, False),
                                         ("contig slice+fp32math", False, False)):
                epoch(use_index, tf32)                      # 预热
                t0 = time.time()
                epoch(use_index, tf32)
                dt = time.time() - t0
                print(f"  hidden {hidden:3d} batch {batch} {tag:<24} {B} 步  {dt:.3f}s"
                      f"   （1 full pass ≈ {dt * (n*0.85/B)/batch*1:.0f}s）")
            del W1, B1, W2, idx, y
            torch.cuda.empty_cache()

    print(f"\n峰值显存 {torch.cuda.max_memory_allocated()/2**30:.2f} GiB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
