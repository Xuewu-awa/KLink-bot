import json
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
path = sys.argv[1]
fn = sys.argv[2]
d = json.load(open(path, encoding="utf-8"))
s = json.dumps(d[fn], ensure_ascii=False)
names = sorted(set(re.findall(r'"Variable Name": "([A-Za-z_0-9]+)"', s)))
print(names)
