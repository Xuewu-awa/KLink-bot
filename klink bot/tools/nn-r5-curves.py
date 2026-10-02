"""第五轮：把 10k / 100k 两条逐 epoch 曲线对齐成表，并判定「压平了没有」。

判据（先说清，避免事后挑口径）：
  * 每个 epoch 的留出准确率直接取训练日志；
  * 「压平」= 某点之后**再没有**超过该点 + 0.20 个点的读数（0.20 是我事先定的容差，
    等于本数据上留出 34.8 万条时的 ~1 个标准误量级，见报告 §口径）。
  * 另外给出「乐观上界线性外推」用于说明「没压平」是不是会被早期陡段骗到：
    只用最后 20 个 epoch 做最小二乘，报斜率与「按该斜率到 epoch 150 会到多少」。
"""
import argparse
import re
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
# C# 日志： `  epoch   5  loss 0.676888  验证准确率 59.49%  训练准确率 59.98%`
PAT_C = re.compile(r"^\s*epoch\s+(\d+)\s+loss\s+([\d.]+)\s+验证准确率\s+([\d.]+)%\s+训练准确率\s+([\d.]+)%", re.M)
# GPU 日志： `      5  0.676736   59.48%   59.98%   (0.3s/epoch)`
PAT_G = re.compile(r"^\s*(\d+)\s+([\d.]+)\s+([\d.]+)%\s+([\d.]+)%\s+\(", re.M)


def parse(path):
    txt = Path(path).read_text(encoding="utf-8", errors="replace")
    pat = PAT_G if re.search(r"\([\d.]+s/epoch\)", txt) else PAT_C
    return [(int(m.group(1)), float(m.group(2)), float(m.group(3)), float(m.group(4)))
            for m in pat.finditer(txt)]


def curve_summary(rows, tol=0.20, tail=20):
    """返回 (最佳epoch, 最佳留出, 压平判定, 尾部斜率/点每epoch, 外推)。"""
    eps = [r[0] for r in rows]
    acc = [r[2] for r in rows]
    n = len(rows)
    best_i = max(range(n), key=lambda i: acc[i])
    # 压平点 = 最后一个「之后再没有超过它 + tol」的 epoch；若最佳点之后还有更大读数，取最佳点
    flat_at = None
    for i in range(n):
        if all(acc[j] <= acc[i] + tol for j in range(i + 1, n)):
            flat_at = i
    tail_rows = rows[-min(tail, n):]
    xs = [r[0] for r in tail_rows]
    ys = [r[2] for r in tail_rows]
    mx = sum(xs) / len(xs)
    my = sum(ys) / len(ys)
    den = sum((x - mx) ** 2 for x in xs)
    slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / den if den else 0.0
    extrap = my + slope * (150 - mx)
    return dict(n=n, last_epoch=eps[-1], last_acc=acc[-1],
                best_epoch=eps[best_i], best_acc=acc[best_i],
                flat_epoch=eps[flat_at] if flat_at is not None else None,
                flat_acc=acc[flat_at] if flat_at is not None else None,
                tail_slope=slope, extrap150=extrap,
                loss_last=rows[-1][1], train_last=rows[-1][3])


def fmt_curve(rows, every=10, extra=()):
    out = []
    want = set(extra)
    for ep, loss, acc, tr in rows:
        if ep % every == 0 or ep in want or ep == rows[-1][0]:
            t = f"{tr:6.2f}%" if tr == tr else "   n/a"
            out.append(f"  {ep:3d}  {loss:.6f}  {acc:7.2f}%  {t}")
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--k10", default="out/_r5-train-win-10k-ep150.txt")
    ap.add_argument("--k100", default="out/_r5-train-win-100k-ep150.txt")
    ap.add_argument("--out", default="out/_r5-curves.txt")
    a = ap.parse_args()

    L = [f"# 第五轮逐 epoch 曲线对齐（生成于本脚本，输入 = 训练日志原文）",
         f"#   10k : {a.k10}", f"#   100k: {a.k100}", ""]
    res = {}
    for tag, p in (("10k", a.k10), ("100k", a.k100)):
        rows = parse(REPO / p)
        if not rows:
            L.append(f"## {tag}: 解析不到 epoch 行")
            continue
        s = curve_summary(rows)
        res[tag] = (rows, s)
        L.append(f"## {tag}  （{s['n']} 个 epoch 采样点，到 epoch {s['last_epoch']}）")
        L.append(f"  最后一点 {s['last_acc']:.2f}%   历史最佳 {s['best_acc']:.2f}% @ epoch {s['best_epoch']}")
        L.append(f"  判定压平点: " + (
            f"epoch {s['flat_epoch']}（{s['flat_acc']:.2f}%）—— 之后所有 epoch 都没有超过它 +0.20 点"
            if s['flat_epoch'] is not None else "无"))
        L.append(f"  最后 20 个 epoch 斜率 {s['tail_slope']:+.4f} 点/epoch"
                 f"  ⇒ 线性外推到 epoch 150 = {s['extrap150']:.2f}%")
        L.append("")
        L.append("  epoch   loss        留出      训练")
        L.append(fmt_curve(rows))
        L.append("")

    if "10k" in res and "100k" in res:
        r10, r100 = res["10k"][0], res["100k"][0]
        m10 = {e: (l, a_, t) for e, l, a_, t in r10}
        m100 = {e: (l, a_, t) for e, l, a_, t in r100}
        common = sorted(set(m10) & set(m100))
        L.append("## 同 epoch 并排（两边的切分/留出局是同一份，见报告 §口径）")
        L.append("  epoch   10k留出   100k留出   差")
        for e in common:
            L.append(f"  {e:5d}   {m10[e][1]:7.2f}%  {m100[e][1]:7.2f}%  {m100[e][1]-m10[e][1]:+6.2f}")
        # 10k 在第 E 轮的读数 vs 100k 在第 E 轮的读数：同 epoch 对照
        L.append("")
        L.append("## 「100k 在第 N 轮 ≈ 10k 在第几轮」（等留出准确率对齐）")
        L.append("  100k epoch   100k 留出   10k 最接近的 epoch（留出）")
        for e in common:
            if e % 10 and e != common[-1]:
                continue
            v = m100[e][1]
            j = min(m10, key=lambda k: abs(m10[k][1] - v))
            L.append(f"  {e:10d}   {v:8.2f}%   {j:3d}（{m10[j][1]:.2f}%）")

    (REPO / a.out).write_text("\n".join(L), encoding="utf-8")
    print("\n".join(L))
    print(f"\n→ {a.out}")


if __name__ == "__main__":
    main()
