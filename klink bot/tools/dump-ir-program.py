"""打印 IR 里的 entry 序列，用来人工确认控制流结构（尤其循环）。

用法：
    python dump-ir-program.py <卡名> [前 N 条] [--entry 事件名]
    python dump-ir-program.py --has-flow          列出含 Execution Flow 的卡
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
lib = json.loads((ROOT / "docs/card-ir.json").read_text(encoding="utf-8"))

if "--has-flow" in sys.argv:
    shown = 0
    for cname, card in lib.items():
        ops = {s["op"] for s in card["steps"]}
        if "pushFlow" in ops or "popFlowIfNot" in ops:
            n = sum(1 for s in card["steps"] if s["op"].startswith(("pushFlow", "popFlow")))
            print(f"{cname:<48} 步数={len(card['steps']):>4}  flow 指令={n}  入口={list(card['entrypoints'])[:3]}")
            shown += 1
            if shown >= 25:
                break
    sys.exit(0)

name = sys.argv[1]
limit = int(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2].isdigit() else 80
entry_filter = None
if "--entry" in sys.argv:
    entry_filter = sys.argv[sys.argv.index("--entry") + 1]

card = lib.get(name)
if card is None:
    print(f"IR 里没有 {name}")
    sys.exit(1)

print(f"=== {name} ===")
print(f"入口点: {card['entrypoints']}")
if entry_filter:
    print(f"起点: {entry_filter} @ {card['entrypoints'].get(entry_filter)}")
print(f"总步数: {len(card['steps'])}")
print()

idxs = {s["i"] for s in card["steps"]}
for s in card["steps"][:limit]:
    i = s["i"]
    op = s["op"]
    extra = ""
    if op in ("jump", "jumpIfNot", "jumpIf"):
        t = s.get("to")
        extra = f"-> {t}" + ("" if t in idxs else "  [越界!]")
    elif op == "pushFlow":
        t = s.get("to")
        extra = f"push -> {t}" + ("" if t in idxs else "  [越界!]")
    elif op == "popFlowIfNot":
        extra = f"popIfNot cond={s.get('cond')}"
    elif op == "call":
        extra = f"{s.get('fn')}({len(s.get('args', []))} args)"
    elif op == "set":
        extra = f"{s.get('dst')} = {s.get('src')}"
    elif op == "math":
        extra = f"{s.get('fn')}"
    elif op == "unknown":
        extra = s.get("inst", "")
    print(f"  {i:>6}  {op:<14} {extra}")
