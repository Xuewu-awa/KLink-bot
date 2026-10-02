import json, re, sys
s = open('src/KLink.Bot/Cards/CardInnateTable.cs', encoding='utf-8').read()
rows = re.findall(r'\["([^"]+)"\] = \(\[([^\]]*)\], (\d+)\)', s)
by = {}
for n, k, a in rows:
    a = int(a)
    if a > 0:
        by.setdefault(a, []).append((n, k))
for a in sorted(by):
    print('armor', a, 'count', len(by[a]), 'ex', by[a][:8])
