import json, os, collections
ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
shapes = collections.Counter()
ex = {}
for a, p in ir.items():
    for st in p.get('steps', []):
        if st.get('op') != 'call' or st.get('fn') != 'GiveKreditsBySide':
            continue
        args = st.get('args') or []
        key = tuple(json.dumps(x, ensure_ascii=False) if isinstance(x, dict) else str(x) for x in args[:3])
        shapes[key] += 1
        ex.setdefault(key, a)
for k, n in shapes.most_common(20):
    print(n, '|', ' ; '.join(k), '| e.g.', ex[k])
