"""统计每个游戏调用在不同卡上的**实际参数形状**，用来写 CardApi 的适配层。

与其猜签名，不如从全部调用点归纳：
    ChangeDefense(card, int, int, int, bool) × 326 次
这类信息直接告诉我们「第几个参数是目标、第几个是数值」。

用法：
    python analyze-call-shapes.py docs/card-ir.json [--top 40] [--call ChangeDefense]
"""

import argparse
import collections
import json
import sys
from pathlib import Path


def kind(e):
    """给一个实参表达式归类。"""
    if not isinstance(e, dict):
        return "?"
    if e.get("self"):
        return "self"
    if "var" in e:
        v = e["var"]
        return "out" if v.startswith("CallFunc_") else f"var:{v}"
    if "int" in e:
        return "int"
    if "bool" in e:
        return "bool"
    if "str" in e:
        return "str"
    if "obj" in e:
        return "obj"
    if "call" in e:
        return f"call:{e['call']}"
    if "math" in e:
        return f"math:{e['math']}"
    if "none" in e:
        return "none"
    if "array" in e:
        return "array"
    return "?"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("--top", type=int, default=45)
    ap.add_argument("--call", help="只看这一个调用的所有形状")
    args = ap.parse_args()

    print(f"读取 {args.src} ...")
    lib = json.loads(Path(args.src).read_text(encoding="utf-8"))

    shapes = collections.defaultdict(collections.Counter)
    outs = collections.defaultdict(collections.Counter)

    for card in lib.values():
        for prog in card["programs"].values():
            for s in prog["steps"]:
                if s.get("op") != "call":
                    continue
                fn = s.get("fn") or ""
                if not fn:
                    continue
                shape = tuple(kind(a) for a in s.get("args", []))
                shapes[fn][shape] += 1
                # 输出槽的参数名（去掉 CallFunc_<fn>_ 前缀）
                for o in s.get("outs", []):
                    slot = o.get("slot", "")
                    short = slot[len(f"CallFunc_{fn}_"):] if slot.startswith(f"CallFunc_{fn}_") else slot
                    outs[fn][(o.get("param"), short)] += 1

    if args.call:
        print(f"\n=== {args.call} ===")
        for shape, n in shapes[args.call].most_common():
            print(f"  ({', '.join(shape)})   ×{n}")
        print("  输出槽:")
        for (p, short), n in outs[args.call].most_common():
            print(f"    param[{p}] = {short}   ×{n}")
        return 0

    print(f"\n=== 调用最多的 {args.top} 个，及其参数形状 ===")
    total = sum(sum(c.values()) for c in shapes.values())
    print(f"（全部调用点 {total} 个，不同调用 {len(shapes)} 个）\n")

    for fn, counter in sorted(shapes.items(), key=lambda kv: -sum(kv[1].values()))[: args.top]:
        n = sum(counter.values())
        top = counter.most_common(3)
        outinfo = outs[fn].most_common(2)
        outstr = "  → " + ", ".join(f"p{p}={s}" for (p, s), _ in outinfo) if outinfo else ""
        print(f"{fn}  ×{n}{outstr}")
        for shape, cnt in top:
            print(f"     ({', '.join(shape)})  ×{cnt}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
