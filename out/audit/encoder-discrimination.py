#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
诊断：`StateEncoder` 编出来的局面向量**真的有区分度吗**？

背景（`rel/data/fyserver/bot-log`）：模型对同一回合的所有候选都输出
**同一个值**（有时全 100.0%、有时全 0.0%）。两种可能：
  A 编码有区分度，但模型退化（训练问题）
  B 编码本身没区分度（编码器/数据问题）

这个脚本从 **dump 数据**（已经编码好的 745 维向量）里直接量：
  · 同一局、相邻回合的向量差多少
  · 不同局、同一回合的向量差多少
  · 哪些维度**恒为 0**（= 编码里根本没用到）
  · 哪个维度**方差最大**（模型最可能依赖它）

如果相邻回合的差 ≈ 0，那编码就是没区分度的（B）。
"""
import struct
import sys
from pathlib import Path

DIM = None          # 从文件头读（v2 = 745 / v3 = 1065）—— 不要写死


def read(path, limit=400000):
    raw = Path(path).read_bytes()
    magic, dim, deck_count = struct.unpack_from('<iii', raw, 0)
    global DIM
    if DIM is None:
        DIM = dim
    assert dim == DIM, f'dim={dim}'
    rec = dim * 4 + 3 * 4
    n = min((len(raw) - 12) // rec, limit)

    vecs, labels, gids, turns = [], [], [], []
    with open(path, 'rb') as f:
        f.seek(12)
        for _ in range(n):
            b = f.read(rec)
            if len(b) < rec:
                break
            vecs.append(b[:dim * 4])
            labels.append(struct.unpack_from('<f', b, dim * 4)[0])
            gids.append(int(struct.unpack_from('<f', b, dim * 4 + 4)[0]))
            turns.append(struct.unpack_from('<f', b, 0)[0] * 30)
    return vecs, labels, gids, turns


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else 'out/nn-data-10k-postfix.bin'
    print(f'读 {path} ...')
    vecs, labels, gids, turns = read(path)
    n = len(vecs)
    print(f'样本 {n}')

    # 转成 float 列表（只取需要的维度，省内存）
    import array
    def f32(b):
        a = array.array('f')
        a.frombytes(b)
        return a

    # ---- 哪些维度恒为 0 ----
    zero = [0] * DIM
    for b in vecs[:20000]:
        a = f32(b)
        for i in range(DIM):
            if a[i] != 0.0:
                zero[i] += 1
    always_zero = [i for i in range(DIM) if zero[i] == 0]
    print(f'\n=== 恒为 0 的维度：{len(always_zero)}/{DIM} ===')
    if always_zero:
        print(f'  前 40 个：{always_zero[:40]}')

    # ---- 同一局内相邻回合的差 vs 不同局的差 ----
    print('\n=== 同局相邻回合 vs 跨局：平均 L1 距离 ===')
    by_game = {}
    for i in range(n):
        by_game.setdefault(gids[i], []).append(i)

    same_d = []
    diff_d = []
    games = sorted(by_game)[:40]
    for g in games:
        idxs = by_game[g]
        for k in range(1, min(len(idxs), 12)):
            a, b = f32(vecs[idxs[k - 1]]), f32(vecs[idxs[k]])
            same_d.append(sum(abs(a[j] - b[j]) for j in range(DIM)))
    for gi in range(1, len(games)):
        i1 = by_game[games[gi - 1]][0]
        i2 = by_game[games[gi]][0]
        a, b = f32(vecs[i1]), f32(vecs[i2])
        diff_d.append(sum(abs(a[j] - b[j]) for j in range(DIM)))

    def stat(name, xs):
        if not xs:
            print(f'  {name}: 无样本')
            return
        xs = sorted(xs)
        print(f'  {name:26s} n={len(xs):5d}  中位 {xs[len(xs)//2]:8.3f}  '
              f'最小 {xs[0]:8.3f}  最大 {xs[-1]:8.3f}')

    stat('同局相邻回合 L1', same_d)
    stat('跨局 L1', diff_d)
    if same_d and diff_d:
        m1 = sorted(same_d)[len(same_d) // 2]
        m2 = sorted(diff_d)[len(diff_d) // 2]
        print(f'\n  ⇒ 跨局/同局 比值 = {m2 / max(m1, 1e-9):.2f}')
        print('    比值接近 1 说明"同局相邻回合"和"完全不同的两局"一样远 —— 编码没区分度')

    # ---- 方差最大的维度 ----
    import statistics
    var = []
    step = max(1, n // 20000)
    sample = vecs[::step]
    cols = list(zip(*[f32(b) for b in sample[:20000]]))
    for i, c in enumerate(cols):
        var.append((statistics.pvariance(c), i, min(c), max(c)))
    var.sort(reverse=True)
    print('\n=== 方差最大的 12 个维度（模型最可能依赖它们）===')
    names = {0: 'turn/30', 1: 'activeIsPerspective', 2: 'cardsPlayedThisTurn/10',
             3: 'hqDef(P)/20', 4: 'kredits(P)/12', 5: 'maxKredits(P)/12',
             6: 'handCount(P)/10', 7: 'deckCount(P)/40', 8: 'boardCount(P)/10',
             373: 'hqDef(O)/20', 374: 'kredits(O)/12'}
    for v, i, lo, hi in var[:12]:
        print(f'  [{i:3d}] 方差 {v:9.5f}  范围 {lo:7.3f}..{hi:7.3f}   {names.get(i, "")}')


if __name__ == '__main__':
    main()
