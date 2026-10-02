import json
import pathlib
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
IR = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))


def fired(src):
    out = set()
    for p in src.rglob("*.cs"):
        t = p.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r'FireTrigger\(\s*"([A-Za-z0-9_]+)"', t):
            out.add(m.group(1))
        for line in t.splitlines():
            if "FireTrigger(" in line or "RunTriggerProgram(" in line:
                for m in re.finditer(r'"((?:On)[A-Za-z0-9_]+)"', line):
                    out.add(m.group(1))
    return out


b = fired(ROOT / "out" / "_p0-baseline-src")
c1 = sum(1 for card, v in IR.items() if any(e not in b for e in (v.get("entrypoints") or {})))
rows = sum(1 for card, v in IR.items() for e in (v.get("entrypoints") or {}) if e not in b)
c3 = sum(1 for card, v in IR.items()
         if any(e.startswith("On") and e not in b and e != "OnPlayedFromHand"
                for e in (v.get("entrypoints") or {})))
c4 = sum(1 for card, v in IR.items()
         if any(e not in b and v["entrypoints"][e] is not None for e in (v.get("entrypoints") or {})))
print("变体1 不排除 UI/战役 :", c1)
print("变体2 订阅行数        :", rows)
print("变体3 只看 On*        :", c3)
print("变体4 同变体1         :", c4)
print("基线 fired 名字数     :", len(b))
print("非 On* 的             :", sorted(x for x in b if not x.startswith("On"))[:12])
