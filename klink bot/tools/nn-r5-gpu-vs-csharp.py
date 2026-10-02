"""GPU 版 vs C# 版逐点对账 —— 「GPU 只是换执行器、不是换算法」的证据。

两种日志格式：
  C#  : `  epoch   5  loss 0.676888  验证准确率 59.49%  训练准确率 59.98%`
  GPU : `      5  0.676736   59.48%   59.98%   (0.3s/epoch)`
"""
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
P_C = re.compile(r"^\s*epoch\s+(\d+)\s+loss\s+([\d.]+)\s+验证准确率\s+([\d.]+)%\s+训练准确率\s+([\d.]+)%", re.M)
P_G = re.compile(r"^\s*(\d+)\s+([\d.]+)\s+([\d.]+)%\s+([\d.]+)%\s+\(", re.M)


def parse(p, pat):
    txt = (REPO / p).read_text(encoding="utf-8", errors="replace")
    return {int(m.group(1)): (float(m.group(2)), float(m.group(3)), float(m.group(4)))
            for m in pat.finditer(txt)}


def compare(tag, cs, gpu, out):
    common = sorted(set(cs) & set(gpu))
    if not common:
        out.append(f"## {tag}: 没有可比对的 epoch（C# {len(cs)} 点 / GPU {len(gpu)} 点）")
        return
    dl = [abs(gpu[e][0] - cs[e][0]) for e in common]
    dacc = [abs(gpu[e][1] - cs[e][1]) for e in common]
    dtr = [abs(gpu[e][2] - cs[e][2]) for e in common]
    out.append(f"## {tag}  （可比 {len(common)} 个 epoch；C# 有 {len(cs)} 点，GPU 有 {len(gpu)} 点）")
    out.append(f"  loss        最大绝对差 {max(dl):.3e}   （loss 量级 ~0.52–0.70）")
    out.append(f"  留出准确率  最大绝对差 {max(dacc):.3f} 个点")
    out.append(f"  训练准确率  最大绝对差 {max(dtr):.3f} 个点")
    out.append("")
    out.append("  epoch   C# loss    GPU loss   |  C# 留出   GPU 留出  | C# 训练  GPU 训练")
    show = [e for e in common if e % 10 == 0 or e == common[-1]] + [1, 2, 3]
    for e in sorted(set(show)):
        c, g = cs[e], gpu[e]
        out.append(f"  {e:5d}   {c[0]:.6f}  {g[0]:.6f}   |  {c[1]:7.2f}%  {g[1]:7.2f}%  |"
                   f" {c[2]:7.2f}%  {g[2]:7.2f}%")
    out.append("")


def main():
    out = ["# GPU 版 ↔ C# NNTrain 逐点对账（第五轮）", ""]
    out.append("背景：100k × 150 epoch 在 C# 单线程上要约 6 小时。为把时间压到分钟级，")
    out.append("我用 `nn-r5-gpu-train.py` 在 RTX 5060 上**复算同一算式**（同一份数据、同一个")
    out.append("`Random(42)` 初始化、同一份 mean/std、同一个按局切分、同一个 `lr/used` 全量 GD）。")
    out.append("下面是把 GPU 结果与 C# 实际跑出来的日志逐点比对 —— 这是「GPU 只是换执行器」的证据。")
    out.append("")
    for tag, cs_p, g_p in (
        ("10k（C# 跑到 45 epoch / GPU 跑满 150）",
         "out/_r5-train-win-10k-ep150.txt", "out/_r5-gpu-10k.txt"),
        ("100k（C# 跑到 10 epoch / GPU 跑满 150）",
         "out/_r5-train-win-100k-ep150.txt", "out/_r5-gpu-100k.txt"),
    ):
        try:
            compare(tag, parse(cs_p, P_C), parse(g_p, P_G), out)
        except FileNotFoundError as e:
            out.append(f"## {tag}: 缺文件 {e}")
    txt = "\n".join(out)
    (REPO / "out/_r5-gpu-vs-csharp.txt").write_text(txt, encoding="utf-8")
    print(txt)


if __name__ == "__main__":
    main()
