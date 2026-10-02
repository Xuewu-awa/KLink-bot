"""GetCardsOnBoardBySide(unitsOnly=false) / GetAllCardsOnBoard 的 39+96 个调用点，
调用方**接下来**有没有对元素做 IsUnit / IsLocation 检查 —— 用来判"false 那一支是否包含非单位"。

用法: python out/audit/p0-unitonly-evidence.py
"""
import json
import pathlib
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = pathlib.Path(r"<repo-root>")
IR = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))

PREDICATES = {"IsUnit", "IsLocation", "IsLocatedOnBoard", "IsOrder", "IsHq", "getTotalDefense", "IsInfantry"}


def main():
    hits = Counter()
    examples = []
    for card, body in IR.items():
        steps = body.get("steps") or []
        for idx, s in enumerate(steps):
            if s.get("op") != "call":
                continue
            fn = s.get("fn")
            args = s.get("args") or []
            interesting = False
            if fn == "GetCardsOnBoardBySide" and len(args) > 1 and args[1] == {"bool": False}:
                interesting = True
            if fn == "GetAllCardsOnBoard" and args and args[0] == {"bool": True}:
                interesting = True
            if not interesting:
                continue
            # 往后看 40 步里有没有对元素做类型判断
            nxt = [x.get("fn") for x in steps[idx + 1: idx + 41] if x.get("op") == "call"]
            used = [f for f in nxt if f in PREDICATES]
            key = f"{fn}: {','.join(sorted(set(used))) or '（没有类型判断）'}"
            hits[key] += 1
            if len(examples) < 40:
                examples.append(f"{card:44s} i={s.get('i')} {fn} → {sorted(set(used))}")

    for k, v in hits.most_common():
        print(f"{v:4d}  {k}")
    print("\n--- 例子 ---")
    for e in examples:
        print(e)


if __name__ == "__main__":
    main()
