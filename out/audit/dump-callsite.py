"""在 cards.full.json 里找某个函数的所有调用点，打印调用点附近的原始字节码上下文。"""
import json, os, sys, re

ROOT = r'<repo-root>'
P = os.path.join(ROOT, r'klink bot\decompiled\cards.full.json')
TARGET = sys.argv[1] if len(sys.argv) > 1 else 'getTotalAttack'
LIMIT = int(sys.argv[2]) if len(sys.argv) > 2 else 6
CTX = int(sys.argv[3]) if len(sys.argv) > 3 else 3

raw = json.load(open(P, encoding='utf-8'))
found = 0
for path, a in raw['assets'].items():
    if found >= LIMIT:
        break
    name = os.path.basename(path).replace('.uasset', '')
    for fname, finfo in (a.get('functions') or {}).items():
        bc = (finfo or {}).get('bytecode') or []
        for idx, e in enumerate(bc):
            if not isinstance(e, dict):
                continue
            s = json.dumps(e, ensure_ascii=False)
            if f'"{TARGET}"' not in s:
                continue
            print(f'===== {name} :: {fname}  idx={idx} =====')
            for j in range(max(0, idx - CTX), min(len(bc), idx + CTX + 1)):
                mark = '>>' if j == idx else '  '
                print(mark, j, json.dumps(bc[j], ensure_ascii=False)[:700])
            print()
            found += 1
            break
        if found >= LIMIT:
            break
print('found', found)
