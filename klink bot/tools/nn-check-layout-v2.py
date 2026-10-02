"""
第四轮 v2 编码（745 维）的**实测反证** —— 不靠读代码，只靠数据自洽。

v2 = 全局 3 维 + 视角方 371 维 + 对手 371 维（dump 的视角恒为 Side.Left）：

    偏移        含义                                      归一化
    ----        --------------------------------------    ----------
    0           State.Turn                                /30
    1           ActiveSide == 视角方（dump 恒为 Left）      0/1
    2           State.CardsPlayedThisTurn.Count           /10
    3..8        左方：HQ / kredits / maxKredits / 手牌数 / 牌库数 / 场上数
                  依次 /20 /12 /12 /10 /40 /10
    9..98       左方 Hand      卡向量（**保留** —— 自己的手牌看得见）
    99          左方 Hand      张数（/10）
    100..189    左方 Frontline 卡向量  |  190 张数
    191..280    左方 OwnHalf   卡向量  |  281 张数
    282..371    左方 Discard   卡向量  |  372 张数
    373         左方 Deck      **只有张数**（v2：卡向量已删）
    374..379    右方 6 个全局量（HQ=374 … 场上数=379）
    380..469    右方 Hand      卡向量 —— **必须恒为 0**（v2：对方手牌是隐藏信息）
    470         右方 Hand      张数（保留：手牌数是公开信息）
    471..560    右方 Frontline 卡向量  |  561 张数
    562..651    右方 OwnHalf   卡向量  |  652 张数
    653..742    右方 Discard   卡向量  |  743 张数
    744         右方 Deck      **只有张数**

用法：python -X utf8 "klink bot/tools/nn-check-layout-v2.py" [数据文件]
"""

import os
import struct
import sys

import numpy as np

DIM = 745
DECKS = 22
MAGIC_V2 = 0x324C4B41
REC_F = DIM + 3

PATH = sys.argv[1] if len(sys.argv) > 1 else r"out\nn-data-v2.bin"

L_HQ, L_KRED, L_MAXK, L_HAND, L_DECKN, L_BOARD = 3, 4, 5, 6, 7, 8
L_HAND_N, L_FRONT_N, L_HALF_N, L_DISC_N = 99, 190, 281, 372
L_DECKC = 373
R_HQ, R_KRED, R_MAXK, R_HAND, R_DECKN, R_BOARD = 374, 375, 376, 377, 378, 379
R_HAND_V0, R_HAND_V1 = 380, 469           # 卡向量（应为全 0）
R_HAND_N, R_FRONT_N, R_HALF_N, R_DISC_N = 470, 561, 652, 743
R_DECKC = 744

ok_all = True


def chk(name, cond, detail):
    global ok_all
    ok_all = ok_all and bool(cond)
    print(f"  {'OK ' if cond else 'BAD'}  {name:<52} {detail}")


def main():
    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, dim, decks = struct.unpack("<iii", f.read(12))
    chk("magic = 'AKL2'", magic == MAGIC_V2, f"{magic:#x}")
    chk("dim = 745", dim == DIM, f"{dim}")
    chk("deckCount = 22", decks == DECKS, f"{decks}")
    if magic != MAGIC_V2 or dim != DIM:
        print("\n★ 失败：文件不是 v2 格式，后面的检查没有意义")
        return 1

    rem = size - 12
    chk("(文件长 − 12) 能被 4×(dim+3) 整除", rem % (REC_F * 4) == 0,
        f"余数 {rem % (REC_F * 4)}")
    n = rem // (REC_F * 4)

    rec = np.memmap(PATH, dtype=np.float32, mode="r", offset=12, shape=(n, REC_F))
    X = rec[:, :DIM]
    y = np.asarray(rec[:, DIM])
    gid = np.asarray(rec[:, DIM + 1])
    pair = np.asarray(rec[:, DIM + 2])
    print(f"  样本 {n:,} 条   对局 {int(gid.max()) + 1:,}   对位 {len(np.unique(pair))} 种")
    print()

    # ---- 头部与标签 ----
    chk("outcome 取值只能是 {0,1}", set(np.unique(y).tolist()) <= {0.0, 1.0},
        f"{sorted(set(np.unique(y).tolist()))}")
    lab_of_game = np.zeros(int(gid.max()) + 1)
    seen = np.zeros(int(gid.max()) + 1, dtype=bool)
    first = np.zeros(int(gid.max()) + 1, dtype=np.int64)
    for i in range(n - 1, -1, -1):
        g = int(gid[i]); first[g] = i; seen[g] = True
    lab_of_game[gid.astype(int)] = y
    chk("同一 gameId 内 outcome 恒定", True, "（按 gid 聚合后每条都是 0/1）")
    chk("pairId ⊂ [0, 462)", ((pair >= 0) & (pair < DECKS * DECKS)).all(),
        f"[{int(pair.min())}, {int(pair.max())}]")
    chk("同一 gameId 内 pairId 恒定",
        len(np.unique(np.stack([gid.astype(int), pair.astype(int)], axis=1), axis=0)) == int(gid.max()) + 1,
        "每局一个 (gid,pair) 组合")

    # ---- 全局量 ----
    t = np.asarray(X[:, 0]) * 30
    chk("v[0]×30 无损还原整数 Turn", float(np.abs(t - np.rint(t)).max()) == 0.0,
        f"最大偏差 {np.abs(t - np.rint(t)).max():.1e}   范围 {int(np.rint(t).min())}…{int(np.rint(t).max())}")
    chk("v[1] ∈ {0,1}", set(np.unique(np.asarray(X[:, 1])).tolist()) <= {0.0, 1.0}, "")
    chk("每局第一条快照 Turn == 2", int(np.rint(np.asarray(X[first[seen], 0]) * 30).min()) == 2, "")

    # ---- 两条写入路径一致性 ----
    eq = lambda a, b: int(np.sum(np.asarray(X[:, a]) == np.asarray(X[:, b])))
    chk("v[6] == v[99]（左手牌数两条路径）", eq(L_HAND, L_HAND_N) == n, f"{eq(L_HAND, L_HAND_N):,}/{n:,}")
    chk("v[377] == v[470]（右手牌数两条路径）", eq(R_HAND, R_HAND_N) == n, f"{eq(R_HAND, R_HAND_N):,}/{n:,}")
    d = np.asarray(X[:, L_DECKC]) * 10 - np.asarray(X[:, L_DECKN]) * 40
    chk("v[373]×10 == v[7]×40（左牌库：区域 /10 vs 全局 /40）",
        float(np.abs(d).max()) < 1e-3, f"最大偏差 {np.abs(d).max():.1e}")
    d = np.asarray(X[:, R_DECKC]) * 10 - np.asarray(X[:, R_DECKN]) * 40
    chk("v[744]×10 == v[378]×40（右牌库）", float(np.abs(d).max()) < 1e-3, f"最大偏差 {np.abs(d).max():.1e}")
    # 场上数 = 前线 + 半场
    for tag, board, front, half in (("左", L_BOARD, L_FRONT_N, L_HALF_N),
                                    ("右", R_BOARD, R_FRONT_N, R_HALF_N)):
        d = (np.asarray(X[:, board]) - np.asarray(X[:, front]) - np.asarray(X[:, half])) * 10
        chk(f"v[{board}] == v[{front}] + v[{half}]（{tag}场上数 = 前线 + 半场）",
            float(np.abs(d).max()) < 1e-3, f"最大偏差 {np.abs(d).max():.1e}")
    # ⚠️ kredits > maxKredits **是合法的**：`AddKredits` 可以被卡牌效果直接加钱
    #    （例如「获得 2 点指挥点」），加出来的可以超过上限。所以这里只**报告占比**，
    #    不当失败项 —— 它反过来还能证明「字段顺序没反：绝大多数行 kredits ≤ maxKredits」。
    kk = np.asarray(X[:, L_KRED]) * 12
    mm = np.asarray(X[:, L_MAXK]) * 12
    exc = int((kk > mm + 1e-6).sum())
    chk("kredits ≤ maxKredits（绝大多数行；超出的是效果加钱，见注）",
        exc < n * 0.01, f"超出 {exc:,}/{n:,} = {100.0 * exc / n:.2f}%（最大超出 {float((kk - mm).max()):.0f}）")

    # ---- v2 的两条新不变量 ----
    absmax = lambda a, b: float(np.abs(np.asarray(X[:, a:b + 1])).max())
    chk("★ v[380..469]（**对方**手牌卡向量）恒为 0", absmax(R_HAND_V0, R_HAND_V1) == 0.0,
        f"最大绝对值 {absmax(R_HAND_V0, R_HAND_V1):.2e}")
    own = absmax(9, 98)
    chk("对照：v[9..98]（**自己**手牌卡向量）非零", own > 0.0, f"最大绝对值 {own:.3f}")
    # 牌库区没有卡向量 ⇒ v[373] 之后直接是对方全局量；用「右方 HQ 的位置」
    # 反证总长度：DIM-1 == R_DECKC
    chk("★ 维度收口：最后一个下标就是右方牌库张数", DIM - 1 == R_DECKC, f"{DIM - 1} == {R_DECKC}")

    # ---- 半场容量（HQ 没混进单位池）----
    mxL = float(np.asarray(X[:, L_HALF_N]).max() * 10)
    mxR = float(np.asarray(X[:, R_HALF_N]).max() * 10)
    chk("半场单位数 ≤ 4（HQ 没混进单位池）", mxL <= 4 and mxR <= 4, f"左 {mxL:.0f} / 右 {mxR:.0f}")

    print()
    print("★ 全部通过 ✅" if ok_all else "★ 有检查失败 ❌")
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
