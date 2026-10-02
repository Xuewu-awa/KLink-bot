"""列出「内核没有效果可执行」的卡，并判断它们是纯白板还是别的程序名。

用法：python check-missing-effect-cards.py card1 card2 ...
      python check-missing-effect-cards.py --from-stdin
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent

lib = json.loads((ROOT / "docs/card-ir.json").read_text(encoding="utf-8"))
cards = json.loads((ROOT / "docs/cards.live.json").read_text(encoding="utf-8"))
eff = json.loads((ROOT / "docs/card-effects.json").read_text(encoding="utf-8"))

names = [a for a in sys.argv[1:] if not a.startswith("--")]
if not names:
    names = [l.strip() for l in sys.stdin if l.strip()]

for n in names:
    c = cards.get(n) or {}
    e = lib.get(n)
    progs = sorted(e["programs"].keys()) if e else []
    ext = (eff.get(n) or {}).get("external_calls") or []
    print(n)
    print("   {k}费 {a}/{d} {t}".format(
        k=c.get("kredits", "?"), a=c.get("attack", "?"), d=c.get("defense", "?"), t=c.get("type", "?")))
    print(f"   文本: {(c.get('text') or '')[:90]}")
    print(f"   IR 程序: {progs if progs else '（无）'}")
    print(f"   外部调用: {len(ext)}  {ext[:6]}")
