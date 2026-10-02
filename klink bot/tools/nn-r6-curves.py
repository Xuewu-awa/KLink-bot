"""
第六轮：**收敛判定 + 消融表 + 曲线表**（只读 JSON，不重跑训练）。

收敛判定规则（先定死，再看数字，避免事后挑标准）：
  记 `best` = 该配置在整条曲线上的最好留出准确率。
  在「最后一个 epoch」往回扫，找**最长的连续尾段** [a, N] 使得该段内每一点 ≥ best − 1.0 点。
  若这段长度 ≥ 50 个 epoch，则判「**已压平**」，压平点记作该段起点之后的**首次达到 best−1.0 点**
  （即 §报告里写的「压平在第几个 epoch」= 尾段起点 a）。
  另外报三条辅助读数：
    * `最后 100 epoch 斜率`（最小二乘，点/epoch）—— 用来量「还在不在涨」；
    * `末点 vs best`（点）—— 用来量「最后有没有回落」；
    * `训练−留出`（点）—— 欠拟合还是过拟合。
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent

# 线性基线（在**同一份固定留出**上重算过，见 out/_r6-holdout-eval-baselines.txt）
BASELINES = {"F1 6 参": 71.107, "F2l 28 参": 75.887, "F2d 50 参": 79.711}


def analyze(hist, tol_pt=1.0, min_len=50, smooth=20):
    """收敛判定 + **平滑**读数。

    ⚠️ 本轮的留出曲线在 epoch 尺度上**有 ±0.9 点的振荡**（周期约 5 epoch，见 §7）：
    每个 epoch 结尾停在「最后一个随机 mini-batch 更新之后」，这个末态本身带噪声。
    所以除了原始 best/末点，这里再给一条 **20 epoch 滑动平均** 的曲线，
    「压平」判定与「压平时留出」都用平滑曲线 —— 否则早停会在噪声上误触发。
    """
    a = np.array([h["acc"] for h in hist]) * 100
    t = np.array([None if h.get("train_acc") is None else h["train_acc"] * 100
                  for h in hist], dtype=object)
    ep = np.array([h["epoch"] for h in hist])
    w = min(smooth, max(1, len(a) // 4))
    k = np.ones(w) / w
    asm = np.convolve(a, k, mode="valid")                 # 长度 len-w+1，对应 ep[w-1:]
    epsm = ep[w - 1:]
    best_s = float(asm.max())
    best_s_ep = int(epsm[int(asm.argmax())])
    # 平滑曲线上的「压平」：尾段 ≥ min_len 都落在 best_s − tol 内
    i = len(asm) - 1
    while i >= 0 and asm[i] >= best_s - tol_pt:
        i -= 1
    tail_start = i + 1
    tail_len = len(asm) - tail_start
    flat = tail_len >= min_len
    kk = min(100, len(asm))
    slope = float(np.polyfit(epsm[-kk:], asm[-kk:], 1)[0]) if kk >= 5 else float("nan")
    # 训练集读数（只在与 epoch 对齐的那些点上有值）
    tr_pairs = [(int(e), float(v)) for e, v in zip(ep, t) if v is not None]
    tr_last = tr_pairs[-1] if tr_pairs else (None, float("nan"))
    best = float(a.max())
    return dict(best=best, best_ep=int(ep[int(a.argmax())]),
                best_smooth=best_s, best_smooth_ep=best_s_ep,
                last=float(a[-1]), last_ep=int(ep[-1]),
                last_smooth=float(asm[-1]),
                train_last=tr_last[1], train_last_ep=tr_last[0],
                gap=tr_last[1] - best_s,
                tail_len=tail_len, tail_start_ep=int(epsm[tail_start]) if tail_len else None,
                flat=flat, slope100=slope, smooth_w=w, n=len(a),
                loss_last=hist[-1]["loss"], dead_last=hist[-1].get("dead_relu"),
                sec_train=hist[-1].get("sec_train"), sec_eval=hist[-1].get("sec_eval"))


def sample(hist, k=14):
    """等间隔采样，**对齐到每个配置都存在的 epoch**（各配置跑的长度差别很大）。"""
    eps = sorted(set(h["epoch"] for h in hist))
    if len(eps) <= k:
        return eps
    lo = eps[0]
    want = [int(round(lo + (eps[-1] - lo) * i / (k - 1))) for i in range(k)]
    return sorted(set(min(eps, key=lambda e: abs(e - w)) for w in want))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", action="append", required=True,
                    help="结果 JSON；可多次给。多个 tag 合并成一张消融表")
    ap.add_argument("--out", default="out/_r6-curves.txt")
    ap.add_argument("--md", default="out/_r6-tables.md")
    a = ap.parse_args()

    runs = {}
    for p in a.json:
        d = json.loads((REPO / p).read_text(encoding="utf-8"))
        for tag, res in d.items():
            if "hist" in res:
                runs[tag] = res
    order = list(runs.keys())

    lines, md = [], []
    lines.append("=" * 112)
    lines.append("第六轮：收敛判定（规则见脚本头部；容差 1.0 点 / 尾段 ≥50 epoch）")
    lines.append("=" * 112)
    md.append("| 配置 | recipe/opt | batch | lr | hidden | init | epoch 上限 | **跑到的 epoch** | "
              "**压平于** | 尾段长 | **压平时留出(平滑)** | **峰值留出(平滑)** | **峰值(原始)** | "
              "**训练集** | 训练−留出 | 最后100ep斜率 | 打赢 F2d? |")
    md.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")

    for tag in order:
        r = runs[tag]
        s = analyze(r["hist"])
        at_flat = None
        if s["flat"]:
            for h in r["hist"]:
                if h["epoch"] == s["tail_start_ep"]:
                    at_flat = h
                    break
        cfg = (f"{r['recipe']}/{r.get('optimizer')}")
        flat_txt = f"ep {s['tail_start_ep']}" if s["flat"] else "**未压平**"
        ho = s["best_smooth"]
        win = "✅" if s["best_smooth"] > BASELINES["F2d 50 参"] else "❌"
        md.append(f"| `{tag}` | {cfg} | {r['batch']} | {r['lr']} | {r['hidden']} | {r['init']} | "
                  f"{r['epochs_cap']} | **{s['last_ep']}** | {flat_txt} | {s['tail_len']} | "
                  f"{s['last_smooth']:.3f}% | **{s['best_smooth']:.3f}%** @ep{s['best_smooth_ep']} | "
                  f"{s['best']:.3f}% @ep{s['best_ep']} | {s['train_last']:.3f}% | {s['gap']:+.2f} | "
                  f"{s['slope100']:+.4f} | {win} |")
        lines.append(f"\n■ {tag}   {cfg}  batch={r['batch']} lr={r['lr']} hidden={r['hidden']} "
                     f"init={r['init']}  epochs {r['epochs_run']}/{r['epochs_cap']}  "
                     f"更新次数 {r['updates_total']:,}")
        lines.append(f"    停止原因: {r['stop_reason']}")
        lines.append(f"    压平: {'是' if s['flat'] else '否'}   压平于 ep {s['tail_start_ep']} "
                     f"（平滑曲线尾段 {s['tail_len']} 个 epoch 都在 best−1.0 点内；"
                     f"平滑窗 {s['smooth_w']} epoch）")
        lines.append(f"    ★ 峰值留出（平滑）**{s['best_smooth']:.3f}%** @ep{s['best_smooth_ep']}"
                     f"   原始峰值 {s['best']:.3f}% @ep{s['best_ep']}")
        lines.append(f"    末点留出（平滑）{s['last_smooth']:.3f}%  训练 {s['train_last']:.3f}%"
                     f" @ep{s['train_last_ep']}   差 {s['gap']:+.2f} 点")
        lines.append(f"    最后 100 epoch 斜率 {s['slope100']:+.4f} 点/epoch   loss {s['loss_last']:.6f}"
                     f"   死ReLU {s['dead_last']:.2%}   每 epoch {s['sec_train']:.2f}s 训 + "
                     f"{s['sec_eval']:.2f}s 评   总时长 {r['total_sec']:.0f}s")

    # 曲线表
    lines.append("\n" + "=" * 112)
    lines.append("曲线（等间隔采样 ≥10 点；留出 / 训练，单位 %）")
    lines.append("=" * 112)
    idxs = {tag: sample(runs[tag]["hist"], 14) for tag in order}
    hdr = "| epoch |" + "|".join(f" {tag} 留出 | {tag} 训练 " for tag in order) + "|"
    md.append("\n### 曲线\n")
    md.append(hdr)
    md.append("|---|" + "---|" * (2 * len(order)))
    eps = sorted(set().union(*[set(idxs[tag]) for tag in order]))
    lines.append("epoch  " + "  ".join(f"{tag:>26}" for tag in order))
    for e in eps:
        row, cells = f"{e:5d}  ", []
        for tag in order:
            h = next((x for x in runs[tag]["hist"] if x["epoch"] == e), None)
            if h:
                trv = h.get("train_acc")
                trs = f"{trv*100:8.3f}" if trv is not None else "       -"
                cells.append(f"{h['acc']*100:9.3f} /{trs}")
                row += f"{h['acc']*100:11.3f} /{trs}  "
            else:
                cells.append("        -/       -")
                row += f"{'-':>26}"
        md.append("| " + str(e) + " |" + "|".join(f" {c} " for c in cells) + "|")
        lines.append(row)

    # 与基线的对照
    lines.append("\n" + "=" * 112)
    lines.append("与线性基线对照（**同一份固定留出** 15,000 局 / 347,635 条）")
    lines.append("=" * 112)
    md.append("\n### 与基线对照（同一份留出 347,635 条）\n")
    md.append("| 模型 | 留出 | F1 71.107% | F2l 75.887% | F2d 79.711% |")
    md.append("|---|---|---|---|---|")
    for tag in order:
        r = runs[tag]
        s = analyze(r["hist"])
        cells = []
        for k, v in BASELINES.items():
            d = s["best_smooth"] - v
            cells.append(f"{d:+.3f} 点 {'✅' if d > 0 else '❌'}")
        lines.append(f"  {tag:<26} 峰值留出（平滑）{s['best_smooth']:.3f}%   " + "   ".join(
            f"vs {k}: {c}" for k, c in zip(BASELINES, cells)))
        md.append(f"| `{tag}` | **{s['best_smooth']:.3f}%** | " + " | ".join(cells) + " |")
    md.append("\n基线（本轮在固定留出上重算，与第五轮逐位相同）："
              + "，".join(f"{k} = {v:.3f}%" for k, v in BASELINES.items()))

    # ★ 墙上时间 / 吞吐（本轮专用：epoch 不是同一个时间单位）
    lines.append("\n" + "=" * 112)
    lines.append("★ 墙上时间与吞吐（「epoch」在两种配方下差 3,855 倍，不能只比 epoch 数）")
    lines.append("=" * 112)
    md.append("\n### ★ 墙上时间 / 吞吐\n")
    md.append("（墙上秒数取**平滑曲线首次达到该水平**的时刻；没用 early-stopping 的 "
              "best−0.2 做唯一判据，因为那对每条曲线是不同水平。）\n")
    md.append("| 配置 | 每 epoch 训(s) | 每次更新(ms) | 更新/秒 | 达 75% | 达 78% (胜 F2l) | "
              "达 79.8% (胜 F2d) | 达 best−0.2 | 总墙上 | 峰值显存 GiB |")
    md.append("|---|---|---|---|---|---|---|---|---|---|")
    for tag in order:
        r = runs[tag]
        h = r["hist"]
        s = analyze(h)
        # 逐 epoch 累计时间：用 sec_train 求和（评分开销单列）
        acc = np.array([x["acc"] for x in h]) * 100
        eps_all = np.array([x["epoch"] for x in h])
        el = np.array([x["elapsed"] for x in h])
        w = min(20, max(1, len(acc) // 4))
        asm = np.convolve(acc, np.ones(w) / w, mode="valid")
        epsm = eps_all[w - 1:]

        def wall_at(threshold):
            """首次（按平滑曲线）达到阈值时的墙上秒数；从未达到返回 None。"""
            hit = asm >= threshold
            if not hit.any():
                return None
            return (float(el[:len(asm)][hit].min()),
                    int(epsm[hit][int(np.argmin(el[:len(asm)][hit]))]))

        def wall_raw(threshold):
            """**原始曲线**首次达到阈值（无平滑滞后，但单点有噪声）。"""
            hit = acc >= threshold
            if not hit.any():
                return None
            return float(el[hit].min()), int(eps_all[hit][int(np.argmin(el[hit]))])

        r75 = wall_at(75.0)
        t_75, ep_75 = r75 if r75 else (None, None)
        t78 = wall_at(78.0)
        r798 = wall_at(79.8)
        r798raw = wall_raw(79.8)
        rbest = wall_at(s["best_smooth"] - 0.2)
        if rbest is None:
            i = int(asm.argmax())
            t_best, ep_best = float(el[i + w - 1]), int(epsm[i])
        else:
            t_best, ep_best = rbest
        upd_s = r["updates_total"] / max(1e-9, r["total_sec"])
        ms_upd = 1000.0 * r["total_sec"] / max(1, r["updates_total"])
        fmt = lambda t: "—" if t is None else f"{t[0]:.0f} s (ep {t[1]})"
        lines.append(f"  {tag:<12} 每 epoch 训 {s['sec_train']:.2f}s   "
                     f"每次更新 {ms_upd:.2f} ms（{upd_s:,.0f} 更新/秒）   "
                     f"达 75% {fmt(r75)}   达 78% {fmt(t78)}   达 79.8% {fmt(r798)}"
                     f"（原始曲线 {fmt(r798raw)}）   达 best−0.2 {t_best:.0f}s   总 {r['total_sec']:.0f}s")
        md.append(f"| `{tag}` | {s['sec_train']:.2f} | {ms_upd:.3f} | {upd_s:,.0f} | "
                  f"{fmt(r75)} | {fmt(t78)} | {fmt(r798)}<br>原始 {fmt(r798raw)} | {t_best:.0f} s | "
                  f"{r['total_sec']:.0f} s | {r.get('peak_vram_gib', float('nan')):.2f} |")

    (REPO / a.out).write_text("\n".join(lines), encoding="utf-8")
    (REPO / a.md).write_text("\n".join(md), encoding="utf-8")
    print("\n".join(lines))
    print(f"\n→ {a.out}\n→ {a.md}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
