"""从 NNPlay 日志里抽出「战况主线」：HQ 变化、单位摧毁、部署、每回合。"""
import re
import sys

path = sys.argv[1]
lines = open(path, encoding="utf-8").read().splitlines()

HQ = re.compile(r"HQ (\w+) 受到 (\d+) 伤害，剩余 (-?\d+)")
KILL = re.compile(r"\[(\d+)\](\S+?)@\S+ (-?\d+)/(-?\d+) 被摧毁")
DEPLOY = re.compile(r"\[(\d+)\](\S+?)@(Board\w+)#(\d+) (\S+) 部署到")
DEC = re.compile(r"决策 #(\d+)\s+\[T(\d+)/(\w+)\]\s+kredit (\S+)\s+手牌 (\d+)\s+场面 (\d+)\s+HQ (\S+)")
PICK = re.compile(r"→ 选 \[([\d.]+)%\]\s+(.+?)(?:\s+目标|\s+（|$)")

cur = None
for ln in lines:
    m = DEC.match(ln.strip())
    if m:
        n, t, side, k, hand, board, hq = m.groups()
        if t != cur:
            cur = t
            print(f"\n{'─'*88}\n【T{t}】  kredit {k}  手牌 {hand}  场面 {board}  HQ {hq}")
        continue
    m = PICK.search(ln)
    if m:
        pct, what = m.groups()
        what = what.strip()
        # 压缩卡名，只留去掉前缀的可读部分
        what = re.sub(r"card_(unit|event|location)_", "", what)
        what = re.sub(r"@\S+", "", what)
        print(f"   ▶ [{pct}%] {what[:78]}")
        continue
    m = HQ.search(ln)
    if m:
        who, dmg, left = m.groups()
        print(f"   💥 {who} HQ 受 {dmg} → 剩 {left}")
        continue
    m = KILL.search(ln)
    if m:
        cid, name, a, d = m.groups()
        name = re.sub(r"card_(unit|event|location)_", "", name)
        print(f"   ☠  {name} ({a}/{d})")
        continue
    m = DEPLOY.search(ln)
    if m:
        cid, name, zone, slot, stat = m.groups()
        name = re.sub(r"card_(unit|event|location)_", "", name)
        print(f"   ▸  {name} {stat} → {zone}#{slot}")
        continue
