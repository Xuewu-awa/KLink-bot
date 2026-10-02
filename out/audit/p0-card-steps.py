"""把某张卡的某个入口点的 IR 步骤打成可读形式。"""
import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
IR = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))

card = sys.argv[1]
only = sys.argv[2] if len(sys.argv) > 2 else None
body = IR.get(card)
if body is None:
    print("没有这张卡")
    raise SystemExit
print("entrypoints:", body["entrypoints"])
lo = min(body["entrypoints"].values())
for i, s in enumerate(body["steps"]):
    if only and i < lo:
        continue
    print(i, json.dumps(s, ensure_ascii=False)[:230])
