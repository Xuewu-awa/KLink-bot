# -*- coding: utf-8 -*-
"""为 B 候选逐个打印「触发命中的文档注释块 + 紧随的方法签名」，供人工复核。"""
import json, re, os, sys
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
rows = json.load(open('out/audit/missing-keys-classify2.json', encoding='utf-8'))
want = sys.argv[1:] or [r['fn'] for r in rows if r['class'] == 'B']
src = {}
for base, _, files in os.walk('src/KLink.Bot'):
    for f in files:
        if f.endswith('.cs'):
            p = os.path.join(base, f)
            src[p] = open(p, encoding='utf-8', errors='replace').read()

METHOD_RE = re.compile(
    r'^[ \t]*(?:(?:public|private|protected|internal|static|sealed|override|virtual|async|partial|new|readonly)\s+)*'
    r'(?:[\w<>?\[\],\.]+\s+)+(\w+)\s*(?:<[^>]*>)?\s*\(', re.M)

for fn in want:
    r = next((x for x in rows if x['fn'] == fn), None)
    if not r:
        print(f'!! {fn} 不在缺失表里'); continue
    print('=' * 100)
    print(f'### {fn}  [{r["rule"]}] {r["evidence"]}  ({r["calls"]}调用/{r["cards"]}卡)')
    print(f'    入口: {r["entrypoints"]}')
    print(f'    定义蓝图: {r["definedIn"]}')
    hit = False
    for p, t in src.items():
        lines = t.split('\n')
        for i, ln in enumerate(lines):
            if ln.lstrip().startswith('///') and re.search(r'`[^`]*\b' + re.escape(fn) + r'\b[^`]*`', ln):
                # 找到这个注释块
                j = i
                while j < len(lines) and lines[j].lstrip().startswith('///'):
                    j += 1
                k = j
                while k < len(lines) and (lines[k].lstrip().startswith('[') or not lines[k].strip()):
                    k += 1
                mm = METHOD_RE.match(lines[k] + '\n') if k < len(lines) else None
                print(f'--- {p}:{i+1} → 紧随方法 `{mm.group(1) if mm else "?"}` ---')
                for x in lines[i:j]:
                    print('   ' + x.strip())
                hit = True
                break
        if hit:
            break
    if not hit:
        print('  （没找到反引号引用点 —— 复核用）')
    print()
