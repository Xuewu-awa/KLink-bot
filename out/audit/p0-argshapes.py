"""统计某个原语在 IR 里的**实参形状**（每个位置的取值分布）+ 有无 recv。

用法: python out/audit/p0-argshapes.py GetCardsOnBoardBySide GetAllCardsOnBoard SpawnCardOnBattlefield
"""
import json
import pathlib
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = pathlib.Path(r"<repo-root>")
IR = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))


def main():
    for target in sys.argv[1:]:
        rows = []
        for card, body in IR.items():
            for s in body.get("steps") or []:
                if s.get("op") == "call" and s.get("fn") == target:
                    rows.append((card, s))
        print(f"=== {target}: {len(rows)} 个调用点 / {len({c for c, _ in rows})} 张卡 ===")
        width = max((len(s.get("args") or []) for _, s in rows), default=0)
        for i in range(width):
            c = Counter()
            for _, s in rows:
                args = s.get("args") or []
                if i < len(args):
                    c[json.dumps(args[i], ensure_ascii=False)[:36]] += 1
            print(f"   a[{i}]: " + ", ".join(f"{k}×{v}" for k, v in c.most_common(8)))
        recv = Counter("有recv" if s.get("recv") is not None else "无recv" for _, s in rows)
        print(f"   {dict(recv)}")
        for c, s in rows[:3]:
            print(f"   例 {c}: {json.dumps(s, ensure_ascii=False)[:230]}")


if __name__ == "__main__":
    main()
