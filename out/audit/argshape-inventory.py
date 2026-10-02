#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
argshape-inventory.py —— 「同一个原语、多种实参形状」形状清单

## 为什么需要它

KLink 的离线规则内核按 `CardApiDispatch.cs` 的派发表解释 `card-ir.json` 里的
`call` 指令。**同一个原语在 IR 里的实参形状并不唯一** —— 例如 `Array_Add` 的第二个
实参，有时是卡对象（`{"var":"Item"}`），有时是整数 cardID
（`{"var":"cardID","ctx":{...}}`）。实现只按一种形状写，另一种就静默失效。

已确认的 4 个实例（`Array_Add` / `DoGiveKeyword` / `CustomAbilityAdd` /
`ChangeKreditCost`）都属这一类，所以需要一张**全量**清单来找出剩下的。

## 输出

1. `原语 | 调用点数 | 形状分布` 主表（每个实参位置一行）
2. `out/audit/argshape-callsites.json` —— 全部调用点原始记录，供后续逐个核对
3. 终端上的「可疑点」提示：整数 cardID 出现在被 `AsCard` 读的位置等

## 分类口径

实参形状先做**语法分类**（`{"int":3}` / `{"str":"x"}` / `{"var":"v"}` …），
再对 `{"var":...}` 做**轻量类型推断**（卡对象 / 整数 / 布尔 / 未知）：

- 卡对象：`{"self":true}`、`{"var":"tempCard"}`、`{"obj":...}`，
  以及变量名/成员名带 card 且不带 ID 的
- 整数 cardID：`{"var":"cardID","ctx":{...}}`（= `GetMember(obj,"cardID")`，
  返回 int）、变量名以 ID 结尾的
- 其它按名字启发式归到 `整数(变量)` / `布尔(变量)` / `未知变量`

类型推断是启发式的，但**「同一个位置出现两种互斥语义」这个信号是可靠的** ——
清单的作用就是把这个信号排出来，再逐个读实现确认。
"""

import json
import re
import sys
import collections
import os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IR = os.path.join(ROOT, 'klink bot', 'docs', 'card-ir.json')
DISPATCH = os.path.join(ROOT, 'src', 'KLink.Bot', 'Effects', 'CardApiDispatch.cs')
OUT_JSON = os.path.join(ROOT, 'out', 'audit', 'argshape-callsites.json')


# ---------------------------------------------------------------- 派发表

def load_dispatch_keys():
    """从 CardApiDispatch.cs 抽出派发表的全部原语名。

    只认「行首（可缩进）`["名字"] =`」这一种写法 —— 这样
    `c.State.UnimplementedCalls["x"] = ...` 这类行内索引不会被误收。
    """
    src = open(DISPATCH, encoding='utf-8').read()
    keys = re.findall(r'^\s*\["([^"]+)"\]\s*=', src, re.M)
    return keys, src


# ---------------------------------------------------------------- 类型推断

# 变量名 → 语义。名字是 Kismet 编译出来的，规则相当稳定。
_INT_NAMES = {'side', 'damage', 'index', 'count', 'amount', 'value', 'n', 'kredits'}
_CARD_HINT = re.compile(r'card|Card|target|Target|unit|Unit|victim|Victim|item|Item')


def var_kind(name):
    """变量名启发式 → 'card' / 'int' / 'bool' / 'unknown'。"""
    if name is None:
        return 'unknown'
    if name == 'cardID' or re.search(r'(?<![a-z])ID(_\d+)?$', name):
        return 'int'
    if re.search(r'^(has|is|b|bIs)[A-Z]', name):
        return 'bool'
    if name in _INT_NAMES:
        return 'int'
    if _CARD_HINT.search(name):
        return 'card'
    return 'unknown'


def classify(v, fname=None, pos=None):
    """把一个实参值分类成 (语法形状, 语义种类)。

    语义种类只在语法形状是 `var` / `ctx` 时才有意义。
    """
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
        return 'self', 'card'
    if 'none' in v:
        return '空', '空'
    if 'obj' in v:
        return 'obj', 'card'
    if 'name' in v:
        return '名称', 'str'
    if 'unknown' in v:
        return 'unknown', 'unknown'
    if 'array' in v or 'struct' in v:
        return '结构/数组字面量', 'unknown'
    if 'math' in v:
        return 'math', 'unknown'
    if 'ctx' in v:
        # `{"var":"cardID","ctx":{"var":"obj"}}` = GetMember(obj, "cardID")
        member = v.get('var')
        k = var_kind(member)
        return '成员读取', k
    if 'var' in v:
        k = var_kind(v.get('var'))
        return '变量', k
    return '其它:' + ','.join(sorted(v.keys())), 'unknown'


# ---------------------------------------------------------------- 主流程

def main():
    keys, src = load_dispatch_keys()
    dispatch = set(keys)

    ir = json.load(open(IR, encoding='utf-8'))

    # fn -> {位置: Counter(形状标签)}
    shapes = collections.defaultdict(lambda: collections.defaultdict(collections.Counter))
    calls = collections.defaultdict(int)
    callsites = []

    for card, body in ir.items():
        for s in body.get('steps', []):
            if s.get('op') != 'call':
                continue
            fn = s.get('fn')
            if fn is None:
                continue
            args = s.get('args', [])
            calls[fn] += 1

            recv_shape, recv_kind = classify(s.get('recv'))
            entry = {
                'card': card,
                'fn': fn,
                'i': s.get('i'),
                'in_dispatch': fn in dispatch,
                'recv': {'shape': recv_shape, 'kind': recv_kind},
                'args': [],
            }
            for idx, a in enumerate(args):
                sh, kind = classify(a, fn, idx)
                label = sh if kind in ('unknown', 'card', 'int', 'str', 'bool', 'float', '空') else sh
                # 主表用的标签：语法形状 + 语义种类（语义有用时）
                if sh == '变量' or sh == '成员读取':
                    tag = '%s(%s)' % (sh, kind)
                else:
                    tag = sh
                shapes[fn][idx][tag] += 1
                entry['args'].append({'shape': sh, 'kind': kind, 'tag': tag, 'raw': a})
            callsites.append(entry)

    # ---------------- 主表 ----------------
    print('=' * 100)
    print('形状清单：派发表原语的实参形状分布')
    print('  派发表原语数：%d ；IR 里被调用的原语数：%d' % (len(dispatch), len(calls)))
    print('=' * 100)

    rows = []
    for fn in sorted(dispatch):
        if fn not in calls:
            continue
        per_pos = shapes[fn]
        tags = set()
        for pos, counter in per_pos.items():
            tags |= set(counter.keys())
        rows.append((fn, calls[fn], per_pos, tags))

    # 先排「形状不唯一」的（最有嫌疑），再排单一形状的
    multi = [r for r in rows if len(r[3]) > 1]
    single = [r for r in rows if len(r[3]) == 1]

    print('\n### A. 有 >1 种实参形状的原语（%d 个）—— 嫌疑区\n' % len(multi))
    print('%-42s %7s  %s' % ('原语', '调用点', '形状分布（按实参位置）'))
    print('-' * 100)
    for fn, n, per_pos, tags in sorted(multi, key=lambda r: -r[1]):
        desc = []
        for pos in sorted(per_pos):
            parts = ['%s×%d' % (t, c) for t, c in per_pos[pos].most_common()]
            desc.append('a[%d]: %s' % (pos, ' / '.join(parts)))
        print('%-42s %7d  %s' % (fn, n, desc[0]))
        for extra in desc[1:]:
            print('%-42s %7s  %s' % ('', '', extra))

    print('\n### B. 只有单一形状的原语（%d 个）\n' % len(single))
    print('%-42s %7s  %s' % ('原语', '调用点', '形状'))
    print('-' * 100)
    for fn, n, per_pos, tags in sorted(single, key=lambda r: -r[1]):
        desc = []
        for pos in sorted(per_pos):
            parts = ['%s×%d' % (t, c) for t, c in per_pos[pos].most_common()]
            desc.append('a[%d]: %s' % (pos, ' / '.join(parts)))
        print('%-42s %7d  %s' % (fn, n, '; '.join(desc)))

    # ---------------- 关键信号：整数 cardID ----------------
    print('\n' + '=' * 100)
    print('关键信号：实参里出现「整数 cardID」形状（`{"var":"cardID","ctx":...}` 或 ID 结尾变量）')
    print('  这些位置如果实现用 `AsCard(...)` 读，就恒为 null ⇒ 静默失效')
    print('=' * 100)
    id_sites = collections.defaultdict(lambda: collections.Counter())
    for e in callsites:
        if not e['in_dispatch']:
            continue
        for idx, a in enumerate(e['args']):
            if a['kind'] == 'int' and a['shape'] in ('成员读取', '变量'):
                id_sites[e['fn']][idx] += 1
    for fn in sorted(id_sites, key=lambda f: -sum(id_sites[f].values())):
        pos = ', '.join('a[%d]×%d' % (p, c) for p, c in sorted(id_sites[fn].items()))
        print('  %-42s %s' % (fn, pos))

    json.dump(callsites, open(OUT_JSON, 'w', encoding='utf-8'), ensure_ascii=False)
    print('\n全部调用点原始记录 → %s' % OUT_JSON)


if __name__ == '__main__':
    main()
