"""把 NNPlay 的对局日志整理成「决策 → 候选 → 选择 → 实际发生」的紧凑表。"""
import re
import sys

path = sys.argv[1] if len(sys.argv) > 1 else r"out\live-game.log"
lines = open(path, encoding="utf-8").read().splitlines()

# 只保留有信息量的行
KEEP_HEAD = re.compile(r"^对局|^种子|^试算|^模型\s")
DEC = re.compile(r"^🧠 决策 #(\d+)\s+\[T(\d+)/(\w+)\]\s+kredit (\S+)\s+手牌 (\d+)\s+场面 (\d+)\s+HQ (\S+)")
EVAL = re.compile(r"^\s*当前局面估值 P\((\w+) 胜\) = ([\d.]+)%\s+（枚举 (\d+) 个")
CAND = re.compile(r"^\s*([✔\s])\s*([\d.]+)%\s+(.+?)(?:\s{2,}(.*))?$")
PICK = re.compile(r"^\s*→ 选 \[([\d.]+)%\]\s+(.+)$")
EXEC = re.compile(r"^#\s*(\d+)\s+\[T(\d+)/(\w+)\]\s+(NN|对手)\s+(.+)$")
NOTE = re.compile(r"^\s*│\s*(.+)$")

out = []
for ln in lines:
    if KEEP_HEAD.match(ln):
        out.append(("head", ln.strip()))
        continue
    m = DEC.match(ln)
    if m:
        out.append(("dec", m.groups()))
        continue
    m = EVAL.match(ln)
    if m:
        out.append(("eval", m.groups()))
        continue
    m = PICK.match(ln)
    if m:
        out.append(("pick", m.groups()))
        continue
    m = CAND.match(ln)
    if m and not ln.strip().startswith("→"):
        out.append(("cand", (m.group(1).strip() == "✔", m.group(2), m.group(3).strip())))
        continue
    m = EXEC.match(ln)
    if m:
        out.append(("exec", m.groups()))
        continue
    m = NOTE.match(ln)
    if m:
        out.append(("note", m.group(1).strip()))

# 打印
for kind, v in out:
    if kind == "head":
        print(f"  {v}")
    elif kind == "dec":
        n, t, side, k, hand, board, hq = v
        print(f"\n决策 #{n}  [T{t}/{side}]  kredit {k}  手牌 {hand}  场面 {board}  HQ {hq}")
    elif kind == "eval":
        who, pct, n = v
        print(f"    局面估值 P({who} 胜) = {pct}%   （枚举 {n} 个候选）")
    elif kind == "cand":
        mark, pct, desc = v
        print(f"      {'✔' if mark else ' '} {pct:>6}%  {desc[:95]}")
    elif kind == "pick":
        pct, desc = v
        print(f"    → 选 [{pct}%] {desc[:95]}")
    elif kind == "exec":
        step, t, side, who, desc = v
        tag = "NN  " if who == "NN" else "对手"
        print(f"    # {step:>3} [T{t}/{side}] {tag} {desc[:95]}")
    elif kind == "note":
        print(f"           │ {v[:95]}")
