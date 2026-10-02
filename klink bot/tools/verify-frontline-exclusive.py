"""
判定真实规则：前线（location=7）是不是**互斥**的 —— 两边能不能同时有单位在上面。

背景：
  真实 KARDS 是**三条线** —— 两条底线（各自半场 location 5/6）+ 一条**共享前线**（7），
  前线要互相抢。而 kardsim 被发现有「两条前线」（等价于 4 条线），
  这会让相邻/射程/掩护关系全错。

  这条规则必须从真实数据里读出来，不能猜。我手上有 6 局 / 250 条真实棋盘快照。

判据：
  · 若**从没出现过两边同时在 location=7** → 前线互斥，真实模型成立
  · 若出现过 → 要么模型不是互斥的，要么我对 location 的理解有误
  · 再看 location=7 上**同时最多几张**：一条 4 格的前线最多 4 张；
    若能到 8 张，那就真的是两条线
  · 以及 locationNumber 的分布：两边是否占不同的号段

⚠️ 分侧只能靠 cardID：真实编号是 **左 1..40 / 右 41..80**（HQ 占 1 和 41）。
   局中生成的卡 ID > 80，无法从编号判侧，单独归为「未知」。
"""
import json
import os
from collections import Counter, defaultdict

CAP = r"<user-home>\AppData\Local\Temp\klink-capture"
FILES = ["snapshot-match51.jsonl", "snapshot-match28.jsonl", "snapshot-match40.jsonl",
         "snapshot-match19.jsonl", "snapshot-match64.jsonl", "snapshot-match82.jsonl"]


def side_of(card_id):
    if 0 < card_id <= 40:
        return "left"
    if 40 < card_id <= 80:
        return "right"
    return "unknown"


total_snaps = 0
both_sides = 0
only_left = 0
only_right = 0
none = 0
max_simul = 0
max_record = None
slot_counts = Counter()
slot_by_side = defaultdict(Counter)
examples = []

print("=" * 78)
print("逐文件统计：location=7（前线）上的情况")
print("=" * 78)
print(f"{'文件':<26}{'快照':>5}{'有前线':>7}{'两边同时':>9}{'只左':>6}{'只右':>6}{'最多张':>7}")

for fn in FILES:
    p = os.path.join(CAP, fn)
    if not os.path.exists(p):
        continue
    rows = []
    for line in open(p, encoding="utf-8"):
        if line.strip():
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                pass

    f_both = f_left = f_right = f_none = 0
    f_max = 0
    for r in rows:
        total_snaps += 1
        fl = [c for c in r["cards"] if c.get("location") == "7"]
        sides = Counter()
        for c in fl:
            cid = c.get("cardID", "")
            if not cid.isdigit():
                continue
            sides[side_of(int(cid))] += 1
            slot_counts[c.get("locationNumber", "?")] += 1
            slot_by_side[side_of(int(cid))][c.get("locationNumber", "?")] += 1
        n = sum(sides.values())
        f_max = max(f_max, n)
        max_simul = max(max_simul, n)
        if n > 0 and max_record is None or n > (max_record or 0):
            max_record = n

        has_l = sides.get("left", 0) > 0
        has_r = sides.get("right", 0) > 0
        if has_l and has_r:
            f_both += 1
            both_sides += 1
            if len(examples) < 8:
                examples.append((fn, r["act"],
                                 [(c.get("cardID"), c.get("locationNumber"), c.get("Name")) for c in fl]))
        elif has_l:
            f_left += 1; only_left += 1
        elif has_r:
            f_right += 1; only_right += 1
        else:
            f_none += 1; none += 1

    print(f"{fn:<26}{len(rows):>5}{len(rows)-f_none:>7}{f_both:>9}{f_left:>6}{f_right:>6}{f_max:>7}")

print()
print("=" * 78)
print("结论")
print("=" * 78)
print(f"  总快照         {total_snaps}")
print(f"  前线为空       {none}")
print(f"  只有一方在前线 {only_left + only_right}  （左 {only_left} / 右 {only_right}）")
print(f"  ★ 两边同时在前线 {both_sides}")
print(f"  ★ 前线同时最多 {max_simul} 张")
print()
print(f"  location=7 的槽位分布: {dict(sorted(slot_counts.items()))}")
print(f"    左侧占的槽位: {dict(sorted(slot_by_side['left'].items()))}")
print(f"    右侧占的槽位: {dict(sorted(slot_by_side['right'].items()))}")

if examples:
    print()
    print("  ⚠ 两边同时在前线的样例：")
    for fn, act, cards in examples:
        print(f"    {fn} act={act}")
        for cid, ln, nm in cards:
            print(f"        cardID={cid:<5} slot={ln:<3} {nm}")
