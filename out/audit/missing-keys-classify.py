import json, re
from collections import Counter, defaultdict
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
cnt = Counter(); cards = defaultdict(set)
for name, v in ir.items():
    if not isinstance(v, dict) or not isinstance(v.get('steps'), list): continue
    for s in v['steps']:
        if s.get('op') == 'call' and s.get('fn'):
            cnt[s['fn']] += 1; cards[s['fn']].add(name)
disp = open('src/KLink.Bot/Effects/CardApiDispatch.cs', encoding='utf-8').read()
keys = set(re.findall(r'\["([^"]+)"\]\s*=', disp))
api = open('src/KLink.Bot/Effects/CardApi.cs', encoding='utf-8').read()
eng = open('src/KLink.Bot/Engine/MatchEngine.cs', encoding='utf-8').read()
def has_impl(fn):
    # 宽松匹配：方法名与键同名（大小写不敏感）
    for src in (api, eng, disp):
        if re.search(r'(public|private|internal)\s+[\w<>?\[\],\s]+\s+' + re.escape(fn) + r'\s*\(', src, re.I):
            return True
    return False
missing = [k for k in cnt if k not in keys]
withimpl = [k for k in missing if has_impl(k)]
without = [k for k in missing if not has_impl(k)]
print(f'缺失键总数: {len(missing)}')
print(f'  其中【已有实现、只差注册】: {len(withimpl)} 种  ← 一行注册就能修')
print(f'  其中【实现也没有】:         {len(without)} 种')
print()
print('=== 已有实现、只差注册（按调用次数排序，前 20）===')
for k in sorted(withimpl, key=lambda x: -cnt[x])[:20]:
    print(f'  {k:38s} 调用 {cnt[k]:4d} / {len(cards[k]):3d} 张卡')
print()
tot = sum(cnt[k] for k in withimpl)
print(f'⇒ 这一类共影响 {tot} 个调用点 / {len(set().union(*[cards[k] for k in withimpl])) if withimpl else 0} 张卡')
