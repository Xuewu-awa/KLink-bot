"""
验证「牌库顺序可由快照还原」这个前提。

如果 loc=1/2 的 locationNumber 是**牌库内的位置索引**，那么：
  1. 同一快照里，同一侧的牌库卡 locationNumber 应当互不相同、且是 0..n-1 的一个排列；
  2. 随着对局推进（不断抽牌），被抽走的应当是**编号最小**的那些，
     剩下的编号集合是原来的一个后缀。
这两条同时成立，就说明 locationNumber 直接给出了真实牌库顺序 ——
对拍时按它填牌库，就能把「洗牌/抽牌」这个随机源完全消掉，
剩下的差异才是真正的规则/效果差异。

⚠️ 这一点很重要：如果牌库顺序对不上，抽到的牌就不一样，
后面整局都会跟着偏，任何对拍数字都会被这个噪声污染。
"""
import json
import os

CAP = r"<user-home>\AppData\Local\Temp\klink-capture"
FILE = "snapshot-match40.jsonl"

rows = []
for line in open(os.path.join(CAP, FILE), encoding="utf-8"):
    if line.strip():
        rows.append(json.loads(line))
rows.sort(key=lambda r: r["act"])

print(f"{FILE}: {len(rows)} 条快照\n")


def deck_slots(row, loc):
    """返回该侧牌库里 (locationNumber, cardID, Name) 列表，按编号排序。"""
    out = []
    for c in row["cards"]:
        if str(c.get("location")) != str(loc):
            continue
        try:
            n = int(c.get("locationNumber", "-1"))
        except ValueError:
            continue
        out.append((n, c.get("cardID"), c.get("Name")))
    return sorted(out)


for tag, row in (("首条", rows[0]), ("末条", rows[-1])):
    print(f"===== {tag} act={row['act']} =====")
    for side, loc in (("左库", 1), ("右库", 2)):
        slots = deck_slots(row, loc)
        nums = [n for n, _, _ in slots]
        ok_unique = len(set(nums)) == len(nums)
        ok_range = nums == list(range(len(nums))) if nums else False
        print(f"  {side}: {len(slots)} 张  编号 {nums[:6]}{'...' if len(nums) > 6 else ''}  "
              f"最大 {max(nums) if nums else '-'}")
        print(f"       互不相同={ok_unique}  恰为 0..n-1 排列={ok_range}")

# 抽牌方向：比较首条与后续快照，看消失的是不是最小的编号
print("\n===== 抽牌方向检验（左库）=====")
first = {cid: n for n, cid, _ in deck_slots(rows[0], 1)}
for row in rows[:6]:
    cur = {cid: n for n, cid, _ in deck_slots(row, 1)}
    gone = sorted(n for cid, n in first.items() if cid not in cur)
    print(f"  act={row['act']:4}  库内 {len(cur):3} 张  已抽走的原编号 {gone}")
