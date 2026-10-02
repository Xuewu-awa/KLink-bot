"""
第四轮：三个问题一次算清 ——
  ① **卡组身份还能不能从编码里被认出来？**（v1 925 维 → v2 745 维，删了牌库卡向量）
  ② **「纯卡组对位基线」到底是多少？**（它是不是真的能掉到 ~53%）
  ③ 换目标（对位内成对排序）之后，MLP 与线性基线各是多少？

为什么要有这个脚本：第三轮报告的 A 基线（73.09%）是**从对位清单直接算**的 ——
它一行编码都不读、一个模型都不看。所以「把卡组身份从编码里去掉」「换训练目标」
这两件事**在数学上不可能改变它**。本脚本把这件事用三组数字钉死：

  第 1 组  编码 → 卡组身份的最近质心命中率
           （v1 全向量 99.9%；v2 全向量 / 只看手牌 / 只看弃牌堆 / 只看场面 ……）
  第 2 组  A 基线（对位多数类，训练局估 → 留出局评）在**原始胜负**口径下是多少
           —— 并给出「卡池不平衡（σ / 跨度）」这个与模型无关的原因
  第 3 组  **口径 B：对位内成对排序**（同一对位 × 同一回合，赢局局面 vs 输局局面）
           随机 = 50%，**与卡池平衡程度无关**；在这个口径下「纯对位基线」结构上就是 50%

口径 A（原始胜负）与口径 B（对位内排序）对**同一批留出局**同时给出，
所有线性基线在两种口径下都用**同一份特征**、各自的目标函数拟合，保证可比。

用法：
  python -X utf8 "klink bot/tools/nn-r4-baselines.py"                       # 全部
  python -X utf8 "klink bot/tools/nn-r4-baselines.py" --data out/nn-data-v2.bin
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import forward_logit, load_model, nntrain_split, open_data, open_data2, sigmoid  # noqa: E402

SEED = 12345
DECKS = 22


# ==================== 编码布局 ====================

def layout(dim):
    """按 StateEncoder 的真实偏移取列号。"""
    if dim == 745:      # v2：全局 3 + 每方 371（牌库区只有张数）
        return dict(ver=2, G_TURN=0, G_ACT=1, G_PLAYED=2,
                    L_HQ=3, L_KRED=4, L_MAXK=5, L_HAND=6, L_DECKN=7, L_BOARD=8,
                    L_HANDV=(9, 99), L_FRONTV=(100, 190), L_HALFV=(191, 281),
                    L_DISCV=(282, 372), L_DECKC=373,
                    R_HQ=374, R_KRED=375, R_MAXK=376, R_HAND=377, R_DECKN=378, R_BOARD=379,
                    R_HANDV=(380, 470), R_FRONTV=(471, 561), R_HALFV=(562, 652),
                    R_DISCV=(653, 743), R_DECKC=744)
    if dim == 925:      # v1：全局 3 + 每方 461（牌库区也有 90 维卡向量）
        return dict(ver=1, G_TURN=0, G_ACT=1, G_PLAYED=2,
                    L_HQ=3, L_KRED=4, L_MAXK=5, L_HAND=6, L_DECKN=7, L_BOARD=8,
                    L_HANDV=(9, 99), L_FRONTV=(100, 190), L_HALFV=(191, 281),
                    L_DISCV=(282, 372), L_DECKV=(373, 463), L_DECKC=463,
                    R_HQ=464, R_KRED=465, R_MAXK=466, R_HAND=467, R_DECKN=468, R_BOARD=469,
                    R_HANDV=(470, 560), R_FRONTV=(561, 651), R_HALFV=(652, 742),
                    R_DISCV=(743, 833), R_DECKV=(834, 924), R_DECKC=924)
    raise SystemExit(f"不认识的维度 {dim}")


def public_feats(rec, L):
    """5 个公开局面差（与历史脚本 nn-effects-baselines.py 的 public_feats 同定义）。"""
    def delta(a, b, k):
        return np.rint((np.asarray(rec[:, a], dtype=np.float32)
                        - np.asarray(rec[:, b], dtype=np.float32)) * k).astype(np.int32) / float(k)

    return np.column_stack([
        delta(L["L_HQ"], L["R_HQ"], 20),
        delta(L["L_BOARD"], L["R_BOARD"], 10),
        delta(L["L_HAND"], L["R_HAND"], 10),
        delta(L["L_KRED"], L["R_KRED"], 12),
        delta(L["L_DISCV"][1], L["R_DISCV"][1], 10),      # 弃牌堆**张数**（区域最后一位）
    ]).astype(np.float32)


def onehot(idx, size):
    oh = np.zeros((len(idx), size), dtype=np.float32)
    oh[np.arange(len(idx)), idx] = 1.0
    return oh


# ==================== 两种目标的拟合器 ====================

def fit_logistic(F, y, l2=1e-3, iters=1200, lr=0.8):
    """口径 A 的拟合器（与历史脚本逐字相同）。"""
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = F.shape[0]
    for _ in range(iters):
        p = sigmoid(F @ w)
        g = F.T @ (p - y) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return w


def build_pairs(pair, turn, y, mask):
    """在 (对位, 回合) 单元内把「赢局样本」与「输局样本」按顺序两两配对（确定性）。"""
    idx = np.flatnonzero(mask)
    cells = {}
    for i in idx:
        key = (int(pair[i]), int(turn[i]))
        c = cells.get(key)
        if c is None:
            c = cells[key] = ([], [])
        (c[0] if y[i] > 0.5 else c[1]).append(int(i))
    ai, bi = [], []
    for w, l in cells.values():
        m = min(len(w), len(l))
        if m:
            ai.extend(w[:m])
            bi.extend(l[:m])
    return np.asarray(ai, dtype=np.int64), np.asarray(bi, dtype=np.int64)


def fit_pairwise(F, ai, bi, l2=1e-3, iters=3000, lr=0.5):
    """口径 B 的拟合器：logistic(sigmoid(w·(F_i − F_j))) → 1，只看**同一个单元内**的差异。

    ⚠️ 对位先验在每一对里是同一个常数、相减即抵消 ⇒ 拟合出来的权重只可能来自局面差异。

    ⚠️ 差值 D 必须先**按列标准化**再拟合：本函数的特征量纲差得很远（HQ 差 ~0.05、
    场面差 ~0.2、kredit 差 ~0.5），不标准化时 lr=0.5 会让权重直接发散 ——
    第一版就是这么错的（留出排序准确率 36.45%，**低于随机**）。
    标准化后返回的权重已经折回原空间，可以直接 `F @ w` 打分。
    """
    if len(ai) == 0:
        return np.zeros(F.shape[1], dtype=np.float32)
    D = (F[ai] - F[bi]).astype(np.float32)
    sd = D.std(axis=0).astype(np.float32) + 1e-6
    sd[0] = 1.0                       # 截距不缩放
    D = D / sd
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = len(ai)
    for _ in range(iters):
        p = sigmoid(D @ w)
        g = D.T @ (p - 1.0) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return (w / sd).astype(np.float32)


def pair_acc(score, ai, bi):
    """对位内排序准确率。**平局算半分** —— 一个对局面完全无感的模型（例如只用卡组身份的）
    会给单元内两个样本**完全相同的分**，按「严格大于」会记成 0%，那是指标假象；
    按半分制它正确地落在 50%。"""
    if len(ai) == 0:
        return float("nan"), 0
    d = score[ai] - score[bi]
    return float((d > 0).mean() + 0.5 * (d == 0).mean()), len(ai)


# ==================== 主流程 ====================

def run(data_path, label, deck_probe_models=()):
    rec, dim, n, deck_count = open_data2(str(data_path))
    L = layout(dim)
    y = np.asarray(rec[:, dim], dtype=np.float32)
    gid = np.asarray(rec[:, dim + 1], dtype=np.float32)
    pair = np.asarray(rec[:, dim + 2], dtype=np.int64)
    pl = (pair // deck_count).astype(np.int64)
    pr = (pair % deck_count).astype(np.int64)
    yl = y.astype(np.float64)

    turn = np.rint(np.asarray(rec[:, L["G_TURN"]]) * 30).astype(np.int64)

    val_mask, train_mask, val_games = nntrain_split(gid, SEED)
    n_val_games = len(val_games)
    print("=" * 108)
    print(f"数据集 {label}：{data_path.name}   编码 v{L['ver']}（dim={dim}）   "
          f"样本 {n:,}   对局 {int(gid.max()) + 1:,}   卡组 {deck_count} 套")
    print(f"  口径 A = 原始胜负（sign(score) 猜左方是否获胜）  |  "
          f"口径 B = 对位内成对排序（同一对位 × 同一回合，赢局局面 vs 输局局面；随机 = 50%）")
    print(f"  留出 = --split-seed {SEED} 的 {n_val_games:,} 局 / {int(val_mask.sum()):,} 条 "
          f"（与 NNTrain 逐样本同一份，由 nn-split-check.py 反证）")
    print("=" * 108)

    out = {"data": data_path.name, "dim": dim, "ver": L["ver"], "n": n,
           "games": int(gid.max()) + 1, "n_val_games": n_val_games,
           "n_val": int(val_mask.sum()), "n_train": int(train_mask.sum())}

    # ---------------------------------------------------------------- 第 1 组
    print()
    print("━" * 108)
    print("【第 1 组】编码 → 卡组身份：最近质心命中率（每局取第一条快照，按真实卡组身份分组）")
    print("━" * 108)
    first = np.zeros(int(gid.max()) + 1, dtype=np.int64)
    seen = np.zeros(int(gid.max()) + 1, dtype=bool)
    for i in range(n - 1, -1, -1):          # 倒着扫 ⇒ 每个 gid 落在**最小**下标 = 第一条快照
        first[int(gid[i])] = i
        seen[int(gid[i])] = True
    gs = np.flatnonzero(seen)
    rows = first[gs]

    def probe(colmask, name):
        """按左/右卡组身份分组，看「离本组质心是否比离最近的他组更近」。"""
        Xs = np.asarray(rec[np.ix_(rows, colmask)], dtype=np.float32)
        res = {}
        for tag, grp in (("左", pl[rows]), ("右", pr[rows])):
            cent = np.zeros((deck_count, Xs.shape[1]), dtype=np.float64)
            for d in range(deck_count):
                m = grp == d
                if m.sum() > 0:
                    cent[d] = Xs[m].mean(axis=0)
            d2 = ((Xs[:, None, :] - cent[None, :, :]) ** 2).sum(axis=2) if Xs.shape[1] <= 200 else None
            if d2 is None:                   # 维度太高就分块算，避免 (G,22,dim) 爆内存
                d2 = np.empty((Xs.shape[0], deck_count), dtype=np.float64)
                for d in range(deck_count):
                    d2[:, d] = ((Xs - cent[d]) ** 2).sum(axis=1)
            res[tag] = float((d2.argmin(axis=1) == grp).mean())
        print(f"  {name:<46} 左 {res['左']:6.2%}   右 {res['右']:6.2%}")
        return res

    def rng(a, b):
        return np.arange(a, b + 1)

    probe_res = {}
    probe_res["全向量"] = probe(rng(0, dim - 1), f"全向量（{dim} 维）")
    if L["ver"] == 1:
        probe_res["牌库区卡向量"] = probe(rng(*L["L_DECKV"]), "牌库区卡向量（90 维，v2 已删）")
    probe_res["手牌区卡向量"] = probe(np.concatenate([rng(*L["L_HANDV"]), rng(*L["R_HANDV"])]),
                                     "手牌区卡向量（90+90 维）")
    probe_res["弃牌堆区卡向量"] = probe(np.concatenate([rng(*L["L_DISCV"]), rng(*L["R_DISCV"])]),
                                       "弃牌堆区卡向量（90+90 维）")
    probe_res["场地区卡向量"] = probe(np.concatenate([rng(*L["L_FRONTV"]), rng(*L["L_HALFV"]),
                                                      rng(*L["R_FRONTV"]), rng(*L["R_HALFV"])]),
                                      "场地区卡向量（前线+半场，90×4 维）")
    probe_res["全局标量"] = probe(np.array([L["L_HQ"], L["L_KRED"], L["L_MAXK"], L["L_HAND"],
                                            L["L_DECKN"], L["L_BOARD"], L["R_HQ"], L["R_KRED"],
                                            L["R_MAXK"], L["R_HAND"], L["R_DECKN"], L["R_BOARD"]]),
                                  "只用 6+6 个全局标量（不含任何卡向量）")
    # 「去掉对方手牌卡向量」之后还剩多少 —— 对方的**手牌内容是隐藏信息**，
    # 真实性上本来就不该进编码；这一行用来判断还值不值得再删一版。
    keep = np.asarray([c for c in range(dim) if not (L["R_HANDV"][0] <= c < L["R_HANDV"][1])])
    probe_res["全向量−对方手牌"] = probe(keep, "全向量去掉**对方**手牌卡向量后")
    out["deck_probe"] = probe_res

    del rows, gs
    print("  ⇒ 「全向量 ≈ 100%」= 模型只要看一眼编码就知道场上是谁打谁 ⇒ 背对位这条捷径是开着的。")

    # ---------------------------------------------------------------- 第 2 组
    print()
    print("━" * 108)
    print("【第 2 组】口径 A（原始胜负）留出基线 —— 「纯卡组对位基线」的真身")
    print("━" * 108)
    feats = public_feats(rec, L)
    F1_all = np.column_stack([np.ones(n, dtype=np.float32), feats])
    ohl = onehot(pl, deck_count)
    ohr = onehot(pr, deck_count)

    tr, ho = train_mask, val_mask

    def acc_a(logits):
        return float(((logits > 0) == (y > 0.5))[ho].mean())

    resA = {}
    guess = 1 if yl[tr].mean() >= 0.5 else 0
    resA["F0 多数类"] = float(((y > 0.5) == (guess == 1))[ho].mean())
    wF1 = fit_logistic(F1_all[tr], y[tr]); resA["F1 读领先(6参)"] = acc_a(F1_all @ wF1)
    F2l = np.column_stack([F1_all, ohl]); wF2l = fit_logistic(F2l[tr], y[tr])
    resA["F2l 读领先+左卡组(28参)"] = acc_a(F2l @ wF2l)
    F2r = np.column_stack([F1_all, ohr]); wF2r = fit_logistic(F2r[tr], y[tr])
    resA["F2r 读领先+右卡组(28参)"] = acc_a(F2r @ wF2r)
    F2d = np.column_stack([F1_all, ohl, ohr]); wF2d = fit_logistic(F2d[tr], y[tr])
    resA["F2d 读领先+左右卡组(50参)"] = acc_a(F2d @ wF2d)
    del F2l, F2r

    # A：对位多数类（训练局估 → 留出局评）—— **完全不看局面、也完全不看编码**
    lab_of_game = np.zeros(int(gid.max()) + 1, dtype=np.float64)
    lab_of_game[gid.astype(int)] = yl
    gs_all = np.arange(int(gid.max()) + 1)
    npair = deck_count * deck_count
    pair_of_game = np.full(int(gid.max()) + 1, -1, dtype=np.int64)
    pair_of_game[gid.astype(int)] = pair
    gp = pair_of_game[gs_all]
    is_val_game = np.zeros(int(gid.max()) + 1, dtype=bool)
    is_val_game[np.unique(gid[ho].astype(int))] = True
    trg = ~is_val_game
    a_lw = np.bincount(gp[trg], weights=lab_of_game[trg], minlength=npair)
    a_n = np.bincount(gp[trg], minlength=npair).astype(np.float64)
    fallback = lab_of_game[trg].mean() >= 0.5
    a_left_win = np.where(a_n > 0, a_lw >= (a_n - a_lw), fallback)
    resA["★A 对位多数类(训练局估→留出局评)"] = float((a_left_win[pair] == (y == 1))[ho].mean())
    # A2/A3 只认单边卡组
    for nm, grp in (("A2 只认左卡组", pl), ("A3 只认右卡组", pr)):
        lw = np.bincount(grp[tr], weights=yl[tr], minlength=deck_count)
        cnt = np.bincount(grp[tr], minlength=deck_count).astype(np.float64)
        pick = lw >= (cnt - lw)
        resA[nm] = float((pick[grp] == (y == 1))[ho].mean())

    print(f"  {'基线':<44}{'留出准确率':>12}{'留出条数':>11}")
    for k, v in resA.items():
        print(f"  {k:<44}{v:>11.2%}{int(ho.sum()):>11,}")
    print()
    print("  ⚠️ ★A 这一行**只用到 (标签, 对位编号)** —— 它不读编码、不读模型。")
    print("     所以「删掉牌库卡向量」「换训练目标」在任何实现下都改不动它。")
    print("     它高，是因为**卡池本身不平衡**（下一节），不是因为编码里有捷径。")
    out["baselineA"] = resA

    # ---------------- 卡池不平衡（与模型无关的事实）----------------
    print()
    print("  卡池不平衡实测（决定 ★A 上限的那个量）：")
    # ⚠️ 必须按**局**取卡组下标：`pl`/`pr` 是按样本的，直接拿 gs_all 去索引是错的
    #    （那会取到前 10000 条样本 = 前几百局）。这里显式建按局的映射。
    ng = int(gid.max()) + 1
    pl_of_game = np.full(ng, -1, dtype=np.int64)
    pr_of_game = np.full(ng, -1, dtype=np.int64)
    pl_of_game[gid.astype(int)] = pl
    pr_of_game[gid.astype(int)] = pr
    labs = lab_of_game[gs_all]
    win_by_deck = np.zeros(deck_count)
    cnt_by_deck = np.zeros(deck_count)
    for d in range(deck_count):
        # 该套牌在自己那一侧的胜场（它在左就取 y，在右就取 1−y）
        c = (pl_of_game == d) | (pr_of_game == d)
        w = np.where(pl_of_game == d, labs, 1 - labs)
        win_by_deck[d] = w[c].sum()
        cnt_by_deck[d] = c.sum()
    wr = win_by_deck / cnt_by_deck
    order = np.argsort(wr)
    man = json.load(open(str(data_path) + ".decks.json", encoding="utf-8"))
    names = man["decks"]
    print(f"    最低 {names[order[0]]} {wr[order[0]]:.1%} … 最高 {names[order[-1]]} {wr[order[-1]]:.1%}"
          f"    跨度 {100 * (wr[order[-1]] - wr[order[0]]):.1f} 个点   σ = {100 * wr.std():.1f} 个点")
    pw = np.zeros(npair)
    pn = np.zeros(npair)
    np.add.at(pw, gp, lab_of_game[gs_all])
    np.add.at(pn, gp, 1.0)
    pmaj = np.maximum(pw, pn - pw) / np.maximum(pn, 1)
    used = pn > 0
    print(f"    462 个有序对位里，多数类占比 ≥90% 的有 {int(((pmaj >= 0.9) & used).sum())} 个，"
          f"≥75% 的有 {int(((pmaj >= 0.75) & used).sum())} 个")
    print(f"    按对位个数加权的多数类准确率 = {pmaj[used].mean():.2%} "
          f"（这就是 ★A 的理论值；实测 {resA['★A 对位多数类(训练局估→留出局评)']:.2%}）")
    out["deck_winrate"] = {names[i]: float(wr[i]) for i in range(deck_count)}
    out["deck_wr_sigma"] = float(100 * wr.std())
    out["pair_majority_mean"] = float(pmaj[used].mean())

    # ---------------------------------------------------------------- 第 3 组
    print()
    print("━" * 108)
    print("【第 3 组】口径 B（对位内成对排序）留出基线 —— 唯一与卡池平衡无关的口径")
    print("━" * 108)

    ai_tr, bi_tr = build_pairs(pair, turn, y, tr)
    ai_ho, bi_ho = build_pairs(pair, turn, y, ho)
    print(f"  训练对 {len(ai_tr):,} 对（同一对位 × 同一回合，赢局局面 → 输局局面）")
    print(f"  留出对 {len(ai_ho):,} 对")

    # 「纯对位基线」在这个口径下的值：它对单元内两个样本给出**完全相同的分**
    # ⇒ 永远打平 ⇒ 按「严格大于」计分 = 50.0%（不是估计值，是结构值）
    out["baselineB_paironly"] = 0.5

    resB = {}
    resB_tr = {}
    w = fit_pairwise(F1_all, ai_tr, bi_tr)
    resB["F1 读领先(6参)"] = pair_acc(F1_all @ w, ai_ho, bi_ho)[0]
    resB_tr["F1 读领先(6参)"] = pair_acc(F1_all @ w, ai_tr, bi_tr)[0]
    for nm, F in (("F2l 读领先+左卡组(28参)", np.column_stack([F1_all, ohl])),
                  ("F2r 读领先+右卡组(28参)", np.column_stack([F1_all, ohr])),
                  ("F2d 读领先+左右卡组(50参)", F2d)):
        w = fit_pairwise(F, ai_tr, bi_tr)
        resB[nm] = pair_acc(F @ w, ai_ho, bi_ho)[0]
        resB_tr[nm] = pair_acc(F @ w, ai_tr, bi_tr)[0]
    # 只用卡组身份（不用局面）—— 这是「纯卡组对位基线」在口径 B 下的直接实现
    Fdeck = np.column_stack([np.ones(n, dtype=np.float32), ohl, ohr])
    w = fit_pairwise(Fdeck, ai_tr, bi_tr)
    resB["★纯卡组对位(44参·完全不看局面)"] = pair_acc(Fdeck @ w, ai_ho, bi_ho)[0]
    resB_tr["★纯卡组对位(44参·完全不看局面)"] = pair_acc(Fdeck @ w, ai_tr, bi_tr)[0]
    resB["F0 常数分（结构上恒 50%）"] = 0.5
    resB_tr["F0 常数分（结构上恒 50%）"] = 0.5

    print(f"  {'基线':<44}{'留出排序准确率':>14}{'训练排序准确率':>14}{'留出对数':>11}")
    for k, v in resB.items():
        print(f"  {k:<44}{v:>13.2%}{resB_tr[k]:>13.2%}{len(ai_ho):>11,}")
    print()
    print("  ★ 这一行才是「捷径堵住了没有」的直接读数：只给卡组身份、不给局面时，")
    print("    在**同一对位 × 同一回合**内它无法区分两个局面 ⇒ 50.0%（结构值，不是拟合出来的）。")
    out["baselineB"] = resB
    out["baselineB_train"] = resB_tr
    out["n_pairs_train"] = int(len(ai_tr))
    out["n_pairs_val"] = int(len(ai_ho))

    # ---------------------------------------------------------------- MLP
    print()
    print("━" * 108)
    print("【MLP】同一批留出局上的两个口径（模型文件里的 forward 与 C# 逐字一致）")
    print("━" * 108)
    for tag, mp in deck_probe_models:
        model = load_model(str(mp))
        if model["dim"] != dim:
            print(f"  {tag}: 模型维度 {model['dim']} ≠ 数据 {dim}，跳过")
            continue
        score = np.empty(n, dtype=np.float32)
        CH = 32768
        for s in range(0, n, CH):
            e = min(n, s + CH)
            score[s:e] = forward_logit(model, np.asarray(rec[s:e, :dim], dtype=np.float32))
        a_mlp = float(((score > 0) == (y > 0.5))[ho].mean())
        b_mlp, _ = pair_acc(score, ai_ho, bi_ho)
        b_mlp_tr, _ = pair_acc(score, ai_tr, bi_tr)
        print(f"  {tag}")
        print(f"     口径 A（原始胜负）留出 {a_mlp:.2%}     "
              f"口径 B（对位内排序）留出 {b_mlp:.2%} / 训练 {b_mlp_tr:.2%}")
        out.setdefault("mlp", {})[tag] = {"A": a_mlp, "B": b_mlp, "B_train": b_mlp_tr}

    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="out/nn-data-v2.bin")
    ap.add_argument("--label", default="第四轮 v2（745 维，牌库区只留张数）")
    ap.add_argument("--models", default="", help="tag=path,tag=path 形式的 MLP 列表")
    ap.add_argument("--out", default="out/_r4-baselines.json")
    a = ap.parse_args()

    models = []
    if a.models:
        for part in a.models.split(","):
            tag, _, p = part.partition("=")
            models.append((tag, REPO / p))

    res = run(REPO / a.data, a.label, models)
    (REPO / a.out).write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")
    print()
    print(f"JSON 汇总 → {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
