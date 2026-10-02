import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
IR = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))

for name in ["ApplyTheBuff", "RemoveTheBuff", "ApplyBuff", "RemoveBuff",
             "Apply The Buff", "Remove the Buff", "ApplyAttackBuff", "RemoveAttackBuff"]:
    hits = {}
    for card, body in IR.items():
        for s in body.get("steps") or []:
            if s.get("op") == "call" and s.get("fn") == name:
                hits[card] = hits.get(card, 0) + 1
    print(f"=== {name}（{len(hits)} 张卡）===")
    for c, n in hits.items():
        # 这个函数是不是定义在这张卡自己的 asset 里（locals 里有没有它）
        local = name in (IR[c].get("locals") or {})
        print(f"   {c:44s} ×{n}   locals里有函数体={local}")
