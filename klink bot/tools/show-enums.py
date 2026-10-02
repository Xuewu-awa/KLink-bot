"""打印从 jmap 提取的 /Script/kards 枚举（已存为 decompiled/kards-enums.json）。"""
import json
import sys
from pathlib import Path

p = Path(__file__).resolve().parent.parent / "decompiled" / "kards-enums.json"
d = json.loads(p.read_text(encoding="utf-8"))

only = sys.argv[1] if len(sys.argv) > 1 else None
for name, obj in sorted(d.items()):
    short = name.replace("/Script/kards.", "")
    if only and only.lower() not in short.lower():
        continue
    names = obj.get("names") or obj.get("Names") or []
    print(f"### {short}    (cpp_type={obj.get('cpp_type')})")
    for item in names:
        try:
            n, v = item[0], item[1]
        except Exception:
            print("   ", item)
            continue
        print(f"    {v:>4}  {str(n).split('::')[-1]}")
    print()
