"""定位「打转」的 Kismet 程序 —— 用来诊断控制流切片问题。

背景：KismetVm 有单程序步数上限（5000），撞上限说明程序里有环。
正常的 Blueprint 事件链不应该有环，所以这是切片不完整的信号。

用法：python find-looping-programs.py [entry ...]
"""
import json
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
lib = json.loads((ROOT / "docs/card-ir.json").read_text(encoding="utf-8"))

wanted = {int(a) for a in sys.argv[1:]} or None

for cname, card in lib.items():
    for pname, prog in card["programs"].items():
        entry = prog["entry"]
        if wanted is not None and entry not in wanted:
            continue

        idxs = [s["i"] for s in prog["steps"]]
        backward = [
            (s["i"], s.get("to"))
            for s in prog["steps"]
            if s.get("op", "").startswith("jump") and 0 < s.get("to", -1) <= s["i"]
        ]
        if wanted is None and not backward:
            continue

        ops = Counter(s["op"] for s in prog["steps"])
        print(f"{cname} / {pname}")
        print(f"   entry={entry}  步数={len(prog['steps'])}  i 范围 {min(idxs)}..{max(idxs)}")
        print(f"   向后跳转: {backward[:8]}")
        print(f"   指令分布: {dict(ops)}")
        print()
