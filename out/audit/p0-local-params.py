import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
assets = json.loads((ROOT / "klink bot" / "decompiled" / "cards.full.json")
                    .read_text(encoding="utf-8-sig"))["assets"]

target = sys.argv[1] if len(sys.argv) > 1 else "ApplyBuff"
for path, info in assets.items():
    fns = (info or {}).get("functions") or {}
    if target in fns:
        print("asset:", path)
        body = fns[target]
        print("  keys:", list(body.keys()))
        for k, v in body.items():
            if k == "bytecode":
                print(f"  bytecode: {len(v)} 条")
            else:
                print(f"  {k}: {json.dumps(v, ensure_ascii=False)[:300]}")
        # 函数体里"先用后写"的变量 = 入参
        used, written = [], set()
        for e in body.get("bytecode") or []:
            t = json.dumps(e, ensure_ascii=False)
            for m in __import__("re").finditer(r'"Variable Name": "([A-Za-z0-9_]+)"', t):
                used.append(m.group(1))
        print("  出现过的变量名前 12 个:", used[:12])
        break
