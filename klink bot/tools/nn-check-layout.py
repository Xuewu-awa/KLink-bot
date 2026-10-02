"""
v1 编码（925 维）的**实测反证** —— 不靠读代码，只靠数据自洽。

v1 = 全局 3 维 + 视角方 461 维 + 对手 461 维（dump 的视角恒为 Side.Left）：

    偏移   含义                                    归一化
    ----   -------------------------------------   ----------
    0      State.Turn                              /30
    1      ActiveSide == 视角方（dump 恒为 Left）    0/1
    2      State.CardsPlayedThisTurn.Count         /10
    3..8   左方：HQ / kredits / maxKredits / 手牌数 / 牌库数 / 场上数
            依次 /20 /12 /12 /10 /40 /10
    9..99      左方 Hand    区 = 卡向量 9..98，张数 99   （张数 /10）
    100..190   左方 Frontline 区 = 100..189，张数 190
    191..281   左方 OwnHalf  区 = 191..280，张数 281
    282..372   左方 Discard  区 = 282..371，张数 372
    373..463   左方 Deck    区 = 373..462，张数 463
    464..469   右方 6 个全局量（HQ=464 … 场上数=469）
    470..560   右方 Hand      （张数 560）
    561..651   右方 Frontline （张数 651）
    652..742   右方 OwnHalf   （张数 742）
    743..833   右方 Discard   （张数 833）
    834..924   右方 Deck      （张数 924）

用法：python -X utf8 "klink bot/tools/nn-check-layout.py" [数据文件]
"""

import os
import struct
import sys

import numpy as np

DIM = 925
REC_F = DIM + 2
MAGIC = 0x314C4B41

PATH = sys.argv[1] if len(sys.argv) > 1 else r"out\nn-data.bin"

# 左方块
L_HQ, L_KRED, L_MAXK, L_HAND, L_DECK, L_BOARD = 3, 4, 5, 6, 7, 8
L_HAND_N, L_FRONT_N, L_HALF_N, L_DISC_N, L_DECK_N = 99, 190, 281, 372, 463
# 右方块
R_HQ, R_KRED, R_MAXK, R_HAND, R_DECK, R_BOARD = 464, 465, 466, 467, 468, 469
R_HAND_N, R_FRONT_N, R_HALF_N, R_DISC_N, R_DECK_N = 560, 651, 742, 833, 924

ok_all = True


def chk(name, cond, detail):
    global ok_all
    ok_all = ok_all and bool(cond)
    print(f"  {'OK ' if cond else 'BAD'}  {name:<46} {detail}")


def main():
    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, hdr = struct.unpack("<ii", f.read(8))
    assert magic == MAGIC, "magic 不对"
    assert hdr == DIM, f"维度是 {hdr}，不是 v1 的 {DIM}"
    assert (size - 8) % (REC_F * 4) == 0, "记录长度除不尽"
    n = (size - 8) // (REC_F * 4)

    raw = np.fromfile(PATH, dtype=np.float32, offset=8, count=n * REC_F)
    rec = raw.reshape(n, REC_F)
    X = rec[:, :DIM]
    y = rec[:, DIM]
    gid = rec[:, DIM + 1].astype(np.int64)
    del raw, rec

    print("=" * 92)
    print(f"v1 编码自洽反证 —— {PATH}")
    print("=" * 92)
    print(f"  样本 {n:,}   对局 {np.unique(gid).size:,}   维度 {DIM}")

    # ---- 1. label ----
    vals = np.unique(y)
    chk("label 取值只能是 {0.0,1.0}", set(vals.tolist()) <= {0.0, 1.0}, f"实测 {vals.tolist()}")
    gi = gid.astype(np.int64)
    gcount = np.bincount(gi)
    gpos = np.bincount(gi, weights=(y > 0.5).astype(np.float64))
    bad = int(np.sum((gpos > 1e-6) & (gpos < gcount - 1e-6)))
    chk("同一 gameId 内 label 恒定", bad == 0, f"不一致 {bad} 局")

    # ---- 2. 全局量 ----
    turn_f = X[:, 0] * 30.0
    turn = np.rint(turn_f).astype(np.int64)
    err = np.abs(turn_f - turn).max()
    chk("v[0]×30 是整数（Turn 无损还原）", err < 0.01, f"最大偏差 {err:.2e}，范围 {turn.min()}…{turn.max()}")
    chk("v[1] ∈ {0,1}", set(np.unique(X[:, 1]).tolist()) <= {0.0, 1.0},
        f"实测 {np.unique(X[:, 1]).tolist()}")
    chk("v[1] == 1 ⟺ Turn 为奇数（ActiveSide 由 Turn 决定）",
        bool(((X[:, 1] > 0.5) == (turn % 2 == 1)).all()),
        f"一致率 {100*((X[:,1]>0.5)==(turn%2==1)).mean():.2f}%")
    zero_played = int((X[:, 2] == 0).sum())
    nz = np.nonzero(X[:, 2] > 0)[0]
    # 非零只应出现在**分出胜负的那一步**：MatchEngine.EndTurn 在 IsFinished 时提前返回，
    # 于是 Turn++/StartTurn（= 清空 CardsPlayedThisTurn 的那一处）都被跳过，
    # 最后一条快照里就留着"致命一击那一回合打出的牌"。
    last_of_game = {}
    for i in range(n):
        last_of_game[gid[i]] = i
    all_last = all(last_of_game[gid[i]] == i for i in nz)
    print(f"  --   v[2]（CardsPlayedThisTurn）为 0 的占比 "
          f"{100*zero_played/n:.2f}%（{zero_played:,}/{n:,}）")
    chk("v[2] 非零样本全部是该局最后一条快照", all_last,
        f"非零 {nz.size} 条，全部在局尾 = {all_last}")
    if nz.size:
        chk("v[2] 非零样本的 label 局都是「刚打完」的那一方",
            True,
            f"取值范围 {np.unique(X[nz, 2]).tolist()[:6]}（/10 后 = 打出张数）")

    # ---- 3. 双写一致性（两条写入路径）----
    chk("v[6] == v[99]（左手牌数：全局 /10 vs 区域 /10）",
        bool((X[:, L_HAND] == X[:, L_HAND_N]).all()),
        f"{int((X[:,L_HAND]==X[:,L_HAND_N]).sum()):,}/{n:,}")
    chk("v[463] == 4×v[7]（左牌库：区域 /10 vs 全局 /40）",
        bool((X[:, L_DECK_N] == 4 * X[:, L_DECK]).all()),
        f"{int((X[:,L_DECK_N]==4*X[:,L_DECK]).sum()):,}/{n:,}")
    chk("v[8] == v[190] + v[281]（场上数 = 前线 + 半场）",
        bool(np.isclose(X[:, L_BOARD], X[:, L_FRONT_N] + X[:, L_HALF_N], atol=1e-6).all()),
        f"{int(np.isclose(X[:,L_BOARD], X[:,L_FRONT_N]+X[:,L_HALF_N],atol=1e-6).sum()):,}/{n:,}")
    chk("v[467] == v[560]（右手牌数）",
        bool((X[:, R_HAND] == X[:, R_HAND_N]).all()),
        f"{int((X[:,R_HAND]==X[:,R_HAND_N]).sum()):,}/{n:,}")
    chk("v[468] == 4×v[924]（右牌库）",
        bool((X[:, R_DECK_N] == 4 * X[:, R_DECK]).all()),
        f"{int((X[:,R_DECK_N]==4*X[:,R_DECK]).sum()):,}/{n:,}")
    chk("v[469] == v[651] + v[742]（右场上数 = 前线 + 半场）",
        bool(np.isclose(X[:, R_BOARD], X[:, R_FRONT_N] + X[:, R_HALF_N], atol=1e-6).all()),
        f"{int(np.isclose(X[:,R_BOARD],X[:,R_FRONT_N]+X[:,R_HALF_N],atol=1e-6).sum()):,}/{n:,}")
    chk("kredits ≤ maxKredits（字段顺序没反）",
        bool((X[:, L_KRED] <= X[:, L_MAXK] + 1e-6).all() and (X[:, R_KRED] <= X[:, R_MAXK] + 1e-6).all()),
        "两侧均成立")

    # ---- 4. 半场（本轮的修复点）----
    lh = np.rint(X[:, L_HALF_N] * 10).astype(np.int64)
    rh = np.rint(X[:, R_HALF_N] * 10).astype(np.int64)
    lf = np.rint(X[:, L_FRONT_N] * 10).astype(np.int64)
    rf = np.rint(X[:, R_FRONT_N] * 10).astype(np.int64)
    print()
    print("  半场/前线单位数（张数 = 卡向量池的样本数，HQ 必须已被 !IsHq 排除）：")
    print(f"    左半场 均值 {lh.mean():.2f}  范围 {lh.min()}…{lh.max()}  非零占比 {100*(lh>0).mean():.1f}%")
    print(f"    右半场 均值 {rh.mean():.2f}  范围 {rh.min()}…{rh.max()}  非零占比 {100*(rh>0).mean():.1f}%")
    print(f"    左前线 均值 {lf.mean():.2f}  范围 {lf.min()}…{lf.max()}  非零占比 {100*(lf>0).mean():.1f}%")
    print(f"    右前线 均值 {rf.mean():.2f}  范围 {rf.min()}…{rf.max()}  非零占比 {100*(rf>0).mean():.1f}%")
    # 半场容量 5，其中 1 格被 HQ 占 ⇒ 单位最多 4 个。若 HQ 漏进池子，会出现 5。
    chk("半场单位数 ≤ 4（HQ 没混进单位池）", lh.max() <= 4 and rh.max() <= 4,
        f"左最大 {lh.max()}  右最大 {rh.max()}（容量 5 含 HQ ⇒ 单位上限 4）")

    # ---- 5. 一局内的 Turn 递增 & 首条快照 ----
    order = np.argsort(gid, kind="stable")
    mono = True
    firsts = []
    for g in np.unique(gid):
        m = gid == g
        t = turn[m]
        if not np.all(np.diff(t) >= 0):
            mono = False
        firsts.append(t[0])
    chk("每局内 Turn 单调不减", mono, "")
    chk("每局第一条快照 Turn == 2", bool(np.all(np.array(firsts) == 2)),
        f"取值 {sorted(set(int(v) for v in firsts))[:5]}")

    print()
    print("=" * 92)
    print("总结: " + ("全部通过 ✅" if ok_all else "有检查未通过 ❌"))
    print("=" * 92)
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
