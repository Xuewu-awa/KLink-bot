import json, re
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
# IR 里所有被调用的函数名 + 调用次数 + 涉及卡数
from collections import Counter, defaultdict
cnt = Counter(); cards = defaultdict(set)
for name, v in ir.items():
    if not isinstance(v, dict) or not isinstance(v.get('steps'), list): continue
    for s in v['steps']:
        if s.get('op') == 'call' and s.get('fn'):
            cnt[s['fn']] += 1; cards[s['fn']].add(name)
# 派发表里的键
src = open('src/KLink.Bot/Effects/CardApiDispatch.cs', encoding='utf-8').read()
keys = set(re.findall(r'\["([^"]+)"\]\s*=', src))
print(f'IR 里被调用的函数: {len(cnt)} 种')
print(f'派发表里的键:      {len(keys)} 种')
missing = {k: (cnt[k], len(cards[k])) for k in cnt if k not in keys}
print(f'\n=== ★ 卡会调但派发表里【没有】的键: {len(missing)} 种 ===')
for k, (n, c) in sorted(missing.items(), key=lambda x: -x[1][0])[:22]:
    print(f'  {k:42s} 调用 {n:4d} 次 / {c:3d} 张卡')
