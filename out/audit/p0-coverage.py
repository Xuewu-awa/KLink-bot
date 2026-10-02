"""P0 复核：当前内核派发了哪些事件 vs 还有哪些事件有订阅者却从不派发。

- 事件清单 + 订阅卡数：out/audit/trigger-coverage.txt（审计时的快照，来自 card-ir.json entrypoints）
- 当前派发情况：实时 grep src/KLink.Bot/**/*.cs 的 FireTrigger("X" 字面量
"""
import re
import pathlib
import sys

ROOT = pathlib.Path(r"<repo-root>")
SRC = ROOT / "src" / "KLink.Bot"
COV = ROOT / "out" / "audit" / "trigger-coverage.txt"

fired = set()
for p in SRC.rglob("*.cs"):
    txt = p.read_text(encoding="utf-8", errors="replace")
    for m in re.finditer(r'FireTrigger\(\s*"([A-Za-z0-9_]+)"', txt):
        fired.add(m.group(1))
    for m in re.finditer(r'otherProgramName:\s*"([A-Za-z0-9_]+)"', txt):
        fired.add(m.group(1))
    # 具名参数里传给 FireTrigger 的"别人"程序名（同一行内）
for p in SRC.rglob("*.cs"):
    for line in p.read_text(encoding="utf-8", errors="replace").splitlines():
        if "FireTrigger(" in line or "RunTriggerProgram(" in line:
            for m in re.finditer(r'"((?:On)[A-Za-z0-9_]+)"', line):
                fired.add(m.group(1))

rows = []
for line in COV.read_text(encoding="utf-8", errors="replace").splitlines():
    m = re.match(r"^(\S+)\s+(\d+)\s+([✔✗])", line)
    if m:
        rows.append((m.group(1), int(m.group(2)), m.group(3) == "✔"))

print(f"事件总表: {len(rows)} 个；当前 FireTrigger 字面量: {len(fired)} 个")
missing = [(n, c) for n, c, was in rows if n not in fired]
missing.sort(key=lambda t: -t[1])
print(f"\n=== 仍有订阅者但内核从不派发（按订阅卡数降序，前 40）===")
for n, c in missing[:40]:
    print(f"{c:5d}  {n}")
print(f"\n合计 {len(missing)} 个，订阅卡数合计 {sum(c for _, c in missing)}（含重复计卡）")

# 反过来：内核派发但不在原生事件表里的（拼写可能错）
names = {n for n, _, _ in rows}
extra = sorted(fired - names)
print(f"\n=== 内核派发但不在原生事件表里（{len(extra)}）===")
print(", ".join(extra))
