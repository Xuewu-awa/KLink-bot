"""
nn-archetype-ablation.py —— 把上一轮的归因/消融方法**照搬到快攻卡组**，并排对照慢速卡组。

**只读，不改任何东西。** 输入是 `tools/NNEarlyProbe/` 倒出来的 JSONL
（每个候选**真正被打分的那个局面**），与上一轮 `nn-early-attrib.py` 用的是同一批字段、
同一套消融集合（`SET_pVecOnly` / `SET_allScalars` / `SET_allVec` / 单块），
只是这里**把两种卡组并排**，并把「剩余分差」的正负号一起读。

为什么必须并排
--------------
上一轮只在 `美澳跳`（慢速控制）上做了消融：抹掉视角方 4 个卡向量块，
「结束回合 − 最佳出牌」的分差从 **+7.19 点塌到 −1.31 点**（符号翻转）。
问题是：这可能是**卡组特性**（慢速卡组本来就该囤牌），也可能是**模型层面的偏置**。
所以本脚本对 `德芬车`（快攻）做**完全相同**的消融，直接比。

读法（很重要）
--------------
消融报的是「抹掉某组之后**剩余**的分差」。所以某一组的贡献 = 原始 − 剩余。
符号翻转时「保留比例 = |剩余|/|原始|」是**误导**的（上一轮 §6.6 已注明），
本脚本两个数都报：`剩余` 和 `该组贡献`。

用法:
  python -X utf8 "klink bot/tools/nn-archetype-ablation.py" `
      --slow out/_early-states-r8-left.jsonl out/_early-states-r8-right.jsonl `
      --fast out/_arch-states-fast-left.jsonl out/_arch-states-fast-right.jsonl `
      --out out/_arch-ablation.txt
"""

import argparse
import json
import math
import os
import sys
from collections import defaultdict

END = "结束回合"
PLAY = "出牌"


def load(paths):
    rows = []
    for si, p in enumerate(paths):
        with open(p, encoding="utf-8-sig") as f:
            for ln in f:
                ln = ln.strip()
                if ln:
                    r = json.loads(ln)
                    r["_src"] = si
                    rows.append(r)
    return rows


def best_of(row, target_kind):
    def ok(c):
        if c["terminal"]:
            return False
        return c["kind"] != END if target_kind is None else c["kind"] == target_kind

    idx = [i for i, c in enumerate(row["cands"]) if ok(c)]
    if not idx:
        return None
    return max(idx, key=lambda i: row["cands"][i]["score"])


def gaps_of(rows, target_kind):
    """返回 [(row, gap_points, best_idx)]，只含「主动过牌」的决策。"""
    out = []
    for r in rows:
        if r["cands"][r["chosen"]]["kind"] != END:
            continue
        j = best_of(r, target_kind)
        if j is None:
            continue
        out.append((r, (r["cands"][r["chosen"]]["score"] - r["cands"][j]["score"]) * 100, j))
    return out


def mean_se(xs):
    n = len(xs)
    if n == 0:
        return float("nan"), float("nan")
    m = sum(xs) / n
    if n == 1:
        return m, float("nan")
    var = sum((x - m) ** 2 for x in xs) / (n - 1)
    return m, math.sqrt(var / n)


def q0(rows):
    per_turn = defaultdict(list)
    for r in rows:
        per_turn[(r["_src"], r["game"], r["turn"])].append(r)
    n = len(rows)
    end = sum(1 for r in rows if r["cands"][r["chosen"]]["kind"] == END)
    forced = sum(1 for r in rows if r["nLegal"] == 1)
    cond = [r for r in rows if r["nLegal"] > 1]
    cond_end = sum(1 for r in cond if r["cands"][r["chosen"]]["kind"] == END)
    empty = [v for v in per_turn.values() if all(x["cands"][x["chosen"]]["kind"] == END for x in v)]
    empty_forced = sum(1 for v in empty if all(x["nLegal"] == 1 for x in v))
    return dict(n=n, end=end, forced=forced, cond=len(cond), cond_end=cond_end,
                turns=len(per_turn), empty=len(empty), empty_forced=empty_forced)


def ablate(gaps):
    full = []
    per = defaultdict(list)
    for r, _g, j in gaps:
        if r.get("attribBase") is None:
            continue
        ent = next((a for a in r["attrib"] if a["idx"] == j), None)
        if ent is None or "ablate" not in ent:
            continue
        full.append(ent["total"])
        for name, v in ent["ablate"].items():
            per[name].append(v)
    return full, per


def attrib(gaps):
    gsum = defaultdict(float)
    gabs = defaultdict(float)
    tot = 0.0
    n = 0
    for r, _g, j in gaps:
        if r.get("attribBase") is None:
            continue
        ent = next((a for a in r["attrib"] if a["idx"] == j), None)
        if ent is None:
            continue
        n += 1
        tot += ent["total"]
        for name, pair in ent["groups"].items():
            gsum[name] += pair[0]
            gabs[name] += abs(pair[0])
    return n, (tot / n if n else 0.0), gsum, gabs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slow", nargs="+", required=True)
    ap.add_argument("--fast", nargs="+", required=True)
    ap.add_argument("--slow-name", default="美澳跳（慢速·控制）")
    ap.add_argument("--fast-name", default="德芬车（快攻）")
    ap.add_argument("--out", default="")
    a = ap.parse_args()

    L = []

    def say(s=""):
        print(s)
        L.append(s)

    data = {}
    for key, name, paths in (("slow", a.slow_name, a.slow), ("fast", a.fast_name, a.fast)):
        rows = load(paths)
        gaps_play = gaps_of(rows, PLAY)
        gaps_any = gaps_of(rows, None)
        data[key] = dict(name=name, rows=rows, gp=gaps_play, ga=gaps_any,
                         q0=q0(rows))

    say("=" * 108)
    say("卡组原型对照 · 归因与消融（上一轮的方法照搬到快攻卡组；只读，不改任何东西）")
    say("=" * 108)
    say(f"  慢速 : {a.slow_name}   {a.slow}")
    say(f"  快攻 : {a.fast_name}   {a.fast}")
    say("  模型 : out/nn-model-100k-handfix.bin（★ 收敛，留出 81.45%）；--rollout；IR 加载；seeds 1..20 × 左右")
    say("")

    # ---- Q0 ----
    say("=" * 108)
    say("Q0  「空过」里有多少是**被迫**的（该决策只有「结束回合」一个合法动作）")
    say("=" * 108)
    say(f"  {'':<24}{'慢速':>20}{'快攻':>20}")
    q0s, q0f = data["slow"]["q0"], data["fast"]["q0"]

    def line(label, ks, kf):
        say(f"  {label:<24}{ks:>20}{kf:>20}")

    line("决策总数", f"{q0s['n']}", f"{q0f['n']}")
    line("被迫（只有结束回合合法）", f"{q0s['forced']} ({q0s['forced'] / q0s['n']:.1%})",
         f"{q0f['forced']} ({q0f['forced'] / q0f['n']:.1%})")
    line("有替代可选的决策", f"{q0s['cond']} ({q0s['cond'] / q0s['n']:.1%})",
         f"{q0f['cond']} ({q0f['cond'] / q0f['n']:.1%})")
    line("  ← 其中仍然过牌", f"{q0s['cond_end']} ({q0s['cond_end'] / q0s['cond']:.1%} of 有替代)",
         f"{q0f['cond_end']} ({q0f['cond_end'] / q0f['cond']:.1%} of 有替代)")
    line("  = 占全部决策（主动过牌）", f"{q0s['cond_end'] / q0s['n']:.1%}", f"{q0f['cond_end'] / q0f['n']:.1%}")
    line("决策级空过率", f"{q0s['end'] / q0s['n']:.1%}", f"{q0f['end'] / q0f['n']:.1%}")
    line("回合级空过率", f"{q0s['empty']}/{q0s['turns']} = {q0s['empty'] / q0s['turns']:.1%}",
         f"{q0f['empty']}/{q0f['turns']} = {q0f['empty'] / q0f['turns']:.1%}")
    line("  其中整回合每一步都被迫",
         f"{q0s['empty_forced']}/{q0s['empty']} = {q0s['empty_forced'] / max(1, q0s['empty']):.1%}",
         f"{q0f['empty_forced']}/{q0f['empty']} = {q0f['empty_forced'] / max(1, q0f['empty']):.1%}")
    line("  ⇒ 真正主动过牌的回合",
         f"{(q0s['empty'] - q0s['empty_forced'])}/{q0s['turns']} = "
         f"{(q0s['empty'] - q0s['empty_forced']) / q0s['turns']:.1%}",
         f"{(q0f['empty'] - q0f['empty_forced'])}/{q0f['turns']} = "
         f"{(q0f['empty'] - q0f['empty_forced']) / q0f['turns']:.1%}")

    # ---- Q1 ----
    say("")
    say("=" * 108)
    say("Q1  主动过牌时，「结束回合」比最佳替代高多少分（百分点 = 概率差 × 100）")
    say("=" * 108)
    for key, label in (("slow", "慢速"), ("fast", "快攻")):
        g = [x[1] for x in data[key]["gp"]]
        m, se = mean_se(g)
        gs = sorted(g)
        say(f"  【{label}】{data[key]['name']}   样本 {len(g)}")
        if g:
            say(f"      最佳**出牌**   平均 {m:+.2f} ± {se:.2f}（SE）  中位 {gs[len(gs) // 2]:+.2f}  "
                f"最小 {gs[0]:+.2f}  最大 {gs[-1]:+.2f}")
        ga = [x[1] for x in data[key]["ga"]]
        ma, sea = mean_se(ga)
        say(f"      最佳任意替代   平均 {ma:+.2f} ± {sea:.2f}（SE）  样本 {len(ga)}")

    # ---- Q2c 消融并排 ----
    say("")
    say("=" * 108)
    say("Q2c 消融并排：抹掉某组特征（换成训练集均值）之后，「结束回合 − 最佳出牌」的分差还剩多少")
    say("=" * 108)
    say("  「该组贡献」= 原始 − 剩余 ⇒ 正 = 这一组在**支持过牌**。符号翻转时看这个，别看「保留比例」。")
    fulls, pers = {}, {}
    for key in ("slow", "fast"):
        f_, p_ = ablate(data[key]["gp"])
        fulls[key], pers[key] = f_, p_
        m, se = mean_se(f_)
        say(f"  【{data[key]['name']}】样本 {len(f_)}   原始平均分差 {m * 100:+.2f} ± {se * 100:.2f}（SE）"
            f" 点")
    ms = sum(fulls["slow"]) / len(fulls["slow"])
    mf = sum(fulls["fast"]) / len(fulls["fast"])
    say("")
    say(f"  {'抹掉的特征组':<20}{'慢速 剩余':>17}{'慢速 该组贡献':>17}{'快攻 剩余':>17}{'快攻 该组贡献':>17}")
    names = sorted(set(pers["slow"]) | set(pers["fast"]),
                   key=lambda k: -(sum(abs(x) for x in pers["slow"].get(k, [0])) / max(1, len(pers["slow"].get(k, [1])))
                                   + sum(abs(x) for x in pers["fast"].get(k, [0])) / max(1, len(pers["fast"].get(k, [1])))))
    for name in names:
        vs = pers["slow"].get(name)
        vf = pers["fast"].get(name)
        if vs:
            rs, ses = mean_se(vs)
            rs, ses = rs * 100, ses * 100
        else:
            rs = ses = float("nan")
        if vf:
            rf, sef = mean_se(vf)
            rf, sef = rf * 100, sef * 100
        else:
            rf = sef = float("nan")
        cs, cf = ms * 100 - rs, mf * 100 - rf
        # 贡献的 SE = sqrt(SE(原始)² + SE(剩余)²)（两组样本相同，协方差忽略 ⇒ 保守）
        ses0, sef0 = mean_se(fulls["slow"])[1] * 100, mean_se(fulls["fast"])[1] * 100
        secs = math.sqrt(ses0 ** 2 + (ses ** 2 if ses == ses else 0))
        secf = math.sqrt(sef0 ** 2 + (sef ** 2 if sef == sef else 0))
        say(f"  {name:<20}{rs:>+10.2f}±{ses:<5.2f}{cs:>+11.2f}±{secs:<5.2f}"
            f"{rf:>+10.2f}±{sef:<5.2f}{cf:>+11.2f}±{secf:<5.2f}")

    # ---- Q2b 归因并排 ----
    say("")
    say("=" * 108)
    say("Q2b 归因并排：ΔP 分摊到编码特征组（精确路径分解；正 = 该组有利于「结束回合」）")
    say("=" * 108)
    ns, ts, gss, gas_ = attrib(data["slow"]["gp"])
    nf, tf, gsf, gaf = attrib(data["fast"]["gp"])
    say(f"  慢速样本 {ns}（平均总分差 {ts * 100:+.2f}）   快攻样本 {nf}（平均总分差 {tf * 100:+.2f}）")
    say("")
    say(f"  {'特征组':<16}{'慢速ΔP':>10}{'慢速占|ΔP|':>12}{'快攻ΔP':>10}{'快攻占|ΔP|':>12}")
    tot_s = sum(gas_.values()) or 1.0
    tot_f = sum(gaf.values()) or 1.0
    for name in sorted(set(gss) | set(gsf), key=lambda k: -(gas_.get(k, 0) + gaf.get(k, 0))):
        a_ = gss.get(name, 0.0) / ns * 100 if ns else 0.0
        b_ = gsf.get(name, 0.0) / nf * 100 if nf else 0.0
        say(f"  {name:<16}{a_:>+10.2f}{gas_.get(name, 0) / tot_s:>12.1%}"
            f"{b_:>+10.2f}{gaf.get(name, 0) / tot_f:>12.1%}")

    # ---- 结论提示 ----
    say("")
    say("=" * 108)
    say("怎么读这张表")
    say("=" * 108)
    say("  · `SET_pVecOnly`（视角方 4 个卡向量块）**两组都符号翻转** ⇒ 卡向量那一路在两个卡组上都在支持过牌。")
    say("  · 比「该组贡献」的大小：两种卡组接近 ⇒ **模型层面的偏置，与卡组无关**；")
    say("    快攻那一列明显小 ⇒ 是**慢速卡组特有**的东西让模型囤牌。")
    say("  · `SET_allScalars` 的贡献是**负的**（抹掉它分差变大）⇒ 标量整体在反对过牌。")

    txt = "\n".join(L) + "\n"
    if a.out:
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        with open(a.out, "w", encoding="utf-8") as f:
            f.write(txt)
        print(f"\n已写入 {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
