"""检查「没有 OnPlayedFromHand 程序」的卡到底是不是纯 vanilla（无战吼）。

如果一张卡没有任何事件程序，它就是一张白板单位 —— 内核什么都不做**才是正确的**，
不应该被计入「未实现」。这个脚本用来把这两种情况分开。

用法：python check-vanilla-cards.py [--ir ...] [--cards ...]
"""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ir", default=str(ROOT / "docs/card-ir.json"))
    ap.add_argument("--cards", default=str(ROOT / "docs/cards.live.json"))
    ap.add_argument("--effects", default=str(ROOT / "docs/card-effects.json"))
    args = ap.parse_args()

    lib = json.loads(Path(args.ir).read_text(encoding="utf-8"))
    cards = json.loads(Path(args.cards).read_text(encoding="utf-8"))
    effects = json.loads(Path(args.effects).read_text(encoding="utf-8"))

    from collections import Counter

    no_program = []
    for name, card in cards.items():
        entry = lib.get(name)
        programs = list(entry["programs"].keys()) if entry else []
        if "OnPlayedFromHand" in programs:
            continue
        no_program.append((name, programs, card))

    print(f"没有 OnPlayedFromHand 的卡: {len(no_program)}")
    print()

    truly_vanilla = 0
    has_other_triggers = 0
    no_ir = 0

    for name, programs, card in no_program:
        ext = (effects.get(name) or {}).get("external_calls") or []
        if not programs:
            no_ir += 1
            kind = "无 IR（无蓝图/无程序）"
        elif programs:
            has_other_triggers += 1
            kind = f"只有触发式: {programs[:3]}"

        if not ext:
            truly_vanilla += 1
            kind += "  ← 无外部调用 = 纯白板"

        print(f"  {name:<48} {card.get('kredits', '?')}费 {card.get('type', '?'):<10} {kind}")
        txt = (card.get("text") or "").strip()
        if txt:
            print(f"       文本: {txt[:70]}")

    print()
    print(f"  纯白板（无外部调用）: {truly_vanilla}")
    print(f"  有其它触发式程序:     {has_other_triggers}")
    print(f"  完全没有 IR:          {no_ir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
