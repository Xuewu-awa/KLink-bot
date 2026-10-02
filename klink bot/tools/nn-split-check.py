"""
反证：Python 侧对 C# `new Random(seed)` + LINQ `OrderBy` 的复刻是否**逐位**正确。

判据（两条，都必须过）：
  1. 复刻出的留出样本条数必须等于 `NNTrain verify` 报的条数；
  2. 用模型文件在 Python 里前向，算出的留出准确率必须等于 `NNTrain verify` 报的准确率
     （**逐样本相同**才可能让这两个数对齐到小数点后两位）。

用 v1-noeffects 的已知答案对账（`out/_verify-v1.txt`）：
     验证集准确率 76.29%   (24,056/31,532，验证集 1500 局)

用法：python -X utf8 "klink bot/tools/nn-split-check.py"
"""

import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import forward_logit, load_model, nntrain_split, open_data   # noqa: E402

CASES = [
    # (数据, 模型, split-seed, verify 报的留出条数, verify 报的留出准确率, verify 输出文件)
    (REPO / "out/nn-data-v1-noeffects.bin", REPO / "out/nn-model-v1-noeffects.bin",
     12345, 31532, 76.29),
    (REPO / "out/nn-data-v0.bin", REPO / "out/nn-model-v0.bin",
     12345, 31704, 76.52),
]

ok_all = True
for data, model_path, seed, exp_n, exp_acc in CASES:
    print("=" * 92)
    print(f"数据 {data.name}  模型 {model_path.name}  --split-seed {seed}")
    print("=" * 92)
    rec, dim, n = open_data(str(data))
    y = np.asarray(rec[:, dim], dtype=np.float32)
    gid = np.asarray(rec[:, dim + 1], dtype=np.float32)
    val, train, val_games = nntrain_split(gid, seed)
    print(f"  样本 {n:,}   对局 {len(set(int(g) for g in gid)):,}   "
          f"复刻留出 {len(val_games)} 局 / {int(val.sum()):,} 条")

    model = load_model(str(model_path))
    logit = forward_logit(model, np.asarray(rec[val, :dim], dtype=np.float32))
    pred = logit > 0
    truth = y[val] > 0.5
    correct = int((pred == truth).sum())
    acc = 100.0 * correct / int(val.sum())

    n_ok = int(val.sum()) == exp_n
    acc_ok = abs(acc - exp_acc) < 0.005
    ok_all &= n_ok and acc_ok
    print(f"  留出条数  复刻 {int(val.sum()):,}  vs  期望 {exp_n:,}      {'✅' if n_ok else '❌'}")
    print(f"  留出准确率 复刻 {acc:.2f}% ({correct:,}/{int(val.sum()):,})  vs  期望 {exp_acc:.2f}%"
          f"      {'✅' if acc_ok else '❌'}")
    print(f"  训练集条数 {int(train.sum()):,}")
    del rec

print()
print("=" * 92)
print("结论：" + ("✅ 复刻成功 —— Python 与 C# 落在**同一份**留出局上，基线数字可与模型留出数字并列"
                 if ok_all else "❌ 复刻失败 —— 不要用本脚本的口径"))
print("=" * 92)
sys.exit(0 if ok_all else 1)
