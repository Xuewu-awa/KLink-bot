# -*- coding: utf-8 -*-
"""人工抽查：打印指定键的分类 + 三层独立证据（IR 调用点 / 直译产物定义处 / 参考实现）。"""
import json, re, os, sys
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
rows = {r['fn']: r for r in json.load(open('out/audit/missing-keys-classify2.json', encoding='utf-8'))}

DEPS = 'out/Generated-gap/_deps'
def find_defs(fn):
    hits = []
    for f in os.listdir(DEPS):
        if not f.endswith('.g.cs'):
            continue
        t = open(os.path.join(DEPS, f), encoding='utf-8', errors='replace').read()
        if re.search(r'^public static Val ' + re.escape(fn) + r'\(IHost H', t, re.M):
            hits.append(f[:-5])
    return hits

def callsites(fn, n=3):
    out = []
    for card, v in ir.items():
        if not isinstance(v, dict):
            continue
        for s in v.get('steps', []):
            if s.get('fn') == fn:
                out.append((card, s.get('i'), card in (v.get('locals') or {})))
                break
    return out[:n]

for fn in sys.argv[1:]:
    r = rows.get(fn)
    print('=' * 100)
    if not r:
        print(f'{fn}: 不在缺失表里（已被注册？）')
        continue
    print(f'### {fn}')
    print(f'  分类: {r["class"]} / {r["rule"]}   真缺口调用点 {r["realCalls"]} / 总 {r["calls"]} / '
          f'{r["realCards"]} 张卡')
    print(f'  证据: {r["evidence"]}')
    print(f'  理由: {r["why"]}')
    print(f'  直译产物定义处: {find_defs(fn)}')
    print(f'  参考实现行号: {r.get("refLine")}')
    print(f'  入口分布: {r["entrypoints"]}')
    print(f'  调用点样例: {callsites(fn)}')
