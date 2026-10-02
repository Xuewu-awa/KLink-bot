#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：把 bp-cardfn.json 里某函数的语句压成一行一条的摘要。"""
import json, sys, re

def brief(st):
    inst = st.get('Inst')
    if inst == 'JumpIfNot':
        return f'JumpIfNot off={st.get("Offset")} cond={brief_expr(st.get("Condition"))}'
    if inst == 'Jump':
        return f'Jump off={st.get("Offset")}'
    if inst == 'PushExecutionFlow':
        return f'PushExecutionFlow off={st.get("Offset")}'
    if inst == 'PopExecutionFlow':
        return f'PopExecutionFlow off={st.get("Offset")}'
    if inst == 'PopExecutionFlowIfNot':
        return f'PopExecutionFlowIfNot off={st.get("SkipOffsetForNull")} cond={brief_expr(st.get("Condition"))}'
    if inst == 'Let' or inst == 'LetBool' or inst == 'LetObj' or inst == 'LetName':
        return f'{inst} {brief_expr(st.get("Variable"))} <- {brief_expr(st.get("Expression"))}'
    if inst in ('LocalVirtualFunction', 'FinalFunction', 'LocalFinalFunction'):
        return f'{inst} {st.get("FunctionName") or st.get("Function")}({", ".join(brief_expr(p) for p in (st.get("Parameters") or []))})'
    return f'{inst} {brief_expr(st)}'

def brief_expr(e):
    if e is None: return 'null'
    if not isinstance(e, dict): return repr(e)
    inst = e.get('Inst')
    if inst == 'LocalVariable' or inst == 'InstanceVariable':
        vo = e.get('Variable Outer')
        return e.get('Variable Name') or (vo.get('Variable Name') if isinstance(vo, dict) else '?')
    if inst == 'LocalOutVariable':
        vo = e.get('Variable Outer')
        return 'out:' + (e.get('Variable Name') or '?')
    if inst in ('IntConst','ByteConst','FloatConst','BoolConst','StringConst','NameConst','ObjectConst'):
        return f'{inst}({e.get("Value", e.get("Object"))})'
    if inst in ('True','False','Self','NoObject','Null'):
        return inst
    if inst == 'Context':
        return f'{brief_expr(e.get("Context"))}.{e.get("RValuePropertyName") or brief_expr(e.get("Expression"))}'
    if inst == 'CallMath':
        return f'{e.get("Function")}({", ".join(brief_expr(p) for p in (e.get("Parameters") or []))})'
    if inst in ('FinalFunction','LocalVirtualFunction','LocalFinalFunction'):
        return f'{e.get("Function") or e.get("FunctionName")}({", ".join(brief_expr(p) for p in (e.get("Parameters") or []))})'
    if inst == 'CallFunc':
        return f'CallFunc({e.get("Function")})'
    return json.dumps(e, ensure_ascii=False)[:120]

def main():
    args = sys.argv[1:]
    path = 'out/bp-cardfn.json'
    if '--file' in args:
        i = args.index('--file'); path = args[i+1]; del args[i:i+2]
    fn = args[0]
    lo = int(args[1]) if len(args) > 1 else -10**9
    hi = int(args[2]) if len(args) > 2 else 10**9
    d = json.load(open(path, encoding='utf-8'))
    if fn not in d:
        print('没有:', fn, [k for k in d if fn.lower() in k.lower()][:20]); return
    body = d[fn]
    bc = body['bytecode'] if isinstance(body, dict) else body
    print(f'### {fn}  stmts={len(bc)}')
    for st in bc:
        si = st.get('StatementIndex')
        if si is None or si < lo or si > hi: continue
        print(f'  si={si:<6} {brief(st)}')

main()
