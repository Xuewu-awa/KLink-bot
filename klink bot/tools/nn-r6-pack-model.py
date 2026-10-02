"""
第六轮：把 `nn-r6-gpu-train.py --weights` 写出的 **npz 权重**打包成 C# `NnModel` 的二进制格式。

目的（与第五轮 §4.2 同样的两重独立验证）：
  1. Python 侧报的留出准确率，由 **C# `NNTrain verify`** 在同一份留出上独立复算一遍；
  2. 打包出来的 .bin 可以直接给 `NNPlay` 用（本报告不涉及对局，只做数值复算）。

格式（见 `src/KLink.Bot/NN/NnModel.cs`）：
  int32 magic('KLM1'=0x314D4C4B), ver=1, dim, hidden, cardDim, zones, perSide, activation
  然后： int32 specLen + spec(utf8) ; int32 metaLen + meta(json)
         float mean[dim] ; float std[dim] ; float W1[hidden*dim] ; float B1[hidden]
         float W2[hidden] ; float B2
"""

import argparse
import importlib.util
import json
import struct
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
MAGIC = 0x314D4C4B


def _load_dashed(name: str):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), HERE / f"{name}.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def pack(npz: Path, out: Path, spec_text: str, meta: dict, b2: float = 0.0):
    z = np.load(npz)
    W1, B1, W2 = z["W1"].astype(np.float32), z["B1"].astype(np.float32), z["W2"].astype(np.float32)
    mean, std = z["mean"].astype(np.float32), z["std"].astype(np.float32)
    hidden, dim = W1.shape
    assert mean.shape == (dim,) and std.shape == (dim,), (mean.shape, std.shape)
    assert B1.shape == (hidden,) and W2.shape == (hidden,), (B1.shape, W2.shape)
    spec_b = spec_text.encode("utf-8")
    meta_b = json.dumps(meta, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    per_side = (dim - 3) // 2
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
        f.write(struct.pack("<f", b2))
    return dict(dim=dim, hidden=hidden, bytes=out.stat().st_size)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--npz", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--spec", required=True, help="编码 spec 文本文件（out/_r5-encoderspec.txt）")
    ap.add_argument("--result", default="", help="训练器写出的结果 JSON（同一 tag 的那一份）")
    ap.add_argument("--tag", default="")
    ap.add_argument("--note", default="")
    a = ap.parse_args()

    spec_text = (REPO / a.spec).read_text(encoding="utf-8").strip()
    res = {}
    if a.result:
        all_res = json.loads((REPO / a.result).read_text(encoding="utf-8"))
        res = all_res.get(a.tag, all_res) if a.tag else all_res
    meta = {
        "tool": "nn-r6-gpu-train.py（第六轮：mini-batch/Adam/He/hidden 消融）",
        "recipe": res.get("recipe"), "optimizer": res.get("optimizer"),
        "batch": res.get("batch"), "lr": res.get("lr"), "hidden": res.get("hidden"),
        "init": res.get("init"), "epochs": res.get("epochs_run"),
        "updates": res.get("updates_total"),
        "data": res.get("data_file"), "splitSeed": res.get("split_seed"),
        "holdoutSha256": res.get("holdout_sha256"),
        "holdout": res.get("holdout_sha256") is not None,
        "nTrain": res.get("n_train"), "nVal": res.get("n_val"),
        "valGames": res.get("n_val_games"),
        "trainAcc": (res.get("final") or {}).get("train_acc"),
        "holdoutAcc": (res.get("final") or {}).get("acc"),
        "bestHoldoutAcc": res.get("best_acc"), "bestEpoch": res.get("best_epoch"),
        "stopReason": res.get("stop_reason"),
        "target": "win", "lossFunction": res.get("loss"),
        "device": "cuda (RTX 5060)", "encoderSpec": a.spec,
        "note": a.note or "第六轮配方消融；损失/数据/切分与 C# 一致，只换了优化器/批次/初始化/宽度",
    }
    info = pack(REPO / a.npz, REPO / a.out, spec_text, meta)
    print(f"已写出 C# 格式模型 {a.out}  ({info['bytes']/1024:.0f} KB)  dim={info['dim']} "
          f"hidden={info['hidden']}")
    print(f"  meta: recipe={meta['recipe']} lr={meta['lr']} hidden={meta['hidden']} "
          f"epochs={meta['epochs']} holdout={meta['holdoutAcc']} best={meta['bestHoldoutAcc']}")


if __name__ == "__main__":
    main()
