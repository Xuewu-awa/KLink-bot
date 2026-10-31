"""对单张卡对比两版 IR 的 JSON，打印第一个差异点。用法：python diff-ir-card.py <卡名> [max]"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

OLD = r"E:\项目\klink-dotnet\friend-main\out\bot-deploy-backup\20261025-110012\card-ir.json.old-10610103"
NEW = r"E:\项目\klink-dotnet\temp\pak-20261031\card-ir.json"

card = sys.argv[1]
limit = int(sys.argv[2]) if len(sys.argv) > 2 else 12

old = json.load(open(OLD, encoding="utf-8-sig"))[card]
new = json.load(open(NEW, encoding="utf-8-sig"))[card]

diffs = []


def walk(a, b, path):
    if len(diffs) >= limit:
        return
    if type(a) is not type(b):
        diffs.append((path, a, b))
        return
    if isinstance(a, dict):
        if set(a) != set(b):
            diffs.append((path + " keys", sorted(set(a) - set(b)), sorted(set(b) - set(a))))
        for k in sorted(set(a) & set(b)):
            walk(a[k], b[k], path + "." + k)
    elif isinstance(a, list):
        if len(a) != len(b):
            diffs.append((path + " len", len(a), len(b)))
        for i in range(min(len(a), len(b))):
            walk(a[i], b[i], "%s[%d]" % (path, i))
    elif a != b:
        diffs.append((path, a, b))


walk(old, new, card)
print("差异点:", len(diffs), "(最多显示 %d)" % limit)
for p, a, b in diffs:
    print()
    print("  " + p)
    print("    old:", json.dumps(a, ensure_ascii=False)[:300])
    print("    new:", json.dumps(b, ensure_ascii=False)[:300])
