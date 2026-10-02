import json
import sys

path = r"klink bot\docs\card-ir.json"
d = json.load(open(path, encoding="utf-8"))
name = sys.argv[1] if len(sys.argv) > 1 else "card_event_pams"
p = d[name]
print("entrypoints:", p["entrypoints"])
print("总步数:", len(p["steps"]))
for i, s in enumerate(p["steps"]):
    print("%3d  i=%-5s %s" % (i, s.get("i"), json.dumps(s, ensure_ascii=False)))
