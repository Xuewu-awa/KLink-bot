#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：从 decompiled/cards.full.json 里按子串找资产 + 函数名，按 StatementIndex 打印语句。"""
import json, sys
sys.path.insert(0, 'out/audit')
from importlib import import_module
m = import_module('p1-brief'.replace('-', '_')) if False else None

def brief_expr(e):
    if e is None: return 'null'
    if not isinstance(e, dict): return repr(e)
    inst = e.get('Inst')
    if inst in ('LocalVariable', 'InstanceVariable'):
        vo = e.get('Variable Outer')
        return e.get('Variable Name') or (vo.get('Variable Name') if isinstance(vo, dict) else '?')
    if inst == 'LocalOutVariable':
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
    return json.dumps(e, ensure_ascii=False)[:200]

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
        return f'PopExecutionFlowIfNot skip={st.get("SkipOffsetForNull")} cond={brief_expr(st.get("Condition"))}'
    if inst in ('Let', 'LetBool', 'LetObj', 'LetName'):
        return f'{inst} {brief_expr(st.get("Variable"))} <- {brief_expr(st.get("Expression"))}'
    if inst in ('LocalVirtualFunction', 'FinalFunction', 'LocalFinalFunction'):
        return f'{inst} {st.get("FunctionName") or st.get("Function")}({", ".join(brief_expr(p) for p in (st.get("Parameters") or []))})'
    if inst == 'Context':
        return f'Context {brief_expr(st)}'
    if inst == 'Return':
        return 'Return'
    return f'{inst} {json.dumps(st, ensure_ascii=False)[:200]}'

def main():
    needle = sys.argv[1]
    want = sys.argv[2] if len(sys.argv) > 2 and not sys.argv[2].startswith('-') else None
    d = json.load(open('klink bot/decompiled/cards.full.json', encoding='utf-8'))
    for k, v in d['assets'].items():
        if needle not in k: continue
        fns = v.get('functions') or {}
        for fn, body in fns.items():
            if want and want != fn: continue
            bc = body.get('bytecode', []) if isinstance(body, dict) else body
            print(f'### {k.split("/")[-1]} :: {fn}  stmts={len(bc)}')
            for st in bc:
                print(f'  si={st.get("StatementIndex"):<6} {brief(st)}')

main()
