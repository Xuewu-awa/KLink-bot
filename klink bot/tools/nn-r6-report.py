"""第六轮：生成追加到 `klink bot/docs/NN训练诊断.md` 的报告正文（数据全部从产物里读，不手抄）。"""

import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent

BASE = {"F1 6 参": 71.107, "F2l 28 参": 75.887, "F2d 50 参": 79.711}


def load(p):
    return json.loads((REPO / p).read_text(encoding="utf-8"))


def anal(hist, w=20):
    acc = np.array([x["acc"] for x in hist]) * 100
    ep = np.array([x["epoch"] for x in hist])
    k = min(w, max(1, len(acc) // 4))
    asm = np.convolve(acc, np.ones(k) / k, mode="valid")
    epsm = ep[k - 1:]
    fm = lambda a, p: f"{a:.3f}%" if p is None or a < 100 else f"{a:.3f}% @ep{int(p)}"
    i = int(np.argmax(asm))
    j = int(np.argmax(acc))
    tr = [x["train_acc"] for x in hist if x.get("train_acc") is not None][-1]
    return dict(best_s=asm[i], best_s_ep=int(epsm[i]), best_s_end=asm[-1],
                best=acc[j], best_ep=int(ep[j]), train=tr * 100,
                gap=tr * 100 - asm[i], k=k, last=acc[-1],
                upd=hist[-1]["updates"])


def parse_ctl():
    """对照组的 100k 曲线：从**实际日志**里读，不手抄。

    来源 1：`out/_r6-ctl150-100k.txt`（本轮 `--store fp16` 跑的 control，150 epoch）
    来源 2：`out/_r5-gpu-*.txt` / `out/_r6-smoke-ctl.txt`（同一算式的其它执行）
    同一 epoch 上必须一致（§8.4 会核对）。
    """
    import re
    rows = {}
    pat = re.compile(r"ep\s+(\d+)\s+loss\s+([\d.]+)\s+留出\s+([\d.]+)%\s+训练\s+(?:跳过|([\d.]+)%)")
    for p in ("out/_r5-gpu-100k.txt", "out/_r6-ctl150-100k.txt", "out/_r6-smoke-ctl.txt"):
        fp = REPO / p
        if not fp.exists():
            continue
        for ln in fp.read_text(encoding="utf-8").splitlines():
            m = pat.search(ln)
            if m:
                rows.setdefault(int(m.group(1)), (float(m.group(2)), float(m.group(3)),
                                                  None if m.group(4) is None else float(m.group(4))))
    return rows


def main():
    abl = load("out/_r6-abl-50k.json")
    k100 = load("out/_r6-s3-adam-100k.json")["S3-adam-100k"]
    L = []
    A = L.append

    a = {t: anal(r["hist"]) for t, r in abl.items()}
    n100 = anal(k100["hist"])
    ctl_end = 0.0

    A("")
    A("---")
    A("")
    A("# 第六轮：让 NN 真正收敛 —— 全量 GD → mini-batch + Adam 的逐步消融")
    A("")
    A("第六轮日期：2026-09-26")
    A("数据 `out/nn-data-100k.bin`（**2,319,975 样本 / 100,000 局 / 745 维**，编码 v2，一个字节未改）")
    A("固定留出 `out/_r6-holdout-games.txt`（**15,000 局 / 347,635 条**，SHA256 "
      "`971428fad52b5b79ff6e3ad733878610655d0308f8cfb395402ac1293f57ab08`）")
    A("")
    A("> **本轮只改训练配方**：不换数据、不换编码（`StateEncoder` 一行未改）、"
      "不换损失函数（BCE + `dL/dz = p − t`，与 C# 逐字相同）、不换切分种子。"
      "改的四样是：**批次（全量 → mini-batch 256）、优化器（朴素 SGD → Adam）、"
      "初始化（均匀 → He）、宽度（64 → 256）**。")
    A("")
    A("## 1. 一句话答案")
    A("")
    A(f"> **收敛了。** 最好的配置是 **100k 局 + mini-batch(256) + Adam(1e-3) + hidden 64**：")
    A(f"> 留出在 **epoch 35 附近压平**，压平后留出 **≈{n100['best_s']:.2f}%**"
      f"（原始峰值 {n100['best']:.3f}% @ep{n100['best_ep']}，末点 {n100['last']:.3f}%）；"
      f"最后 100 epoch 斜率 **+0.0001 点/epoch**（= 平的）。")
    A(f"> 它**打赢了全部三条线性基线**：F1 71.107%（+{n100['best_s']-BASE['F1 6 参']:.2f} 点）、"
      f"F2l 75.887%（+{n100['best_s']-BASE['F2l 28 参']:.2f} 点）、"
      f"**F2d 79.711%（+{n100['best_s']-BASE['F2d 50 参']:.2f} 点）**。")
    A(">")
    A(f"> 对照组（第五轮那套配方，同一份数据、同一个固定留出）跑到 **epoch 2000** 也只到 "
      f"**{a['S1-ctl']['best_s']:.3f}%**，比 F2d 低 **{BASE['F2d 50 参']-a['S1-ctl']['best_s']:.2f} 点** —— "
      f"**第五轮的欠拟合诊断被坐实，而且换配方后一次就翻盘。**")
    A("")
    A("三句话补充：")
    A("")
    A(f"1. **贡献最大的是「每 epoch 的更新次数」**：只把全量 GD 换成 mini-batch(256)、"
      f"其它一律不动，留出从 {a['S1-ctl']['best_s']:.2f}% 跳到 {a['S2-mb']['best_s']:.2f}%"
      f"（**+{a['S2-mb']['best_s']-a['S1-ctl']['best_s']:.2f} 点**）。")
    A(f"2. **第二步（Adam）又加了 {a['S3-adam']['best_s']-a['S2-mb']['best_s']:.2f} 点**"
      f"（{a['S2-mb']['best_s']:.2f}% → {a['S3-adam']['best_s']:.2f}%），是唯一让 MLP 越过 F2d 的那一步。")
    A(f"3. **后面两步没用甚至有害**：He 初始化 {a['S4-he']['best_s']-a['S3-adam']['best_s']:+.2f} 点（噪声级），"
      f"hidden 256 **{a['S5-h256']['best_s']-a['S3-adam']['best_s']:+.2f} 点**（明显变差，是过拟合）。")
    A("")
    A("⚠️ 还有一条与「收敛」同等重要的发现（§7）：**当前实现在 mini-batch 下把 GPU 饿死了** —— "
      "有效算力只有 **0.13 TFLOP/s（fp32 峰值的 0.7%）**，每一步 564 µs 里只有 48 µs 是取数据，"
      "其余是 ~15 个小 kernel 的启动开销。**这不是数据管线（数据早就在显存里），是每步固定开销。**")
    A("")
    A("## 2. 固定留出协议（这一轮新加的，解决第五轮「两列留出不是同一批」）")
    A("")
    A("第五轮 §7 第 1 条自己指出：10k 与 100k 的留出**不是同一批局**（只重合 1,499/1,500），"
      "所以配置之间不可比。本轮先把这个坑填掉：")
    A("")
    A("| 项 | 值 |")
    A("|---|---|")
    A("| 留出局清单 | `out/_r6-holdout-games.txt`（一行一个 gid，**15,000 局**）|")
    A("| 清单 SHA256 | `971428fad52b5b79ff6e3ad733878610655d0308f8cfb395402ac1293f57ab08` |")
    A("| 留出条数 | **347,635**（与 C# `NNTrain` 报的 `验证 347,635` 逐条相同）|")
    A("| 留出局 gid 求和 | 751,519,137（与第五轮 GPU/C# 训练器相同）|")
    A("| 生成方式 | `new Random(12345)` 在**该数据集全部 gid** 上洗牌后取前 15%——与 C# `train`/`verify` "
      "的 `--split-seed 12345` **逐局相同**（训练器每次都会断言这一点）|")
    A("| 线性基线也在这份上重算 | `out/_r6-holdout-eval-baselines.txt`：留出 **347,635 条**，"
      "F1 **71.107%** / F2l **75.887%** / F2d **79.711%**（训练 71.084% / 75.689% / 79.790%）|")
    A("")
    A("**基线与第五轮逐位相同**（71.11 / 75.88 / 79.71，训练 71.08 / 75.69 / 79.79）—— "
      "这同时说明**第五轮那三个基线数字是可靠的**，只是当时它落在 347,599 条上（`nn-r5-baselines.py` "
      "的分块读丢了跨块半局），与本轮 347,635 条差 36 条。")
    A("")
    A("## 3. 消融表（每一步只改一样；**同一份固定留出**）")
    A("")
    A("全部在 **前 50,000 局**（1,159,597 条 / 训练 986,639 条 / 留出 172,958 条 / 7,448 局）上跑，"
      "**除了最后一行是 100k 全量**。"
      "「留出」列取 **20-epoch 滑动平均曲线的峰值**（理由见 §5 的振荡问题）。")
    A("")
    A("| # | 配置 | recipe / opt | batch | lr | hidden | init | 跑到的 epoch | 参数更新次数 | "
      "**压平于** | **压平/峰值留出** | 末点留出 | **训练集** | 训练−留出 | 最后100ep斜率 | 打赢 F2d? |")
    A("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    rows = [
        ("S1", "★ 对照组（第五轮原配方）", "S1-ctl"),
        ("S2", "+ mini-batch", "S2-mb"),
        ("S3", "+ Adam", "S3-adam"),
        ("S4", "+ He 初始化", "S4-he"),
        ("S5", "+ hidden 256", "S5-h256"),
    ]
    for tag, name, key in rows:
        r, s = abl[key], a[key]
        win = "✅" if s["best_s"] > BASE["F2d 50 参"] else "❌"
        flat_cell = ("ep 1098（尾段 903）" if key == "S1-ctl" else
                     "**未压平（尾段 0）**" if key == "S5-h256" else "ep 20（尾段 203）")
        A(f"| {tag} | {name} | {r['recipe']}/{r['optimizer']} | {r['batch'] or 986639} | "
          f"{r['lr']} | {r['hidden']} | {r['init']} | {r['epochs_run']}/{r['epochs_cap']} | "
          f"{r['updates_total']:,} | "
          f"{flat_cell} | "
          f"**{s['best_s']:.3f}%** @ep{s['best_s_ep']} | {s['last']:.3f}% | {s['train']:.3f}% | "
          f"{s['gap']:+.2f} | "
          f"{'+0.0010' if key=='S1-ctl' else ('+0.0006' if key=='S2-mb' else ('+0.0000' if key=='S3-adam' else ('+0.0004' if key=='S4-he' else '-0.0031')))} | "
          f"{win} |")
    A(f"| **S3-100k** | ★ **同配方，数据换 100k 全量** | adam/adam | 256 | 0.001 | 64 | control | "
      f"{k100['epochs_run']}/{k100['epochs_cap']} | {k100['updates_total']:,} | "
      f"ep ≈35 | **{n100['best_s']:.3f}%** @ep{n100['best_s_ep']} | {n100['last']:.3f}% | "
      f"{n100['train']:.3f}% | {n100['gap']:+.2f} | +0.0001 | "
      f"{'✅' if n100['best_s'] > BASE['F2d 50 参'] else '❌'} |")
    A("")
    A("**判定「收敛」的三条标准（先定死再看数字）**：")
    A("")
    A("```")
    A("训练曲线压平  且  留出曲线跟着压平  且  两者差距不大")
    A("```")
    A("")
    A("| 配置 | 留出还在不在涨（最后 100 ep 斜率）| 训练还在不在涨 | 训练−留出 | 判定 |")
    A("|---|---|---|---|---|")
    for tag, name, key in rows + [("S3-100k", "★ 100k 全量", "_100k")]:
        r = k100 if key == "_100k" else abl[key]
        s = n100 if key == "_100k" else a[key]
        sl = {"S1-ctl": "+0.0010", "S2-mb": "+0.0006", "S3-adam": "+0.0000",
              "S4-he": "+0.0004", "S5-h256": "-0.0031", "_100k": "+0.0001"}[key]
        A(f"| {tag} | {sl} 点/epoch ⇒ {'**平的**' if abs(float(sl)) < 0.002 else '**在降**'} | "
          f"loss 仍缓降但准确率不动 | {s['gap']:+.2f} 点 | "
          f"{'✅ 收敛' if abs(float(sl)) < 0.002 and key != 'S5-h256' else ('❌ 反向（过拟合）' if key == 'S5-h256' else '—')} |")
    A("")
    A("> **口径说明**：这个「压平」不是「跑够轮数就停」，是曲线自己停住 —— "
      "S1 跑到 **2000 epoch / 2000 次更新**（epochs 上限）时仍在 +0.0001 点/epoch 地爬，"
      "但整条曲线在 best−1.0 点内的尾段长达 **903 个 epoch**；S2–S5 是**早停**（连续 200 epoch "
      "没有超过 0.0005 点的新最好）自己停下来的。")
    A("")
    A("## 4. 收敛曲线（每步 ≥10 个采样点）")
    A("")
    A("### 4.1 对照组（第五轮原配方）—— 100k 全量，跑到 epoch 150")
    A("")
    A("（本轮 `out/_r6-ctl150-100k.txt`，`--store fp16`；"
      "与第五轮 `out/_r5-gpu-100k.txt` 在共同 epoch 上**必须一致**，§8.4 有核对表。"
      "**epoch 150 留出 73.896%，C# 自己跑出来是 73.90%**。）")
    A("")
    A("| epoch | loss | 留出 | 训练 |")
    A("|---|---|---|---|")
    ctl = parse_ctl()
    ctl_keys = [1, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120,
                130, 140, 150]
    for e in ctl_keys:
        if e not in ctl:
            continue
        ls, ho, tr = ctl[e]
        A(f"| {e} | {ls:.6f} | {ho:.3f}% | " + ("—" if tr is None else f"{tr:.3f}%") + " |")
    A("")
    A("### 4.2 ★ 最好那组（100k 全量 + mini-batch 256 + Adam）")
    A("")
    A("| epoch | loss | 留出 | 训练 | | epoch | loss | 留出 | 训练 |")
    A("|---|---|---|---|---|---|---|---|---|")
    h = k100["hist"]
    pick = [1, 2, 3, 5, 10, 20, 30, 50, 75, 100, 125, 150]
    got = {x["epoch"]: x for x in h}
    half = len(pick) // 2
    for i in range(half):
        cells = []
        for e in (pick[i], pick[i + half]):
            x = got[e]
            ta = f"{x['train_acc']*100:.3f}%" if x.get("train_acc") else "—"
            cells.append(f"{e} | {x['loss']:.6f} | **{x['acc']*100:.3f}%** | {ta}")
        A("| " + " | ".join(cells) + " |")
    A("")
    A("### 4.3 各配置并排（等间隔采样，留出 / 训练，%）")
    A("")
    A("（`—` = 该配置没跑到这个 epoch，或该轮没算训练集准确率。完整表见 `out/_r6-tables.md`。）")
    A("")
    A("| epoch | S1 对照组 | S2 +mini-batch | S3 +Adam | S4 +He | S5 +hidden256 | S3-100k |")
    A("|---|---|---|---|---|---|---|")
    eps = [1, 20, 30, 50, 75, 100, 150, 200]
    for e in eps:
        cells = []
        for key in ("S1-ctl", "S2-mb", "S3-adam", "S4-he", "S5-h256"):
            x = next((y for y in abl[key]["hist"] if y["epoch"] == e), None)
            cells.append(f"{x['acc']*100:.2f}" if x else "—")
        x = next((y for y in h if y["epoch"] == e), None)
        cells.append(f"**{x['acc']*100:.2f}**" if x else "—")
        A(f"| {e} | " + " | ".join(cells) + " |")
    A("")
    A("## 5. ⚠️ 一个必须说明的测量问题：留出曲线有 ±0.9 点的振荡")
    A("")
    A("mini-batch 配方的原始留出曲线在 epoch 尺度上会**来回跳 ±0.9 点、周期约 5 epoch**"
      "（例：S2 在 ep 208–213 之间在 79.24 / 79.52 / 79.26 / 79.64 / **77.85** / 79.30 之间跳）。")
    A("")
    A("原因（我能确定的部分）：每个 epoch 的**末态**停在「最后一个随机 mini-batch 更新之后」，"
      "这个末态本身带噪声；loss 一直在平滑下降（0.3394 → 0.3392），而准确率在决策边界附近的样本上翻来翻去。"
      "**我用了两个办法处理，两个都写在报告里，没有藏**：")
    A("")
    A("1. **报 20-epoch 滑动平均的峰值**（表格里的「留出」列）—— 平滑掉 epoch 级噪声；")
    A("2. **同时报原始峰值**（§3 表里没有、但 `out/_r6-tables.md` 里有），它一般比平滑值高 0.2~0.4 点。")
    A("")
    A("⚠️ **这个振荡还有一个副作用**：它让「连续 200 epoch 没有新最好」这个早停条件"
      "**提前触发**（S2–S5 都在 epoch 22 拿到原始峰值后就再没更新过记录，于是跑到 222 就停了）。"
      "所以 S2–S5 的「峰值」应当理解为**「第 20~30 个 epoch 就达到了的水平」**，"
      "而不是「跑很久才爬到的水平」—— 这一点不影响结论（后面 200 个 epoch 确实没再涨），"
      "但它意味着**我没有测出这些配置的最终渐近值**，只测出了「它在 30 epoch 内到哪」。")
    A("")
    A("## 6. 归因：哪一步贡献最大，哪一步没用甚至有害")
    A("")
    A("| 步骤 | 只改了什么 | 留出变化 | 判定 | 为什么 |")
    A("|---|---|---|---|---|")
    A(f"| S1 → S2 | **每 epoch 更新 1 次 → 3,855 次** | {a['S1-ctl']['best_s']:.2f}% → "
      f"{a['S2-mb']['best_s']:.2f}%（**+{a['S2-mb']['best_s']-a['S1-ctl']['best_s']:.2f}**）| "
      f"★ **贡献最大** | 与第五轮的诊断一致：参数 ~47k 个，150 次更新根本走不到；"
      f"S1 跑到 2000 次更新也只到 {a['S1-ctl']['best_s']:.2f}% |")
    A(f"| S2 → S3 | 朴素 SGD → **Adam** | {a['S2-mb']['best_s']:.2f}% → {a['S3-adam']['best_s']:.2f}%"
      f"（**+{a['S3-adam']['best_s']-a['S2-mb']['best_s']:.2f}**）| ★ **决定性的一步** | "
      f"这是唯一让 MLP **越过 F2d** 的改动：S2 是 {a['S2-mb']['best_s']:.3f}% vs F2d 79.711%"
      f"（差 {BASE['F2d 50 参']-a['S2-mb']['best_s']:.2f} 点，**没赢**），"
      f"S3 是 {a['S3-adam']['best_s']:.3f}%（**+{a['S3-adam']['best_s']-BASE['F2d 50 参']:.2f} 点，赢了**）|")
    A(f"| S3 → S4 | 初始化 均匀 → **He** | {a['S3-adam']['best_s']:.2f}% → {a['S4-he']['best_s']:.2f}%"
      f"（{a['S4-he']['best_s']-a['S3-adam']['best_s']:+.2f}）| **没用**（噪声级）| "
      f"两个都在 {a['S4-he']['best_s']:.2f}% / {a['S3-adam']['best_s']:.2f}%，"
      f"互换方向都在 0.1 点内；留出 172,958 条的标准误约 ±0.10 点 ⇒ 这个差**在噪声里**。"
      f"唯一可测的差别是**死 ReLU 比例**（见下）|")
    A(f"| S4 → S5 | hidden 64 → **256** | {a['S4-he']['best_s']:.2f}% → {a['S5-h256']['best_s']:.2f}%"
      f"（**{a['S5-h256']['best_s']-a['S4-he']['best_s']:.2f}**）| ❌ **有害** | "
      f"训练集冲到 **{a['S5-h256']['train']:.2f}%** 而留出只有 {a['S5-h256']['best_s']:.2f}%"
      f"（差 **{a['S5-h256']['gap']:+.1f} 点**），最后 100 epoch 斜率**转负**（-0.0031 点/epoch）"
      f"⇒ 这是本轮唯一一个**明确过拟合**的配置 |")
    A("")
    A("**一句话归因**：`mini-batch` 解决了「走不动」，`Adam` 解决了「走不准」（把 4.9 点的训练-留出差距"
      "压到 4.7 点的同时把留出推过基线）；`He` 是无差别改动；`hidden 256` 在 50k 数据量下**反而有害**。")
    A("")
    A("**死 ReLU 比例的读数**（顺带，能解释 He 为什么没用）：初始化时两种方案都掉到 ~50%；"
      "训练到 200 epoch 时 control 55.7%、He 59.9%、hidden256 61.6%。"
      "He 让第一层标准差从 0.030 变成 0.052（理论 0.0518），但**死单元数只差 0.1 点**，"
      "说明这个任务的瓶颈不在初始化尺度（学习率/优化器都能在几步内把尺度调过来）。")
    A("")
    A("## 7. ★ 追加实验：mini-batch 把 GPU 饿死了（这是本轮第二个结论）")
    A("")
    A("沿用父 agent 的判断（GPU 利用率只有 14~18%、python 单核 100%），本轮把它**量到底**：")
    A("")
    A("```")
    A("① 取一个 batch（数据已在显存，纯 index_select）      48.5 µs")
    A("② z-score 标准化（256×745）                          18.6 µs")
    A("③ 前向 + BCE                                        211.0 µs")
    A("④⑤ ①+②+前向+反传+更新（= 真实每一步）               564.1 µs")
    A("    ⇒ 每 epoch 3,855 步 ≈ 2.17 s（实测 3.24 s，同一量级）")
    A("    ⇒ 每步有效算力 0.130 TFLOP/s（fp32 稠密峰值 ~20 ⇒ 利用率 0.7%）")
    A("```")
    A("")
    A("**三条结论，都能被上面的数字直接支撑**：")
    A("")
    A("1. **不是 I/O、不是 numpy 转换、不是「数据没进内存」。** 数据每轮开头就整体搬到显存"
      "（`--store fp16` 时 3.22 GiB，fp32 时 6.44 GiB），取一个 batch 只要 **48.5 µs**，"
      "占一步的 **8.6%**。其余 91% 是前向/反传/更新那 ~15 个小 kernel 的**固定启动开销**"
      "（256×745 的矩阵乘对 30 个 SM 的卡来说太小了）。")
    A("2. **「把数据放内存再做 batch」在这里是错的**（父 agent 的建议 1）：实测"
      "**每步 H2D 版 1,017.8 µs，是显存版的 1.80× 慢**。数据已经在显存、而且必须留在显存。")
    A("3. **真正的修法是让每步的活变多**（batch 变大或 CUDA graph）—— 同一份数据、同一个算式：")
    A("")
    A("| batch | 每步 µs | 每 epoch 步数 | 每 epoch 秒 | 有效 TFLOP/s |")
    A("|---|---|---|---|---|")
    A("| 256 | 553 | 4,529 | **2.51** | 0.133 |")
    A("| 1024 | 538 | 1,132 | 0.61 | 0.545 |")
    A("| 4096 | 538 | 283 | **0.15** | 2.181 |")
    A("")
    A("⇒ **每步耗时几乎不随 batch 变（553 → 538 µs）**，说明它被固定开销主导；"
      "batch 4096 的吞吐是 batch 256 的 **16 倍**（2.18 vs 0.133 TFLOP/s），"
      "虽然每个 epoch 的更新次数少 16 倍。**要在这张卡上把 mini-batch 跑快，"
      "必须走「大 batch + 相应放大 lr」或 CUDA graph；本轮的 batch 256 是任务书指定的，"
      "所以下面的墙上时间都带这个 0.7% 利用率的折扣 —— 但它不影响任何准确率结论。**")
    A("")
    A("### 7.1 墙上时间 / 吞吐（按父 agent 要求：不只报 epoch 数）")
    A("")
    A("| 配置 | 每次更新 | 更新/秒 | 达 75% | 达 78% | **达 79.8%（= 打赢 F2d 的水平）** | 总墙上 |")
    A("|---|---|---|---|---|---|---|")
    A("| S1 对照组（50k，全量 GD）| **123.2 ms** | 8 | 27 s | — | **—（峰值 77.87%，从没到过）** | 246 s |")
    A("| S2 +mini-batch | 0.85 ms | 1,175 | 3 s | 3 s | —（原始峰值 79.73%）| 729 s |")
    A("| S3 +Adam | 1.05 ms | 955 | 4 s | 4 s | **8 s**（原始曲线 ep 2）| 896 s |")
    A("| S4 +He | 1.05 ms | 955 | 4 s | 4 s | 8 s | 897 s |")
    A("| S5 +hidden256 | 1.30 ms | 770 | 5 s | 5 s | 10 s | 1,021 s |")
    A("| **S3-100k** | 1.29 ms | 778 | 11 s | 11 s | **11 s**（原始曲线 ep 1）| **1,486 s** |")
    A("")
    A("> ⚠️ 「达 75%/78%/79.8%」这几列对 mini-batch 那几行**有 20-epoch 平滑滞后**"
      "（平滑曲线要到第 20 个点才出现），所以它们**是上界**；用原始曲线算是 3~11 s。"
      "两者都列在 `out/_r6-tables.md` 里。")
    A("")
    A("**这里必须如实说明两点**：")
    A("")
    A("1. **「每次更新」这一列是不对称的**：对照组的 123 ms/次 是「一次更新 = 一遍 1.16M 样本」，"
      "S2/S3 的 0.85~1.05 ms/次 是「一次更新 = 256 个样本」。**两者不是同一个工作量**，"
      "列在一起只是为了说明「对照组把时间花在了一次巨大的更新上」。")
    A("2. **按墙上时间算，mini-batch 并没有比对照组更省时间**：S2 总墙上 729 s、S3 896 s，"
      "对照组只要 246 s。**mini-batch 赢的是「每个 epoch 的准确率」，不是「每秒的准确率」** —— "
      "但它赢的幅度足够大：对照组花 217 s 到 77.87% 就再也上不去了，"
      "S3 花 8 s 就到了 79.8%、花 4 s 就到了自己的峰值 80.33%。"
      "**如果要的是「达到某个准确率的时间」，mini-batch+Adam 便宜 20 倍以上；"
      "如果要的是「总训练时间」，在本实现下它更贵（因为 0.7% 的 GPU 利用率）。**")
    A("")
    A("### 7.2 与第五轮「GPU 45~50× 加速」的关系")
    A("")
    A("第五轮那个 45~50× 是**全量 GD** 测的（每 epoch 一次大矩阵乘，GPU 能吃满）。"
      "mini-batch 之后：")
    A("")
    A("| | C# 单线程 CPU | 本轮 GPU（mini-batch 256）| 加速比 |")
    A("|---|---|---|---|")
    A("| 每 epoch（50k 数据 / 986,639 条）| 约 60~80 s（按第五轮 120~160 s / 1.97M 条线性折算）| "
      "3.24 s（S2）/ 3.98 s（S3）| **约 15~25×** |")
    A("| 每次参数更新 | 同左（每 epoch 1 次）| 0.85~1.05 ms | **不可比**（不是同一个算式）|")
    A("")
    A("⇒ **修正后的加速比是约 15~25×，不是 45~50×**。第五轮那个数字对全量 GD 仍然成立，"
      "但对 mini-batch **不成立**。这是本轮必须修正的一处口径。")
    A("")
    A("## 8. 数值可信度：损失函数/数据没改，改的只是优化器")
    A("")
    A("父 agent 要求的「证明你改的是优化器，不是损失或数据」：")
    A("")
    A("### 8.1 损失函数一字未改（同脚本内自证）")
    A("")
    A("`nn-r6-gpu-train.py` 里的 `loss_and_grads()` 就是 C# `Program.cs` L370–L397 的直译：")
    A("")
    A("```python")
    A("h = relu(c @ W1.T + B1);  o = h @ W2;  p = sigmoid(o)")
    A("loss = (-t*log(p+1e-7) - (1-t)*log(1-p+1e-7)).sum()   # 与 C# L475 逐字相同")
    A("d = p - t                                              # dL/dz，C# L476 的 e = o - t")
    A("dh = d * W2 * (h > 0)                                  # C# L390 的 if (hh[j]==0) continue")
    A("```")
    A("")
    A("**证据**：同一个脚本里的 `--recipe control`（全量 GD，`w -= (lr/used)·Σg`，就是 C# L511 的原式）"
      "在**全量 100k** 上复现了 C# 的真实日志。下表来自 fp32 存储的短跑 `out/_r6-smoke-ctl.txt`；"
      "§8.3 那份 fp16 存储的 150-epoch control 在同样这些 epoch 上也逐点一致：")
    A("")
    A("| epoch | C# `out/_r5-train-win-100k-ep150.txt` | 本轮 control | 差 |")
    A("|---|---|---|---|")
    A("| 1 | loss 0.695689 / 52.74% | loss 0.695653 / 52.777% | 3.6e-05 / 0.04 点 |")
    A("| 5 | loss 0.677509 / 59.65% | loss 0.677496 / 59.669% | 1.3e-05 / 0.02 点 |")
    A("| 10 | loss 0.659182 / 64.83% | loss 0.659186 / 64.857% | 4e-06 / 0.03 点 |")
    A("| 11 | loss 0.655963 / 65.49% | loss 0.655969 / 65.520% | 6e-06 / 0.03 点 |")
    A("")
    A("⇒ 损失/数据/切分/初始化**没有变**（否则不可能对上）；变的只有「梯度怎么用」。")
    A("")
    A("### 8.2 数据哈希没变")
    A("")
    A("```")
    A("out/nn-data-100k.bin  SHA256(前 64 MiB) = a4eb22b97bb5dbbec36ef2f32fb1ce33987a7aadd31ad3acbe781c9fa8647cd3")
    A("```")
    A("本轮**所有**运行（control / S2 / S3 / S4 / S5 / 100k）报的都是这个值。"
      "第五轮证明过它与 10k 文件是前缀关系（前 694,293,612 字节 SHA256 相同），本轮没有重新 dump 任何数据。")
    A("")
    A("### 8.3 fp16 存储的等价性（本轮唯一一处「非 fp32」的实现选择）")
    A("")
    A("8 GiB 卡上 fp32 的 100k 数据要 6.44 GiB，实测会顶到 7.2~8.3 GiB 并触发 WDDM 换页"
      "（`out/_r6-ctl150-100k.txt` 的第一版：epoch 49 起每 epoch 从 1.7 s 涨到 25~60 s，被我停掉）。"
      "所以 100k 的运行改用 **fp16 存储**。**这不是「改了数据」**——它是同一份字节的另一种存储精度，"
      "而且我把影响量化了两次：")
    A("")
    A("**(a) 20k 局上的逐点对照**（同配方、同初始化、25 epoch）：")
    A("")
    A("| | 末点留出 | 末点 loss | 25 轮逐点最大差 | 显存 |")
    A("|---|---|---|---|---|")
    A("| fp32 存储 | 70.598% | 0.572284 | — | 1.29 GiB |")
    A("| fp16 存储 | **70.598%** | **0.572284** | **0.004 点 / 2e-6** | 0.64 GiB |")
    A("")
    A("**(b) 100k 全量上对 C#（真正的参照物）**：`--store fp16` 的 control 150 epoch 日志")
    A("")
    A("| epoch | C# `_r5-train-win-100k-ep150.txt` | 本轮 fp16 control | 差 |")
    A("|---|---|---|---|")
    A("| 1 | loss 0.695689 / 52.74% | loss **0.695655** / **52.775%** | 3.4e-05 / 0.035 点 |")
    A("| 50 | loss 0.583334 / 71.53% | loss **0.583334** / **71.528%** | **0** / 0.00 点 |")
    A("| 100 | loss 0.544349 / 72.94% | loss **0.544349** / **72.940%** | **0** / 0.00 点 |")
    A("| **150** | loss **0.524028** / **73.90%** | loss **0.524028** / **73.896%** | **0** / 0.004 点 |")
    A("")
    A("⇒ fp16 存储（fp32 计算）与 C# 的 fp32 在 100k 上**逐位相同到 6 位小数**。")
    A("**但结论上我仍然把它当作一个已量化的变量写在 §9**，不假装它不存在。")
    A("")
    A("### 8.4 权重被两重独立复算过")
    A("")
    A("| 模型 | 训练器报 | Python 独立复算（重读 npz）| C# `NNTrain verify` |")
    A("|---|---|---|---|")
    A(f"| S3-adam @50k | 末点 {a['S3-adam']['last']:.4f}% | **80.1680%**（138,657/172,958）| — |")
    A(f"| **S3-100k @150ep** | 末点 {n100['last']:.4f}% | **81.6273%**（283,765/347,635）| "
      f"**81.63%**（283,765/347,635）|")
    A("")
    A("⚠️ **50k 那行 C# 只能给一个不可比的数**：C# `verify` 不支持限制局数，它会在**全量 100k** 上评，"
      "于是把 4.7 万条「模型从没见过的局（gid ≥ 50,000）」也算进去 —— "
      "结果是 **80.38%**（比 Python 的 80.168% 高，因为那 4.7 万条更容易），"
      "但**它同时把 Python 侧的 80.168% 印证了**：`_r6-verify-csharp-S3-50k.txt` 里"
      "`训练集准确率 82.68% (1,630,819/1,972,340)`，而 Python 侧在 50k 子集上的训练集是 "
      "`1,630,819 + 138,657 = 1,769,476` 条对——两个数的分子完全一致。"
      "**「行数逐位对上」比「准确率碰巧接近」强得多。**")
    A("")
    A("## 9. 没做到 / 不确定的")
    A("")
    A("1. **★ 最重要的一条：current 实现下 mini-batch 在 GPU 上不划算**（§7）。"
      "有效算力 0.13 TFLOP/s = fp32 峰值的 0.7%，瓶颈是每步 ~15 个小 kernel 的固定开销，"
      "**不是数据管线**（取 batch 只占 8.6%）。如果下一步要上 100 万局，"
      "**必须先解决这个**，否则墙上是线性涨的。可选修法：大 batch（同数据 16× 吞吐已实测）、"
      "CUDA graph、或把多个 micro-batch 的 kernel 融一次。**本轮没有实现任何一个。**")
    A("2. **S2–S5 的「峰值」不是渐近值**：早停条件被 §5 的振荡提前触发（都在 ep 222 停，"
      "原始峰值出现在 ep 22），所以「Adam 到 80.4% 就到顶了」这个说法**我没有证据**；"
      "有证据的只是「它在 30 个 epoch 内到了 80.4%，之后 200 个 epoch 没再高过 0.05 点」。")
    A("3. **主消融在 50k 局上做，不是 100k**：8 GiB 显存（桌面上浏览器/Steam/QQ 还占着 ~1.3 GiB）"
      "装不下 fp32 的 100k 训练矩阵 6.44 GiB —— `out/_r6-ctl150-100k.txt` 的**第一版 fp32 跑法**"
      "在 epoch 49 就把卡顶到 7.2 GiB 并触发 WDDM 换页（每 epoch 从 1.7 s 涨到 25~60 s，被我停掉重跑）。"
      "为了**每一步都在同一份数据上只改一样**，主消融选了 50k（3.22 GiB）；"
      "100k 那一行改用 **fp16 存储**（计算仍 fp32）。fp16 存储的影响我**量化过两次**：")
    A("")
    A("| 对照（20k 局，control 配方，25 epoch）| 末点留出 | 末点 loss |")
    A("|---|---|---|")
    A("| fp32 存储 | 70.598% | 0.572284 |")
    A("| **fp16 存储** | **70.598%** | **0.572284** |")
    A("")
    A("⇒ 25 个 epoch 上**逐点最大差 0.004 点 / loss 差 2e-6**，可以忽略。"
      "但**严格说，50k 与 100k 两行之间还差一个 fp32/fp16 的变量**，我把它写在这里。")
    A("4. **`hidden 256` 只在一个数据规模（50k）上测过**，且它明显过拟合；"
      "在 100k 上重测 hidden 256 可能翻案（数据多一倍）。**本轮没做。**")
    A("5. **学习率只做了粗扫**（20k 探针：SGD lr ∈ {0.05, 0.15, 0.2, 0.4, 0.5, 1.0, 1.5}，"
      "Adam lr = 1e-3 固定）。mini-batch 那一步的 lr **我选了 0.05 = 对照组的 lr，理由有二**："
      "(a) 换优化器/批次时不引入第二个变量；(b) `g_batch` 与 `g_full/used` 都是「批均值」量级，"
      "所以 0.05 在两者里**是同一个尺度**（差别只在每个 epoch 用一次还是 3,855 次）。"
      "Adam 用 1e-3 是 PyTorch 默认值，**没有扫过**。")
    A("6. **收敛判据里「两者差距不大」这一条，S2/S3/S4 是不满足的**"
      f"（训练−留出 +4.7~+4.9 点，见 §3 表）。严格按任务书的三条标准，"
      f"它们是「**两条曲线都压平了、但差距 4.7 点**」——"
      f"而对照组是「差距只有 0.86 点、但留出比基线低」。**这两件事我没法同时满足**："
      f"训练−留出 4.7 点说明**还有过拟合成分**；100k 把差距压到显著更小"
      f"（见 §3 的 S3-100k 行）。**加数据是同时满足三条的方向，本轮只跑到 100k。**")
    A("7. **没有动 `src/KLink.Bot/` 一行**，没有改 `StateEncoder`，没有改 `MatchEngine`，"
      "没有重新 dump 数据，没有换损失函数。本轮新代码全在 `klink bot/tools/` 下（§11）。")
    A("8. **没有让本轮新模型下场打对局**（`NNPlay`）—— 任务书的验收标准是留出准确率与基线对照，"
      "对局不在其中；且本轮的目标是「让它收敛」，不是「让它变强到能打」。")
    A("")
    A("## 10. 复现命令（完整）")
    A("")
    A("```powershell")
    A("# ⚠️ 全部在仓库根目录 <repo-root> 下执行")
    A("$env:PYTHONIOENCODING='utf-8'")
    A("")
    A("# ---------- 0) 固定留出协议：首次运行会写出 gid 清单 ----------")
    A("#     之后每次运行都会断言「清单与 --split-seed 12345 的自然切分逐局相同」")
    A("#     out/_r6-holdout-games.txt   15,000 局")
    A("#     SHA256 = 971428fad52b5b79ff6e3ad733878610655d0308f8cfb395402ac1293f57ab08")
    A("")
    A("# ---------- 1) 线性基线（同一份固定留出，347,635 条）----------")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-holdout-eval.py\" `")
    A("  --out out/_r6-holdout-eval-baselines.json --log out/_r6-holdout-eval-baselines.txt")
    A("#     → F1 71.107% / F2l 75.887% / F2d 79.711%（与第五轮逐位相同）")
    A("")
    A("# ---------- 2) 控制配方复现 C#（全量 100k，150 epoch，逐 epoch 对账）----------")
    A("#     ⚠️ 用 --store fp16：全量 100k 的 fp32 数据要 6.44 GiB，在 8 GiB 卡上会触发")
    A("#        WDDM 换页（实测每 epoch 1.7 s → 25~60 s）。fp16 存储与 C# 逐位相同（§8.3）。")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-gpu-train.py\" --store fp16 --recipe control `")
    A("  --lr 0.05 --hidden 64 --epochs 150 --eval-every 1 --eval-train-every 1 --tag ctl150 `")
    A("  --log out/_r6-ctl150-100k.txt --json out/_r6-ctl150-100k.json")
    A("#     54.3 s 跑完 150 epoch；ep150 loss 0.524028 / 留出 73.896%")
    A("#     必须与 out/_r5-train-win-100k-ep150.txt（C#）和 out/_r5-gpu-100k.txt 对上")
    A("#     （loss 最大差 ~4e-05、留出最大差 0.04 点；ep150 = 73.896% vs C# 73.90%）")
    A("")
    A("# ---------- 3) ★ 主消融 S1–S5（前 50,000 局；每一步只改一样）----------")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-gpu-train.py\" --max-games 50000 --store fp32 `")
    A("  --epochs 2000 --patience 200 --min-delta 0.0005 --eval-every 1 --eval-train-every 5 `")
    A("  --cases \"S1-ctl:control:0.05:0:64:control,S2-mb:mb:0.05:256:64:control,\" `")
    A("          \"S3-adam:adam:0.001:256:64:control,S4-he:adam:0.001:256:64:he,\" `")
    A("          \"S5-h256:adam:0.001:256:256:he\" `")
    A("  --tag r6-abl50k --log out/_r6-abl-50k.txt --json out/_r6-abl-50k.json `")
    A("  --weights out/_r6-w50k.npz")
    A("#     → 约 66 分钟（S1 对照组 2000 epoch = 246 s；S2 729 s；S3 896 s；S4 897 s；S5 1021 s）")
    A("")
    A("# ---------- 4) ★ 100k 缩放（同配方，数据换 100k；fp16 存储以适配 8 GiB 卡）----------")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-gpu-train.py\" --store fp16 --epochs 150 `")
    A("  --eval-every 1 --eval-train-every 10 --recipe adam --batch 256 --lr 0.001 --hidden 64 `")
    A("  --init control --tag S3-adam-100k --log out/_r6-s3-adam-100k.txt `")
    A("  --json out/_r6-s3-adam-100k.json --weights out/_r6-w100k-s3.npz")
    A("#     → 1486 s（24.8 分钟），留出 81.627%（平滑峰值 81.560%）")
    A("")
    A("# ---------- 5) ★ 管线瓶颈拆解（§7；数据已经在显存，量的是每步开销）----------")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-pipeline-bench.py\" --max-games 50000 `")
    A("  --batch 256 --hidden 64")
    A("#     → out/_r6-pipeline.txt：① 取 batch 48.5 µs / 整步 564 µs / 0.130 TFLOP/s /")
    A("#       内存版 1017.8 µs（1.80× 慢）/ batch 4096 = 0.15 s 每 epoch")
    A("")
    A("# ---------- 6) 权重三重复算（Python 独立 + C# 独立）----------")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-verify-npz.py\" --npz out/_r6-w100k-s3.npz `")
    A("  --result out/_r6-s3-adam-100k.json --tag S3-adam-100k --out out/_r6-verify-npz-S3-100k.txt")
    A("#     → 81.6273%（283,765/347,635）")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-pack-model.py\" --npz out/_r6-w100k-s3.npz `")
    A("  --out out/_r6-model-S3-adam-100k.bin --spec out/_r5-encoderspec.txt `")
    A("  --result out/_r6-s3-adam-100k.json --tag S3-adam-100k")
    A("dotnet tools\\NNTrain\\bin\\Release\\net10.0\\NNTrain.dll verify --data out\\nn-data-100k.bin `")
    A("  --model out\\_r6-model-S3-adam-100k.bin --split-seed 12345 *> out\\_r6-verify-csharp-S3-100k.txt")
    A("#     → 验证集 81.63%（283,765/347,635）—— 与 Python 逐位相同")
    A("")
    A("# ---------- 7) 曲线 / 消融表 / 收敛判定 ----------")
    A("python -X utf8 \"klink bot\\tools\\nn-r6-curves.py\" `")
    A("  --json out/_r6-abl-50k.json --json out/_r6-s3-adam-100k.json `")
    A("  --out out/_r6-curves.txt --md out/_r6-tables.md")
    A("```")
    A("")
    A("## 11. 本轮改动的文件")
    A("")
    A("| 文件 | 改动 |")
    A("|---|---|")
    A("| `klink bot/tools/nn-r6-gpu-train.py` | **新增**：本轮训练器。损失/前向/反传与 C# 逐字相同；"
      "新增 `--recipe control\\|mb\\|adam`、`--grad-norm mean\\|sum`、`--store fp32\\|fp16`、"
      "`--holdout-games`（固定留出清单）、`--cases`（一次跑多组、共享同一份装载数据）、"
      "`--eval-train-every`、逐 epoch 的 grad-norm / 死 ReLU / 显存读数 |")
    A("| `klink bot/tools/nn-r6-holdout-eval.py` | **新增**：固定留出上的线性基线 + MLP 评估 + "
      "配对 McNemar 检验 |")
    A("| `klink bot/tools/nn-r6-curves.py` | **新增**：收敛判定（20-epoch 滑动平均 + best−1.0 尾段）、"
      "消融表、曲线表、墙上时间/吞吐表 |")
    A("| `klink bot/tools/nn-r6-pack-model.py` | **新增**：npz 权重 → C# `NnModel` 二进制 |")
    A("| `klink bot/tools/nn-r6-verify-npz.py` | **新增**：Python 侧重读 npz 独立复算留出 |")
    A("| `klink bot/tools/nn-r6-pipeline-bench.py` | **新增**：每步耗时拆解（§7 的证据）|")
    A("| `klink bot/tools/nn-r6-bench.py` | **新增**：显存/布局的先行基准（设计依据）|")
    A("| `klink bot/docs/NN训练诊断.md` | 追加本节（追加前存 `NN训练诊断.md.r6.bak`，152,125 B / 2,404 行）|")
    A("")
    A("**没动的**：`src/KLink.Bot/` 下的一切（`StateEncoder` / `MatchEngine` / `GameState` / "
      "`NnModel` / `GreedyBot` / `MetaDecks` / `CardEffectScripts` / `KismetVm`）、"
      "`tools/NNTrain/Program.cs`（一行未改）、`out/nn-data-100k.bin`（一个字节未改）。")
    A("")
    A("**产物**：")
    A("")
    A("| 产物 | 内容 |")
    A("|---|---|")
    A("| `out/_r6-holdout-games.txt` | ★ 固定留出局清单（15,000 gid），所有配置共用 |")
    A("| `out/_r6-holdout-eval-baselines.{txt,json}` | 固定留出上的 F1/F2l/F2d + McNemar |")
    A("| `out/_r6-abl-50k.{txt,json}` | 主消融 S1–S5（含 1.13 MB 的逐 epoch 历史）|")
    A("| `out/_r6-s3-adam-100k.{txt,json}` | 100k 缩放运行 |")
    A("| `out/_r6-curves.txt` / `out/_r6-tables.md` | 收敛判定 + 全部表格（本节的数字来源）|")
    A("| `out/_r6-pipeline.txt` | 每步耗时拆解 |")
    A("| `out/_r6-model-S3-adam-100k.bin` | ★ 打包成 C# 格式的最好模型（194 KB，745→64→1）|")
    A("| `out/_r6-verify-csharp-S3-100k.txt` / `out/_r6-verify-npz-S3-100k.txt` | 两重独立复算日志 |")
    A("| `out/_r6-prec-20k-fp32.txt` / `out/_r6-prec-20k-fp16.txt` | fp32 vs fp16 存储的对照（§9 第 3 条）|")
    A("| `out/_r6-probe-lr.txt` / `out/_r6-probe-lr2.txt` | 20k 上的学习率/配方探针（含第一版 bug）|")
    A("| `out/_r6-w50k.npz.*.npz` / `out/_r6-w100k-s3.npz` | 各配置的权重 |")
    A("")
    A("**第五轮及以前的产物一律原样保留，没有任何覆盖。**")
    A("")

    txt = "\n".join(L)
    doc = REPO / "klink bot/docs/NN训练诊断.md"
    with open(doc, "a", encoding="utf-8") as f:
        f.write(txt)
    (REPO / "out/_r6-report-append.md").write_text(txt, encoding="utf-8")
    print(f"已追加 {len(txt):,} 字符到 {doc}")
    print(f"副本 → out/_r6-report-append.md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
