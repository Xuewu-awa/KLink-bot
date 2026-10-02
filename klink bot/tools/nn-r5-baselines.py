"""
第五轮：**10 万局 vs 1 万局**的单变量对照 —— 全部落在同一份留出协议上。

本轮唯一自变量：dump 的**局数**（与 epoch 数）。所以这个脚本的作用是
「在**同一份留出**上，把 10k 与 100k 的每个数字都算出来」，而不是复用上一轮已经报出的
数字 —— 上一轮那些数字是在**旧脚本 + 可能不同的实现细节**下算的，直接搬过来不足以做
1:1 对照。

与第四轮的 `nn-r4-baselines.py` 的关系：
  * **口径、特征、拟合器逐字相同**（`public_feats` 5 个公开局面差、`fit_logistic`、
    `fit_pairwise`、`pair_acc` 平局算半分、✂ `nntrain_split(gid, 12345)`）；
  * **切分逐样本相同**（复用 `nn_common.nntrain_split`，并与 C# 的 `--split-seed 12345` 对齐）；
  * 两处**只影响内存/速度、不影响任何数字**的改动：
      1. 分块读 + 只保留 6 个公开特征列（100k 局 × 745 维全量 memmap → 物化要 6.9 GB，
         本机空闲内存不够；这里对**每一列**做的都是逐元素同样的算式，见 `public_feats`）；
      2. `build_pairs` 向量化（原版是 Python 循环建 dict，2.32M 样本要跑几分钟；
         配对**顺序与语义完全一致**：单元内赢局样本按下标顺序、输局按下标顺序，各取前 m 个）。
  * 新增 `--max-games`：只取**前 N 局**（gid < N），用来把 10k 与 100k 放在同一份代码下对照。
    因为 dump 的第 g 局只由 `(seed, g)` 决定，前 1 万局在 10k 文件与 100k 文件里**逐位相同**。

用法：
  # 10 万局（全部）
  python -X utf8 "klink bot/tools/nn-r5-baselines.py" --data out/nn-data-100k.bin \
      --label "本轮 100k" --models "MLP-win-ep150=out/_r5-model-win-100k-ep150.bin" \
      --out out/_r5-baselines-100k.json
  # 1 万局（同一份代码 / 同一份留出的前 1500 局）
  python -X utf8 "klink bot/tools/nn-r5-baselines.py" --data out/nn-data-100k.bin \
      --max-games 10000 --label "本轮 10k（100k 文件的前 1 万局）" \
      --out out/_r5-baselines-10k.json
"""

import argparse
import json
import struct
import sys
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import forward_logit, load_model, nntrain_split, sigmoid  # noqa: E402

SEED = 12345
DECKS = 22
MAGIC_V1 = 0x314C4B41
MAGIC_V2 = 0x324C4B41
CHUNK = 120_000          # 每次读多少条样本（120k × 748 × 4B ≈ 359 MB）


# ==================== 编码布局（与 nn-r4-baselines.py 逐字相同）====================

def layout(dim):
    if dim == 745:      # v2：全局 3 + 每方 371（牌库区只有张数）
        return dict(ver=2, G_TURN=0, G_ACT=1, G_PLAYED=2,
                    L_HQ=3, L_KRED=4, L_MAXK=5, L_HAND=6, L_DECKN=7, L_BOARD=8,
                    L_HANDV=(9, 99), L_FRONTV=(100, 190), L_HALFV=(191, 281),
                    L_DISCV=(282, 372), L_DECKC=373,
                    R_HQ=374, R_KRED=375, R_MAXK=376, R_HAND=377, R_DECKN=378, R_BOARD=379,
                    R_HANDV=(380, 470), R_FRONTV=(471, 561), R_HALFV=(562, 652),
                    R_DISCV=(653, 743), R_DECKC=744)
    if dim == 925:      # v1
        return dict(ver=1, G_TURN=0, G_ACT=1, G_PLAYED=2,
                    L_HQ=3, L_KRED=4, L_MAXK=5, L_HAND=6, L_DECKN=7, L_BOARD=8,
                    L_HANDV=(9, 99), L_FRONTV=(100, 190), L_HALFV=(191, 281),
                    L_DISCV=(282, 372), L_DECKV=(373, 463), L_DECKC=463,
                    R_HQ=464, R_KRED=465, R_MAXK=466, R_HAND=467, R_DECKN=468, R_BOARD=469,
                    R_HANDV=(470, 560), R_FRONTV=(561, 651), R_HALFV=(652, 742),
                    R_DISCV=(743, 833), R_DECKV=(834, 924), R_DECKC=924)
    raise SystemExit(f"不认识的维度 {dim}")


# ==================== 分块读 ====================

def read_cols(path, n_games=None, log=print):
    """按块读 dump，只物化 6 个公开特征列 + label/gid/pair/turn。

    `public_feats` 的每个输出列都只依赖**单个编码列**（两个编码列之差 × 常数再取整），
    所以逐列读出来单独算，结果与「先物化整块 745 维再算」**逐位相同**。

    块边界**按局对齐**（丢掉跨块的半局，塞进下一块）—— 保证每个 gid 只有一个块持有它，
    否则 `lab_of_game[gid] = y` 那种写法会被后一块覆盖成同值（无害）但 `first-snapshot`
    之类的逻辑会错。本脚本用不到 first-snapshot，但仍然按局对齐，保持简单可推理。

    返回 dict：feats(6 列), y, gid, pair, turn, n
    """
    size = Path(path).stat().st_size
    with open(path, "rb") as f:
        head = f.read(12)
    magic, dim = struct.unpack_from("<ii", head, 0)
    if magic == MAGIC_V2:
        deck_count, hdr = struct.unpack_from("<i", head, 8)[0], 12
    elif magic == MAGIC_V1:
        deck_count, hdr = DECKS, 8
    else:
        raise SystemExit(f"不认识的 magic {magic:#x}")
    ncol = dim + (3 if hdr == 12 else 2)
    if (size - hdr) % (ncol * 4) != 0:
        raise SystemExit("文件长度除不尽，格式不对")
    n_total = (size - hdr) // (ncol * 4)
    log(f"  文件 {Path(path).name}: magic={magic:#x} dim={dim} 每条 {ncol} 列 样本 {n_total:,}"
        f" 卡组 {deck_count} 套" + (f"  只取前 {n_games:,} 局" if n_games else ""))

    L = layout(dim)
    # 需要读的编码列（0-based）：6 个公开量的 8 个来源列 + 两个弃牌堆张数 + Turn
    need = [L["L_HQ"], L["R_HQ"], L["L_BOARD"], L["R_BOARD"], L["L_HAND"], L["R_HAND"],
            L["L_KRED"], L["R_KRED"], L["L_DISCV"][1], L["R_DISCV"][1], L["G_TURN"]]

    ys, gids, pairs, turns = [], [], [], []
    cols = {c: [] for c in need}
    off = hdr
    carry = None          # 上一块尾部未结束的那一局（行数组）
    t0 = time.time()
    with open(path, "rb") as f:
        while off < size:
            cnt = min(CHUNK, (size - off) // (ncol * 4))
            if cnt <= 0:
                break
            f.seek(off)
            buf = np.frombuffer(f.read(cnt * ncol * 4), dtype=np.float32).reshape(cnt, ncol)
            off += cnt * ncol * 4
            if carry is not None:
                buf = np.concatenate([carry, buf], axis=0)
                carry = None
            # 丢掉末尾未结束的局
            g = buf[:, dim + 1]
            last = g[-1]
            end = int(np.flatnonzero(g != last)[-1]) + 1 if (g != last).any() else 0
            if end == 0:
                carry = buf                      # 整块都是同一局（不可能，但要安全）
                continue
            carry = buf[end:].copy()
            buf = buf[:end]

            gg = buf[:, dim + 1].astype(np.int64)
            if n_games is not None:
                keep = gg < n_games
                if not keep.all():
                    buf = buf[keep]
                    gg = gg[keep]
                    if buf.shape[0] == 0:
                        continue                 # ⚠️ 不能 break：前面已经攒下的块还有效
            if buf.shape[0] == 0:
                break
            ys.append(buf[:, dim].copy())
            gids.append(gg)
            pairs.append(buf[:, dim + 2].astype(np.int64))
            for c in need:
                cols[c].append(buf[:, c].copy())
            if n_games is not None and gg[-1] >= n_games - 1:
                break
            if (time.time() - t0) > 60:
                log(f"    … 已读 {off/1e9:F2}/{size/1e9:F2} GB")
                t0 = time.time()

    def cat(a):
        return np.concatenate(a) if a else np.zeros(0, dtype=np.float32)

    y = cat(ys)
    gid = np.concatenate(gids) if gids else np.zeros(0, dtype=np.int64)
    pair = np.concatenate(pairs) if pairs else np.zeros(0, dtype=np.int64)
    C = {c: cat(cols[c]) for c in need}

    def delta(a, b, k):
        return np.rint((C[a] - C[b]) * k).astype(np.int32) / float(k)

    feats = np.column_stack([
        delta(L["L_HQ"], L["R_HQ"], 20),
        delta(L["L_BOARD"], L["R_BOARD"], 10),
        delta(L["L_HAND"], L["R_HAND"], 10),
        delta(L["L_KRED"], L["R_KRED"], 12),
        delta(L["L_DISCV"][1], L["R_DISCV"][1], 10),   # 弃牌堆**张数**（区域最后一位）
    ]).astype(np.float32)
    turn = np.rint(C[L["G_TURN"]] * 30).astype(np.int64)
    n = len(y)
    log(f"  → 物化 {n:,} 条，特征 {feats.shape[1]} 列，对局 {int(gid.max())+1:,}，"
        f"内存 {feats.nbytes/1e6:.0f} MB(特征)")
    return dict(feats=feats, y=y, gid=gid, pair=pair, turn=turn, n=n,
                dim=dim, deck_count=deck_count, L=L)


# ==================== 两种目标的拟合器（与第四轮逐字相同）====================

def fit_logistic(F, y, l2=1e-3, iters=1200, lr=0.8):
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = F.shape[0]
    for _ in range(iters):
        p = sigmoid(F @ w)
        g = F.T @ (p - y) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return w


def build_pairs(pair, turn, y, mask, deck_count=DECKS):
    """在 (对位, 回合) 单元内把「赢局样本」与「输局样本」按顺序两两配对（确定性）。

    与第四轮的 Python 循环版本**语义相同**：单元内赢局样本按下标升序、输局样本按下标升序，
    各取前 m = min(赢数, 输数) 个，再按顺序一对一配成 (赢局局面, 输局局面)。
    （C# 侧 `PairRank` 用的也是同一个顺序，所以三者可比。）

    实现只是把上面的循环向量化（2.32M 样本用 Python dict 建单元要跑好几分钟）。

    做法（不做任何坐标换算）：
      * `grp[k]` = 第 k 个样本（在 `sel` 的位置空间里）的单元编号；
      * 赢样本里，单元内第 i 个（i 从 0 数）当左 ⇔ i < m[单元]；输样本同；
      * 最后把左、右都按「(单元, 单元内序号)」排序 ⇒ 第 i 个左与第 i 个右同单元同序号。
    """
    sel = np.flatnonzero(mask)
    is_win = y[sel] > 0.5
    # ⚠️ 单元 key 必须**唯一**：`pair` 本身可以到 461，所以间隔要用 `deck_count²`
    #    （461×462+29 = 213,023）而不是拍脑袋的 4096 —— 用 4096 会把不同对位撞成
    #    同一个单元（第一版就是这么错的，被 nn-r5-pair-check.py 当场抓住）。
    #    编号用 np.unique 的**排序编号**即可：每个单元内的配对是独立的，
    #    与第四轮 dict 的插入序只差一个整体重排（对照时两边都按下标升序 canonicalize）。
    key = pair[sel].astype(np.int64) * (deck_count * deck_count) + turn[sel]
    _, grp, cnt = np.unique(key, return_inverse=True, return_counts=True)
    grp = grp.reshape(-1)

    n_all = cnt[grp]                                         # 每个样本所在单元的大小
    n_w = np.bincount(grp, weights=is_win.astype(np.float64),
                      minlength=len(cnt)).astype(np.int64)[grp]
    m = np.minimum(n_w, n_all - n_w)                         # 每个样本所在单元的可配对数

    win_rank = _rank_within(sel, grp, is_win, len(cnt))       # 单元内第几个赢样本（从 0）
    loss_rank = _rank_within(sel, grp, ~is_win, len(cnt))     # 单元内第几个输样本（从 0）

    take_l = is_win & (win_rank < m)
    take_r = (~is_win) & (loss_rank < m)
    # ⚠️ 必须**按 (单元, 单元内序号) 重排**再返回：上面的过滤各自按「sel 位置升序」扫，
    #    left 与 right 的第 i 个元素分别落在哪个单元并不相同 —— 直接 zip 会跨单元配对
    #    （第一版就是这个错，同样被 nn-r5-pair-check.py 抓住）。
    ol = np.lexsort((win_rank[take_l], grp[take_l]))
    orr = np.lexsort((loss_rank[take_r], grp[take_r]))
    return (sel[take_l][ol].astype(np.int64),
            sel[take_r][orr].astype(np.int64))


def _rank_within(sel, grp, pick, n_cells):
    """`pick` 为 True 的样本在「同单元同 pick」里按原下标升序的序号（从 0 开始）。

    只对 `pick` 的子集排序；`grp` 的编号取自**全体**单元，所以 `np.bincount(..., minlength=n_cells)`
    的长度恰好是单元总数，`np.repeat(starts, cnt)` 也就恰好展开成子集长度。
    """
    out = np.full(len(sel), -1, dtype=np.int64)
    j = np.flatnonzero(pick)
    if len(j) == 0:
        return out
    o = np.argsort(grp[j], kind="stable")                     # 单元升序 + 组内下标升序
    g = grp[j][o]
    # 各单元的起点：对**出现过的**单元编号（升序）算偏移，再 map 回每个样本所在单元
    uniq = np.unique(g)
    first = np.flatnonzero(np.r_[True, g[1:] != g[:-1]])
    start_of = np.zeros(n_cells, dtype=np.int64)
    start_of[uniq] = first
    out[j[o]] = np.arange(len(j)) - start_of[g]
    return out


def fit_pairwise(F, ai, bi, l2=1e-3, iters=3000, lr=0.5, mem=40000):
    if len(ai) == 0:
        return np.zeros(F.shape[1], dtype=np.float32)
    D = np.empty((len(ai), F.shape[1]), dtype=np.float32)
    for s in range(0, len(ai), mem):
        e = min(len(ai), s + mem)
        D[s:e] = F[ai[s:e]] - F[bi[s:e]]
    sd = D.std(axis=0).astype(np.float32) + 1e-6
    sd[0] = 1.0
    D /= sd
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = len(ai)
    for _ in range(iters):
        p = sigmoid(D @ w)
        g = D.T @ (p - 1.0) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return (w / sd).astype(np.float32)


def pair_acc(score, ai, bi):
    if len(ai) == 0:
        return float("nan"), 0
    d = score[ai] - score[bi]
    return float((d > 0).mean() + 0.5 * (d == 0).mean()), len(ai)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="out/nn-data-100k.bin")
    ap.add_argument("--max-games", type=int, default=0, help="只用 gid < N 的前 N 局（0 = 全部）")
    ap.add_argument("--label", default="")
    ap.add_argument("--models", default="", help="tag=path,tag=path")
    ap.add_argument("--out", default="out/_r5-baselines.json")
    ap.add_argument("--skip-train-acc", action="store_true",
                    help="不算拟合器自己的训练集准确率（省时间；模型的一定会算）")
    a = ap.parse_args()

    data_path = REPO / a.data
    log = lambda *m: print(*m, flush=True)
    log("=" * 108)
    log(f"第五轮留出对照：{a.label or data_path.name}"
        + (f"  【只取前 {a.max_games:,} 局】" if a.max_games else ""))
    log("=" * 108)

    D = read_cols(data_path, a.max_games or None, log=log)
    n, y = D["n"], D["y"]
    pair, turn, feats, gid = D["pair"], D["turn"], D["feats"], D["gid"]
    deck_count = D["deck_count"]
    pl = (pair // deck_count).astype(np.int64)
    pr = (pair % deck_count).astype(np.int64)

    val_mask, train_mask, val_games = nntrain_split(gid, SEED)
    ho, tr = val_mask, train_mask
    log(f"  留出（--split-seed {SEED}）: {len(val_games):,} 局 / {int(ho.sum()):,} 条；"
        f"训练 {int(tr.sum()):,} 条")

    out = {"data": data_path.name, "max_games": a.max_games, "n": n,
           "games": int(gid.max()) + 1, "n_val_games": len(val_games),
           "n_val": int(ho.sum()), "n_train": int(tr.sum()),
           "val_games_first5": [int(v) for v in val_games[:5]],
           "val_games_sum": int(sum(int(v) for v in val_games))}

    F1 = np.column_stack([np.ones(n, dtype=np.float32), feats])
    oh = np.zeros((n, deck_count), dtype=np.float32)
    oh[np.arange(n), pl] = 1.0
    ohr = np.zeros((n, deck_count), dtype=np.float32)
    ohr[np.arange(n), pr] = 1.0
    F2l = np.column_stack([F1, oh])
    F2d = np.column_stack([F1, oh, ohr])
    del oh

    def acc_a(sc):
        return float(((sc > 0) == (y > 0.5))[ho].mean())

    # ---------------- 口径 A ----------------
    log("\n【口径 A】原始胜负（sign(score) 猜左方是否获胜）—— 同留出 " + f"{int(ho.sum()):,} 条")
    resA = {}
    yl = y.astype(np.float64)
    resA["F0 多数类"] = float(((y > 0.5) == (yl[tr].mean() >= 0.5))[ho].mean())
    for nm, F in (("F1 读领先(6参)", F1), ("F2l 读领先+左卡组(28参)", F2l),
                  ("F2d 读领先+左右卡组(50参)", F2d)):
        w = fit_logistic(F[tr], y[tr])
        resA[nm] = acc_a(F @ w)
        resA[nm + "·训练"] = float(((F @ w > 0) == (y > 0.5))[tr].mean())
        log(f"    {nm:<30} 留出 {resA[nm]:>7.2%}   训练 {resA[nm+'·训练']:>7.2%}")
    # ★A 对位多数类（训练局估 → 留出局评）
    ng = int(gid.max()) + 1
    lab_of_game = np.zeros(ng, dtype=np.float64)
    lab_of_game[gid] = yl
    pair_of_game = np.full(ng, -1, dtype=np.int64)
    pair_of_game[gid] = pair
    is_val_game = np.zeros(ng, dtype=bool)
    is_val_game[np.unique(gid[ho])] = True
    trg = ~is_val_game
    npair = deck_count * deck_count
    a_lw = np.bincount(pair_of_game[trg], weights=lab_of_game[trg], minlength=npair)
    a_n = np.bincount(pair_of_game[trg], minlength=npair).astype(np.float64)
    fallback = lab_of_game[trg].mean() >= 0.5
    a_left_win = np.where(a_n > 0, a_lw >= (a_n - a_lw), fallback)
    resA["★A 对位多数类(训练局估→留出局评)"] = float((a_left_win[pair] == (y == 1))[ho].mean())
    log(f"    {'★A 对位多数类(训练局估→留出局评)':<30} 留出 {resA['★A 对位多数类(训练局估→留出局评)']:>7.2%}")
    for nm, grp in (("A2 只认左卡组", pl), ("A3 只认右卡组", pr)):
        lw = np.bincount(grp[tr], weights=yl[tr], minlength=deck_count)
        cnt = np.bincount(grp[tr], minlength=deck_count).astype(np.float64)
        resA[nm] = float(((lw >= (cnt - lw))[grp] == (y == 1))[ho].mean())
        log(f"    {nm:<30} 留出 {resA[nm]:>7.2%}")
    out["baselineA"] = resA

    # 卡池不平衡（与模型无关的事实）
    pl_g = np.full(ng, -1, dtype=np.int64); pl_g[gid] = pl
    pr_g = np.full(ng, -1, dtype=np.int64); pr_g[gid] = pr
    labs = lab_of_game
    wr = np.zeros(deck_count); cn = np.zeros(deck_count)
    for d in range(deck_count):
        m = (pl_g == d) | (pr_g == d)
        wr[d] = np.where(pl_g == d, labs, 1 - labs)[m].sum() / max(1, m.sum())
        cn[d] = m.sum()
    man = json.load(open(str(data_path) + ".decks.json", encoding="utf-8"))
    names = man["decks"]
    order = np.argsort(wr)
    log(f"    卡池：最低 {names[order[0]]} {wr[order[0]]:.1%} … 最高 {names[order[-1]]} {wr[order[-1]]:.1%}"
        f"  跨度 {100*(wr[order[-1]]-wr[order[0]]):.1f} 点  σ={100*wr.std():.1f} 点")
    out["deck_winrate"] = {names[i]: float(wr[i]) for i in range(deck_count)}
    out["deck_wr_sigma"] = float(100 * wr.std())

    # ---------------- 口径 B ----------------
    t0 = time.time()
    ai_tr, bi_tr = build_pairs(pair, turn, y, tr)
    ai_ho, bi_ho = build_pairs(pair, turn, y, ho)
    log(f"\n【口径 B】对位内成对排序（随机 = 50%）  训练对 {len(ai_tr):,} / 留出对 {len(ai_ho):,}"
        f"  （配对耗时 {time.time()-t0:.1f}s）")
    resB, resB_tr = {}, {}
    for nm, F in (("F1 读领先(6参)", F1), ("F2l 读领先+左卡组(28参)", F2l),
                  ("F2r 读领先+右卡组(28参)", np.column_stack([F1, ohr])),
                  ("F2d 读领先+左右卡组(50参)", F2d)):
        w = fit_pairwise(F, ai_tr, bi_tr)
        resB[nm] = pair_acc(F @ w, ai_ho, bi_ho)[0]
        if not a.skip_train_acc:
            resB_tr[nm] = pair_acc(F @ w, ai_tr, bi_tr)[0]
        log(f"    {nm:<30} 留出 {resB[nm]:>7.2%}" + (f"   训练 {resB_tr[nm]:>7.2%}" if nm in resB_tr else ""))
    resB["★纯卡组对位(44参·完全不看局面)"] = 0.5
    resB_tr["★纯卡组对位(44参·完全不看局面)"] = 0.5
    out["baselineB"] = resB
    out["baselineB_train"] = resB_tr
    out["n_pairs_train"] = int(len(ai_tr)); out["n_pairs_val"] = int(len(ai_ho))

    # ---------------- MLP ----------------
    if a.models:
        log("\n【MLP】同一批留出局上的两个口径（Python 独立解析模型文件）")
        ncol = D["dim"] + 3
        for part in a.models.split(","):
            tag, _, p = part.partition("=")
            mp = REPO / p
            model = load_model(str(mp))
            if model["dim"] != D["dim"]:
                log(f"    {tag}: 维度 {model['dim']} ≠ 数据 {D['dim']}，跳过")
                continue
            score = np.empty(n, dtype=np.float32)
            # 分块前向（100k 局全量物化 745 维要 6.9 GB）—— memmap 是只读视图，不占内存
            buf_all = np.memmap(data_path, dtype=np.float32, mode="r", offset=12, shape=(n, ncol))
            for s in range(0, n, 65536):
                e = min(n, s + 65536)
                score[s:e] = forward_logit(model, np.asarray(buf_all[s:e, :D["dim"]],
                                                             dtype=np.float32))
            del buf_all
            a_mlp = acc_a(score)
            b_mlp, _ = pair_acc(score, ai_ho, bi_ho)
            b_tr, _ = pair_acc(score, ai_tr, bi_tr)
            tr_acc = float(((score > 0) == (y > 0.5))[tr].mean())
            log(f"    {tag}")
            log(f"      口径 A 留出 {a_mlp:.2%}   训练 {tr_acc:.2%}   （差 {100*(tr_acc-a_mlp):+.2f} 点）")
            log(f"      口径 B 留出 {b_mlp:.2%}   训练 {b_tr:.2%}")
            out.setdefault("mlp", {})[tag] = {"A": a_mlp, "A_train": tr_acc,
                                              "B": b_mlp, "B_train": b_tr,
                                              "meta": model["meta"]}

    (REPO / a.out).write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    log(f"\nJSON → {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
