#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
atlantic-convoy-evidence.py —— 对局 508065 `card_event_atlantic_convoy` 的两组证据

① 「生成卡」一族的**全部调用形状**（含接收者）—— 用来判定
   `SpawnCardOnBattlefield` / `SpawnCardInHandBySide` 的参数位有没有读错。
② 「扫全卡池」形状的程序清单 —— 这些程序在 `KismetVm.MaxStepsPerProgram=5000`
   的预算下会被硬截断（`atlantic_convoy` 就是第一个被证实的）。

输出：out/audit/atlantic-convoy-evidence.txt
"""

import json
import os
import sys
import collections

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IR = os.path.join(ROOT, 'klink bot', 'docs', 'card-ir.json')

SPAWN_FNS = [
    'SpawnCardOnBattlefield', 'SpawnCardInHandBySide', 'SpawnCardInHand',
    'SpawnCardInDeckBySide', 'SpawnCardInDeck', 'GetRandomCard',
]

# 事件程序里合法的最长循环 = 「扫一遍 GetActiveStaticCards 卡池」。
# 每张卡 13 步（Array_Get / EnumCompareFaction / popFlow / 计数 / Array_Length /
# Less / jump / idx=… ），2021 张 ⇒ ~27000 步。
POOL = 2021


def classify(v):
    if v is None:
        return '空'
    if not isinstance(v, dict):
        return type(v).__name__
    if 'int' in v:
        return '整数常量'
    if 'bool' in v:
        return '布尔常量'
    if 'str' in v:
        return '字符串常量'
    if 'self' in v:
        return 'self(施法者)'
    if 'obj' in v:
        return 'obj字面量'
    if 'ctx' in v:
        return '成员读取(%s).%s' % (v.get('ctx', {}).get('var'), v.get('var'))
    if 'var' in v:
        return '变量(%s)' % v.get('var')
    if 'math' in v:
        return 'math'
    if 'call' in v:
        return 'call(%s)' % v.get('call')
    return '其它'


def main():
    ir = json.load(open(IR, encoding='utf-8'))
    out = []

    def p(s=''):
        out.append(s)

    # ---------------- ① 生成卡一族的调用形状 ----------------
    p('=' * 110)
    p('① 「生成卡」一族的全部调用形状（原语 | 程序 | i= | recv | a[0..n]）')
    p('=' * 110)
    shapes = collections.defaultdict(list)
    for card, body in ir.items():
        for prog_name, prog in _programs(body):
            for s in prog.get('steps', []):
                if s.get('op') != 'call' or s.get('fn') not in SPAWN_FNS:
                    continue
                shapes[s['fn']].append((card, prog_name, s))

    for fn in SPAWN_FNS:
        sites = shapes.get(fn, [])
        p()
        p('---- %s：%d 个调用点 ----' % (fn, len(sites)))
        # 每个实参位置上的形状分布
        maxpos = max((len(s.get('args', [])) for _, _, s in sites), default=0)
        for pos in range(maxpos):
            c = collections.Counter()
            for _, _, s in sites:
                args = s.get('args', [])
                c[classify(args[pos]) if pos < len(args) else '—'] += 1
            p('   a[%d]: %s' % (pos, '  '.join('%s×%d' % kv for kv in c.most_common())))
        r = collections.Counter(classify(s.get('recv')) for _, _, s in sites)
        p('   recv: %s' % '  '.join('%s×%d' % kv for kv in r.most_common()))
        # 具体样例（最多 6 个）
        for card, prog, s in sites[:6]:
            args = ' , '.join(classify(a) for a in s.get('args', []))
            p('     %-42s %-18s i=%-5s  %s' % (card, prog, s.get('i'), args))

    # ---------------- ② 会被 5000 步截断的「扫全池」程序 ----------------
    #
    # 动态步数怎么估：找出「回跳环」= 从 jump 的 i= 回到更小的 to=，
    # 环体长度 = 该区间里的语句数，一张卡池模板跑一轮 ≈ 环体长度，
    # 全池 2021 张 ⇒ 动态 ≈ 环体长度 × 2021（实际循环体里还夹着一条
    # `Array_Get` 的 6 条语句，量级一致）。
    # ⚠️ 只有 **事件程序**（ubergraph / OnPlayedFromHand）受 5000 约束；
    #    局部函数（locals）走 MaxStepsPerLocalProgram=400000，本来就不受限。
    p()
    p('=' * 110)
    p('② 「扫全卡池」形状的程序 —— 动态步数 vs 两个上限')
    p('  事件程序上限 5000（旧）/ 105,092（新，2021×52）；局部函数上限 400,000')
    p('=' * 110)
    risky = []
    for card, body in ir.items():
        for prog_name, prog in _programs(body):
            steps = prog.get('steps', [])
            has_pool = any(s.get('op') == 'call' and s.get('fn') == 'GetAllActiveStaticCards'
                           for s in steps)
            if not has_pool:
                continue
            best = 0
            for s in steps:
                if s.get('op') not in ('jump', 'popFlow', 'popFlowIfNot'):
                    continue
                if not isinstance(s.get('to'), int) or not isinstance(s.get('i'), int):
                    continue
                if s['to'] >= s['i']:
                    continue
                body_len = sum(1 for t in steps
                               if isinstance(t.get('i'), int) and s['to'] <= t['i'] <= s['i'])
                best = max(best, body_len)
            if best:
                risky.append((card, prog_name, len(steps), best, best * POOL))

    ev = [r for r in risky if r[1] == 'OnPlayedFromHand']
    lo = [r for r in risky if r[1] != 'OnPlayedFromHand']
    p()
    p('  【事件程序（旧上限 5000）】共 %d 个 —— 这些才是真正会撞墙的' % len(ev))
    for card, prog, n, bl, dyn in sorted(ev, key=lambda r: -r[4]):
        flag = '★撞墙' if dyn > 5000 else '  ok  '
        p('    %s %-46s 环体=%-4d 动态≈%-8d 语句数=%d' % (flag, card, bl, dyn, n))
    p()
    p('  【局部函数（上限 400000，本来就不受限）】共 %d 个' % len(lo))
    for card, prog, n, bl, dyn in sorted(lo, key=lambda r: -r[4])[:12]:
        p('      %-46s %-32s 环体=%-4d 动态≈%d' % (card, prog, bl, dyn))
    if len(lo) > 12:
        p('      … 另 %d 个' % (len(lo) - 12))

    txt = '\n'.join(out)
    dest = os.path.join(ROOT, 'out', 'audit', 'atlantic-convoy-evidence.txt')
    open(dest, 'w', encoding='utf-8').write(txt + '\n')
    print(txt)
    print('\n→ %s' % dest)


def _programs(body):
    """(程序名, 程序体) —— 含 ubergraph 与 locals。"""
    yield 'OnPlayedFromHand', {'steps': body.get('steps', [])}
    for name, prog in (body.get('locals') or {}).items():
        yield name, prog if isinstance(prog, dict) else {'steps': prog}


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
