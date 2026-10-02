"""触发点覆盖表：原生事件（BaseCardObject.h）× 卡蓝图订阅数（card-ir.json entrypoints）× 内核是否派发。"""
import json, re, os, collections

ROOT = r'<repo-root>'
hdr = open(r'E:\peoject\kards\Source\kards\Public\BaseCardObject.h', encoding='utf-8', errors='replace').read()
native_events = sorted(set(re.findall(r'\n\s*void\s+(On[A-Za-z_0-9]+)\s*\(', hdr)))

ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
subs = collections.Counter()
for a, p in ir.items():
    for e in (p.get('entrypoints') or {}):
        subs[e] += 1

# 内核真正派发的（FireTrigger 的第一个参数 + otherProgramName/selfProgramName 字面量）
blob = ''
for f in ['Engine/MatchEngine.cs', 'Effects/CardApi.cs', 'Effects/CardApiDispatch.cs',
          'Effects/Blueprint/KismetVm.cs', 'Effects/CardEffectScripts.cs']:
    blob += open(os.path.join(ROOT, 'src/KLink.Bot', f), encoding='utf-8').read()
fired = set(re.findall(r'FireTrigger\(\s*"([A-Za-z_0-9]+)"\s*,[^;]*?"(On[A-Za-z_0-9]+)"', blob, re.S))
fired1 = set(re.findall(r'FireTrigger\(\s*"([A-Za-z_0-9]+)"', blob))
allfired = fired1 | {b for _, b in fired}

out = open(os.path.join(ROOT, r'out\audit\trigger-coverage.txt'), 'w', encoding='utf-8')
def P(*a): print(*a, file=out)

P('=== 原生事件（BaseCardObject.h 的 void OnXxx(...)）× 订阅卡数 × 内核是否派发 ===')
P(f'{"事件":44s} {"订阅卡数":>8s}  内核')
covered = miss = 0
for e in native_events:
    n = subs.get(e, 0)
    ok = e in allfired
    covered += ok
    miss += (not ok)
    P(f'{e:44s} {n:8d}  {"✔ 已派发" if ok else "✗ 未派发"}')
P('')
P(f'原生事件总数={len(native_events)}  已派发={covered}  未派发={miss}')
P('')
P('=== 卡蓝图里订阅了、但不是 BaseCardObject 原生事件的入口点（≥2 张卡）===')
for e, n in subs.most_common():
    if e in native_events:
        continue
    if n >= 2:
        P(f'{e:44s} {n:8d}  {"✔" if e in allfired else "✗"}')
out.close()
print('native events:', len(native_events), 'fired:', covered, 'not fired:', miss)
