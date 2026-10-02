"""在已 dump 的蓝图 JSON 里按字符串定位语句（带函数名 + StatementIndex + 原文）。

用法:
  python out/audit/bpfind.py OnOtherCardBecomingVeteran
  python out/audit/bpfind.py --fn MakeVeteran            # 整个函数逐语句
  python out/audit/bpfind.py --fn MakeVeteran --range 20 60

只用 UAssetCLI dump 出来的 JSON（StatementIndex 与 Jump/JumpIfNot 的 Offset 同坐标系）。
"""
import json
import pathlib
import sys

ROOT = pathlib.Path(r"<repo-root>")
DUMPS = sorted(ROOT.glob("out/bp-*.json")) + sorted(ROOT.glob("out/*-bp.json"))


def load_all():
    out = []
    for p in DUMPS:
        try:
            d = json.loads(p.read_text(encoding="utf-8"))
        except Exception as e:  # noqa
            print(f"!! {p.name}: {e}", file=sys.stderr)
            continue
        if isinstance(d, dict):
            out.append((p.name, d))
    return out


def stmt_text(s):
    return json.dumps(s, ensure_ascii=False)


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    fn = None
    lo = hi = None
    needles = []
    i = 0
    while i < len(args):
        if args[i] == "--fn":
            fn = args[i + 1]; i += 2
        elif args[i] == "--range":
            lo, hi = int(args[i + 1]), int(args[i + 2]); i += 3
        else:
            needles.append(args[i]); i += 1

    for dump_name, dump in load_all():
        for fname, body in dump.items():
            if fn is not None and fname != fn:
                continue
            bc = body.get("bytecode") if isinstance(body, dict) else None
            if not isinstance(bc, list):
                continue
            for idx, s in enumerate(bc):
                if lo is not None and not (lo <= idx <= hi):
                    continue
                txt = stmt_text(s)
                if fn is not None or any(n in txt for n in needles):
                    print(f"{dump_name} :: {fname}  i={idx}")
                    print(f"    {txt[:400]}")


if __name__ == "__main__":
    main()
