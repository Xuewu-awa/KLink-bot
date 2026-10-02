"""
nn-early-probe.py —— 「NN 前期不出牌」诊断：对**值网**做受控探针（partial dependence）。

**只测量，不改任何东西。** 本脚本不训练、不改模型、不改内核。

问题：NNPlay 走 rollout 时，值网常常认为「结束回合」比「出牌」好。
     本脚本直接在**值网函数**上做单变量扫描：固定一个真实局面，只改一个特征，
     看 P(视角方胜) 怎么变。这比对局日志里的相关性有力得多 —— 变量是控干净的。

数据来源：`NNTrain dump` 的 v2 文件（'AKL2'）。每条 = 一个真实「回合交界」局面 +
          左方最终胜负。默认只用**留出集**（复刻 NNTrain 的按局切分，见 nn_common）。

用法:
  python "klink bot/tools/nn-early-probe.py" \
      --model out/nn-model-100k-handfix.bin \
      --data  out/nn-data-100k-handfix.bin \
      --n 4000 --out out/_early-probe.txt
"""

import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nn_common as nc          # noqa: E402

# ---- 编码布局（v2，dim=745；见 src/KLink.Bot/NN/StateEncoder.cs 的偏移注释）----
OFF_P = 3                       # 视角方块（训练时 perspective = Left）
PER_SIDE = 371
OFF_O = OFF_P + PER_SIDE        # 对手块
SLOTS = dict(hqDef=0, kredits=1, maxKredits=2, handCount=3, deckCount=4, boardCount=5)

TURN_SCALE = 30.0
CPT_SCALE = 10.0


def pcol(name):
    return OFF_P + SLOTS[name]


def ocol(name):
    return OFF_O + SLOTS[name]


def probs(model, X):
    return nc.sigmoid(nc.forward_logit(model, X))


def sweep(model, X, col, values, base_p):
    """把 X[:, col] 整列换成 values 里的每个值，返回 (值, 平均P, 平均ΔP, 翻转率)。"""
    out = []
    for v in values:
        Y = X.copy()
        Y[:, col] = v
        p = probs(model, Y)
        out.append((v, float(p.mean()), float((p - base_p).mean()),
                    float(((p > 0.5) != (base_p > 0.5)).mean())))
    return out


def table(title, unit, rows, base_p, note=""):
    lines = [f"\n### {title}" + (f"   {note}" if note else "")]
    lines.append(f"   {'值':>6}  {'平均P(胜)':>10}  {'ΔP vs 实际值':>13}  {'跨过0.5的比例':>13}")
    for v, mp, dp, flip in rows:
        disp = v * unit
        lines.append(f"   {disp:>6.2f}  {mp:>9.4f}  {dp:>+12.4f}  {flip:>12.1%}")
    lines.append(f"   （基准 = 各样本**实际**特征值下的平均 P = {base_p.mean():.4f}）")
    return "\n".join(lines)


def rate_table(title, vals, labels, unit, note=""):
    """真实数据里「特征值 → 左方实际胜率」的对照表（回答「相关」而不是「模型权重」）。"""
    lines = [f"\n### {title}" + (f"   {note}" if note else "")]
    lines.append(f"   {'值':>6}  {'样本数':>9}  {'实际胜率':>9}")
    for v, m, n in zip(vals, labels, [None] * len(vals)):
        lines.append(f"   {v * unit:>6.2f}  {m[1]:>9d}  {m[0]:>8.4f}")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="out/nn-model-100k-handfix.bin")
    ap.add_argument("--data", default="out/nn-data-100k-handfix.bin")
    ap.add_argument("--n", type=int, default=4000, help="探针用的局面条数")
    ap.add_argument("--split-seed", type=int, default=12345)
    ap.add_argument("--all", action="store_true", help="用全量而不是留出集")
    ap.add_argument("--out", default="")
    ap.add_argument("--seed", type=int, default=20260927)
    args = ap.parse_args()

    L = []

    def say(s=""):
        print(s)
        L.append(s)

    model = nc.load_model(args.model)
    rec, dim, n, decks = nc.open_data2(args.data)
    assert dim == 745, dim

    say(f"模型     : {args.model}")
    say(f"           MLP {model['dim']}→{model['hidden']}→1  hidden={model['hidden']}")
    say(f"数据     : {args.data}   共 {n:,} 条（v2, dim={dim}, 卡组 {decks} 套）")

    # ---- 留出集切分（逐位复刻 NNTrain）----
    rng = np.random.default_rng(args.seed)
    if args.all:
        sel = rng.choice(n, size=min(args.n, n), replace=False)
        split_note = "全量随机抽样"
    else:
        gids = np.asarray(rec[:, dim + 1], dtype=np.float64)
        val_mask, _, val_games = nc.nntrain_split(gids, args.split_seed)
        idx = np.flatnonzero(val_mask)
        sel = rng.choice(idx, size=min(args.n, idx.size), replace=False)
        split_note = f"留出集（{len(val_games)} 局 / {idx.size:,} 条）"
    sel = np.sort(sel)

    X = np.asarray(rec[sel, :dim], dtype=np.float32).copy()
    y = np.asarray(rec[sel, dim], dtype=np.float32)
    say(f"探针样本 : {len(sel):,} 条   {split_note}   实际胜率 {y.mean():.4f}")
    say(f"           （模型对这批样本的平均 P = {probs(model, X).mean():.4f}）")

    base_p = probs(model, X)

    # ---- 决策点子集：NNPlay 决策时 ActiveSide 恒等于视角方 ⇒ v[1]=1 ----
    act1 = X[:, 1] > 0.5
    say(f"           其中 ActiveSide==视角方（= NNPlay 决策点形态）的 {int(act1.sum()):,} 条"
        f"（{act1.mean():.1%}），平均 P = {base_p[act1].mean() if act1.any() else float('nan'):.4f}")

    # ==================== 单变量扫描 ====================
    say("\n" + "=" * 78)
    say("单变量扫描（partial dependence）：固定每个真实局面，只改一个特征")
    say("=" * 78)

    # --- 视角方手牌数 /10 ---
    rows = sweep(model, X, pcol("handCount"), [h / 10 for h in range(0, 13)], base_p)
    say(table("H2-a  视角方手牌数（handCount/10）", 10, rows, base_p))
    if act1.any():
        rows1 = sweep(model, X[act1], pcol("handCount"), [h / 10 for h in range(0, 13)],
                      base_p[act1])
        say(table("H2-a' 同上，只取决策点形态（v[1]=1）", 10, rows1, base_p[act1]))

    # --- 视角方 kredit /12 ---
    rows = sweep(model, X, pcol("kredits"), [k / 12 for k in range(0, 13)], base_p)
    say(table("H2-b  视角方当前 kredit（kredits/12）", 12, rows, base_p))

    # --- 视角方 maxKredits /12 ---
    rows = sweep(model, X, pcol("maxKredits"), [k / 12 for k in range(1, 13)], base_p)
    say(table("H2-c  视角方 kredit 上限（maxKredits/12）", 12, rows, base_p))

    # --- 视角方场上单位数 /10 ---
    rows = sweep(model, X, pcol("boardCount"), [b / 10 for b in range(0, 7)], base_p)
    say(table("H2-d  视角方场上单位数（boardCount/10）", 10, rows, base_p))

    # --- 对手场上单位数 /10 ---
    rows = sweep(model, X, ocol("boardCount"), [b / 10 for b in range(0, 7)], base_p)
    say(table("H2-e  对手场上单位数（对手 boardCount/10）", 10, rows, base_p))

    # --- 视角方 HQ 防御 /20 ---
    rows = sweep(model, X, pcol("hqDef"), [h / 20 for h in range(0, 21, 2)], base_p)
    say(table("H2-f  视角方 HQ 防御（hqDef/20）", 20, rows, base_p))

    # --- 回合数 /30 ---
    rows = sweep(model, X, 0, [t / TURN_SCALE for t in range(1, 31)], base_p)
    say(table("H4-a  全局回合号（Turn/30）", TURN_SCALE, rows, base_p))

    # --- activeIsPerspective ---
    rows = sweep(model, X, 1, [0.0, 1.0], base_p)
    say(table("H4-b  ActiveSide==视角方（activeIsPerspective）", 1, rows, base_p))

    # --- CardsPlayedThisTurn /10 ---
    rows = sweep(model, X, 2, [c / CPT_SCALE for c in range(0, 9)], base_p)
    say(table("H3   本回合已出牌数（cardsPlayedThisTurn/10）", CPT_SCALE, rows, base_p))

    # ==================== 卡向量块的消融 ====================
    #
    # 动机：NNEarlyProbe 的消融实验显示，「过牌 vs 出牌」的分差**几乎全靠**视角方那 4 个
    #       90 维池化卡向量块（抹掉它们分差就塌了）。这里独立量一下：单看值网，
    #       每个块本身承载多少信息 —— 把它换成训练集均值（z=0）看 P 变多少。
    say("\n" + "=" * 78)
    say("卡向量块消融：把某一块换成训练集均值（= 抹掉「这里有哪些牌」）")
    say("=" * 78)
    block_defs = []
    for tag, base in (("p", OFF_P), ("o", OFF_O)):
        for i, zn in enumerate(["Hand", "Front", "Half", "Disc"]):
            block_defs.append((f"{tag}_{zn}Vec", base + 6 + i * 91, 90))
            block_defs.append((f"{tag}_{zn}Count", base + 6 + i * 91 + 90, 1))

    say(f"   {'块':<12} {'平均ΔP(点)':>11} {'平均|ΔP|(点)':>13}   （正 = 抹掉之后 P 上升）")
    for name, start, ln in block_defs:
        Y = X.copy()
        Y[:, start:start + ln] = model["mean"][start:start + ln]
        p = probs(model, Y)
        d = p - base_p
        say(f"   {name:<12} {d.mean() * 100:>+11.2f} {np.abs(d).mean() * 100:>13.2f}")

    # 全部 4 个视角方卡向量块一起抹掉
    Y = X.copy()
    for name, start, ln in block_defs:
        if name.startswith("p_") and name.endswith("Vec"):
            Y[:, start:start + ln] = model["mean"][start:start + ln]
    d = probs(model, Y) - base_p
    say(f"   {'p_*Vec 全抹':<12} {d.mean() * 100:>+11.2f} {np.abs(d).mean() * 100:>13.2f}")

    # ==================== 数据侧的真实相关（对照）====================
    say("\n" + "=" * 78)
    say("对照：这些特征在**真实数据**里与最终胜负的相关（不是模型的权重）")
    say("=" * 78)

    def by_value(col, scale, rng_):
        raw = np.rint(X[:, col] * scale).astype(np.int32)
        out = []
        for v in rng_:
            m = raw == v
            out.append((float(y[m].mean()) if m.any() else float("nan"), int(m.sum())))
        return out

    for name, col, scale, rng_ in [
        ("视角方手牌数", pcol("handCount"), 10, range(0, 12)),
        ("视角方当前 kredit", pcol("kredits"), 12, range(0, 13)),
        ("视角方场上单位数", pcol("boardCount"), 10, range(0, 6)),
        ("视角方半场单位数", OFF_P + 6 + 2 * 91 + 90, 10, range(0, 6)),
        ("视角方前线单位数", OFF_P + 6 + 1 * 91 + 90, 10, range(0, 6)),
        ("全局回合号", 0, TURN_SCALE, range(1, 31, 2)),
    ]:
        raw = np.rint(X[:, col] * scale).astype(np.int32)
        say(f"\n### 数据对照  {name}")
        say(f"   {'值':>6}  {'样本数':>9}  {'实际胜率':>9}  {'模型平均P':>10}  {'模型−实际':>10}")
        for v in rng_:
            m = raw == v
            if not m.any():
                continue
            say(f"   {v:>6}  {int(m.sum()):>9,}  {y[m].mean():>8.4f}  {base_p[m].mean():>10.4f}"
                f"  {base_p[m].mean() - y[m].mean():>+10.4f}")

    # ---- 决策点形态下的数据对照（最相关的一格）----
    say("\n### 数据对照（只取决策点形态 v[1]=1）  视角方手牌数 → 实际胜率")
    say(f"   {'值':>6}  {'样本数':>9}  {'实际胜率':>9}  {'模型平均P':>10}")
    if act1.any():
        raw = np.rint(X[act1, pcol("handCount")] * 10).astype(np.int32)
        pp = base_p[act1]
        for v in range(0, 12):
            m = raw == v
            if m.sum() == 0:
                continue
            say(f"   {v:>6}  {int(m.sum()):>9,}  {y[act1][m].mean():>8.4f}  {pp[m].mean():>10.4f}")

    # ==================== 决策点形态：模型 vs 数据 的一致性 ====================
    say("\n" + "=" * 78)
    say("汇总：决策点形态（v[1]=1）下，各特征每 +1 单位带来的 ΔP")
    say("=" * 78)
    say(f"   {'特征':<26} {'ΔP / +1':>10}  {'方向':<8}")
    if act1.any():
        Xa = X[act1]
        ba = base_p[act1]
        for name, col, scale, lo, hi in [
            ("视角方手牌数", pcol("handCount"), 10, 3, 10),
            ("视角方 kredit", pcol("kredits"), 12, 0, 12),
            ("视角方场上单位数", pcol("boardCount"), 10, 0, 5),
            ("对手场上单位数", ocol("boardCount"), 10, 0, 5),
            ("视角方 HQ 防御", pcol("hqDef"), 20, 5, 20),
            ("全局回合号", 0, TURN_SCALE, 3, 25),
            ("本回合已出牌数", 2, CPT_SCALE, 0, 8),
        ]:
            plo = probs(model, _setcol(Xa, col, lo / scale)).mean()
            phi = probs(model, _setcol(Xa, col, hi / scale)).mean()
            d = (phi - plo) / (hi - lo)
            say(f"   {name:<26} {d:>+10.4f}  {'越大越好' if d > 0 else '越大越差':<8}")

    txt = "\n".join(L) + "\n"
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(txt)
        print(f"\n已写入 {args.out}")


def _setcol(X, col, v):
    Y = X.copy()
    Y[:, col] = v
    return Y


if __name__ == "__main__":
    main()
