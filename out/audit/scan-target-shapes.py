#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
scan-target-shapes.py —— 「目标卡族」原语的实参形状清单

## 为什么需要它

KLink 的离线规则内核按 `CardApiDispatch.cs` 的派发表解释 `card-ir.json` 里的 `call`
指令。**同一个原语在 IR 里的实参形状并不唯一**：同一个位置的实参，有时是**卡对象**
（`{"var":"tempCard"}` / `{"self":true}`），有时是**整数 cardID**
（`{"var":"cardID","ctx":{...}}` = `GetMember(obj,"cardID")`，返回 int）。
实现只按一种形状写，另一种就**静默失效**（不抛异常，只是效果不打）。

本脚本**只扫「目标卡族」**（见 TARGETS）：这些原语的语义是"作用在另一张卡上"，
所以「第 0 实参到底是不是卡」直接决定效果落到谁身上。

## 输出

1. 主表：`原语 | 调用点数 | 第0实参形状分布 | 第1实参形状分布 | …`
2. `out/audit/target-shapes.json` —— 全部调用点原始记录（含 i= 偏移、recv、args）
3. 终端「嫌疑点」：目标位出现**整数 cardID** 的调用点（实现若用 `AsCard` 读就恒 null）

## 分类口径

先**语法分类**（`int` / `str` / `bool` / `self` / `obj` / `var` / `ctx` / `math` …），
再对 `var` / `ctx` 做**名字启发式**语义推断（卡对象 / 整数 / 未知）。

- 卡对象：`{"self":true}`、`{"obj":...}`、变量名含 card/target/unit/victim/item
- 整数 cardID：`{"var":"cardID","ctx":{...}}`，或变量名以 `ID` 结尾
  （Kismet 编译出的 `K2Node_Event_...ID`、`CallFunc_..._cardID` 全是 int）
- 启发式只用来**排序嫌疑**，结论一律以「读实现 + 读具体调用点」为准。
"""

import json
import re
import os
import sys
import collections

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IR = os.path.join(ROOT, 'klink bot', 'docs', 'card-ir.json')
DISPATCH = os.path.join(ROOT, 'src', 'KLink.Bot', 'Effects', 'CardApiDispatch.cs')
OUT_JSON = os.path.join(ROOT, 'out', 'audit', 'target-shapes.json')

# 任务范围：目标卡族
TARGETS = [
    'DamageCard', 'ChangeAttack', 'ChangeDefense', 'GainAttack', 'GainDefense',
    'LoseAttack', 'SetDefense', 'DestroyCard', 'HealCard', 'ChangeKreditCost',
    'ChangeOperationCost', 'ChangeHeavyArmor', 'GiveBlitz', 'GiveGuard',
    'GiveSmokescreen', 'GiveShock', 'GiveBond', 'GiveAmbush', 'GiveFury',
    'GiveImmune', 'GiveAlpine', 'GiveMobilize', 'GiveSalvage', 'AddHeavyArmor',
    'RemoveBlitz', 'RemoveGuard', 'RemoveSmokescreen', 'PinUnit', 'UnpinUnit',
    'SuppressUnit', 'AddAttackUntilEndOfTurn', 'MakeVeteran', 'GetAdjacentCards',
    'IsSameSideUnit', '_isBigRedOne',
]

# ---------------------------------------------------------------- 形状分类

_INT_NAMES = {'damage', 'index', 'count', 'amount', 'value', 'n',
              'kredits', 'cost', 'attack', 'defense', 'armor', 'num'}
# 阵营变量（`ESideEnum` int）—— ⚠️ **必须和「整数 cardID」分开**：
# `IsSameSideUnit` 的 a[0] 是 `{"var":"side"}`（阵营），不是卡 ID。
# 混为一谈会让"嫌疑点 A"把 `IsSameSideUnit` 误报成"目标位是 cardID"。
_SIDE_NAMES = {'side', 'mySide', 'drawnSide', 'spawnedSide', 'sideGaining',
               'oppositeSide', 'SalvageSide', 'AttackerSide', 'DefenderSide'}
# 变量名里出现这些词 ⇒ 更像卡对象
_CARD_HINT = re.compile(r'card|Card|target|Target|unit|Unit|victim|Victim|item|Item|'
                        r'self|Self|hq|HQ|receiver|Receiver|actor|Actor')
# 以 ID 结尾（且前面不是小写紧邻）⇒ 整数 cardID
_ID_SUFFIX = re.compile(r'(?<![a-z])ID(_\d+)?$')


def var_kind(name):
    """变量名启发式 → 'card' / 'int' / 'side' / 'bool' / 'unknown'。"""
    if name is None:
        return 'unknown'
    if name in _SIDE_NAMES or re.search(r'Side(_\d+)?$', name):
        return 'side'
    if name == 'cardID' or _ID_SUFFIX.search(name):
        return 'int'
    if re.search(r'^(has|is|b|bIs)[A-Z]', name):
        return 'bool'
    if name in _INT_NAMES:
        return 'int'
    if _CARD_HINT.search(name):
        return 'card'
    return 'unknown'


def classify(v):
    """实参值 → (形状标签, 语义 kind)。形状标签是主表用的。"""
    if v is None:
        return '空', '空'
    if not isinstance(v, dict):
        return type(v).__name__, 'unknown'

    if 'int' in v:
        return '整数常量', 'int'
    if 'float' in v:
        return '浮点常量', 'float'
    if 'str' in v:
        return '字符串常量', 'str'
    if 'bool' in v:
        return '布尔常量', 'bool'
    if 'self' in v:
        return '卡对象(self)', 'card'
    if 'none' in v:
        return '空', '空'
    if 'obj' in v:
        return '卡对象(obj)', 'card'
    if 'name' in v:
        return '名称', 'str'
    if 'unknown' in v:
        return 'unknown', 'unknown'
    if 'array' in v or 'struct' in v:
        return '结构/数组字面量', 'unknown'
    if 'math' in v:
        return 'math', 'unknown'
    if 'ctx' in v:
        member = v.get('var')
        k = var_kind(member)
        return '成员读取(%s)' % k, k
    if 'var' in v:
        k = var_kind(v.get('var'))
        return '变量(%s)' % k, k
    return '其它:' + ','.join(sorted(v.keys())), 'unknown'


def shape_group(shape):
    """把细形状归到粗类，主表按粗类统计（细类在 JSON 里）。"""
    if shape == '整数常量':
        return '整数常量'
    if shape == '字符串常量':
        return '字符串常量'
    if shape in ('卡对象(self)', '卡对象(obj)'):
        return '卡对象'
    if shape == '变量(card)':
        return '卡对象(变量)'
    if shape in ('变量(int)', '成员读取(int)'):
        return '整数cardID'
    if shape in ('变量(side)', '成员读取(side)'):
        return '阵营side'
    if shape == '空':
        return '空'
    if shape in ('布尔常量', '浮点常量'):
        return shape
    return '其它变量'


# ---------------------------------------------------------------- 主流程

def main():
    ir = json.load(open(IR, encoding='utf-8'))
    want = set(TARGETS)

    calls = collections.defaultdict(int)
    per_pos = collections.defaultdict(lambda: collections.defaultdict(collections.Counter))
    recvs = collections.defaultdict(collections.Counter)
    sites = []

    for card, body in ir.items():
        for s in body.get('steps', []):
            if s.get('op') != 'call':
                continue
            fn = s.get('fn')
            if fn not in want:
                continue
            args = s.get('args', [])
            calls[fn] += 1
            rs, rk = classify(s.get('recv'))
            recvs[fn][rs] += 1
            rec = {'card': card, 'fn': fn, 'i': s.get('i'), 'recv': s.get('recv'),
                   'args': [], 'raw': s}
            for idx, a in enumerate(args):
                sh, k = classify(a)
                per_pos[fn][idx][shape_group(sh)] += 1
                rec['args'].append({'shape': sh, 'group': shape_group(sh),
                                    'kind': k, 'raw': a})
            sites.append(rec)

    # ---------------- 主表 ----------------
    print('=' * 118)
    print('目标卡族形状清单：%d 个原语，%d 个调用点' % (len(TARGETS), len(sites)))
    print('=' * 118)
    maxpos = max((max(per_pos[f]) for f in per_pos if per_pos[f]), default=-1)
    hdr = '%-26s %6s  %s' % ('原语', '调用点', '  '.join('a[%d]' % p for p in range(maxpos + 1)))
    print(hdr)
    print('-' * 118)
    for fn in TARGETS:
        if fn not in calls:
            print('%-26s %6d  （IR 里没有调用点）' % (fn, 0))
            continue
        cols = []
        for p in range(maxpos + 1):
            c = per_pos[fn].get(p)
            if not c:
                cols.append('-')
                continue
            cols.append('/'.join('%s×%d' % (g, n) for g, n in c.most_common()))
        print('%-26s %6d  %s' % (fn, calls[fn], '  '.join(cols)))

    # ---------------- 嫌疑点 ----------------
    print()
    print('=' * 118)
    print('嫌疑点 A：目标位（a[0]）出现「整数 cardID」—— 实现若用 AsCard 读就恒 null')
    print('=' * 118)
    n_a = 0
    for fn in TARGETS:
        bad = [e for e in sites if e['fn'] == fn and e['args']
               and e['args'][0]['group'] == '整数cardID']
        if bad:
            n_a += len(bad)
            cards = sorted({e['card'] for e in bad})
            print('  %-26s a[0]=整数cardID ×%-4d  卡数=%-4d  例：%s'
                  % (fn, len(bad), len(cards), ', '.join(cards[:4])))
    if not n_a:
        print('  （无）')

    print()
    print('=' * 118)
    print('嫌疑点 A2：a[0] 是「阵营 side」—— 说明这是 **接收者成员函数** `Context{卡}.F(side)`，')
    print('  接收者才是被作用的卡；实现若按 `(卡, 卡)` 或 `AsCard(a[0])` 读就恒 null/false')
    print('=' * 118)
    n_a2 = 0
    for fn in TARGETS:
        bad = [e for e in sites if e['fn'] == fn and e['args']
               and e['args'][0]['group'] == '阵营side']
        if bad:
            n_a2 += len(bad)
            withrecv = sum(1 for e in bad
                           if e['recv'] and e['recv'].get('var'))
            print('  %-26s a[0]=阵营side ×%-4d  带 recv 的 ×%-4d  例：%s'
                  % (fn, len(bad), withrecv,
                     ', '.join(sorted({e['card'] for e in bad})[:3])))
    if not n_a2:
        print('  （无）')

    print()
    print('=' * 118)
    print('嫌疑点 B：目标位（a[0]）是卡对象、但**别的**位置也出现卡对象 —— 实现若「扫全部实参取第一个卡」会挑错')
    print('=' * 118)
    n_b = 0
    for fn in TARGETS:
        bad = []
        for e in sites:
            if e['fn'] != fn or len(e['args']) < 2:
                continue
            if e['args'][0]['group'] not in ('卡对象', '卡对象(变量)'):
                continue
            later = [i for i, a in enumerate(e['args'][1:], 1)
                     if a['group'] in ('卡对象', '卡对象(变量)')]
            if later:
                bad.append((e, later))
        if bad:
            n_b += len(bad)
            ex = bad[0]
            print('  %-26s ×%-4d 例：%s i=%s 卡对象位=%s'
                  % (fn, len(bad), ex[0]['card'], ex[0]['i'],
                     [0] + ex[1]))
    if not n_b:
        print('  （无）')

    print()
    print('=' * 118)
    print('嫌疑点 B2：a[0] **不是卡对象**、但**后面**位置有卡对象 ——')
    print('  实现若「扫全部实参取第一个卡」（`TargetCard`），a[0] 解析不出来时会**挑错卡**（静默打错目标）')
    print('=' * 118)
    n_b2 = 0
    for fn in TARGETS:
        bad = []
        for e in sites:
            if e['fn'] != fn or not e['args']:
                continue
            if e['args'][0]['group'] in ('卡对象', '卡对象(变量)'):
                continue
            later = [i for i, a in enumerate(e['args'][1:], 1)
                     if a['group'] in ('卡对象', '卡对象(变量)')]
            if later:
                bad.append((e, later))
        if bad:
            n_b2 += len(bad)
            ex = bad[0]
            print('  %-26s ×%-4d 例：%s i=%s a[0]=%s 后续卡位=%s'
                  % (fn, len(bad), ex[0]['card'], ex[0]['i'],
                     ex[0]['args'][0]['shape'], [0] + ex[1]))
    if not n_b2:
        print('  （无）')

    print()
    print('=' * 118)
    print('嫌疑点 C：接收者(recv)形状分布 —— `cardFunction` 恒为施法者自己')
    print('=' * 118)
    for fn in TARGETS:
        if fn in recvs:
            print('  %-26s %s' % (fn, '/'.join('%s×%d' % (k, v)
                                               for k, v in recvs[fn].most_common())))

    json.dump(sites, open(OUT_JSON, 'w', encoding='utf-8'),
              ensure_ascii=False, indent=0)
    print('\n全部调用点原始记录 → %s' % OUT_JSON)


if __name__ == '__main__':
    main()
