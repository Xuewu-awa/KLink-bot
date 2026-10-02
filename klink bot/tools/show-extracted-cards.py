"""看一眼 KardsDataExtract 抽出来的 cards.json（字段名不确定，先自适应）。"""
import json
import sys

path = sys.argv[1] if len(sys.argv) > 1 else r"out\test-cards.json"
d = json.load(open(path, encoding="utf-8"))
print(f"{path}: {len(d)} 张")
if not d:
    sys.exit(0)

print("字段:", sorted(d[0].keys()))
print()


def g(c, *names):
    for n in names:
        if c.get(n) is not None:
            return c[n]
    return ""


for c in d[:12]:
    print("  {:<42} {:<26} {:<10} {}费  atk={} def={}".format(
        str(g(c, "id", "Id")),
        str(g(c, "name", "Name", "title", "Title"))[:26],
        str(g(c, "type", "Type")),
        g(c, "kredits", "Kredits"),
        g(c, "attack", "Attack"),
        g(c, "defense", "Defense"),
    ))
