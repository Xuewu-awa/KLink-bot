"""
第八轮 ★ 的三方对比表：把 4 个格子摆到同一张表上，并回答任务书的三个问题。

格子：
  A `R6-adam-100k`   100k · **旧内核**训练 · **旧内核**评测  —— 第六轮/第七轮那组数字（88%）
  B `R6-adam-100k`   100k · **旧内核**训练 · **新内核**评测  —— 上一轮 ①
  C `R8-10k-handfix` 10k  · **新内核**训练 · 新内核评测     —— 上一轮 ③
  D `R8-100k-handfix`100k · **新内核**训练 · 新内核评测     —— ★ 本轮

B/C/D 三格来自 `out/_r8-rollout.json`（本轮重跑，seeds 1..40，同一内核/IR/对位）；
A 来自 `out/_r7-rollout.json`（旧内核，seeds 1..40）。

同时给出 seeds 1..20 子集（任务书要求的 20 局/边）。

用法：python -X utf8 "klink bot/tools/nn-r8-compare.py"
"""

import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent

MU = "C-真均势"


def wilson(k, n, z=1.96):
    if n == 0:
        return (float("nan"), float("nan"))
    p = k / n
    d = 1 + z * z / n
    c = p + z * z / (2 * n)
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return (100 * (c - h) / d, 100 * (c + h) / d)


def load(p):
    return json.loads((REPO / p).read_text(encoding="utf-8"))


def agg(rows):
    win = sum(1 for r in rows if r["outcome"] == "win")
    loss = sum(1 for r in rows if r["outcome"] == "loss")
    abort = len(rows) - win - loss
    turns = sum(r.get("nn_turns", 0) for r in rows)
    idle = sum(r.get("idle_turns", 0) for r in rows)
    atk = sum(r.get("attack", 0) for r in rows)
    atkt = sum(r.get("attack_turns", 0) for r in rows)
    return dict(n=len(rows), win=win, loss=loss, abort=abort, turns=turns, idle=idle,
                atk=atk, atk_turns=atkt, play=sum(r.get("play", 0) for r in rows),
                dec=sum(r.get("decisions", 0) for r in rows),
                sb=sum(r.get("self_before", 0) for r in rows) / len(rows),
                sa=sum(r.get("self_after", 0) for r in rows) / len(rows))


def pick(data, tag, seeds):
    d = data.get(f"{tag}|{MU}|{{side}}")  # 占位，实际下面逐边取
    rows = []
    for side in ("left", "right"):
        blk = data.get(f"{tag}|{MU}|{side}", {})
        rows += [blk[str(s)] for s in seeds if str(s) in blk]
    return rows


def line(label, g, note=""):
    valid = g["win"] + g["loss"]
    lo, hi = wilson(g["win"], valid) if valid else (float("nan"), float("nan"))
    wr = f"{100.0 * g['win'] / valid:.0f}%" if valid else "n/a"
    ci = f"[{lo:.0f},{hi:.0f}]" if valid else ""
    return (f"{label:<34}{g['n']:>5}{g['win']:>5}{g['loss']:>5}{g['abort']:>5}"
            f"{(wr + ' ' + ci):>16}{g['atk'] / g['n']:>10.2f}"
            f"{g['atk'] / g['turns']:>12.3f}{g['atk_turns'] / g['n']:>13.2f}"
            f"{g['play'] / g['n']:>10.2f}"
            f"{g['idle']:>6}/{g['turns']:<5}{100.0 * g['idle'] / g['turns']:>8.1f}%"
            f"{g['sb']:>8.1f}%{g['sa']:>8.1f}%{g['dec'] / g['n']:>9.1f}   {note}")


def main():
    r8 = load("out/_r8-rollout.json")        # 新内核：B / C / D
    r7 = load("out/_r7-rollout.json")        # 旧内核：A

    L = []
    p = L.append
    HDR = (f"{'格子（数据·训练内核·评测内核）':<34}{'局数':>5}{'胜':>5}{'负':>5}{'中止':>5}"
           f"{'胜率(95%CI)':>16}{'攻击/局':>10}{'攻击/NN回合':>12}{'有攻击回合/局':>13}"
           f"{'出牌/局':>10}{'空过':>11}{'空过率':>9}{'自评前':>8}{'自评后':>8}{'决策/局':>9}")

    for seeds, title in ((list(range(1, 41)), "seeds 1..40（每边 40 局，n=80）"),
                         (list(range(1, 21)), "seeds 1..20（每边 20 局，n=40，任务书要求）")):
        p("=" * 190)
        p(f"三方对比   {title}   对位 {MU}（NN 美澳跳 vs 对手 英日）")
        p("=" * 190)
        p(HDR)
        p(line("A 第六轮 100k · 旧 · 旧（R6 模型）", agg(pick(r7, "R6-adam-100k", seeds)),
               "第七轮那组数字"))
        p(line("B 100k · 旧 · 新（同一 R6 模型）", agg(pick(r8, "R6-adam-100k", seeds)),
               "内核单变量 vs A"))
        p(line("C 10k · 新 · 新", agg(pick(r8, "R8-10k-handfix", seeds)),
               "上一轮 ③"))
        p(line("D ★ 100k · 新 · 新", agg(pick(r8, "R8-100k-handfix", seeds)),
               "本轮 ★"))
        p("")

    # ---------------- 确定性自证 ----------------
    old = load("out/_handfix-rollout.json")
    p("=" * 190)
    p("确定性自证：本轮重跑的 B/C 两格 vs 上一轮 `out/_handfix-rollout.json`（逐 seed 逐字段）")
    p("=" * 190)
    for tag in ("R6-adam-100k", "R8-10k-handfix"):
        same = all(old.get(f"{tag}|{MU}|{s}", {}).get(str(k)) == r8.get(f"{tag}|{MU}|{s}", {}).get(str(k))
                   for s in ("left", "right") for k in range(1, 41))
        p(f"  {tag:<20} 40 seeds × 2 边 全部字段相同 = {same}")
    p("")

    txt = "\n".join(L)
    print(txt)
    (REPO / "out/_r8-compare.txt").write_text(txt, encoding="utf-8")
    print("→ out/_r8-compare.txt")
    return 0


if __name__ == "__main__":
    sys.exit(main())
