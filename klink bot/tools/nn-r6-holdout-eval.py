"""
第六轮：**在同一份固定留出局清单上**，重算线性基线 F1/F2l/F2d，并评估本轮所有 MLP。

为什么必须重算（第五轮留下的坑）：
  第五轮的两列留出不是同一批：`nn-r5-baselines.py` 用分块读，**丢掉了跨块尾部的半局**，
  所以它的自然切分只切出 14,999 局 / 347,599 条（C# 与 GPU 训练器是 15,000 局 / 347,635 条）。
  于是「71.11 / 75.88 / 79.71」这三个基线与 MLP 的留出**差 36 条样本、且清单不一致**。

本轮的做法：
  * 留出局清单 = `out/_r6-holdout-games.txt`（15,000 局，SHA256 见下），
    与 C# `--split-seed 12345` 的自然切分**逐局相同**（训练器已断言过）；
  * 基线与全部 MLP 都只在这 15,000 局的 **347,635 条**上评；
  * 顺带输出 McNemar 精确检验（配对），回答「A 是不是真的比 B 强」。

口径（与第四轮/第五轮逐字相同）：口径 A = 原始胜负，sign(score) 猜左方是否获胜。
"""

import argparse
import importlib.util
import json
import math
import sys
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import forward_logit  # noqa: E402


def _load_dashed(name: str):
    """文件名带连字符，不能 `import`；按路径加载。"""
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), HERE / f"{name}.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_r5 = _load_dashed("nn-r5-baselines")
fit_logistic, read_cols = _r5.fit_logistic, _r5.read_cols

HOLDOUT = REPO / "out/_r6-holdout-games.txt"


def mcnemar_exact(b: int, c: int):
    """双尾精确 McNemar（b/c 是不一致对数）。用对数算，避免 2**n 溢出。"""
    n = b + c
    if n == 0:
        return 1.0
    k = min(b, c)
    ln2 = math.log(2.0)
    tail = sum(math.exp(math.lgamma(n + 1) - math.lgamma(i + 1) - math.lgamma(n - i + 1)
                        - n * ln2) for i in range(0, k + 1))
    return min(1.0, 2 * tail)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="out/nn-data-100k.bin")
    ap.add_argument("--models", default="", help="tag=/abs/或相对路径.npz,...（本轮训练的权重）")
    ap.add_argument("--models-dir", default="out")
    ap.add_argument("--out", default="out/_r6-holdout-eval.json")
    ap.add_argument("--log", default="out/_r6-holdout-eval.txt")
    a = ap.parse_args()

    lines = []

    def log(*m):
        s = " ".join(str(x) for x in m)
        print(s, flush=True)
        lines.append(s)

    val_games = np.sort(np.loadtxt(HOLDOUT, dtype=np.int64))
    import hashlib
    sha = hashlib.sha256(HOLDOUT.read_bytes()).hexdigest()
    log("=" * 104)
    log("第六轮：固定留出上的基线 + MLP 评估")
    log("=" * 104)
    log(f"★ 留出清单 {HOLDOUT.relative_to(REPO)}  {len(val_games):,} 局  "
        f"gid 求和 {int(val_games.sum()):,}")
    log(f"  清单 SHA256 {sha}")
    log(f"  前 5 局 {list(val_games[:5])}")

    t0 = time.time()
    D = read_cols(REPO / a.data, None, log=log)
    n, y, feats, gid = D["n"], D["y"], D["feats"], D["gid"]
    pair, turn, deck_count = D["pair"], D["turn"], D["deck_count"]
    is_val = np.zeros(int(gid.max()) + 1, dtype=bool)
    is_val[val_games] = True
    ho = is_val[gid]
    tr = ~ho
    log(f"  留出 {int(ho.sum()):,} 条 / 训练 {int(tr.sum()):,} 条   "
        f"（读列 {time.time()-t0:.0f}s）")
    if int(ho.sum()) != 347635:
        log(f"  ⚠️ 留出条数 {int(ho.sum()):,} ≠ 训练器报的 347,635 —— 说明读列丢过跨块半局，"
            f"数字与训练器不可比！")

    pl = (pair // deck_count).astype(np.int64)
    pr = (pair % deck_count).astype(np.int64)
    F1 = np.column_stack([np.ones(n, dtype=np.float32), feats])
    oh = np.zeros((n, deck_count), dtype=np.float32)
    oh[np.arange(n), pl] = 1.0
    ohr = np.zeros((n, deck_count), dtype=np.float32)
    ohr[np.arange(n), pr] = 1.0
    F2l = np.column_stack([F1, oh])
    F2d = np.column_stack([F1, oh, ohr])
    del oh

    out = dict(holdout_games=len(val_games), holdout_sha256=sha,
               n_val=int(ho.sum()), n_train=int(tr.sum()),
               val_games_sum=int(val_games.sum()), baselineA={}, mlp={})

    log("\n【口径 A】原始胜负（sign(score) 猜左方胜）—— 同一份留出 %d 条" % int(ho.sum()))
    preds = {}
    for nm, F in (("F1 读领先(6参)", F1), ("F2l 读领先+左卡组(28参)", F2l),
                  ("F2d 读领先+左右卡组(50参)", F2d)):
        w = fit_logistic(F[tr], y[tr])
        sc = F @ w
        ok = (sc > 0) == (y > 0.5)
        preds[nm] = ok[ho]
        acc = float(ok[ho].mean())
        tracc = float(ok[tr].mean())
        out["baselineA"][nm] = {"holdout": acc, "train": tracc, "params": F.shape[1]}
        log(f"  {nm:<26} 留出 {acc:>7.3%}   训练 {tracc:>7.3%}   参数 {F.shape[1]}")
    yl = y.astype(np.float64)
    mj = float(((y > 0.5) == (yl[tr].mean() >= 0.5))[ho].mean())
    out["baselineA"]["F0 多数类"] = {"holdout": mj, "train": None, "params": 1}
    log(f"  {'F0 多数类':<26} 留出 {mj:>7.3%}")

    # ---------- MLP ----------
    if a.models:
        log("\n【本轮 MLP】同一份留出（权重来自 npz，Python 侧独立前向）")
        for part in a.models.split(","):
            tag, _, p = part.partition("=") if "=" in part else (part, "", part)
            fp = Path(p)
            if not fp.is_absolute():
                fp = REPO / a.models_dir / (p if p.endswith(".npz") else p + ".npz")
            if not fp.exists():
                log(f"  ⚠️ 找不到 {fp}，跳过")
                continue
            z = np.load(fp)
            model = dict(dim=D["dim"], hidden=z["W1"].shape[0], mean=z["mean"], std=z["std"],
                         W1=z["W1"], B1=z["B1"], W2=z["W2"], B2=np.float32(0.0))
            ncol = D["dim"] + 3
            buf = np.memmap(REPO / a.data, dtype=np.float32, mode="r", offset=12, shape=(n, ncol))
            score = np.empty(n, dtype=np.float32)
            for s in range(0, n, 65536):
                e = min(n, s + 65536)
                score[s:e] = forward_logit(model, np.array(buf[s:e, :D["dim"]], dtype=np.float32))
            del buf
            ok = (score > 0) == (y > 0.5)
            acc = float(ok[ho].mean())
            tracc = float(ok[tr].mean())
            preds[tag] = ok[ho]
            out["mlp"][tag] = dict(path=str(fp), hidden=int(model["hidden"]),
                                   holdout=acc, train=tracc,
                                   gap_pt=100 * (tracc - acc))
            log(f"  {tag:<26} 留出 {acc:>7.3%}   训练 {tracc:>7.3%}   差 {100*(tracc-acc):+.2f} 点"
                f"   结构 745→{model['hidden']}→1")
            np.save(REPO / f"out/_r6-score-{tag}.npy", score.astype(np.float32))

    # ---------- 配对显著性（对最强基线 F2d）----------
    log("\n【配对 McNemar 精确检验】本模型 vs F2d 50 参（同一批留出样本）")
    base = preds["F2d 读领先+左右卡组(50参)"]
    out["mcnemar_vs_F2d"] = {}
    for tag, ok in preds.items():
        if tag.startswith("F2d"):
            continue
        b = int((~ok & base).sum())      # 本模型错、F2d 对
        c = int((ok & ~base).sum())      # 本模型对、F2d 错
        pval = mcnemar_exact(b, c)
        out["mcnemar_vs_F2d"][tag] = {"model_wrong_F2d_right": b, "model_right_F2d_wrong": c,
                                      "p": pval}
        log(f"  {tag:<26} 我错/它对 {b:>7,}   我对/它错 {c:>7,}   p = {pval:.3g}")

    (REPO / a.log).write_text("\n".join(lines), encoding="utf-8")
    (REPO / a.out).write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    log(f"\n日志 → {a.log}   JSON → {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
