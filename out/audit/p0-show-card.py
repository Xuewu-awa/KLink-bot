"""打印某张卡某个入口点的 IR 程序（可读形式）。

用法: python out/audit/p0-show-card.py <卡名> <入口点名> [最多条数]
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
name = sys.argv[1]
ev = sys.argv[2]
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 60

d = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
v = d.get(name)
if v is None:
    print('没有这张卡:', name)
    sys.exit(1)
eps = v['entrypoints']
print('entrypoints:', eps)
if ev not in eps:
    print('该卡没有入口点', ev)
    sys.exit(0)
start = eps[ev]
starts = sorted(x for x in eps.values() if x > start)
end = starts[0] if starts else 10 ** 9
for s in v['steps']:
    i = s.get('i', 0)
    if start <= i < end:
        print(json.dumps(s, ensure_ascii=False)[:400])
