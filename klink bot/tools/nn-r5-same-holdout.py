"""把 10k 与 100k 两个模型放到**同一份最严格的留出**上比 —— 共有的那 1,500 局。

为什么要这一步：
  100k 的留出是 15,000 局（`--split-seed 12345` 取前 15%），它**包含** 10k 的那 1,500 局
  （切分只依赖 (gameIds 顺序, seed)，与总对数无关，所以 100k 的前 1,500 个留出局就是 10k 的全部留出局）。
  于是 10k 报的是「34,800 条」上的准确率、100k 报的是「347,635 条」上的 —— 两个数**不在同一批样本上**。
  最严格的做法是把两边都限制到**共有的 1,500 局 / 34,800 条**上重算。

同时给出各自完整留出上的数字，方便与训练日志/verify 对账。
"""
import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import forward_logit, load_model, nntrain_split, open_data2  # noqa: E402

SEED = 12345


def score_all(model, rec, dim, n, chunk=65536):
    s = np.empty(n, dtype=np.float32)
    for a in range(0, n, chunk):
        b = min(n, a + chunk)
        s[a:b] = forward_logit(model, np.asarray(rec[a:b, :dim], dtype=np.float32))
    return s


def main():
    data100 = REPO / "out/nn-data-100k.bin"
    rec, dim, n, deck_count = open_data2(str(data100))
    y = np.asarray(rec[:, dim], dtype=np.float32)
    gid = np.asarray(rec[:, dim + 1], dtype=np.int64)
    pair = np.asarray(rec[:, dim + 2], dtype=np.int64)

    val100_mask, _, val100_games = nntrain_split(gid, SEED)
    val100_games = np.asarray([int(g) for g in val100_games], dtype=np.int64)
    n_val100 = len(val100_games)

    # 10k 的留出局 = 把「前 1 万局」这一子集再切一次的前 15%。
    # ⚠️ 与 100k 的留出**近似嵌套但不完全**：实测 1499/1500 重合，
    #    唯一漏掉的是 gid 4493（它在 100k 的切分里落在训练侧）——
    #    所以严格对照必须把「100k 协议下属于训练集」的那些行剔掉。
    sub = gid < 10000
    val10_mask_sub, _, val10_games = nntrain_split(gid[sub], SEED)
    val10_games = np.asarray([int(g) for g in val10_games], dtype=np.int64)

    nested = np.isin(val10_games, val100_games).all()
    shared = np.isin(gid, val10_games) & val100_mask      # ← 两边都判为留出的行
    shared_naive = np.isin(gid, val10_games)

    L = []
    L.append("# 10k vs 100k：同一份最严格留出上的对照")
    L.append("")
    L.append(f"100k 留出局 {n_val100:,}    10k 留出局 {len(val10_games):,}    "
             f"10k ⊆ 100k ? **{nested}**")
    L.append(f"10k 留出局里不在 100k 留出里的："
             f"{list(np.setdiff1d(val10_games, val100_games))}")
    L.append(f"共有留出（两边都判为留出）：**{int(shared.sum()):,} 条**"
             f"   （仅按 10k 留出局算是 {int(shared_naive.sum()):,} 条，"
             f"差 {int(shared_naive.sum())-int(shared.sum())} 条 = 落进 100k 训练侧的那一局）")
    L.append(f"100k 留出局前 5 个 {list(val100_games[:5])}")
    L.append(f"10k  留出局前 5 个 {list(val10_games[:5])}")
    L.append("")

    models = {
        "MLP win 10k @150ep": REPO / "out/nn-model-win-10k-ep150.bin",
        "MLP win 100k @150ep": REPO / "out/nn-model-win-100k-ep150.bin",
    }
    rows = {}
    for tag, mp in models.items():
        m = load_model(str(mp))
        assert m["dim"] == dim, (tag, m["dim"], dim)
        sc = score_all(m, rec, dim, n)
        a_shared = float(((sc > 0) == (y > 0.5))[shared].mean())
        rows[tag] = sc
        L.append(f"{tag}")
        L.append(f"    **共有留出**（{int(shared.sum()):,} 条）口径A：**{a_shared:.2%}**")
        if "100k" in tag:
            a_own = float(((sc > 0) == (y > 0.5))[val100_mask].mean())
            L.append(f"    自己完整留出（15,000 局 / {int(val100_mask.sum()):,} 条）口径A：{a_own:.2%}"
                     f"   （训练日志报 73.90%，C# verify 报 73.90%）")
        else:
            L.append(f"    自己完整留出（1,500 局 / {int(shared_naive.sum()):,} 条）口径A："
                     f"{float(((sc > 0) == (y > 0.5))[shared_naive].mean()):.2%}"
                     f"   （训练日志报 72.39%，C# verify 报 72.39%）")
        L.append("")

    L.append(f"## ★ 同一份留出（1,500 局 / {int(shared.sum()):,} 条）上的差")
    a = float(((rows["MLP win 10k @150ep"] > 0) == (y > 0.5))[shared].mean())
    b = float(((rows["MLP win 100k @150ep"] > 0) == (y > 0.5))[shared].mean())
    L.append(f"    10k  {a:.2%}")
    L.append(f"    100k {b:.2%}")
    L.append(f"    **差 {100*(b-a):+.2f} 个点**")
    # 配对显著性：两边对同一批样本的错/对，用 McNemar 精确检验
    c10 = ((rows["MLP win 10k @150ep"] > 0) == (y > 0.5))[shared]
    c100 = ((rows["MLP win 100k @150ep"] > 0) == (y > 0.5))[shared]
    n01 = int((~c10 & c100).sum())     # 10k 错、100k 对
    n10 = int((c10 & ~c100).sum())     # 10k 对、100k 错
    from math import comb
    k = min(n01, n10)
    tot = n01 + n10
    p = sum(comb(tot, i) for i in range(k + 1)) / (2 ** tot) * 2 if tot else 1.0
    L.append(f"    McNemar：10k 错&100k 对 = {n01:,}，10k 对&100k 错 = {n10:,}，"
             f"双尾精确 p = {min(p,1.0):.3g}")
    L.append("")

    txt = "\n".join(L)
    (REPO / "out/_r5-same-holdout.txt").write_text(txt, encoding="utf-8")
    print(txt)
    json.dump({"shared_rows": int(shared.sum()), "nested": bool(nested),
               "a_10k": a, "a_100k": b, "n01": n01, "n10": n10, "p": float(min(p, 1.0))},
              open(REPO / "out/_r5-same-holdout.json", "w", encoding="utf-8"), indent=1)


if __name__ == "__main__":
    main()
