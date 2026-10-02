"""
「对位随机化之后 A 基线为什么没掉」的排查。

结论要在报告里说清楚：随机化**确实生效了**（462 种有序对位、每对位 8~38 局，
且卡组身份能从编码里被反证出来），但 A 基线仍然在 75% —— 因为
**这个引擎里 22 套卡组的强弱差距极大**，「认出是哪一对卡组」本身就足以预测胜负。
本脚本把这件事拆成可检验的几块：

  1. 随机化生效证据：每对位局数分布、有无自配对、清单能否被编码反证
  2. **互补性检验**：P(A 左 vs B 右) + P(B 左 vs A 右) 是否 ≈ 1
     （=「胜负由卡组对决定、与左右无关」；若明显偏离 1 说明还有左右/先手效应）
  3. 每套卡组的边际胜率（在随机对手、随机左右下的真实强弱）
  4. **分半检验**（无自证、比留一局更保守）：一半局估每对位胜率，另一半局上评估
  5. 只用「左卡组」「右卡组」「两者」的边际先验准确率

用法：python -X utf8 "klink bot/tools/nn-matchup-probe.py" [数据文件] [清单文件]
"""

import collections
import json
import os
import struct
import sys

import numpy as np

MAGIC = 0x314C4B41
DECKS = 22

PATH = sys.argv[1] if len(sys.argv) > 1 else r"out\nn-data.bin"
MANIFEST = sys.argv[2] if len(sys.argv) > 2 else PATH + ".decks.json"


def main():
    size = os.path.getsize(PATH)
    with open(PATH, "rb") as f:
        magic, dim = struct.unpack("<ii", f.read(8))
    assert magic == MAGIC
    REC_F = dim + 2
    n = (size - 8) // (REC_F * 4)
    raw = np.fromfile(PATH, dtype=np.float32, offset=8, count=n * REC_F)
    rec = raw.reshape(n, REC_F)
    y = rec[:, dim].astype(np.int8)
    gid = rec[:, dim + 1].astype(np.int64)
    del raw, rec

    man = json.load(open(MANIFEST, encoding="utf-8"))
    names = man["decks"]
    pl_of_g = {}
    pr_of_g = {}
    for g, l, r in man["pairs"]:
        pl_of_g[g] = l
        pr_of_g[g] = r
    lab_of_g = np.zeros(10001, dtype=np.int8)
    lab_of_g[gid.astype(int)] = y

    gs = np.array(sorted(pl_of_g))
    GL = np.array([pl_of_g[int(g)] for g in gs])
    GR = np.array([pr_of_g[int(g)] for g in gs])
    GY = lab_of_g[gs].astype(np.float64)          # 1 = 左(=GL)胜

    print("=" * 92)
    print(f"对位随机化排查 —— {PATH}   对局 {gs.size:,}   样本 {n:,}")
    print("=" * 92)

    # ---------- 1. 随机化生效证据 ----------
    print("\n[1] 随机化生效证据")
    pc = collections.Counter(zip(GL.tolist(), GR.tolist()))
    v = np.array(sorted(pc.values()))
    print(f"    有序对位 {len(pc)} 种（理论上限 22×21 = {22*21}），"
          f"每对位 {v.min()}~{v.max()} 局，均值 {v.mean():.1f}，中位 {int(np.median(v))}")
    same = int((GL == GR).sum())
    print(f"    自己打自己（左卡组 == 右卡组）的局数：{same}  {'OK' if same == 0 else 'BAD'}")
    # 左右是否真的互换了：左卡组的分布应该近似均匀
    lc = np.bincount(GL, minlength=DECKS)
    rc = np.bincount(GR, minlength=DECKS)
    print(f"    每套卡组当左方的局数 {lc.min()}~{lc.max()}（期望 {gs.size/DECKS:.0f}），"
          f"当右方 {rc.min()}~{rc.max()}")

    # ---------- 2. 互补性 ----------
    print("\n[2] 互补性：同一对卡组，换个左右，胜负是否反过来")
    print("    若「谁是赢家」由卡组对决定而与左右无关 ⇒ 两次胜率之和 ≈ 1.00")
    lw = np.zeros((DECKS, DECKS))
    ln = np.zeros((DECKS, DECKS))
    for l, r, w in zip(GL, GR, GY):
        lw[l, r] += w
        ln[l, r] += 1
    rows = []
    for a in range(DECKS):
        for b in range(a + 1, DECKS):
            if ln[a, b] >= 5 and ln[b, a] >= 5:
                p1 = lw[a, b] / ln[a, b]               # A 在左、B 在右，A 胜率
                p2 = 1 - lw[b, a] / ln[b, a]           # B 在左、A 在右，A 胜率
                rows.append((a, b, p1, p2, ln[a, b] + ln[b, a]))
    rows.sort(key=lambda t: -t[4])
    print(f"    {'卡组对':<26}{'A左胜率':>9}{'A右胜率':>9}{'两者平均':>10}{'和':>7}{'局数':>7}")
    for a, b, p1, p2, tot in rows[:12]:
        print(f"    {names[a][:11]:>11} vs {names[b][:11]:<11}{100*p1:>8.1f}%{100*p2:>8.1f}%"
              f"{100*(p1+p2)/2:>9.1f}%{100*(p1+p2):>6.0f}%{int(tot):>7}")
    s = np.array([p1 + p2 for _, _, p1, p2, _ in rows])
    print(f"    ⇒ 231 个卡组对里 {len(rows)} 个两侧都 ≥5 局："
          f"和的中位数 {np.median(s):.3f}（1.000 = 完全互补/与左右无关）")
    extrem = np.array([abs((p1 + p2) / 2 - 0.5) for _, _, p1, p2, _ in rows])
    print(f"       |平均胜率 - 50%| 的中位数 {100*np.median(extrem):.1f} 个百分点"
          f"；超过 25 个百分点的卡组对占 {100*(extrem>0.25).mean():.1f}%")

    # ---------- 3. 边际卡组强弱 ----------
    print("\n[3] 每套卡组在**随机对手 + 随机左右**下的真实强弱（这是 A 基线的燃料）")
    print(f"    {'卡组':<14}{'总胜/总局':>10}{'胜率':>8}{'当左(胜率)':>16}{'当右(胜率)':>16}")
    wr = []
    for d in range(DECKS):
        tot = int((GL == d).sum() + (GR == d).sum())
        win = float(GY[GL == d].sum() + (1 - GY)[GR == d].sum())
        nl, wl = int((GL == d).sum()), float(GY[GL == d].sum())
        nr, wr_ = int((GR == d).sum()), float((1 - GY)[GR == d].sum())
        wr.append(win / tot)
        print(f"    {names[d]:<14}{int(win):>4}/{tot:<5}{100*win/tot:>7.1f}%"
              f"{100*wl/max(1,nl):>12.1f}%({nl:>4}){100*wr_/max(1,nr):>12.1f}%({nr:>4})")
    wr = np.array(wr)
    print(f"    ⇒ 22 套卡组胜率跨度 {100*wr.min():.1f}% ~ {100*wr.max():.1f}%"
          f"（标准差 {100*wr.std():.1f} 个百分点）")

    # ---------- 4. 分半检验（无自证） ----------
    print("\n[4] 分半检验：一半局估「每对位胜率」，在另一半局上评估（无自证）")
    half = (gs % 2 == 0)                      # 按局号奇偶切，与卡组/结果无关
    def split_half(group_key):
        """group_key: 每局一个整数分组 id。返回另一半点上的准确率（组内多数类）。"""
        k = np.array(group_key)
        score = np.zeros(k.max() + 1)
        cnt = np.zeros(k.max() + 1)
        np.add.at(score, k[half], GY[half])
        np.add.at(cnt, k[half], 1.0)
        p = np.divide(score, np.maximum(cnt, 1))
        pred = (p[k[~half]] > 0.5).astype(np.int8)
        # 没见过的组 ⇒ 落到全局多数类
        unseen = cnt[k[~half]] == 0
        pred[unseen] = 1
        return float((pred == GY[~half]).mean()), int(unseen.sum())

    pair_key = GL * DECKS + GR
    a_pair, un_pair = split_half(pair_key)
    a_left, un_l = split_half(GL)
    a_right, un_r = split_half(GR)
    # 两个边际相加（左卡组 + 右卡组 的胜率差）
    def marg(dk):
        s = np.zeros(DECKS); c = np.zeros(DECKS)
        np.add.at(s, dk[half], GY[half]); np.add.at(c, dk[half], 1.0)
        return np.divide(s, np.maximum(c, 1)) - 0.5
    a_both = float((((marg(GL)[GL[~half]] + marg(GR)[GR[~half]]) > 0).astype(np.int8)
                    == GY[~half]).mean())
    a_maj = float((np.int8(1) == GY[~half]).mean())
    print(f"    多数类（永远猜左）                     {100*a_maj:>6.2f}%")
    print(f"    只认「左卡组」(22 组)                  {100*a_left:>6.2f}%   （未见过的组 {un_l}）")
    print(f"    只认「右卡组」(22 组)                  {100*a_right:>6.2f}%   （未见过的组 {un_r}）")
    print(f"    左右卡组边际相加(44 个数)              {100*a_both:>6.2f}%")
    print(f"    ★ 只认「有序对位」(462 组)            {100*a_pair:>6.2f}%   （未见过的组 {un_pair}）")
    # 对照组：把 (左卡组,右卡组) 打乱，看这个口径下的"随机"水平
    rng = np.random.default_rng(0)
    sh = rng.permutation(GR)
    k2 = GL * DECKS + sh
    a_shuf, _ = split_half(k2)
    print(f"    （对照）把右卡组随机打乱后同样口径     {100*a_shuf:>6.2f}%   ← 应当掉到多数类附近")
    return 0


if __name__ == "__main__":
    sys.exit(main())
