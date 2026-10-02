"""
③ 卡池平衡：带效果之后，22 套牌在「随机对手 + 随机左右」下的胜率分布收敛了吗？

对照基准（第二轮报告 §3.3，无效果内核）：
    跨度 17.0%（德美）~ 88.5%（自残苏），标准差 σ = 22.0 点。

用法：python -X utf8 "klink bot/tools/nn-effects-deck-compare.py"
"""

import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(HERE))

from nn_common import nntrain_split, open_data   # noqa: E402

# 第二轮报告 §3.3 的表（无效果内核，10,000 局全体）
OLD = {"德美": 17.0, "英日": 18.5, "日美": 22.5, "美澳跳": 24.8, "英美2": 27.1,
       "德澳老兵": 32.5, "德芬车2": 33.9, "米色团": 38.3, "德芬车": 39.8, "美英跳": 40.7,
       "英芬": 44.9, "日澳快攻": 47.7, "日法": 51.3, "德澳老兵2": 59.5, "日波情报": 61.6,
       "Sid日波情报": 67.3, "美澳极限快": 73.0, "英苏中立": 73.2, "苏澳中速": 75.7,
       "日波炸槽": 80.5, "苏英爆破": 81.9, "自残苏": 88.5}


def deck_win(path, manifest, keep_games=None):
    rec, dim, n = open_data(str(path))
    y = np.asarray(rec[:, dim], dtype=np.float64)
    gid = np.asarray(rec[:, dim + 1], dtype=np.int64)
    man = json.load(open(manifest, encoding="utf-8"))
    names = man["decks"]
    GL, GR = {}, {}
    for g, l, r in man["pairs"]:
        GL[g], GR[g] = l, r
    lab = {}
    for g, yy in zip(gid.tolist(), y.tolist()):
        lab[g] = yy
    games = sorted(lab) if keep_games is None else sorted(keep_games)
    win = np.zeros(22)
    tot = np.zeros(22)
    for g in games:
        l, r, yy = GL[g], GR[g], lab[g]
        tot[l] += 1
        tot[r] += 1
        win[l] += yy
        win[r] += 1 - yy
    del rec
    return {names[i]: 100.0 * win[i] / tot[i] for i in range(22)}, tot


def main():
    new, tot = deck_win(REPO / "out/nn-data.bin", REPO / "out/nn-data.bin.decks.json")

    print("=" * 96)
    print("③ 22 套卡组在「随机对手 + 随机左右」下的实测胜率（10,000 局全体口径，与第二轮同口径）")
    print("=" * 96)
    print(f"  {'卡组':<14}{'无效果(v1-nofx)':>17}{'带效果(v1-fx)':>16}{'变化':>10}")
    for k in sorted(OLD, key=lambda k: -new[k]):
        print(f"  {k:<14}{OLD[k]:>16.1f}%{new[k]:>15.1f}%{new[k] - OLD[k]:>+9.1f}")

    ov = np.array([OLD[k] for k in OLD])
    nv = np.array([new[k] for k in OLD])
    print()
    print(f"  无效果：跨度 {ov.min():.1f}% ~ {ov.max():.1f}%   标准差 σ = {ov.std():.1f} 点")
    print(f"  带效果：跨度 {nv.min():.1f}% ~ {nv.max():.1f}%   标准差 σ = {nv.std():.1f} 点")
    print(f"  新旧卡组胜率  Pearson r = {np.corrcoef(ov, nv)[0, 1]:+.3f}   "
          f"Spearman(秩) r = {np.corrcoef(np.argsort(np.argsort(ov)), np.argsort(np.argsort(nv)))[0, 1]:+.3f}")
    print(f"  平均绝对变化 = {np.abs(nv - ov).mean():.1f} 点")
    print("  变化最大的 5 套：")
    for k in sorted(OLD, key=lambda k: -abs(new[k] - OLD[k]))[:5]:
        print(f"    {k:<14}{OLD[k]:>6.1f}% → {new[k]:>6.1f}%  ({new[k] - OLD[k]:+.1f})")

    # ---- 留出局口径（与模型的 1500 局同一份留出）----
    rec, dim, n = open_data(str(REPO / "out/nn-data.bin"))
    gid = np.asarray(rec[:, dim + 1], dtype=np.float32)
    val, _tr, _vg = nntrain_split(gid, 12345)
    del rec
    val_games = set(int(g) for g in np.unique(gid[val]))
    ho, tot_ho = deck_win(REPO / "out/nn-data.bin", REPO / "out/nn-data.bin.decks.json", val_games)
    hv = np.array([ho[k] for k in OLD])
    print()
    print(f"  只算留出 1500 局（每套牌 {int(tot_ho.min())}~{int(tot_ho.max())} 局）："
          f"跨度 {hv.min():.1f}% ~ {hv.max():.1f}%，σ = {hv.std():.1f} 点")
    print(f"    与全体口径的卡组胜率  Pearson r = {np.corrcoef(nv, hv)[0, 1]:+.3f}"
          "（一致 ⇒ 这个不平衡不是小样本噪声）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
