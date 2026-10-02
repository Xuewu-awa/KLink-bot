import re, collections, io, sys
s = open('src/KLink.Bot/Cards/CardInnateTable.cs', encoding='utf-8').read()
c = collections.Counter(re.findall(r'"(Blitz|Guard|Ambush|Fury|Smokescreen|Alpine|Mobilize|Salvage|Shock|Deployment|Destruction|Covert|Pincer|Scrying)"', s))
print(dict(c))
print('entries', len(re.findall(r'\] = \(\[', s)))
old = open('out/_p1-innate-before.cs', encoding='utf-8').read()
print('identical to before:', s == old)
