# -*- coding: utf-8 -*-
"""对 608 个缺失键做「归一化名字」候选匹配：列出 src/KLink.Bot 里名字相近的方法。"""
import json, re, os, sys, difflib

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)

METHOD_RE = re.compile(
    r'^[ \t]*(?:(?:public|private|protected|internal|static|sealed|override|virtual|async|partial|new|readonly|unsafe|extern)\s+)*'
    r'(?:[\w<>?\[\],\.]+\s+)+(\w+)\s*(?:<[^>]*>)?\s*\(', re.M)

methods = {}          # name -> (file, line)
for base, _, files in os.walk('src/KLink.Bot'):
    for f in files:
        if not f.endswith('.cs'):
            continue
        p = os.path.join(base, f)
        t = open(p, encoding='utf-8', errors='replace').read()
        for m in METHOD_RE.finditer(t):
            methods.setdefault(m.group(1), (p, t[:m.start()].count('\n') + 1))


def norm(s):
    s = s.lower()
    s = re.sub(r'[^a-z0-9]', '', s)
    for pre in ('get', 'has', 'is', 'can', 'do', 'make', 'set', 'add', 'remove', 'apply', 'fetch'):
        if s.startswith(pre) and len(s) > len(pre) + 2:
            s = s[len(pre):]
            break
    for suf in ('fromhand', 'inhand', 'fromdeck', 'bycard', 'byid', 'byside', 'fromboard',
                'onboard', 'endof', 'isit', 'returnvalue', 'card', 'cards', 'multiple'):
        if s.endswith(suf) and len(s) > len(suf) + 2:
            s = s[: -len(suf)]
    return s


norm_methods = {}
for m in methods:
    norm_methods.setdefault(norm(m), []).append(m)

rows = json.load(open('out/audit/missing-keys-classify2.json', encoding='utf-8'))
out = []
for r in sorted(rows, key=lambda r: -r['calls']):
    fn = r['fn']
    n = norm(fn)
    exact = [m for m in methods if m.lower() == fn.lower()]
    nrm = norm_methods.get(n, [])
    close = difflib.get_close_matches(n, list(norm_methods), n=3, cutoff=0.86)
    cand = sorted(set(exact + nrm + [c for k in close for c in norm_methods[k]]))
    if cand:
        out.append((r['calls'], r['cards'], fn, r['class'], cand,
                    [methods[c] for c in cand[:3]]))

out.sort(key=lambda x: -x[0])
print(f'=== 名字相近（归一化/近似）的缺失键：{len(out)} 种 ===')
for calls, cards, fn, cl, cand, sites in out:
    print(f'{fn:42s} {calls:4d}调用 {cards:3d}卡 [{cl}]  → {cand}   {sites}')
