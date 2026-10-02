"""把 bp-*.json 里某个函数的语句按 StatementIndex 顺序打印成可读形式。

用法:
  python out/audit/p0-dump-fn.py <bp-json> <FunctionName> [起始下标] [条数]
  python out/audit/p0-dump-fn.py <bp-json> --find <子串>     # 找含该子串的语句

证据出处用：StatementIndex 与 Jump/JumpIfNot 的 Offset 同坐标系。
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")


def short(v, depth=0):
    if isinstance(v, dict):
        inst = v.get("Inst")
        if inst == "LocalVariable":
            return v.get("Variable Name")
        if inst in ("IntConst", "FloatConst", "BoolConst", "StringConst"):
            for k in ("Value", "IntValue", "BoolValue", "StringValue"):
                if k in v:
                    return repr(v[k])
            return json.dumps(v, ensure_ascii=False)
        if inst == "FinalFunction" or inst == "LocalFinalFunction":
            return "FinalFunction %s" % v.get("Function")
        if inst == "LocalVirtualFunction" or inst == "VirtualFunction":
            return "%s %s" % (inst, v.get("FunctionName") or v.get("Function"))
        if inst == "Context":
            return "Context{%s}.%s" % (short(v.get("Context"), depth + 1), short(v.get("Expression"), depth + 1))
        if inst == "CallMath":
            return "Math %s" % v.get("Function")
        if depth > 4:
            return inst or "?"
        keys = [k for k in v.keys() if k not in ("StatementIndex",)]
        return "%s{%s}" % (inst, ", ".join("%s=%s" % (k, short(v[k], depth + 1)) for k in keys[:6]))
    if isinstance(v, list):
        return "[%s]" % ", ".join(short(x, depth + 1) for x in v)
    return repr(v)


def stmt(s):
    inst = s.get("Inst")
    idx = s.get("StatementIndex")
    parts = []
    if inst == "Jump":
        parts.append("Jump %s" % s.get("Offset"))
    elif inst == "JumpIfNot":
        parts.append("JumpIfNot %s" % s.get("Offset"))
    if "FunctionName" in s:
        parts.append(s["FunctionName"])
    if "Function" in s:
        parts.append(str(s["Function"]))
    for k in ("Variable", "Expression", "Context", "Target", "Value", "Offset"):
        if k in s and k != "Offset":
            parts.append("%s=%s" % (k, short(s[k])))
    if "Parameters" in s:
        parts.append("args=[%s]" % ", ".join(short(p) for p in s["Parameters"]))
    return "i=%-5s %-22s %s" % (idx, inst, "  ".join(parts))


def main():
    path = sys.argv[1]
    d = json.load(open(path, encoding="utf-8"))
    if sys.argv[2] == "--find":
        needle = sys.argv[3]
        for fname, f in d.items():
            for s in (f or {}).get("bytecode") or []:
                line = stmt(s)
                if needle in line:
                    print("[%s] %s" % (fname, line))
        return
    fname = sys.argv[2]
    lo = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    cnt = int(sys.argv[4]) if len(sys.argv) > 4 else 10 ** 9
    f = d[fname]
    for s in f["bytecode"][lo:lo + cnt]:
        print(stmt(s))


main()
