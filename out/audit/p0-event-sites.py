"""把 bp-cardfn.json 里所有「事件派发点」按所在函数分组列出。

判据：语句的 Expression 是 LocalVirtualFunction/VirtualFunction，且函数名以 On 开头
（这些就是 BlueprintImplementableEvent 的调用点），或者 FunctionName 是
ExecuteOn*Events / TriggerDeployment / TriggerDestruction。

用法: python out/audit/p0-event-sites.py [bp-json]
"""
import json
import sys
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8")
path = sys.argv[1] if len(sys.argv) > 1 else 'out/bp-cardfn.json'
d = json.load(open(path, encoding='utf-8'))
by_fn = defaultdict(list)
for fn, f in d.items():
    for s in (f or {}).get('bytecode') or []:
        exp = s.get('Expression')
        name = None
        if isinstance(exp, dict):
            if exp.get('Inst') in ('LocalVirtualFunction', 'VirtualFunction'):
                name = exp.get('FunctionName') or exp.get('Function')
        if s.get('Inst') in ('LocalVirtualFunction', 'VirtualFunction'):
            name = s.get('FunctionName') or s.get('Function')
        if name and (name.startswith('On') or name.startswith('Trigger') or name.startswith('ExecuteOn')):
            by_fn[fn].append((s.get('StatementIndex'), name))
for fn in sorted(by_fn):
    names = sorted(set(n for _, n in by_fn[fn]))
    print('%-46s %s' % (fn, ', '.join(names)))
