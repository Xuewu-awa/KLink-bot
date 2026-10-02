#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
体检一份 dump 数据的**标签质量** —— 在训练之前就知道模型能"作弊"到什么程度。

背景（`NN训练诊断.md` 结论 1）：那个 84.9% 的留出准确率里几乎没有局面判断 ——
28 个参数的线性模型（只读「双方当前领先量」+「卡组对位」）就有 81.98%。
所以**先量基线**，再看模型有没有超过它。

这里算三条基线（全部只用编码向量里的少数几维，不训练）：

  A **只看先手**        ：恒猜「视角方赢」——先手优势越大，这条越强
  B **读当前 HQ 领先**  ：谁 HQ 高猜谁赢（`v[0]` vs `v[370]`）
  C **只看卡组对位**    ：同对位内多数票

再按**回合**分档看 A/B —— 如果第 2 回合（双方都 20/20、几乎无单位）就有很高准确率，
那说明分档里混进了"局外信息"，模型学到的不是局面。

用法：
  python -X utf8 out/audit/dump-label-health.py out/nn-data-10k-postfix.bin
"""
import json
import struct
import sys
from collections import defaultdict
from pathlib import Path

MAGIC = 0x324C4B41   # 'AKL2' 小端


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else 'out/nn-data-10k-postfix.bin'
    raw = Path(path).read_bytes()
    magic, dim, deck_count = struct.unpack_from('<iii', raw, 0)
    print(f'文件 {path}')
    print(f'  magic=0x{magic:08X}（期望 0x{MAGIC:08X}）  dim={dim}  deckCount={deck_count}')

    rec = dim * 4 + 3 * 4
    body = len(raw) - 12
    n = body // rec
    print(f'  记录 {n} 条，每条 {rec} B，余数 {body % rec}')
    print()

    # 读出来（10 万局那份 11 GB，所以按需只读需要的维度）
    # 布局：global3 + perspective371 + opposite371
    #   v[0]=turn/30  v[1]=activeIsPerspective  v[2]=cardsPlayedThisTurn/10
    #   视角: v[3]=hqDef/20 …   对手: v[3+371]=hqDef/20
    HQ_P = 3
    HQ_O = 3 + 371
    TURN = 0

    labels = []
    turns = []
    hqp = []
    hqo = []
    gids = []
    pairs = []

    with open(path, 'rb') as f:
        f.seek(12)
        for _ in range(n):
            buf = f.read(rec)
            if len(buf) < rec:
                break
            labels.append(struct.unpack_from('<f', buf, dim * 4)[0])
            gids.append(struct.unpack_from('<f', buf, dim * 4 + 4)[0])
            pairs.append(struct.unpack_from('<f', buf, dim * 4 + 8)[0])
            turns.append(struct.unpack_from('<f', buf, TURN * 4)[0])
            hqp.append(struct.unpack_from('<f', buf, HQ_P * 4)[0])
            hqo.append(struct.unpack_from('<f', buf, HQ_O * 4)[0])

    decided = [(l, t, a, b) for l, t, a, b in zip(labels, turns, hqp, hqo) if l in (0.0, 1.0)]
    print(f'标注完成的记录 {len(decided)}/{n}（label ∈ {{0,1}}）')
    if not decided:
        print('没有已结束的记录，无法体检')
        return

    base = sum(1 for l, *_ in decided if l == 1.0) / len(decided)
    print(f'  label=1 比例（= 视角方胜率）: {base:.4f}')
    print()

    # --- A 恒猜视角方赢 ---
    accA = base
    print(f'A 只看先手（恒猜"视角方赢"）        : {accA:.4f}   ← 这就是标签的类别不平衡')

    # --- B 读当前 HQ 领先 ---
    ok = sum(1 for l, t, a, b in decided
             if (a > b and l == 1.0) or (a < b and l == 0.0) or (a == b and l == 1.0))
    print(f'B 读当前 HQ 领先（平局算猜赢）       : {ok / len(decided):.4f}')

    # --- C 只看卡组对位（同 pairId 内多数） ---
    bypair = defaultdict(list)
    for l, p in zip(labels, pairs):
        if l in (0.0, 1.0):
            bypair[p].append(l)
    tot = hit = 0
    for p, ls in bypair.items():
        maj = 1.0 if sum(ls) * 2 >= len(ls) else 0.0
        hit += sum(1 for l in ls if l == maj)
        tot += len(ls)
    print(f'C 只看卡组对位（对位内多数票）       : {hit / tot:.4f}   （{len(bypair)} 种对位）')
    print()

    # --- 按回合分档看 A / B ---
    print('按回合分档（turn 是 /30 归一化后的值，乘 30 还原）：')
    print(f'  {"回合":>5} {"样本":>7} {"A 先手":>8} {"B 读领先":>9} {"双方HQ都满":>10}')
    buckets = defaultdict(list)
    for l, t, a, b in decided:
        buckets[round(t * 30)].append((l, a, b))
    for turn in sorted(buckets):
        rows = buckets[turn]
        if len(rows) < 50:
            continue
        a_acc = sum(1 for l, _, _ in rows if l == 1.0) / len(rows)
        b_ok = sum(1 for l, x, y in rows
                   if (x > y and l == 1.0) or (x < y and l == 0.0) or (x == y and l == 1.0))
        bothfull = sum(1 for _, x, y in rows if x >= 1.0 and y >= 1.0) / len(rows)
        print(f'  {turn:>5} {len(rows):>7} {a_acc:>8.3f} {b_ok / len(rows):>9.3f} {bothfull:>10.3f}')


if __name__ == '__main__':
    main()
