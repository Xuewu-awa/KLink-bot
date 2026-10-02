"""找「同形 bug」：蓝图里既有显式接收者调用、又有隐式 self 调用的函数，
   而内核 handler 只看 receiver（AsCard(r) / r is CardInstance），不看 SelfArg。
   这类 handler 会在隐式 self 的调用点静默返回默认值。
"""
import json, collections, re, os

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
lines = src.split('\n')

# 1) 切出每个派发表条目的完整文本（从 ["name"] = 到下一条 ["name"] = 或 } 结束）
entries = {}
cur = None
buf = []
start = None
for idx, l in enumerate(lines):
    m = re.match(r'\s*\["([A-Za-z_0-9]+)"\]\s*=', l)
    if m:
        if cur:
            entries[cur] = (start, '\n'.join(buf))
        cur = m.group(1)
        buf = [l]
        start = idx + 1
    elif cur is not None:
        if re.match(r'\s*\};?\s*$', l) and buf and not l.strip().startswith('//'):
            entries[cur] = (start, '\n'.join(buf))
            cur = None
            buf = []
        else:
            buf.append(l)
if cur:
    entries[cur] = (start, '\n'.join(buf))

# 2) 统计每个函数在 IR 里 recv 的有无
recv = collections.Counter()
norecv = collections.Counter()
for a, p in ir.items():
    for st in p.get('steps', []):
        if st.get('op') != 'call':
            continue
        fn = st.get('fn')
        if st.get('recv') is None:
            norecv[fn] += 1
        else:
            recv[fn] += 1

out = open(os.path.join(ROOT, r'out\audit\selfarg-bug-scan.txt'), 'w', encoding='utf-8')
def P(*a): print(*a, file=out)

P('=== 只用 receiver、不用 SelfArg 的 handler，且蓝图里有「隐式 self」调用点 ===')
P('（隐式 self = IR 里没有 recv；VM 传进来的 receiver 是 null）\n')
rows = []
for name, (ln, body) in entries.items():
    n = norecv.get(name, 0)
    if n == 0:
        continue
    uses_selfarg = 'SelfArg' in body
    uses_r = bool(re.search(r'AsCard\(r\)|r is CardInstance|\br\b\s+is\s+CardInstance', body))
    if uses_selfarg:
        continue
    rows.append((n, name, ln, uses_r, recv.get(name, 0)))
rows.sort(reverse=True)
for n, name, ln, uses_r, wr in rows:
    P(f'{name:34s} 隐式self调用点={n:4d}  显式recv={wr:4d}  只看receiver={uses_r}  CardApiDispatch.cs:{ln}')
P('')
P('=== 对照组：用了 SelfArg 的（这些是安全的）===')
for name, (ln, body) in sorted(entries.items()):
    if norecv.get(name, 0) and 'SelfArg' in body:
        P(f'{name:34s} 隐式self={norecv[name]:4d}  SelfArg ✔  CardApiDispatch.cs:{ln}')
out.close()
print('entries parsed:', len(entries))
print('flagged:', len(rows))
