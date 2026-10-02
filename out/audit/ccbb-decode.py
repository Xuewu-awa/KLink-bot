#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ccbb-* 调查脚本 1：解码 BP_CardFunctions::CanCardBeBuffed，并列出全部调用点。

只读 out/bp-cardfn.json，不写任何 src/tools。
"""
import json
import sys
from collections import deque

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = r"<repo-root>"
D = json.load(open(ROOT + r"\out\bp-cardfn.json", encoding="utf-8"))


def stmts(fn):
    bc = D[fn]["bytecode"] if isinstance(D[fn], dict) else D[fn]
    bc = [s for s in bc if s.get("StatementIndex") is not None]
    bc.sort(key=lambda s: s["StatementIndex"])
    return bc


def brief(s, depth=0):
    """把一条语句压成一行可读文本。"""
    inst = s.get("Inst")
    if inst == "Jump":
        return f"JUMP -> {s.get('Offset')}"
    if inst == "JumpIfNot":
        return f"JUMP-IF-NOT({expr(s.get('Condition'))}) -> {s.get('Offset')}"
    if inst == "PopExecutionFlow":
        return "POP-FLOW"
    if inst == "PopExecutionFlowIfNot":
        return f"POP-FLOW-IF-NOT({expr(s.get('Condition'))})"
    if inst == "Return":
        return "RETURN"
    if inst == "EndOfScript":
        return "END-OF-SCRIPT"
    if inst == "LetBool":
        return f"bool {expr(s.get('Variable'))} = {expr(s.get('Expression'))}"
    if inst == "LetObj":
        return f"obj {expr(s.get('Variable'))} = {expr(s.get('Expression'))}"
    if inst == "Let":
        return f"{expr(s.get('Variable'))} = {expr(s.get('Expression'))}"
    if inst == "Context":
        # 形如  <Context>.<FinalFunction>(...)
        rv = s.get("RValuePropertyName")
        e = s.get("Expression") or {}
        if rv:
            return f"({expr(s.get('Context'))}).{rv}   [Context rvalue]"
        return f"{expr(s.get('Context'))} . {expr(e)}"
    return f"{inst} {json.dumps({k: v for k, v in s.items() if k != 'StatementIndex'}, ensure_ascii=False)[:180]}"


def expr(e, depth=0):
    if not isinstance(e, dict):
        return repr(e)
    inst = e.get("Inst")
    if inst in ("True", "False", "Nothing"):
        return inst
    if inst == "ByteConst":
        return str(e.get("Value"))
    if inst == "IntConst":
        return str(e.get("Value"))
    if inst == "LocalVariable" or inst == "LocalOutVariable":
        return str(e.get("Variable Name"))
    if inst == "InstanceVariable":
        return f"this.{e.get('Variable Name')}"
    if inst == "FinalFunction" or inst == "Function":
        ps = ", ".join(expr(p) for p in (e.get("Parameters") or []))
        return f"{e.get('Function')}({ps})"
    if inst == "CallMath":
        ps = ", ".join(expr(p) for p in (e.get("Parameters") or []))
        return f"{e.get('Function')}({ps})"
    if inst == "Context":
        ctx = expr(e.get("Context"))
        rv = e.get("RValuePropertyName")
        if rv:
            return f"{ctx}.{rv}"
        return f"({ctx}).{expr(e.get('Expression'))}"
    ps = e.get("Parameters")
    if ps is not None:
        return f"{e.get('Function', inst)}({', '.join(expr(p) for p in ps)})"
    return json.dumps(e, ensure_ascii=False)[:140]


def decode(fn):
    bc = stmts(fn)
    by = {s["StatementIndex"]: s for s in bc}
    print(f"===== {fn} : {len(bc)} 条语句 =====")
    for s in bc:
        si = s["StatementIndex"]
        print(f"  si={si:<6} {brief(s)}")


def reachable(fn, start):
    bc = stmts(fn)
    order = [s["StatementIndex"] for s in bc]
    by = {s["StatementIndex"]: s for s in bc}
    nxt = {order[i]: (order[i + 1] if i + 1 < len(order) else None) for i in range(len(order))}

    def succs(si):
        s = by[si]
        inst = s.get("Inst")
        out = []
        if inst == "Jump":
            out.append(s["Offset"])
        elif inst == "JumpIfNot":
            out.append(s["Offset"])
            if nxt[si] is not None:
                out.append(nxt[si])
        elif inst in ("PopExecutionFlow", "PopExecutionFlowIfNot"):
            pass
        elif inst in ("Return", "EndOfScript"):
            pass
        else:
            if nxt[si] is not None:
                out.append(nxt[si])
        return [t for t in out if t in by]

    seen, q = set(), deque([start])
    while q:
        si = q.popleft()
        if si in seen:
            continue
        seen.add(si)
        for t in succs(si):
            if t not in seen:
                q.append(t)
    return sorted(seen)


def _is_call_to(s, target):
    """这条语句是不是「调用 target」。
    BP_CardFunctions 里跨函数调用编成 LocalVirtualFunction（带 FunctionName）。"""
    if s.get("Inst") == "LocalVirtualFunction":
        return s.get("FunctionName") == target
    if s.get("Inst") == "Context":
        e = s.get("Expression") or {}
        return e.get("Function") == target or e.get("FunctionName") == target
    if s.get("Inst") == "FinalFunction":
        return s.get("Function") == target
    return False


def callers(target):
    """列出所有调用 target 的函数 + 调用点 si + 之后的守位语句。"""
    out = []
    for fn in D:
        body = D[fn]
        bc = body.get("bytecode") if isinstance(body, dict) else body
        if not bc:
            continue
        bc = sorted([s for s in bc if s.get("StatementIndex") is not None],
                    key=lambda s: s["StatementIndex"])
        for i, s in enumerate(bc):
            if not _is_call_to(s, target):
                continue
            nxt = bc[i + 1] if i + 1 < len(bc) else None
            nxt2 = bc[i + 2] if i + 2 < len(bc) else None
            out.append((fn, s, nxt, nxt2))
    return out


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else "all"
    if what in ("all", "decode"):
        decode("CanCardBeBuffed")
    if what in ("all", "callers"):
        print()
        print("===== CanCardBeBuffed 调用点 =====")
        for fn, s, n1, n2 in callers("CanCardBeBuffed"):
            si = s["StatementIndex"]
            # 该调用的实参
            ps = ", ".join(expr(p) for p in ((s.get("Expression") or {}).get("Parameters") or []))
            print(f"\n  [{fn}] si={si}  CanCardBeBuffed({ps})")
            if n1:
                print(f"      next  si={n1['StatementIndex']:<6} {brief(n1)}")
            if n2:
                print(f"      next2 si={n2['StatementIndex']:<6} {brief(n2)}")
