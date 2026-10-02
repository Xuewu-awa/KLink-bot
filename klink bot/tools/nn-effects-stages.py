"""把 nn-effects-baselines.py 的 JSON 汇总成**分阶段**表（报告用）。

用法：python -X utf8 "klink bot/tools/nn-effects-stages.py"
"""

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent

STAGES = [("T2", 2, 2), ("T3–T5", 3, 5), ("T6–T12", 6, 12), ("T13–T23", 13, 23), ("T24+", 24, 99)]
COLS = [("MLP", "MLP"), ("A 对位多数类(逐样本留一·与二轮75.29%同口径)", "A对位"),
        ("A 对位多数类(训练局估→留出局评)", "A对位HO"),
        ("A2 只认左卡组(训练局估→留出局评)", "A2左"),
        ("B 网格读领先(训练局选权重)", "B读领先"), ("F1 读领先(6参)", "F1"),
        ("F2d 读领先+左右卡组(50参)", "F2d50")]


def main():
    data = json.load(open(REPO / "out/_effects-baselines.json", encoding="utf-8"))
    for ds in data:
        key = [k for k in ds if k.startswith("caliber_") and "split-seed" in k][0]
        cal = ds[key]
        strat = {int(k): v for k, v in cal["strat"].items()}
        f2p = [c for c in cal["cols"] if c.startswith("F2p")]
        cols = COLS + [(f2p[0], "F2p对位")] if f2p else COLS
        print("=" * 110)
        print(f"【{ds['key']}】{ds['data']}   留出 {cal['n_val_games']} 局 / {cal['n_val']:,} 条"
              f"（--split-seed 12345，与模型同一份留出）")
        print("=" * 110)
        print(f"  {'阶段':<9}{'样本':>8}" + "".join(f"{lab:>10}" for _, lab in cols) + f"{'多数类':>9}")
        for nm, lo, hi in STAGES:
            rows = [v for t, v in strat.items() if lo <= t <= hi]
            if not rows:
                continue
            n = sum(r["n"] for r in rows)
            cells = "".join(f"{sum(r[c] * r['n'] for r in rows) / n:>9.1f}%" for c, _ in cols)
            maj = sum(r["多数类"] * r["n"] for r in rows) / n
            print(f"  {nm:<9}{n:>8,}{cells}{maj:>8.1f}%")
        print(f"  {'合计':<9}{cal['n_val']:>8,}"
              + "".join(f"{cal['acc'][c]:>9.1f}%" for c, _ in cols)
              + f"{'':>9}")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
