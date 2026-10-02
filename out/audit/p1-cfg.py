#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：从 bp-cardfn.json 的某个函数里，按 StatementIndex 做控制流可达性分析。

边规则（StatementIndex 与 Jump/JumpIfNot 的 Offset 同坐标系，已用 TriggerDeployment 验证：
`si=951 Jump off=1363` 正好落在 `si=1363 Return`）：
  Jump off=X          → X
  JumpIfNot off=X     → X（条件假） + 顺序下落（条件真）
  PopExecutionFlow*   → 不建模（回边目标是运行期栈，静态算不出）
  其它                → 顺序下落（按 StatementIndex 升序的下一条）

用法: python out/audit/p1-cfg.py <fn> <startSi> [--avoid Si,Si] [--file out/bp-cardfn.json]
"""
import json, sys
from collections import deque

args = sys.argv[1:]
path = 'out/bp-cardfn.json'
if '--file' in args:
    i = args.index('--file'); path = args[i+1]; del args[i:i+2]
avoid = set()
if '--avoid' in args:
    i = args.index('--avoid'); avoid = {int(x) for x in args[i+1].split(',')}; del args[i:i+2]
fn = args[0]
start = int(args[1])

d = json.load(open(path, encoding='utf-8'))
bc = d[fn]['bytecode'] if isinstance(d[fn], dict) else d[fn]
bc = [s for s in bc if s.get('StatementIndex') is not None]
bc.sort(key=lambda s: s['StatementIndex'])
order = [s['StatementIndex'] for s in bc]
by = {s['StatementIndex']: s for s in bc}
nxt = {order[i]: (order[i+1] if i+1 < len(order) else None) for i in range(len(order))}


def succs(si):
    s = by[si]
    inst = s.get('Inst')
    out = []
    if inst == 'Jump':
        out.append(s['Offset'])
    elif inst == 'JumpIfNot':
        out.append(s['Offset'])
        if nxt[si] is not None:
            out.append(nxt[si])
    elif inst in ('PopExecutionFlow', 'PopExecutionFlowIfNot'):
        pass          # 回边目标在运行期栈上，静态不建模
    elif inst in ('Return', 'EndOfScript'):
        pass
    else:
        if nxt[si] is not None:
            out.append(nxt[si])
    return [t for t in out if t in by and t not in avoid]


seen = set()
q = deque([start])
while q:
    si = q.popleft()
    if si in seen:
        continue
    seen.add(si)
    for t in succs(si):
        if t not in seen:
            q.append(t)

print(f'{fn}  从 si={start} 可达 {len(seen)} 条语句'
      + (f'  （避开 {sorted(avoid)}）' if avoid else ''))
for si in sorted(seen):
    s = by[si]
    print(f'  si={si:<6} {s.get("Inst")}'
          + (f'  -> {s.get("Offset")}' if s.get('Offset') is not None else '')
          + (f'  fn={s.get("FunctionName") or s.get("Function")}'
             if (s.get('FunctionName') or s.get('Function')) else ''))
