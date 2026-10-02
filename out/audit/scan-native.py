"""把 BaseCardObject.h 的原生成员函数清单 × IR 调用名 × 内核派发表 三方对账。"""
import json, re, os, collections

ROOT = r'<repo-root>'
hdr = open(r'E:\peoject\kards\Source\kards\Public\BaseCardObject.h', encoding='utf-8', errors='replace').read()
native = {}
for m in re.finditer(r'\n\s*(?:void|bool|int32|float|FString|TArray<[^>]*>|[A-Za-z_][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)\s*;', hdr):
    native[m.group(1)] = m.group(2).strip()

ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))

calls = collections.Counter()
callcards = collections.defaultdict(set)
norecv = collections.Counter()
for a, p in ir.items():
    for st in p.get('steps', []):
        if st.get('op') != 'call':
            continue
        calls[st['fn']] += 1
        callcards[st['fn']].add(a)
        if st.get('recv') is None:
            norecv[st['fn']] += 1

out = open(os.path.join(ROOT, r'out\audit\native-vs-kernel.txt'), 'w', encoding='utf-8')
def P(*a): print(*a, file=out)

P('=== BaseCardObject 原生成员：被卡蓝图调用、但内核派发表里没有 ===')
P(f'{"原生函数":38s} {"卡数":>5s} {"调用":>5s} {"隐式self":>7s}  签名')
miss = [(f, len(callcards[f]), calls[f], norecv[f]) for f in native
        if f in calls and f not in dispatched]
for f, nc, n, nr in sorted(miss, key=lambda x: -x[1]):
    P(f'{f:38s} {nc:5d} {n:5d} {nr:7d}  {f}({native[f]})')

P('')
P('=== 原生成员：内核有实现（对照用）===')
have = [(f, len(callcards[f]), calls[f], norecv[f]) for f in native
        if f in calls and f in dispatched]
for f, nc, n, nr in sorted(have, key=lambda x: -x[1]):
    P(f'{f:38s} {nc:5d} {n:5d} {nr:7d}  {f}({native[f]})')

P('')
P('=== 原生成员：卡蓝图一次都没调用（可能是引擎内部/服务端调用）===')
never = [f for f in native if f not in calls]
P('  ' + ', '.join(sorted(never)))
out.close()
print('native:', len(native), 'missing-in-kernel:', len(miss), 'have:', len(have), 'never-called:', len(never))
