"""打印某张卡某个函数里的 Kismet 语句（含 locals / pins），用于核对 IR 的参数接线。

用法: python tools/show-kismet.py <卡名> [函数名前缀]
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

card = sys.argv[1]
fn_prefix = sys.argv[2] if len(sys.argv) > 2 else ""

data = json.load(open("decompiled/cards.full.json", encoding="utf-8"))
assets = data["assets"]

target = None
for path, entry in assets.items():
    if path.rsplit("/", 1)[-1] == card + ".uasset":
        target = entry
        break

if target is None:
    print("没找到卡:", card)
    sys.exit(1)

funcs = target.get("functions") or {}
if isinstance(funcs, list):
    funcs = {f.get("name", "?"): f for f in funcs}

print("资产:", target.get("asset"))
print("卡的函数:", list(funcs.keys()))

for fname, f in funcs.items():
    if fn_prefix and not fname.startswith(fn_prefix):
        continue
    print("=" * 92)
    print("函数", fname, " 键:", list(f.keys()) if isinstance(f, dict) else type(f))
    if not isinstance(f, dict):
        print(json.dumps(f, ensure_ascii=False)[:1500])
        continue
    for key in ("locals", "statements"):
        val = f.get(key)
        if val is None:
            continue
        print("  %s:" % key)
        for item in val:
            if isinstance(item, str):
                print("     ", item)
            else:
                print("     ", json.dumps(item, ensure_ascii=False)[:400])
