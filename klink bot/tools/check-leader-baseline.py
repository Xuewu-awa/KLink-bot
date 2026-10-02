"""
平凡基线检查：NN 的 77.4% 到底是「预测谁会赢」还是「读当前谁领先」？

═══════════════════════════════════════════════════════════════════════════
⚠️ 上一版这个脚本的结论（32.1% 基线 / maxKredits=1 档 0.0%）是错的，
   错因**不是**「全局量字段顺序假设错了」—— 偏移 0/370 取到的**确实**是双方 HQ。
   真正的错因是读数方式：

     1. label 字段是 **float32**（`BinaryWriter.Write(float)`，见
        tools/NNTrain/Program.cs:139），脚本却用 `<i` 按 int32 读。
        float 1.0f 的位模式是 0x3F800000 = 1065353216，于是
        `pred == label` 只在 label 位模式 == 0（= 右方胜）时才可能成立
        ⇒ 只统计了「预测右方胜」那一半样本，另一半全被判错。
        **那不是准确率，是两个数偶然相乘。**
     2. 分档表的分母用了整档样本数，而分子把「HQ 打平」的样本剔除了
        ⇒ 第一回合全员打平 ⇒ 分子 0 / 分母 2 万 ⇒ 显示 0.0% 的假象。

   本版按 StateEncoder.cs 的**真实字段顺序**取数，并逐条自检（见 "自检" 段）。
═══════════════════════════════════════════════════════════════════════════

二进制格式（tools/NNTrain/Program.cs:90-142 + 436-454）：
  头 8 字节 = magic(int32, 0x314C4B41) + dim(int32, 740)
  每条 = float32[740] + float32 label + float32 gameId   = 2968 字节
  label  = 1.0 左方(perspective)胜 / 0.0 右方胜（未分胜负的**整局丢掉**，
           见 Program.cs:134，所以文件里不会出现 0.5）

编码布局（src/KLink.Bot/NN/StateEncoder.cs:107-143）：
  视角方在前 370 维、对手在后 370 维；训练时 perspective **恒为 Side.Left**
  （tools/NNTrain/Program.cs:125），label 也恒为「左方是否获胜」（同文件 129-131）
  ⇒ 全文件的「前半」= 左方，「后半」= 右方，没有右视角样本。

  单方 370 维内部（相对本方的偏移）：
     0  hqDef/20
     1  kredits/12
     2  maxKredits/12
     3  handCount/10
     4  deckCount/40
     5  boardCount/10
     6..95    Hand 区 池化卡向量 90 维(均值)
     96       Hand 区张数/10
     97..186  BoardFrontline 区 池化卡向量 90 维
     187      BoardFrontline 区张数/10
     188..277 Discard 区 池化卡向量 90 维
     278      Discard 区张数/10
     279..368 Deck 区 池化卡向量 90 维
     369      Deck 区张数/10

用法：
  python "klink bot/tools/check-leader-baseline.py" [数据文件]
  默认数据文件 = out/nn-data.bin
"""

import array
import os
import struct
import sys
from collections import defaultdict

DIM = 740
REC = DIM * 4 + 8
MAGIC = 0x314C4B41

# ---- 绝对偏移（左方 = 视角方 = 前半）----
L_HQ, L_KRED, L_MAXK, L_HAND, L_DECK, L_BOARD = 0, 1, 2, 3, 4, 5
L_HAND_N, L_FRONT_N, L_DISC_N, L_DECK_N = 96, 187, 278, 369
# ---- 右方 = 对手 = 后半（+370）----
R_HQ, R_KRED, R_MAXK, R_HAND, R_DECK, R_BOARD = 370, 371, 372, 373, 374, 375
R_HAND_N, R_FRONT_N, R_DISC_N, R_DECK_N = 466, 557, 648, 739

BASELINES = ("left", "right", "hq", "board", "hand", "hq_board", "hq_rev", "board_rev")
# hq_board = HQ → 场上 → 手牌 依次打破平局（三者全平才不可判）

PATH = sys.argv[1] if len(sys.argv) > 1 else r"out\nn-data.bin"


def leader(a, b, value_if_tie):
    """返回 (预测, 是否可判)。a > b ⇒ 猜左(1)；a < b ⇒ 猜右(0)；平 ⇒ value_if_tie。"""
    if a > b:
        return 1, True
    if a < b:
        return 0, True
    return value_if_tie, False


def main():
    if not os.path.exists(PATH):
        print(f"找不到 {PATH}（请在仓库根目录运行）")
        return 1

    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, hdr_dim = struct.unpack("<ii", f.read(8))
        print("=" * 78)
        print("格式自检")
        print("=" * 78)
        print(f"  文件           {PATH}  {size:,} B")
        print(f"  magic          {magic:#x}  (期望 {MAGIC:#x})  "
              f"{'OK' if magic == MAGIC else 'BAD'}")
        print(f"  头里的维度     {hdr_dim}  {'OK' if hdr_dim == DIM else 'BAD'}")
        rest = size - 8
        n = rest // REC
        print(f"  每条 {REC} B ⇒ 样本数 {n:,}，余数 {rest % REC}  "
              f"{'OK' if rest % REC == 0 else 'BAD'}")
        if magic != MAGIC or hdr_dim != DIM or rest % REC:
            print("  格式不符，终止")
            return 1

        # [正确数, 可判数, 总数]
        base = {k: [0, 0, 0] for k in BASELINES}

        def new_bucket():
            return {"n": 0, "leftwin": 0, "b": {k: [0, 0] for k in BASELINES}}

        by_turn = defaultdict(new_bucket)    # turn = 左maxK + 右maxK
        by_maxk = defaultdict(new_bucket)    # 左方 maxKredits

        chk = defaultdict(int)
        label_vals = defaultdict(int)
        games = []            # [gid, first_rec, last_rec, label]
        prev_gid = None
        lwins = 0

        CHUNK = 8192
        done = 0
        while done < n:
            m = min(CHUNK, n - done)
            buf = f.read(m * REC)
            for i in range(m):
                off = i * REC
                v = array.array("f")
                v.frombytes(buf[off:off + DIM * 4])
                label, gid = struct.unpack_from("<ff", buf, off + DIM * 4)
                idx = done + i

                lhq, rhq = v[L_HQ], v[R_HQ]
                lbd, rbd = v[L_BOARD], v[R_BOARD]
                lhd, rhd = v[L_HAND], v[R_HAND]
                lk = int(round(v[L_MAXK] * 12))
                rk = int(round(v[R_MAXK] * 12))

                # ---------------- 自检 ----------------
                label_vals[label] += 1
                chk["hq_left_ahead" if lhq > rhq else
                    "hq_right_ahead" if lhq < rhq else "hq_tie"] += 1
                if abs(v[L_HAND_N] - lhd) < 1e-6:
                    chk["hand_count_matches_zone"] += 1
                # 牌库：全局槽位是 deckCount/**40**，Deck 区计数是 count/**10**
                # ⇒ 正确关系是 v[369] == 4 * v[4]（不是相等！）
                if abs(v[L_DECK_N] - 4 * v[L_DECK]) < 1e-6:
                    chk["deck_count_matches_zone"] += 1
                # 场上：全局 boardCount 走 st.Board() = IsBoard() 全部（前线**+半场**），
                # 而 BoardFrontline 区计数只数前线（Enums.cs:86-87、GameState.cs:199）
                # ⇒ 关系是「全局 >= 前线」，差值 = 停在自己半场(支援线)的单位数
                off_l = round((lbd - v[L_FRONT_N]) * 10)
                off_r = round((rbd - v[R_FRONT_N]) * 10)
                if off_l >= 0:
                    chk["board_ge_frontline_l"] += 1
                if off_r >= 0:
                    chk["board_ge_frontline_r"] += 1
                chk["halfboard_units_l"] += off_l
                chk["halfboard_units_r"] += off_r
                if abs(v[R_HAND_N] - rhd) < 1e-6:
                    chk["r_hand_count_matches_zone"] += 1
                if abs(v[R_DECK_N] - 4 * v[R_DECK]) < 1e-6:
                    chk["r_deck_count_matches_zone"] += 1
                # kredits 必须 <= maxKredits（否则说明字段 1/2 顺序反了）
                if round(v[L_KRED] * 12) <= lk and round(v[R_KRED] * 12) <= rk:
                    chk["kredits_le_maxk"] += 1
                # maxKredits 必须落在 1..12 的整数梯级上
                if 1 <= lk <= 12 and 1 <= rk <= 12:
                    chk["maxk_on_ladder"] += 1
                # 第一条快照：双方都已行动过 ⇒ 双方 maxK 都是 1（turn=2）
                if lk == 1 and rk == 1:
                    chk["first_snapshot"] += 1
                    if lhq == 1.0 and rhq == 1.0:
                        chk["first_both_hq_20"] += 1
                chk["min_turn"] = min(chk["min_turn"] or 99, lk + rk)
                if gid != prev_gid:
                    games.append([gid, idx, idx, label])
                    prev_gid = gid
                else:
                    games[-1][2] = idx
                    if games[-1][3] != label:
                        chk["label_not_constant_in_game"] += 1

                t = 1 if label == 1.0 else 0
                if t == 1:
                    lwins += 1

                # ---------------- 基线 ----------------
                hq_p, hq_d = leader(lhq, rhq, 1)
                bd_p, bd_d = leader(lbd, rbd, 1)
                hd_p, hd_d = leader(lhd, rhd, 1)
                # HQ → 场上 → 手牌 依次打破平局
                if hq_d:
                    cas_p, cas_d = hq_p, True
                elif bd_d:
                    cas_p, cas_d = bd_p, True
                elif hd_d:
                    cas_p, cas_d = hd_p, True
                else:
                    cas_p, cas_d = 1, False

                preds = {
                    "left": (1, True),
                    "right": (0, True),
                    "hq": (hq_p, hq_d),
                    "board": (bd_p, bd_d),
                    "hand": (hd_p, hd_d),
                    "hq_board": (cas_p, cas_d),
                    "hq_rev": (1 - hq_p, hq_d),
                    "board_rev": (1 - bd_p, bd_d),
                }
                for k, (p, d) in preds.items():
                    s = base[k]
                    s[2] += 1
                    if d:
                        s[1] += 1
                    if p == t:
                        s[0] += 1

                # ---------------- 分层 ----------------
                for tbl, key in ((by_turn, lk + rk), (by_maxk, lk)):
                    sc = tbl[key]
                    sc["n"] += 1
                    if t == 1:
                        sc["leftwin"] += 1
                    for k, (p, d) in preds.items():
                        # 分档表统一用「总数口径」：不可判的按猜左算【已在 preds 里】
                        sc["b"][k][1] += 1
                        if p == t:
                            sc["b"][k][0] += 1
            done += m

    pct = lambda c, t: f"{100.0*c/t:.2f}%" if t else "—"   # noqa: E731

    # ---------------- 自检输出 ----------------
    print()
    print("=" * 78)
    print("自检（用一致性反证偏移取对了没有）")
    print("=" * 78)
    ok_labels = set(label_vals) <= {0.0, 1.0}
    print(f"  label 取值集合            {dict(label_vals)}  "
          f"{'OK 只有 0/1' if ok_labels else 'BAD'}")
    print(f"  每局内 label 恒定         不一致次数 {chk['label_not_constant_in_game']}  "
          f"{'OK' if chk['label_not_constant_in_game'] == 0 else 'BAD'}")
    print(f"  对局数（记录按局连续）    {len(games):,}")
    print("  ── 偏移交叉验证（同一物理量有两条写入路径，关系必须逐样本成立）──")
    for name, key in (("左手牌数 v[3]   == Hand区计数 v[96]（都 /10）", "hand_count_matches_zone"),
                      ("左牌库数 v[369] == 4 * v[4]（区 /10，全局 /40）", "deck_count_matches_zone"),
                      ("右手牌数 v[373] == v[466]", "r_hand_count_matches_zone"),
                      ("右牌库数 v[739] == 4 * v[374]", "r_deck_count_matches_zone"),
                      ("kredits<=maxKredits（字段 1/2 顺序）", "kredits_le_maxk"),
                      ("maxKredits 落在 1..12 整数梯级", "maxk_on_ladder"),
                      ("左场上数 v[5]  >= 前线区计数 v[187]", "board_ge_frontline_l"),
                      ("右场上数 v[375] >= 前线区计数 v[557]", "board_ge_frontline_r")):
        print(f"  {name:<44} 成立 {chk[key]:>7,}/{n:,}  "
              f"{'OK' if chk[key] == n else 'MISMATCH'}")
    print("  ── 被 4 个区域漏掉的单位（停在自己半场/支援线的单位）──")
    print(f"  左方半场单位总数 {chk['halfboard_units_l']:,}，"
          f"平均每样本 {chk['halfboard_units_l']/n:.3f} 个")
    print(f"  右方半场单位总数 {chk['halfboard_units_r']:,}，"
          f"平均每样本 {chk['halfboard_units_r']/n:.3f} 个")
    print("  ⇒ 这些单位的**卡向量不进入任何区域**（4 区只有 Hand/BoardFrontline/"
          "Discard/Deck），只有全局计数 v[5]/v[375] 数到它们。")
    print("  ── 第一条快照（双方各行动一次后，turn=2）──")
    print(f"  样本 {chk['first_snapshot']:,}（原始回合号最小 = {chk['min_turn']}），"
          f"其中双方 HQ 都是 20/20 的 {chk['first_both_hq_20']:,}  "
          f"{'OK' if chk['first_both_hq_20'] == chk['first_snapshot'] else 'note'}")

    # 终局反证：每局最后一条快照，败方 HQ 必须 <= 0（MatchEngine.cs:758-760）
    print("  ── 终局反证（label ↔ 左右 的独立验证）──")
    ok_l = ok_r = bad = 0
    with open(PATH, "rb") as f:
        for gid, _first, last, label in games:
            f.seek(8 + last * REC)
            v = array.array("f")
            v.frombytes(f.read(DIM * 4))
            lhq, rhq = v[L_HQ] * 20, v[R_HQ] * 20
            if label == 1.0:
                ok_l += 1 if (lhq > 0 and rhq <= 0) else 0
                bad += 0 if (lhq > 0 and rhq <= 0) else 1
            else:
                ok_r += 1 if (rhq > 0 and lhq <= 0) else 0
                bad += 0 if (rhq > 0 and lhq <= 0) else 1
    print(f"  label=1(左胜) 且终局 左HQ>0、右HQ<=0 : {ok_l:,}")
    print(f"  label=0(右胜) 且终局 右HQ>0、左HQ<=0 : {ok_r:,}")
    print(f"  反证失败                              : {bad:,}")
    print(f"  ⇒ label=1 ⟺ 左方 HQ 未破，一致率 "
          f"{100*(ok_l+ok_r)/max(1,len(games)):.2f}%")

    # ---------------- 基线表 ----------------
    print()
    print("=" * 78)
    print(f"平凡基线（全部 {n:,} 样本 —— 与模型 verify 的『全体准确率』同一集合）")
    print("=" * 78)
    print(f"  左方胜率（多数类基线）= {100*lwins/n:.2f}%   "
          f"（左胜 {lwins:,} / 右胜 {n-lwins:,}）")
    print(f"  HQ 领先分布：左领先 {chk['hq_left_ahead']:,} / 右领先 "
          f"{chk['hq_right_ahead']:,} / 打平 {chk['hq_tie']:,}")
    print()
    print(f"  {'基线':<32}{'总数口径':>11}{'非平局口径':>13}")
    names = {
        "left": "永远猜左方胜（多数类）",
        "right": "永远猜右方胜",
        "hq": "双方 HQ 高者胜（平局猜左）",
        "board": "场上单位多者胜（平局猜左）",
        "hand": "手牌多者胜（平局猜左）",
        "hq_board": "HQ→场上→手牌 依次破平局",
        "hq_rev": "反向：HQ 低者胜（对照）",
        "board_rev": "反向：场上少者胜（对照）",
    }
    for k in BASELINES:
        c, d, tt = base[k]
        print(f"  {names[k]:<32}{pct(c, tt):>11}{pct(c, d):>13}")

    # ---------------- 分层表 ----------------
    def table(tbl, title, fmt):
        print()
        print("=" * 78)
        print(title)
        print("=" * 78)
        print(f"  {fmt:>5}{'样本':>9}{'左胜率':>9}{'HQ':>8}{'场上':>8}"
              f"{'手牌':>8}{'猜左':>8}{'HQ→场':>8}")
        for k in sorted(tbl):
            s = tbl[k]
            if s["n"] < 100:
                continue
            row = [100 * s["leftwin"] / s["n"]]
            for key in ("hq", "board", "hand", "left", "hq_board"):
                c, tt = s["b"][key]
                row.append(100 * c / tt if tt else 0.0)
            print(f"  {k:>5}{s['n']:>9,}" + "".join(f"{x:>7.1f}%" for x in row))

    table(by_turn, "按回合分层（turn = 左maxKredits + 右maxKredits）", "turn")
    table(by_maxk, "按左方 maxKredits 分层（旧版口径，1=左方第 1 回合）", "maxK")

    print()
    print("注：turn 是**代理量**，数据文件里不存回合号。左方 maxKredits = 左方已开始的")
    print("    回合数（MatchEngine.cs:203 每人自己回合 +1，上限 12），右方同理 ⇒")
    print("    turn = 左maxK + 右maxK 精确等于全局回合号，直到有人触到 12 上限后饱和在 24。")
    print("    记录按「左方回合、右方回合」交替产生（Program.cs:117-127 每回合一条快照）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
