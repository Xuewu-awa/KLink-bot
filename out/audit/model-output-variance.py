#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
诊断：**模型对局面的输出到底有没有区分度？**

## 为什么要单独量这个

真对局日志（`rel/data/fyserver/bot-log/`）里，模型对**同一回合的所有候选**
输出**同一个值**，而且常常饱和：

```
回合 2 :  100.0 / 0.0 / 0.0 / 0.0 / 0.0 / 0.0 / 0.0
回合 5 : （全 100.0）
回合 17: （全 0.0）
```

从来不是"有梯度地排序"。这意味着 `argmax` 退化成"取枚举顺序里的第一个"
—— **AI 不是在选择，是在碰运气**。

这**不能只归因于编码**：`NnPolicy` 的做法是「把每个候选 Apply 之后再编码 → 打分」，
如果两张不同的牌打完后局面接近，分数自然接近。所以要先量清楚：
是**模型**饱和，还是**编码**把不同局面压成了同一个向量。

## 量什么

1. 模型输出在**全部样本**上的分布（均值/标准差/分位数）—— 看是否饱和在 0/1
2. **同一局内**输出的标准差 —— 同一局的不同回合应当有不同胜率
3. 输出值的直方图 —— 若集中在 0.0/1.0 两端就是饱和
4. 与**标签**的关系（是否只是恒等于先手优势）

## 与训练无关

这里**不重训**，只读打包好的 `.bin`（C# 实际加载的那个），
所以量到的就是**服务器上真正在跑的那个模型**。
"""
import argparse
import struct
import sys
from pathlib import Path

import numpy as np

MAGIC = 0x314D4C4B


def load_model(path):
    raw = Path(path).read_bytes()
    magic, ver, dim, hidden, card_dim, zones, per_side, act = struct.unpack_from("<8i", raw, 0)
    assert magic == MAGIC, f"不是 KLM1 模型：magic={magic:#x}"
    off = 32
    slen = struct.unpack_from("<i", raw, off)[0]
    off += 4
    spec = raw[off:off + slen].decode("utf-8")
    off += slen
    mlen = struct.unpack_from("<i", raw, off)[0]
    off += 4
    meta = raw[off:off + mlen].decode("utf-8")
    off += mlen

    n = lambda k: np.frombuffer(raw, dtype="<f4", count=k, offset=off)
    mean = n(dim); off += dim * 4
    std = n(dim); off += dim * 4
    W1 = np.frombuffer(raw, dtype="<f4", count=hidden * dim, offset=off).reshape(hidden, dim); off += hidden * dim * 4
    B1 = n(hidden); off += hidden * 4
    W2 = n(hidden); off += hidden * 4
    b2 = struct.unpack_from("<f", raw, off)[0]

    return dict(dim=dim, hidden=hidden, activation=act, spec=spec, meta=meta,
                mean=mean, std=std, W1=W1, B1=B1, W2=W2, b2=b2)


def forward(m, X):
    """与 NnModel 的推理一致：z-score → 线性 → 激活 → 线性 → sigmoid。"""
    Z = (X - m["mean"]) / np.where(m["std"] < 1e-6, 1.0, m["std"])
    H = Z @ m["W1"].T + m["B1"]
    if m["activation"] == 0:
        H = np.maximum(H, 0.0)
    elif m["activation"] == 1:
        H = np.tanh(H)
    return 1.0 / (1.0 + np.exp(-(H @ m["W2"] + m["b2"])))


def read_dump(path, limit=400000):
    """
    记录布局（见 `tools/NNTrain/Program.cs:206-209`）：
        float v[dim] + float label + float gid + float pairId     共 dim+3 个 float
    ⚠️ 别把 `pairId` 当 gid —— 它是**对位编号**（0..461），
       拿它分组会得到"每局只有几个样本"的假象（踩过）。
    """
    raw = Path(path).read_bytes()
    magic, dim, dc = struct.unpack_from("<iii", raw, 0)
    stride = dim + 3
    n = min((len(raw) - 12) // (stride * 4), limit)
    arr = np.frombuffer(raw, dtype="<f4", count=n * stride, offset=12).reshape(n, stride)
    return dim, arr[:, :dim], arr[:, dim], arr[:, dim + 1].astype(np.int64), arr[:, dim + 2]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--data", required=True, help="dump 数据（用来取真实局面）")
    ap.add_argument("--limit", type=int, default=400000)
    a = ap.parse_args()

    print(f"模型 {a.model}")
    m = load_model(a.model)
    print(f"  dim={m['dim']} hidden={m['hidden']} activation={m['activation']}")
    print(f"  spec={m['spec']}")
    print(f"  meta={m['meta']}")
    print()

    print(f"数据 {a.data}")
    dim, X, y, gid, pair = read_dump(a.data, a.limit)
    assert dim == m["dim"], f"维度不符：数据 {dim} vs 模型 {m['dim']}"
    print(f"  {len(X):,} 条 × {dim} 维   对局 {len(np.unique(gid)):,}   标签均值 {y.mean():.4f}")
    print()

    p = forward(m, X.astype(np.float32))

    # ---- ① 整体分布 ----
    print("=== ① 模型输出的整体分布 ===")
    for q in (0, 1, 5, 25, 50, 75, 95, 99, 100):
        print(f"  P{q:<3d} = {np.percentile(p, q):.4f}")
    print(f"  均值 {p.mean():.4f}   标准差 {p.std():.4f}")

    # ---- ② 饱和程度 ----
    print()
    print("=== ② 饱和程度（输出贴到 0/1 的比例）===")
    for t in (0.001, 0.01, 0.05):
        lo = (p < t).mean()
        hi = (p > 1 - t).mean()
        print(f"  <{t:<6} {lo:6.2%}     >{1-t:<6.3f} {hi:6.2%}     合计 {lo+hi:6.2%}")

    # ---- ③ 同一局内有没有变化 ----
    print()
    print("=== ③ 同一局内的标准差（局面不同 ⇒ 输出应当不同）===")
    order = np.argsort(gid, kind="stable")
    g_sorted, p_sorted = gid[order], p[order]
    bounds = np.flatnonzero(np.diff(g_sorted)) + 1
    starts = np.concatenate(([0], bounds))
    ends = np.concatenate((bounds, [len(g_sorted)]))
    lens = ends - starts
    keep = lens >= 5
    sds = np.array([p_sorted[s:e].std() for s, e in zip(starts[keep], ends[keep])])
    rng = np.array([p_sorted[s:e].max() - p_sorted[s:e].min() for s, e in zip(starts[keep], ends[keep])])
    print(f"  统计了 {keep.sum():,} 局（每局 ≥5 个样本）")
    print(f"  局内标准差  中位 {np.median(sds):.4f}  均值 {sds.mean():.4f}  为 0 的局 {np.mean(sds < 1e-6):.2%}")
    print(f"  局内极差    中位 {np.median(rng):.4f}  均值 {rng.mean():.4f}  为 0 的局 {np.mean(rng < 1e-6):.2%}")

    # ---- ④ 与标签的关系 ----
    print()
    print("=== ④ 输出的判别力 ===")
    acc = ((p > 0.5) == (y > 0.5)).mean()
    print(f"  在**训练集**上按 0.5 判正负的准确率 = {acc:.4f}（标签均值 {y.mean():.4f}）")
    print("  ⚠️ 这是训练集，不是留出 —— 只用来看'模型有没有在用输入'")

    # ---- ⑤ 输入真的在变吗（对照）----
    print()
    print("=== ⑤ 对照：输入向量的变化幅度（同局 vs 全局）===")
    idx = order[:min(len(order), 200000)]
    Xi = X[idx]
    d = np.abs(np.diff(Xi, axis=0)).sum(axis=1)
    same = (np.diff(gid[idx]) == 0)
    print(f"  相邻样本 L1 距离：同局中位 {np.median(d[same]):.3f}   "
          f"跨局中位 {np.median(d[~same]) if (~same).any() else float('nan'):.3f}")
    print(f"  输入非零维数（每样本平均）= {(X != 0).sum(axis=1).mean():.1f} / {dim}")

    print()
    print("=== 判读 ===")
    sat = ((p < 0.01) | (p > 0.99)).mean()
    if sat > 0.5:
        print(f"  ⚠️ 输出**严重饱和**（{sat:.1%} 贴到两端）—— argmax 会退化成取第一个候选")
    if np.median(sds) < 0.01:
        print(f"  ⚠️ **局内几乎没有变化**（中位标准差 {np.median(sds):.4f}）")
        print("     ⇒ 模型基本没在看局面，只输出一个近似常数")
    if np.median(sds) >= 0.05:
        print(f"  ✅ 局内变化正常（中位标准差 {np.median(sds):.4f}）")


if __name__ == "__main__":
    main()
