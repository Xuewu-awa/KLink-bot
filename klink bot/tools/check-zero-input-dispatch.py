"""交叉比对：把 find-zero-input-calls 的结果与 CardApiDispatch 的入口做对照，
列出「字节码零入参、但内核 lambda 里却从 args 取值」的入口 —— 这些都会读到垃圾。
"""
import json
import re
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

# ---- 1) 从字节码统计零入参函数 ----
assets = json.load(open("decompiled/cards.full.json", encoding="utf-8"))["assets"]
shapes = defaultdict(Counter)


def param_kind(p):
    if not isinstance(p, dict):
        return "?"
    inst = p.get("Inst")
    return "local:" + str(p.get("Variable Name")) if inst == "LocalVariable" else (inst or "?")


def walk(node):
    if isinstance(node, dict):
        inst = node.get("Inst")
        fname = node.get("Function") or node.get("FunctionName")
        if inst in ("FinalFunction", "LocalVirtualFunction", "VirtualFunction") and fname:
            shapes[fname][tuple(param_kind(p) for p in (node.get("Parameters") or []))] += 1
        for v in node.values():
            walk(v)
    elif isinstance(node, list):
        for v in node:
            walk(v)


walk(assets)

zero_input = set()
for fname, sigs in shapes.items():
    if sum(sigs.values()) < 5:
        continue
    if all(all(k.startswith("local:CallFunc_%s_" % fname) for k in sig) and len(sig) <= 1
           for sig in sigs):
        zero_input.add(fname)

# ---- 2) 解析 dispatch 里的入口 ----
src = open("../src/KLink.Bot/Effects/CardApiDispatch.cs", encoding="utf-8").read()
entries = re.findall(r'\["([^"]+)"\]\s*=\s*\(c, r, a\)\s*=>\s*(.+?),\n', src)

print("=== ⚠️ 零入参、但内核却从 args 取值的入口 ===")
bad = 0
for name, body in entries:
    if name not in zero_input:
        continue
    if re.search(r'\b(a\[|IntArg\(a|SideArg\(r, a|AsCard\(a|BoolArg\(a|FloatArg\(a|StrArg\(a)', body):
        print("  %-38s %s" % (name, body[:110]))
        bad += 1
print("  共 %d 个" % bad)

print()
print("=== 零入参、而且已经实现、但 dispatch 里查不到的（可能是缺口）===")
implemented = {n for n, _ in entries}
missing = sorted(n for n in zero_input if n not in implemented)
print("  共 %d 个，前 30：" % len(missing))
for n in missing[:30]:
    print("   ", n)
