import json, collections, os
ROOT = r'<repo-root>'
cards = json.load(open(os.path.join(ROOT, r'out\cards-full2.json'), encoding='utf-8'))
c = collections.Counter()
for x in cards:
    for t in ((x.get('raw') or {}).get('usedTriggers') or []):
        c[str(t)] += 1
print('distinct usedTriggers values:', len(c))
for k, v in c.most_common(40):
    print(f'  {k:44s} {v}')
