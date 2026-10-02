"""
① 带效果数据上的**留出**平凡基线（v0 / v1-noeffects / v1-effects 三份数据同一份代码、同一口径）。

要回答的问题（见 klink bot/docs/NN训练诊断.md 第一轮 §3.2 与第二轮 §7.1）：
    第一轮那个致命对比「MLP 76.52% vs 28 参数线性 81.98%」在**卡牌效果打开之后**还成立吗？

口径（**全部只用留出局**，与 NNTrain 的 `verify` 逐样本同一份；复刻的可靠性由
`nn-split-check.py` 反证）：
  · `--split-seed 12345` → 与模型同一个留出集（1500 局），本脚本的★主口径
  · `gid % 5 == 0`      → 2000 局；第二轮报告里 F1/F2 用的就是这个口径，用于和历史数字对齐

基线（拟合只用**训练局**，评估只用**留出局**；A 另给「留一局」口径以便与历史数字并列）：
  F0      多数类（永远猜左）
  F1      读领先：Δhq/Δ场上/Δ手牌/Δkredits/Δ弃牌堆 的逻辑回归（6 参）
  F2l     F1 + 左卡组 one-hot(22)   = **28 参数**  ← 与第一轮那个 28 参数模型同参数个数
  F2r     F1 + 右卡组 one-hot(22)   = **28 参数**
  F2d     F1 + 左/右卡组 one-hot(44) = 50 参数
  F2p     F1 + 有序对位 one-hot      = 468 参数（v1）/ 28 参数（v0，只有 22 种对位）
  F3      F2d + 回合 one-hot
  A       卡组对位多数类（不看局面）—— 留一局 + 纯训练局两种口径
  A2/A3   只认左卡组 / 只认右卡组
  B       网格最优线性「读领先」（权重在训练局上选，在留出局上评；另给全数据上界）

用法：
  python -X utf8 "klink bot/tools/nn-effects-baselines.py" effects        # 新数据（带效果）
  python -X utf8 "klink bot/tools/nn-effects-baselines.py" noeffects      # 归档的 v1 无效果
  python -X utf8 "klink bot/tools/nn-effects-baselines.py" v0             # 第一轮 v0
  python -X utf8 "klink bot/tools/nn-effects-baselines.py" all
"""

import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import forward_logit, load_model, nntrain_split, open_data   # noqa: E402

DECKS = 22
TURN_MAX = 56          # v1 编码里实测 Turn 范围 2…56
SEED = 12345


def layout(dim):
    """按 StateEncoder 的真实偏移给出需要的列号（见 src/KLink.Bot/NN/StateEncoder.cs:193-211）。"""
    if dim == 925:      # v1：全局 3 维在前，每方 461
        return dict(ver=1, G_TURN=0, G_ACT=1, G_PLAYED=2,
                    L_HQ=3, L_KRED=4, L_MAXK=5, L_HAND=6, L_DECKN=7, L_BOARD=8,
                    L_DISC=372, L_DECKV=373,
                    R_HQ=464, R_KRED=465, R_MAXK=466, R_HAND=467, R_DECKN=468, R_BOARD=469,
                    R_DISC=833, R_DECKV=834)
    if dim == 740:      # v0：没有全局 3 维，每方 370
        return dict(ver=0, G_TURN=None, G_ACT=None, G_PLAYED=None,
                    L_HQ=0, L_KRED=1, L_MAXK=2, L_HAND=3, L_DECKN=4, L_BOARD=5,
                    L_DISC=278, L_DECKV=279,
                    R_HQ=370, R_KRED=371, R_MAXK=372, R_HAND=373, R_DECKN=374, R_BOARD=375,
                    R_DISC=648, R_DECKV=649)
    raise SystemExit(f"不认识的维度 {dim}")


def public_feats(rec, L):
    """5 个公开局面差（与 nn-baseline3.py 的 `feats` **逐位相同**）。

    ⚠️ 先 `rint(·*k)` 还原成整数点差、再除回 k —— 与既有脚本一致（编码是 float32，
       `hqDef/20` 这种除法会有舍入，直接相减得到的差值不是精确的整数点差）。
    """
    def delta(a, b, k):
        return np.rint((np.asarray(rec[:, a], dtype=np.float32)
                        - np.asarray(rec[:, b], dtype=np.float32)) * k).astype(np.int32) / float(k)

    return np.column_stack([
        delta(L["L_HQ"], L["R_HQ"], 20),
        delta(L["L_BOARD"], L["R_BOARD"], 10),
        delta(L["L_HAND"], L["R_HAND"], 10),
        delta(L["L_KRED"], L["R_KRED"], 12),
        delta(L["L_DISC"], L["R_DISC"], 10),
    ]).astype(np.float32)


def fit_logistic(F, y, l2=1e-3, iters=1200, lr=0.8):
    """与 nn-baseline3.py / nn-ceiling.py **逐字相同**的拟合器（float32、无 mini-batch、l2 不含截距）。"""
    w = np.zeros(F.shape[1], dtype=np.float32)
    n = F.shape[0]
    for _ in range(iters):
        p = 1.0 / (1.0 + np.exp(-np.clip(F @ w, -30, 30)))
        g = F.T @ (p - y) / n
        g[1:] += l2 * w[1:]
        w -= (lr * g).astype(np.float32)
    return w


def logit(F, w):
    return F @ w


def onehot(idx, size):
    oh = np.zeros((len(idx), size), dtype=np.float32)
    oh[np.arange(len(idx)), idx] = 1.0
    return oh


def run(key):
    if key == "effects":
        data = REPO / "out/nn-data.bin"
        model_path = REPO / "out/nn-model.bin"
        manifest = REPO / "out/nn-data.bin.decks.json"
        label = "v1 **带卡牌效果**（本轮新数据，10,000 局 / 462 随机对位）"
    elif key == "noeffects":
        data = REPO / "out/nn-data-v1-noeffects.bin"
        model_path = REPO / "out/nn-model-v1-noeffects.bin"
        manifest = REPO / "out/nn-data.bin.decks.json"   # 对位流只由 (seed,g) 决定 ⇒ 与 effects 完全相同
        label = "v1 无效果（第二轮归档；对位清单与 effects 版逐字节相同）"
    elif key == "v0":
        data = REPO / "out/nn-data-v0.bin"
        model_path = REPO / "out/nn-model-v0.bin"
        manifest = None
        label = "v0（第一轮；卡组写死轮换 decks[g%22] vs decks[(g+7)%22]）"
    else:
        raise SystemExit(f"不认识的数据集 {key}")

    rec, dim, n = open_data(str(data))
    L = layout(dim)
    X = rec                     # memmap，按列取用，不整体载入
    y = np.asarray(rec[:, dim], dtype=np.float32)
    gid = np.asarray(rec[:, dim + 1], dtype=np.float32)
    yl = y.astype(np.float64)

    # ---- 对位（每局一对卡组）----
    gmax = int(gid.max())
    pair_of_game = np.full(gmax + 1, -1, dtype=np.int64)
    if manifest is None:
        gs_all = np.arange(gmax + 1)
        pair_of_game[gs_all] = (gs_all % DECKS) * DECKS + ((gs_all + 7) % DECKS)
        pair_desc = "对位 = gid%22（v0 写死轮换）"
        npair = DECKS * DECKS
    else:
        man = json.load(open(manifest, encoding="utf-8"))
        for g, li, ri in man["pairs"]:
            pair_of_game[g] = li * DECKS + ri
        pair_desc = f"对位 = {manifest.name} 的 (左卡组, 右卡组)"
        npair = DECKS * DECKS
    pg = pair_of_game[gid.astype(int)]
    assert (pg >= 0).all()
    pl = (pg // DECKS).astype(np.int64)          # 左卡组下标
    pr = (pg % DECKS).astype(np.int64)           # 右卡组下标

    # ---- 公开局面量（双方都可见的 5 个差）----
    feats = public_feats(X, L)

    if L["ver"] == 1:
        turn = np.rint(np.asarray(X[:, 0]) * 30).astype(np.int64)
    else:
        turn = (np.rint(np.asarray(X[:, L["L_MAXK"]]) * 12)
                + np.rint(np.asarray(X[:, L["R_MAXK"]]) * 12)).astype(np.int64)

    print("=" * 100)
    print(f"数据集 {key}：{label}")
    print(f"  文件 {data.name}   编码 v{L['ver']}（dim={dim}）   样本 {n:,}   对局 {gmax + 1:,}")
    print(f"  {pair_desc}")
    print(f"  模型 {model_path.name}")
    print("=" * 100)

    model = load_model(str(model_path))
    out = {"key": key, "n": n, "dim": dim, "ver": L["ver"],
           "data": data.name, "model": model_path.name, "modelMeta": model["meta"]}

    g5 = (gid.astype(np.int64) % 5) == 0     # ⚠️ 按**局号**取模，不是按样本下标
    for caliber, val_mask, train_mask, n_val_games in (
            (f"split-seed {SEED}（与模型同一份留出）",
             *nntrain_split(gid, SEED)[:2], 1500),
            ("gid%5==0（第二轮 F1/F2 的历史口径，按局号取模）",
             g5, ~g5, 2000)):
        ho = val_mask
        tr = train_mask
        # ⚠️ MLP 只有在「与它训练时同一份切分」的口径下才是留出数字。
        #    在一份**不同**的切分上评它，等于用 85% 训练过的局去考它 —— 是污染值。
        mlp_valid = caliber.startswith("split-seed")
        print()
        print("-" * 100)
        print(f"口径【{caliber}】：留出 {n_val_games} 局 / {int(ho.sum()):,} 条样本，"
              f"训练 {gmax + 1 - n_val_games:,} 局 / {int(tr.sum()):,} 条")
        print("-" * 100)

        res = {}

        # ---------- MLP ----------
        logits = np.empty(n, dtype=np.float32)
        CH = 32768
        for s in range(0, n, CH):
            e = min(n, s + CH)
            logits[s:e] = forward_logit(model, np.asarray(X[s:e, :dim], dtype=np.float32))
        ok_mlp = (logits > 0) == (y > 0.5)
        res["MLP"] = ok_mlp

        # ---------- F0 多数类 ----------
        guess = 1 if yl[tr].mean() >= 0.5 else 0
        ok_maj = (y > 0.5) == (guess == 1)
        res["多数类"] = ok_maj

        # ---------- F1 读领先（逻辑回归 6 参）----------
        F1_all = np.column_stack([np.ones(n, dtype=np.float32), feats])
        wF1 = fit_logistic(F1_all[tr], y[tr])
        okF1 = (logit(F1_all, wF1) > 0) == (y > 0.5)
        res["F1 读领先(6参)"] = okF1

        # ---------- F2l / F2r：各 28 参数 ----------
        ohl = onehot(pl, DECKS)
        ohr = onehot(pr, DECKS)
        F2l = np.column_stack([F1_all, ohl])
        F2r = np.column_stack([F1_all, ohr])
        wF2l = fit_logistic(F2l[tr], y[tr])
        wF2r = fit_logistic(F2r[tr], y[tr])
        res["F2l 读领先+左卡组(28参)"] = (logit(F2l, wF2l) > 0) == (y > 0.5)
        res["F2r 读领先+右卡组(28参)"] = (logit(F2r, wF2r) > 0) == (y > 0.5)
        del F2l, F2r

        # ---------- F2d：50 参数 ----------
        ohd = np.column_stack([ohl, ohr])
        F2d = np.column_stack([F1_all, ohd])
        wF2d = fit_logistic(F2d[tr], y[tr])
        okF2d = (logit(F2d, wF2d) > 0) == (y > 0.5)
        res["F2d 读领先+左右卡组(50参)"] = okF2d
        del ohl, ohr

        # ---------- F3：F2d + 回合 one-hot ----------
        oht = onehot(np.clip(turn, 2, TURN_MAX) - 2, TURN_MAX - 1)
        F3 = np.column_stack([F2d, oht])
        del oht
        wF3 = fit_logistic(F3[tr], y[tr])
        res[f"F3 F2d+回合({F3.shape[1]}参)"] = (logit(F3, wF3) > 0) == (y > 0.5)
        del F3, F2d, ohd

        # ---------- F2p：有序对位 one-hot（最大的一块，放最后以免峰值叠加）----------
        F2p = np.zeros((n, 6 + npair), dtype=np.float32)
        F2p[:, :6] = F1_all
        F2p[np.arange(n), 6 + pg] = 1.0
        wF2p = fit_logistic(F2p[tr], y[tr])
        okF2p = (logit(F2p, wF2p) > 0) == (y > 0.5)
        nz = int((np.bincount(pg, minlength=npair) > 0).sum())
        res[f"F2p 读领先+有序对位({6 + npair}列/其中{nz}列被用到)"] = okF2p
        del F2p

        # ---------- A：卡组对位多数类 ----------
        lab_of_game = np.zeros(gmax + 1, dtype=np.float64)
        lab_of_game[gid.astype(int)] = yl
        gs = np.arange(gmax + 1)
        gp = pair_of_game[gs]
        pair_lw = np.bincount(gp, weights=lab_of_game, minlength=npair)
        pair_n = np.bincount(gp, minlength=npair).astype(np.float64)
        # (a) 留一局（与第二轮 §2.3 的 75.29% 同口径）
        Lm = pair_lw[pg] - (y == 1)
        Rm = (pair_n[pg] - pair_lw[pg]) - (y == 0)
        okA_loo = ((Lm >= Rm) == (y == 1))
        # (b) 纯训练局估、留出局评（完全没有自证）
        is_val_game = np.zeros(gmax + 1, dtype=bool)
        is_val_game[np.unique(gid[ho].astype(int))] = True
        tr_games = ~is_val_game
        a_lw = np.bincount(gp[tr_games], weights=lab_of_game[tr_games], minlength=npair)
        a_n = np.bincount(gp[tr_games], minlength=npair).astype(np.float64)
        # 训练集里没出现过的对位 ⇒ 猜多数类（全局）
        fallback_left = lab_of_game[tr_games].mean() >= 0.5
        seen = a_n > 0
        a_left_win = np.where(seen, a_lw >= (a_n - a_lw), fallback_left)
        okA_ho = (a_left_win[pg] == (y == 1))
        res["A 对位多数类(逐样本留一·与二轮75.29%同口径)"] = okA_loo
        res["A 对位多数类(训练局估→留出局评)"] = okA_ho

        # ---------- A2/A3：只认单边卡组 ----------
        for nm, grp in (("A2 只认左卡组", pl), ("A3 只认右卡组", pr)):
            lw = np.bincount(grp[tr], weights=yl[tr], minlength=DECKS)
            nn_ = np.bincount(grp[tr], minlength=DECKS).astype(np.float64)
            lw_loo = np.bincount(grp, weights=yl, minlength=DECKS)
            n_loo = np.bincount(grp, minlength=DECKS).astype(np.float64)
            pick_tr = (lw >= (nn_ - lw))
            pick_loo = (lw_loo[grp] - (y == 1)) >= ((n_loo[grp] - lw_loo[grp]) - (y == 0))
            res[nm + "(训练局估→留出局评)"] = (pick_tr[grp] == (y == 1))
            res[nm + "(留一局)"] = (pick_loo == (y == 1))

        # ---------- B：网格最优线性读领先 ----------
        grid = [0, 1, 2, 3, 4, 6]
        best_tr = (-1, None)
        for w1 in grid:
            for w2 in grid:
                for w3 in (0, 1, 2):
                    for w4 in (0, 1):
                        for w5 in (0, 1):
                            for b in (-1.0, -0.5, 0.0, 0.5, 1.0):
                                s = (w1 * feats[tr, 0] + w2 * feats[tr, 1] + w3 * feats[tr, 2]
                                     + w4 * feats[tr, 3] + w5 * feats[tr, 4] + b)
                                c = int(((s > 0) == (y[tr] > 0.5)).sum())
                                if c > best_tr[0]:
                                    best_tr = (c, (w1, w2, w3, w4, w5, b))
        wts_tr = best_tr[1]
        sB = (wts_tr[0] * feats[:, 0] + wts_tr[1] * feats[:, 1] + wts_tr[2] * feats[:, 2]
              + wts_tr[3] * feats[:, 3] + wts_tr[4] * feats[:, 4] + wts_tr[5])
        res["B 网格读领先(训练局选权重)"] = ((sB > 0) == (y > 0.5))
        best_all = (-1, None)
        for w1 in grid:
            for w2 in grid:
                for w3 in (0, 1, 2):
                    for w4 in (0, 1):
                        for w5 in (0, 1):
                            for b in (-1.0, -0.5, 0.0, 0.5, 1.0):
                                s = (w1 * feats[:, 0] + w2 * feats[:, 1] + w3 * feats[:, 2]
                                     + w4 * feats[:, 3] + w5 * feats[:, 4] + b)
                                c = int(((s > 0) == (y > 0.5)).sum())
                                if c > best_all[0]:
                                    best_all = (c, (w1, w2, w3, w4, w5, b))
        wts_all = best_all[1]
        sBa = (wts_all[0] * feats[:, 0] + wts_all[1] * feats[:, 1] + wts_all[2] * feats[:, 2]
               + wts_all[3] * feats[:, 3] + wts_all[4] * feats[:, 4] + wts_all[5])
        res["B 网格读领先(全数据选权重·上界)"] = ((sBa > 0) == (y > 0.5))

        # ---------- 汇总 ----------
        print(f"  {'基线':<52}{'留出准确率':>13}{'留出条数':>11}")
        order = ["MLP", "多数类", "F1 读领先(6参)",
                 "F2l 读领先+左卡组(28参)", "F2r 读领先+右卡组(28参)",
                 "F2d 读领先+左右卡组(50参)", "F3 F2d+回合(105参)",
                 "A 对位多数类(逐样本留一·与二轮75.29%同口径)",
                 "A 对位多数类(训练局估→留出局评)",
                 "A2 只认左卡组(训练局估→留出局评)", "A2 只认左卡组(留一局)",
                 "A3 只认右卡组(训练局估→留出局评)", "A3 只认右卡组(留一局)",
                 "B 网格读领先(训练局选权重)", "B 网格读领先(全数据选权重·上界)"]
        order += [k for k in res if k not in order]
        for k in order:
            if k not in res:
                continue
            ok = res[k]
            a = 100.0 * ok[ho].mean()
            tag = k
            if k == "MLP" and not mlp_valid:
                tag = "MLP ⚠ 该口径下模型训练时见过这些局 ⇒ 污染值，不可用"
            print(f"  {tag:<52}{a:>12.2f}%{int(ho.sum()):>11,}")
        print(f"  {'（参考）':<52}{'':>13}")
        print(f"    MLP 全体（含训练样本，仅供与历史数字对齐）{100 * ok_mlp.mean():>10.2f}%")
        print(f"    B 全数据上界权重 = {wts_all}   训练局选出的权重 = {wts_tr}")

        # ---------- 按回合分层（留出局）----------
        print()
        print(f"  按回合分层（**只用留出局**；分档 = 编码字段 0 的 Turn，v{L['ver']}）")
        spec = [("MLP", "MLP" if mlp_valid else "MLP污染", "MLP"),
                ("A 对位多数类(逐样本留一·与二轮75.29%同口径)", "A对位", "A对位"),
                ("A 对位多数类(训练局估→留出局评)", "A对位HO", "A对位HO"),
                ("A2 只认左卡组(训练局估→留出局评)", "A2左", "A2左"),
                ("B 网格读领先(训练局选权重)", "B读领先", "B读领先"),
                ("F1 读领先(6参)", "F1", "F1"),
                ("F2d 读领先+左右卡组(50参)", "F2d50", "F2d50")]
        f2p_keys = [k for k in res if k.startswith("F2p")]
        for k in f2p_keys:
            spec.append((k, "F2p对位", "F2p对位"))
        cols = [s[0] for s in spec]
        hdr = f"    {'turn':>5}{'样本':>8}" + "".join(f"{s[1]:>10}" for s in spec) + f"{'多数类':>9}"
        print(hdr)
        strat = {}
        for t in range(int(turn.min()), int(turn.max()) + 1):
            m = ho & (turn == t)
            if m.sum() < 30:
                continue
            row = {s[0]: 100.0 * res[s[0]][m].mean() for s in spec}
            row["多数类"] = 100.0 * max(yl[m].mean(), 1 - yl[m].mean())
            row["n"] = int(m.sum())
            strat[t] = row
            print(f"    {t:>5}{int(m.sum()):>8,}" + "".join(f"{row[s[0]]:>9.1f}%" for s in spec)
                  + f"{row['多数类']:>8.1f}%")

        out[f"caliber_{caliber}"] = dict(
            n_val_games=n_val_games, n_val=int(ho.sum()), n_train=int(tr.sum()),
            acc={k: 100.0 * v[ho].mean() for k, v in res.items()},
            npar={k: None for k in res},
            strat=strat, cols=cols,
            mlp_all=100.0 * ok_mlp.mean(),
            weights_train=wts_tr, weights_all=wts_all)

    return out


def main():
    keys = sys.argv[1:] or ["effects"]
    if keys == ["all"]:
        keys = ["v0", "noeffects", "effects"]
    outs = [run(k) for k in keys]
    (REPO / "out/_effects-baselines.json").write_text(
        json.dumps(outs, ensure_ascii=False, indent=1), encoding="utf-8")
    print()
    print("JSON 汇总 → out/_effects-baselines.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
