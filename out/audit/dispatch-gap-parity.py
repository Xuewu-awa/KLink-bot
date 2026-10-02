# -*- coding: utf-8 -*-
"""
用**与 C# 守卫（tools/BotSim/DispatchGap.cs）完全相同的规则**重算缺口，
以便和 `out/audit/missing-keys-classify2.py` 的数字对账。

规则（与 C# 一致）：
  遍历 lib.AllCards 的 Steps **和** Locals 的全部 body；
  op == "call" 且 fn 不在派发表、且 `FindLocalProgram(card, fn)` 为 null ⇒ 真缺口。
`FindLocalProgram` 会先试卡名，再试 `ResolveBaseName`（剥 `_bal`/`_vet` 之类的变体后缀）。
"""
import json, re, os
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
keys = set(re.findall(r'^\s*\["([^"]+)"\]\s*=',
                      open('src/KLink.Bot/Effects/CardApiDispatch.cs', encoding='utf-8').read(), re.M))

# 变体后缀（CardDatabase.VariantSuffixes）
SUFFIXES = ['_bal', '_vet', '_trop', '_cam1', '_cam2', '_cam3', '_skirm', '_promo',
            '_tutorial', '_ai', '_unlock1', '_unlock2', '_unlock3', '_unlock4', '_unlock5']


def candidates(name):
    yield name
    for s in SUFFIXES:
        if name.endswith(s):
            base = name[: -len(s)]
            if base != name:
                yield base
            return


def find_local(card_name, fn):
    for cand in candidates(card_name):
        c = ir.get(cand)
        if isinstance(c, dict) and fn in (c.get('locals') or {}):
            return True
    return False


gaps = Counter()
for card_name, card in ir.items():
    if not isinstance(card, dict):
        continue
    bodies = [card.get('steps') or []]
    bodies += list((card.get('locals') or {}).values())
    for body in bodies:
        for s in body:
            if s.get('op') != 'call' or not s.get('fn'):
                continue
            fn = s['fn']
            if fn in keys:
                continue
            if find_local(card_name, fn):
                continue
            gaps[fn] += 1

print(f'（与 C# 守卫同规则）缺口 {len(gaps)} 种 / {sum(gaps.values())} 个调用点')
print('  前 10：', gaps.most_common(10))

# 与分类脚本的口径差异
prev = json.load(open('out/audit/missing-keys-classify2.json', encoding='utf-8'))
prev_gap = {r['fn'] for r in prev if r['realCalls'] > 0}
now = set(gaps)
print(f'\n分类脚本口径：{len(prev_gap)} 种')
print(f'  只在分类脚本里（被 C# 守卫判为 locals 兜得住）：{sorted(prev_gap - now)}')
print(f'  只在 C# 守卫里（分类脚本漏了 locals 体内的调用）：{sorted(now - prev_gap)}')
