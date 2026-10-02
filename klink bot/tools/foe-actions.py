"""只看对手（GreedyBot 控制的英日）在做什么。"""
import re
import sys

path = sys.argv[1]
lines = open(path, encoding="utf-8").read().splitlines()

FOE = re.compile(r"^#\s*(\d+)\s+\[T(\d+)/(\w+)\]\s+对手\s+(.+)$")
NOTE = re.compile(r"^\s*│\s*\[T(\d+)/(\w+)\]\s+(.+)$")
HQ = re.compile(r"HQ (\w+) 受到 (\d+) 伤害，剩余 (-?\d+)")

def clean(s):
    s = re.sub(r"card_(unit|event|location)_", "", s)
    s = re.sub(r"@\S+", "", s)
    s = re.sub(r"\s+", " ", s).strip()
    return s[:96]

cur = None
buf = []
for ln in lines:
    m = FOE.match(ln.strip())
    if m:
        step, t, side, what = m.groups()
        if t != cur:
            if cur is not None:
                print()
            cur = t
            print(f"【T{t}】")
        if side == "right":
            print(f"   {clean(what)}")
        continue
    m = HQ.search(ln)
    if m:
        who, dmg, left = m.groups()
        if who == "right":
            print(f"      💥 右方 HQ 受 {dmg} → 剩 {left}")
        continue
