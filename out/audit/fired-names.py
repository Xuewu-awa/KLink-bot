import re, os
ROOT = r'<repo-root>'
blob = ''
for f in ['Engine/MatchEngine.cs', 'Effects/CardApi.cs', 'Effects/CardApiDispatch.cs',
          'Effects/Blueprint/KismetVm.cs']:
    blob += open(os.path.join(ROOT, 'src/KLink.Bot', f), encoding='utf-8').read()
names = set()
for m in re.finditer(r'FireTrigger\((.{0,240}?)\);', blob, re.S):
    for s in re.findall(r'"(On[A-Za-z_0-9]+)"', m.group(1)):
        names.add(s)
print(len(names))
for n in sorted(names):
    print(' ', n)
