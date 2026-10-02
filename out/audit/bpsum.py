"""把蓝图函数压成"一行一语句"，并且**把跳转目标解析成 StatementIndex**。

为什么必须这样：`bpasm` 的标签定位不可靠；Jump/JumpIfNot/PopExecutionFlowIfNot 的
`Offset` 是**字节码偏移**，要和同一条语句上的 `StatementIndex` 同坐标系才对得上。
本脚本先建 Offset→StatementIndex 映射，再把跳转标注成 `->si=N`。

用法:
  python out/audit/bpsum.py out/bp-cardfn.json SuppressMultipleUnits 5700 6300
  python out/audit/bpsum.py out/bp-cardfn.json MakeVeteran
（第 3/4 个参数是 **StatementIndex** 区间）
"""
import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = pathlib.Path(r"<repo-root>")
JUMPY = ("Jump", "JumpIfNot", "PopExecutionFlowIfNot", "JumpIfNot_")

CONST_KEYS = ("Value", "Function", "FunctionName", "Variable Name", "Name")


def collect_offsets(node, out):
    if isinstance(node, dict):
        si, off = node.get("StatementIndex"), node.get("Offset")
        if isinstance(si, int):
            out.add(si)
        if isinstance(off, int):
            out.add(off)
        for v in node.values():
            collect_offsets(v, out)
    elif isinstance(node, list):
        for v in node:
            collect_offsets(v, out)


def walk(node):
    if isinstance(node, dict):
        inst = node.get("Inst")
        bits = []
        if inst:
            bits.append(str(inst))
        for key in CONST_KEYS:
            if key in node and not isinstance(node[key], dict):
                bits.append(f"{key}={node[key]}")
        var = node.get("Variable")
        if isinstance(var, dict) and "Variable Name" in var:
            bits.append(f"var={var['Variable Name']}")
        subs = []
        for k in ("Expression", "Condition", "Context", "Parameters", "Value"):
            if k in node:
                subs.append(walk(node[k]))
        sub = " | ".join(x for x in subs if x)
        s = " ".join(bits)
        return (s + ("  <" + sub + ">") if sub else s) if s else sub
    if isinstance(node, list):
        return " ; ".join(x for x in (walk(v) for v in node) if x)
    return str(node)


def main():
    dump = ROOT / sys.argv[1]
    fn = sys.argv[2]
    lo = int(sys.argv[3]) if len(sys.argv) > 3 else -1
    hi = int(sys.argv[4]) if len(sys.argv) > 4 else 10 ** 9
    d = json.loads(dump.read_text(encoding="utf-8"))
    body = d.get(fn)
    if body is None:
        print(f"!! {fn} 不在 {dump.name}；相近: {[k for k in d if fn.lower() in k.lower()]}")
        return
    bc = body["bytecode"]
    off2si = set()
    for s in bc:
        collect_offsets(s, off2si)

    print(f"=== {dump.name} :: {fn}  {len(bc)} 条语句 ===")
    for idx, s in enumerate(bc):
        si = s.get("StatementIndex")
        if si is None:
            continue
        if not (lo <= si <= hi):
            continue
        line = walk(s)
        # 跳转目标解析
        tgt = ""
        for key in ("Offset",):
            pass
        for k in ("Condition",):
            pass
        # Jump/JumpIfNot 的目标藏在语句自己或 Condition 里
        def find_offsets(node):
            res = []
            if isinstance(node, dict):
                if str(node.get("Inst", "")).startswith("Jump") and isinstance(node.get("Offset"), int):
                    res.append((node.get("Inst"), node["Offset"]))
                for v in node.values():
                    res += find_offsets(v)
            elif isinstance(node, list):
                for v in node:
                    res += find_offsets(v)
            return res

        jmps = find_offsets(s)
        if jmps:
            tgt = "   [" + ", ".join(
                f"{i}->si={o}{'' if o in off2si else ' ?(无此语句)'}" for i, o in jmps) + "]"
        # 语句自己的 Offset（作为跳转落点时用）
        own = f"off={s.get('Offset')}" if isinstance(s.get("Offset"), int) else ""
        print(f"si={si:<5} {own:<11} {line[:300]}{tgt}")


if __name__ == "__main__":
    main()
