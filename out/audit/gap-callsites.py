#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
P2：把一个 BP 函数的**声明参数表**和它在全卡池里的**调用点实参形状**并排列出来。

为什么两个都要：
  · 声明参数表给出**名字和类型**（语义）
  · 调用点实参给出**位置**（哪个 index 是目标、哪个是数值）
只有两者互证了，写进内核的 `IntArg(a, 2)` 才不是猜的。

用法：
  python -X utf8 out/audit/gap-callsites.py AddAttackUntilEndOfTurn
  python -X utf8 out/audit/gap-callsites.py --max 12 DamageMultipleCards MakeCardRetreat
"""
import json
import sys

FULL = 'klink bot/decompiled/cards.full.json'


def brief(node, depth=0):
    """把表达式节点压成一行可读形状。"""
    if not isinstance(node, dict):
        return repr(node)[:60]
    inst = node.get('Inst', '?')
    if inst in ('LocalVariable', 'LocalOutVariable'):
        v = node.get('Variable Name', '?')
        return f'var:{v}'
    if inst == 'InstanceVariable':
        return f'this:{node.get("Variable Name", "?")}'
    if inst == 'ObjectConst':
        return 'const:' + str(node.get('Object', '?')).split('.')[-1]
    if inst in ('IntConst', 'FloatConst', 'BoolConst', 'StringConst', 'NameConst', 'ByteConst'):
        for k in ('Value', 'Int Value', 'String Value', 'Bool Value', 'Name Value'):
            if k in node:
                return f'{inst}:{node[k]}'
        return inst
    if inst in ('CallMath', 'CallFunction', 'LocalVirtualFunction', 'VirtualFunction', 'LocalFinalFunction', 'FinalFunction'):
        fn = node.get('Function') or node.get('FunctionName') or '?'
        params = node.get('Parameters') or []
        inner = ', '.join(brief(p, depth + 1) for p in params[:5]) if depth < 2 else '…'
        return f'{fn}({inner})'
    if inst == 'Context':
        ctx = node.get('Context')
        expr = node.get('Expression')
        return f'ctx[{brief(ctx, depth + 1)}].{brief(expr, depth + 1)}'
    # 兜底：把 Parameters 展开展示
    params = node.get('Parameters')
    if params and depth < 2:
        return inst + '(' + ', '.join(brief(p, depth + 1) for p in params[:5]) + ')'
    return inst


def main():
    args = sys.argv[1:]
    nmax = 8
    if args and args[0] == '--max':
        nmax = int(args[1])
        args = args[2:]

    assets = json.load(open(FULL, encoding='utf-8'))['assets']

    for want in args:
        print('=' * 104)
        print(f'### {want}')
        print('=' * 104)

        # ---- 1) 声明参数表 ----
        decl = None
        for asset, body in assets.items():
            fns = body.get('functions')
            if not isinstance(fns, dict) or want not in fns:
                continue
            fb = fns[want]
            if isinstance(fb, dict):
                print(f'定义资产: {asset.split("/")[-1]}')
                for key in ('parameters', 'params', 'inputs', 'outputs', 'properties', 'signature', 'flags'):
                    if key in fb:
                        print(f'  {key}: {json.dumps(fb[key], ensure_ascii=False)[:600]}')
                decl = fb
        if decl is None:
            print('  （没有函数定义 ⇒ 原生函数，只能看调用点）')

        # ---- 2) 调用点实参形状 ----
        print(f'\n调用点（最多 {nmax} 个）：')
        n = 0
        seen_cards = set()
        for asset, body in assets.items():
            if n >= nmax:
                break
            fns = body.get('functions')
            if not isinstance(fns, dict):
                continue
            for fn, fb in fns.items():
                if n >= nmax:
                    break
                bc = fb.get('bytecode', []) if isinstance(fb, dict) else fb
                if not isinstance(bc, list):
                    continue
                for ins in bc:
                    if n >= nmax or not isinstance(ins, dict):
                        continue
                    s = json.dumps(ins, ensure_ascii=False)
                    if want not in s:
                        continue
                    # 只取「确实是调用」的节点
                    node = ins
                    if 'Expression' in ins and want in json.dumps(ins.get('Expression') or {}, ensure_ascii=False):
                        node = ins['Expression']
                    params = node.get('Parameters')
                    if not isinstance(params, list) or not params:
                        continue
                    card = asset.split('/')[-1].replace('.uasset', '')
                    if card in seen_cards:
                        continue
                    seen_cards.add(card)
                    n += 1
                    args_txt = '  |  '.join(f'[{i}] {brief(p)}' for i, p in enumerate(params))
                    print(f'  {card:36s} {fn[:26]:26s}')
                    print(f'        {args_txt}')
        if n == 0:
            print('  （没找到带 Parameters 的调用点）')
        print()


if __name__ == '__main__':
    main()
