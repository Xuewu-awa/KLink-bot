"""
手牌上限修复轮（handfix）：**复用第七轮 `nn-r7-rollout.py` 的全部解析与统计**，
只替换「跑哪些模型」这一张表，用来做两个对照：

  ① `R6-adam-100k`  —— 第七轮那个收敛模型，**同一个文件**。它在本轮的新内核下重跑一遍，
     与 `out/_r7-rollout.txt` 里**旧内核**的同名行逐 seed 对齐 ⇒ **单变量 = 内核**。
  ② `R8-10k-handfix` —— 本轮新 dump 的 1 万局（已修手牌上限）+ 同一套配方训出来的模型。

对位固定为 `C-真均势`（NN 美澳跳 vs 对手 英日），seeds 1..N，左右各 N 局 ——
与第七轮 `R6-adam-100k|C-真均势|{left,right}` 逐字相同。

⚠️ 本脚本**不改 `nn-r7-rollout.py`**：它用 importlib 把那个文件当模块载入，
   只覆盖模块级的 `MODELS`，其余（正则、`kind()`、`agg()`、Wilson、AUC）全部原样复用。

用法（仓库根目录）：
  python -X utf8 "klink bot/tools/nn-handfix-rollout.py" --seeds 40 --jobs 8
"""

import importlib.util
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

spec = importlib.util.spec_from_file_location("nn_r7_rollout", HERE / "nn-r7-rollout.py")
r7 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r7)

# ★ 只改这一张表
r7.MODELS = [
    ("R6-adam-100k", "out/_r6-model-S3-adam-100k.bin",
     "第七轮收敛模型（10 万局·旧内核训练）—— 本轮在新内核下重跑，= 内核单变量对照"),
    ("R8-10k-nofix", "out/nn-model-10k-nofix.bin",
     "对照：1 万局·**未修**手牌上限 + 同一配方（10000 局 / 150ep）"),
    ("R8-10k-handfix", "out/nn-model-10k-handfix.bin",
     "本轮：1 万局（已修手牌上限）+ mini-batch256/Adam1e-3/hidden64/150ep"),
]

# 对位只留「真均势」那一组
r7.MATCHUPS = [("C-真均势", "美澳跳", "英日")]
r7.MODEL_MATCHUPS = {}
r7.CONFIGS = r7.build_configs(r7.MATCHUPS)

if __name__ == "__main__":
    sys.exit(r7.main())
