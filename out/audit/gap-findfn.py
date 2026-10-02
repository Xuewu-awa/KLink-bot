#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
P2：对「内核缺口 TOP N」的调用名，找出它到底是谁定义的。

背景：缺口名字分三类，处理方式完全不同 ——
  ① BP 自己实现的函数（如 BP_CardFunctions::Something）→ 直接反编译函数体就能照抄语义
  ② 原生 /Script/kards.* 函数 → 反编译不到，只能靠调用点形状推
  ③ 卡私有函数（<local-ran:xxx>）→ 在 card-ir.json 的 locals 里，应当由 VM 执行

用法：
  python -X utf8 out/audit/gap-findfn.py AddAttackUntilEndOfTurn DamageMultipleCards ...
  python -X utf8 out/audit/gap-findfn.py --stmts BP_CardFunctions AddAttackUntilEndOfTurn
"""
import json
import sys
import re

FULL = 'klink bot/decompiled/cards.full.json'
IR = 'klink bot/docs/card-ir.json'

LOADED = None


def load():
    global LOADED
    if LOADED is None:
        LOADED = json.load(open(FULL, encoding='utf-8'))['assets']
    return LOADED


def find_defs(name):
    """哪些资产的**函数定义**里出现了这个名字（定义 vs 调用点要分开看）。"""
    assets = load()
    defs, calls = [], []
    for asset, body in assets.items():
        fns = body.get('functions')
        if not isinstance(fns, dict):
            continue
        for fn, fb in fns.items():
            bc = fb.get('bytecode', []) if isinstance(fb, dict) else fb
            if not isinstance(bc, list):
                continue
            blob = json.dumps(bc, ensure_ascii=False)
            if name not in blob:
                continue
            stem = asset.split('/')[-1]
            # 定义：函数名字里带这个名字（例如 GetStaticCard 由 math::GetStaticCard 定义）
            if name.lower() in fn.lower():
                defs.append((stem, fn, len(bc), asset))
            else:
                # 只收「确实是调用」的：表达式里出现 Inst=Call 且 Function 名匹配
                for ins in bc:
                    if not isinstance(ins, dict):
                        continue
                    expr = ins.get('Expression') or {}
                    if not isinstance(expr, dict):
                        continue
                    fname = expr.get('Function') or expr.get('Variable Name') or ''
                    if isinstance(fname, str) and name.lower() in fname.lower():
                        calls.append((stem, fn, fname, asset))
                        break
    return defs, calls


def ir_local_cards(name):
    """IR 里哪些卡带这个名字的 locals 程序（= 卡私有函数）。"""
    try:
        ir = json.load(open(IR, encoding='utf-8'))
    except OSError:
        return []
    out = []
    cards = ir.get('cards') or ir
    if not isinstance(cards, dict):
        return []
    for card, c in cards.items():
        if not isinstance(c, dict):
            continue
        loc = c.get('locals') or {}
        if isinstance(loc, dict) and name in loc:
            out.append(card)
    return out


def main():
    args = sys.argv[1:]
    show_stmts = False
    if args and args[0] == '--stmts':
        show_stmts = True
        args = args[1:]

    for name in args:
        print('=' * 78)
        print(f'### {name}')
        defs, calls = find_defs(name)

        loc = ir_local_cards(name)
        if loc:
            print(f'  [③ 卡私有函数] IR locals 命中 {len(loc)} 张卡，例：{", ".join(loc[:4])}')

        if defs:
            print(f'  [①/② 函数定义] {len(defs)} 处：')
            seen = set()
            for stem, fn, n, asset in defs:
                key = (stem, fn)
                if key in seen:
                    continue
                seen.add(key)
                print(f'      {stem:34s} {fn:52s} {n:5d} 语句')
        else:
            print('  [② 找不到定义] ⇒ 很可能是原生 /Script/kards.* 函数（反编译不到）')

        if calls:
            print(f'  [调用点] {len(calls)} 处，样例：')
            seen = set()
            for stem, fn, fname, asset in calls:
                if stem in seen:
                    continue
                seen.add(stem)
                if len(seen) > 6:
                    break
                print(f'      {stem:34s} 在 {fn:44s} 调 {fname}')
        print()


if __name__ == '__main__':
    main()
