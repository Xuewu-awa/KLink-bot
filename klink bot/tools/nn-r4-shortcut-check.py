"""
第四轮 §2 的两条独立反证（都不依赖训练，纯统计）：

  ① **卡组身份在编码里还剩多少？** —— v1（925 维）与 v2（745 维）用**同一个**最近质心探针，
     逐区域拆开看是哪一格漏出去的。
  ② **「优势回归」为什么没有改变任务？** —— 对每个样本算 t = y − b（b = 同对位基线胜率，
     留一局口径），统计 `sign(t) == 2y−1` 的比例。若恒为 1，则分类任务的标签一个字没变，
     A 卡组对位基线在它上面仍然是 73% —— 先验并没有被"减掉"。

用法：
  python -X utf8 "klink bot/tools/nn-r4-shortcut-check.py"
"""

import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import open_data, open_data2   # noqa: E402

DECKS = 22


def nearest_centroid(X, groups, n_groups):
    """按 groups 分组，看「离本组质心是否比离最近的他组更近」。"""
    cent = np.zeros((n_groups, X.shape[1]), dtype=np.float64)
    for d in range(n_groups):
        m = groups == d
        if m.sum() > 0:
            cent[d] = X[m].mean(axis=0)
    d2 = np.empty((X.shape[0], n_groups), dtype=np.float64)
    for d in range(n_groups):
        d2[:, d] = ((X - cent[d]) ** 2).sum(axis=1)
    return float((d2.argmin(axis=1) == groups).mean())


def load(path):
    """自动识别 v1 / v2，返回 (rec, dim, ver, 按局的 (左卡组, 右卡组, y, 每局第一条快照下标))。"""
    with open(path, "rb") as f:
        magic = int.from_bytes(f.read(4), "little")
    if magic == 0x324C4B41:
        rec, dim, n, decks = open_data2(str(path))
        y = np.asarray(rec[:, dim]); gid = np.asarray(rec[:, dim + 1])
        pl = (np.asarray(rec[:, dim + 2], dtype=np.int64) // decks)
        pr = (np.asarray(rec[:, dim + 2], dtype=np.int64) % decks)
        ver = 2
    elif magic == 0x314C4B41:
        rec, dim, n = open_data(str(path))
        y = np.asarray(rec[:, dim]); gid = np.asarray(rec[:, dim + 1])
        # v1 没有对位编号：对位由 (seed, g) 派生，清单在旁边的 .decks.json 里
        import json
        man = json.load(open(str(path) + ".decks.json", encoding="utf-8"))
        gl = np.full(int(gid.max()) + 1, -1, dtype=np.int64)
        gr = np.full(int(gid.max()) + 1, -1, dtype=np.int64)
        for g, li, ri in man["pairs"]:
            gl[g], gr[g] = li, ri
        pl = gl[gid.astype(int)]; pr = gr[gid.astype(int)]
        ver = 1
    else:
        raise SystemExit(f"不认识的文件 {path}")

    ng = int(gid.max()) + 1
    first = np.zeros(ng, dtype=np.int64)
    seen = np.zeros(ng, dtype=bool)
    for i in range(len(gid) - 1, -1, -1):
        g = int(gid[i]); first[g] = i; seen[g] = True
    return rec, dim, ver, y, gid, pl, pr, first[np.flatnonzero(seen)]


def zones(ver):
    if ver == 2:
        return {"Hand 手牌区(双方)": [*range(9, 99), *range(380, 469)],
                "Deck 牌库区(双方)": [],
                "Discard 弃牌堆区(双方)": [*range(282, 372), *range(653, 743)],
                "Board 场地区(前线+半场)": [*range(100, 190), *range(191, 281),
                                            *range(471, 561), *range(562, 652)],
                "全局 6+6 标量": [3, 4, 5, 6, 7, 8, 374, 375, 376, 377, 378, 379],
                "全向量": list(range(745))}
    return {"Hand 手牌区(双方)": [*range(9, 99), *range(470, 560)],
            "Deck 牌库区(双方)": [*range(373, 463), *range(834, 924)],
            "Discard 弃牌堆区(双方)": [*range(282, 372), *range(743, 833)],
            "Board 场地区(前线+半场)": [*range(100, 190), *range(191, 281),
                                        *range(561, 651), *range(652, 742)],
            "全局 6+6 标量": [3, 4, 5, 6, 7, 8, 464, 465, 466, 467, 468, 469],
            "全向量": list(range(925))}


def main():
    only2 = "--only-2" in sys.argv
    files = [("v1（925 维，第三轮数据）", REPO / "out/nn-data.bin"),
             ("v2（745 维，本轮数据）", REPO / "out/nn-data-v2.bin")]
    print("=" * 104)
    print("① 编码 → 卡组身份：最近质心命中率（22 套牌，随机 = 1/22 = 4.55%）")
    print("   每局取**第一条快照**（Turn=2，双方各只动过一次），按真实卡组身份分组")
    print("=" * 104)
    print(f"  {'数据集 / 特征子集':<40}{'认左方卡组':>12}{'认右方卡组':>12}{'维度':>7}")
    results = {}
    for tag, path in ([] if only2 else files):
        rec, dim, ver, y, gid, pl, pr, rows = load(path)
        Z = zones(ver)
        print(f"  ---- {tag}  样本 {len(gid):,}  对局 {int(gid.max()) + 1:,}")
        results[tag] = {}
        for zname, cols in Z.items():
            if not cols:
                print(f"    {zname:<38}{'（已删）':>12}{'（已删）':>12}{0:>7}")
                results[tag][zname] = None
                continue
            X = np.asarray(rec[np.ix_(rows, cols)], dtype=np.float32)
            aL = nearest_centroid(X, pl[rows], DECKS)
            aR = nearest_centroid(X, pr[rows], DECKS)
            results[tag][zname] = (aL, aR)
            print(f"    {zname:<38}{aL:>11.2%}{aR:>12.2%}{len(cols):>7}")
        del rec
        print()

    print("=" * 104)
    print("② 「优势回归」为什么不改变任务：t = y − b（b = 同对位基线胜率，逐样本留一）")
    print("=" * 104)
    for tag, path in files:
        rec, dim, ver, y, gid, pl, pr, rows = load(path)
        ng = int(gid.max()) + 1
        labs = np.zeros(ng)
        labs[gid.astype(int)] = y
        gl = np.full(ng, -1, dtype=np.int64)
        gr = np.full(ng, -1, dtype=np.int64)
        gl[gid.astype(int)] = pl
        gr[gid.astype(int)] = pr
        pid = gl * DECKS + gr
        gs = np.arange(ng)
        lw = np.bincount(pid, weights=labs, minlength=DECKS * DECKS)
        pn = np.bincount(pid, minlength=DECKS * DECKS).astype(np.float64)
        # 逐样本留一：把这一局自己从分子分母里去掉
        b = (lw[pid[gid.astype(int)]] - (y == 1)) / np.maximum(pn[pid[gid.astype(int)]] - 1, 1)
        t = y - b
        # ⚠️ b 会取到**正好 0 或 1**（该对位在其余局里全输或全赢）⇒ t = 0，符号无定义。
        #    这不是"任务变了"，是那一档的基线把结果完全解释了。要分开报。
        deg = (b <= 0.0) | (b >= 1.0)
        same_all = float((np.sign(t) == (2 * y - 1)).mean())
        same_ok = float((np.sign(t[~deg]) == (2 * y - 1)[~deg]).mean()) if (~deg).any() else float("nan")
        print(f"  {tag}")
        print(f"    b 范围 {b.min():.4f}…{b.max():.4f}   t = y − b 范围 {t.min():+.4f}…{t.max():+.4f}")
        print(f"    退化样本（b ∈ {{0,1}} ⇒ t = 0，占 {100.0 * deg.mean():.2f}%）：{int(deg.sum()):,} 条")
        print(f"    全体 sign(t) == 2y−1 的比例            = {same_all:.4%}")
        print(f"    ★ 非退化样本里 sign(t) == 2y−1 的比例  = {same_ok:.6%}"
              f"   ⇒ {'标签的正负号**一个字都没变**' if same_ok > 0.99999 else '有变化'}")
        del rec
    print()
    print("  ⇒ 「减去同对位基线」只改损失的权重尺度，不改任何样本的正负号；")
    print("    所以 A 卡组对位基线在优势回归口径下**仍然是 73%**，先验没有被减掉。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
