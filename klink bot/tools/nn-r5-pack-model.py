"""把 GPU 版（nn-r5-gpu-train.py）训出的权重打包成 **C# NnModel 的文件格式**。

目的：让 `NNTrain verify` 能独立复算 GPU 版的结果 —— 这是「GPU ≠ 换算法」的最终证据：
  * Python 侧算出的前向 → C# 侧 NnModel 的前向，两者报出的留出准确率必须一致；
  * 同时 `verify` 会给出分档表与训练/全体准确率，报告里直接引用 C# 的数字。

格式（见 src/KLink.Bot/NN/NnModel.cs 与 nn_common.load_model 的反向工程）：
  int32 magic('KLM1' = 0x314D4C4B), ver=1, dim, hidden, cardDim, zones, perSide, activation
  然后依次： int32 specLen + spec(utf8) ; int32 metaLen + meta(json)
            float mean[dim] ; float std[dim] ; float W1[hidden*dim] ; float B1[hidden]
            float W2[hidden] ; float B2
"""
import argparse
import json
import struct
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

MAGIC = 0x314D4C4B


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", required=True, help="nn-r5-gpu-train.py 写出的 <log>.json")
    ap.add_argument("--out", required=True)
    ap.add_argument("--data", default="nn-data-100k.bin")
    ap.add_argument("--epochs", type=int, required=True)
    ap.add_argument("--lr", type=float, default=0.05)
    ap.add_argument("--split-seed", type=int, default=12345)
    ap.add_argument("--spec", default="")
    ap.add_argument("--note", default="")
    a = ap.parse_args()

    src = json.loads((REPO / a.json).read_text(encoding="utf-8"))
    dim = src["dim"]
    hidden = src["hidden"]
    mean = np.asarray(src["mean"], dtype=np.float32)
    std = np.asarray(src["std"], dtype=np.float32)
    W1 = np.asarray(src["W1"], dtype=np.float32)      # (hidden, dim)
    B1 = np.asarray(src["B1"], dtype=np.float32)
    W2 = np.asarray(src["W2"], dtype=np.float32)
    hist = src["hist"]
    assert W1.shape == (hidden, dim), W1.shape
    assert mean.shape == (dim,) and std.shape == (dim,), (mean.shape, std.shape)

    if not a.spec:
        raise SystemExit("必须给 --spec（从已有模型读出，见报告 §复现命令）")
    spec = (REPO / a.spec).read_text(encoding="utf-8").strip() if Path(a.spec).suffix == ".txt" else a.spec

    best = max(hist, key=lambda h: h["acc"])
    meta = {
        "tool": "nn-r5-gpu-train.py（GPU 复算 NNTrain 的同一算式）+ pack_model_for_csharp",
        "encoderSpec": a.spec,
        "data": a.data,
        "samples": src["n_train"] + src["n_val"],
        "trainSamples": src["n_train"],
        "valSamples": src["n_val"],
        "valGames": src["val_games"],
        "epochs": a.epochs,
        "hidden": hidden,
        "lr": a.lr,
        "weightInitSeed": 42,
        "splitSeed": a.split_seed,
        "target": "win",
        "valAccuracy": hist[-1]["acc"],
        "bestValAccuracy": best["acc"],
        "savedEpochWeights": a.epochs,
        "device": "cuda (RTX 5060)",
        "note": a.note or "GPU 复算；已与 C# 参考曲线逐点对账（见 out/_r5-gpu-vs-csharp.txt）",
    }
    meta_json = json.dumps(meta, ensure_ascii=False, separators=(",", ":"))
    spec_b = spec.encode("utf-8")
    meta_b = meta_json.encode("utf-8")
    per_side = (dim - 3) // 2

    out = REPO / a.out
    with open(out, "wb") as f:
        f.write(struct.pack("<8i", MAGIC, 1, dim, hidden, 90, 5, per_side, 0))
        f.write(struct.pack("<i", len(spec_b)))
        f.write(spec_b)
        f.write(struct.pack("<i", len(meta_b)))
        f.write(meta_b)
        f.write(mean.tobytes(order="C"))
        f.write(std.tobytes(order="C"))
        f.write(W1.reshape(-1).tobytes(order="C"))
        f.write(B1.tobytes(order="C"))
        f.write(W2.tobytes(order="C"))
        f.write(struct.pack("<f", 0.0))
    print(f"已写出 C# 格式模型 {out}  ({out.stat().st_size/1024:.0f} KB)")
    print(f"  dim={dim} hidden={hidden}  最后留出 {hist[-1]['acc']:.2%}  最佳 {best['acc']:.2%} @ {best['epoch']}")


if __name__ == "__main__":
    main()
