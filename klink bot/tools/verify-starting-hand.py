"""
用回放的 `starting_hand_left/right`（独立于快照的第二来源）去校验快照。

这是唯一能分清三件事的判据：
  · 配对错（快照根本不是这一局）
  · 左右定向错
  · 快照 act=1 的时点不对（不是"第 1 回合开始后"）

回放明确列出了起手牌是哪些 card_id，而快照 act=1 的 location=3/4 是客户端认为的手牌。
两者必须一致 —— 且这个比对**完全不依赖我之前的任何假设**。
"""
import json
import os

CAP = r"<user-home>\AppData\Local\Temp\klink-capture"
REP = r"klink bot\docs\fresh-replays"

PAIRS = [  # (回放, 我当前配对到的快照文件)
    ("206428", "snapshot-match51.jsonl"),
    ("634651", "snapshot-match28.jsonl"),
    ("989040", "snapshot-match40.jsonl"),
    ("590119", "snapshot-match19.jsonl"),
    ("641464", "snapshot-match64.jsonl"),
    ("499982", "snapshot-match82.jsonl"),
]


def flipid(i):
    return i + 40 if 0 < i <= 40 else (i - 40 if 40 < i <= 80 else i)


def snap_hands(path):
    for line in open(path, encoding="utf-8"):
        if not line.strip():
            continue
        r = json.loads(line)
        if r["act"] != 1:
            continue
        left = {int(c["cardID"]) for c in r["cards"]
                if c.get("location") == "3" and c.get("cardID", "").isdigit()}
        right = {int(c["cardID"]) for c in r["cards"]
                 if c.get("location") == "4" and c.get("cardID", "").isdigit()}
        nloc = {}
        for c in r["cards"]:
            nloc[c.get("location")] = nloc.get(c.get("location"), 0) + 1
        return left, right, nloc
    return set(), set(), {}


print(f"{'回放':<10}{'快照':<24}{'回放起手':<20}{'快照手牌(原)':<20}{'翻转后比对'}")
print("-" * 100)
for rid, fn in PAIRS:
    p = os.path.join(CAP, fn)
    if not os.path.exists(p):
        print(f"{rid:<10}{fn:<24}  (快照文件不存在)")
        continue
    rep = json.load(open(os.path.join(REP, f"replay-{rid}.json"), encoding="utf-8"))
    sd = rep["starting_info"]["match_and_starting_data"]["starting_data"]
    rl = {c["card_id"] for c in sd.get("starting_hand_left", [])}
    rr = {c["card_id"] for c in sd.get("starting_hand_right", [])}
    sl, sr, nloc = snap_hands(p)

    # 假设「定向一致」：快照左=回放左
    same = (sl == rl and sr == rr)
    # 假设「对调」：快照左=回放右
    swapped = ({flipid(x) for x in sl} == rr and {flipid(x) for x in sr} == rl)

    verdict = "✓ 一致" if same else ("✓ 对调" if swapped else "✗ 都不匹配")
    print(f"{rid:<10}{fn:<24}{str(sorted(rl)):<20}{str(sorted(sl)):<20}{verdict}")
    if not same and not swapped:
        print(f"{'':<10}{'':<24}回放右手={sorted(rr)}")
        print(f"{'':<10}{'':<24}快照右手={sorted(sr)}")
        print(f"{'':<10}{'':<24}快照 location 分布={nloc}")
