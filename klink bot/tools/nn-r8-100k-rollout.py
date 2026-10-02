"""
第八轮 ★（handfix 100k）：**补上缺的那一格** —— 10 万局数据 + 已修手牌上限的内核。

与 `nn-handfix-rollout.py` 的关系：**同一个套路**（importlib 载入 `nn-r7-rollout.py`，
只覆盖 `MODELS` / `MATCHUPS` / `CONFIGS`，正则 / `kind()` / `agg()` / Wilson / AUC 原样复用），
只是把模型表换成三方对照：

  * `R6-adam-100k`    —— 10 万局 · **旧内核**训练（第六轮那个文件，一个字节没动）
  * `R8-10k-handfix`  —— 1 万局 · 新内核训练（上一轮的 ③）
  * `R8-100k-handfix` —— ★ 10 万局 · 新内核训练（本轮新产物）

三者用**同一批 seed**（默认 1..20，左右各 20 局 = 每模型 40 局），同一内核（当前已修版）、
同一 IR、同一对位（`C-真均势` = NN 美澳跳 vs 对手 英日），所以三方表可以逐列并列。

用法（仓库根目录）：
  python -X utf8 "klink bot/tools/nn-r8-100k-rollout.py" --seeds 20 --jobs 8 `
    --dump out/_r8-rollout.json --out out/_r8-rollout.txt --logs out/_r8-logs
  # 复用已跑结果重算表格：
  python -X utf8 "klink bot/tools/nn-r8-100k-rollout.py" --reuse --dump out/_r8-rollout.json
"""

import importlib.util
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

spec = importlib.util.spec_from_file_location("nn_r7_rollout", HERE / "nn-r7-rollout.py")
r7 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r7)

# ★ 只改这两张表
r7.MODELS = [
    ("R6-adam-100k", "out/_r6-model-S3-adam-100k.bin",
     "10 万局·**旧内核**训练（第六轮，文件未动）"),
    ("R8-10k-handfix", "out/nn-model-10k-handfix.bin",
     "1 万局·新内核训练（上一轮 ③）"),
    ("R8-100k-handfix", "out/nn-model-100k-handfix.bin",
     "★ 10 万局·新内核训练（本轮）"),
]

r7.MATCHUPS = [("C-真均势", "美澳跳", "英日")]
r7.MODEL_MATCHUPS = {}
r7.CONFIGS = r7.build_configs(r7.MATCHUPS)

if __name__ == "__main__":
    sys.exit(r7.main())
