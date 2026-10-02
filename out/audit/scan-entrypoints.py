import json, collections, re, os

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))

eps = collections.Counter()
for a, p in ir.items():
    for e in (p.get('entrypoints') or {}):
        eps[e] += 1
print('=== entrypoints across all cards ===')
for k, v in eps.most_common():
    print(f'{v:6d}  {k}')
