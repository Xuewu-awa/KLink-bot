import re, os
ROOT = r'<repo-root>'
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
lines = src.split('\n')
entries = {}
cur, buf, start = None, [], None
for idx, l in enumerate(lines):
    m = re.match(r'\s*\["([A-Za-z_0-9]+)"\]\s*=', l)
    if m:
        if cur:
            entries[cur] = (start, '\n'.join(buf))
        cur, buf, start = m.group(1), [l], idx + 1
    elif cur is not None:
        if re.match(r'\s*\};?\s*$', l):
            entries[cur] = (start, '\n'.join(buf))
            cur, buf = None, []
        else:
            buf.append(l)
if cur:
    entries[cur] = (start, '\n'.join(buf))

STUB = re.compile(r'=>\s*(false|null|"")\s*[,;}]|return (false|null|"");|TODO|近似|未验证|保守|待回放|桩|占位|not implemented|未实现|吃掉')
out = []
for name, (ln, body) in sorted(entries.items()):
    if STUB.search(body):
        one = ' '.join(x.strip() for x in body.split('\n') if x.strip())
        out.append((name, ln, one[:230]))
print(len(out))
for n, l, b in out:
    print(f'--- {n}  (CardApiDispatch.cs:{l})')
    print('    ', b)
