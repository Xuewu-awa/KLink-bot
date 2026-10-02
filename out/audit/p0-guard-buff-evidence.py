import glob
import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")

print("=== 谁调用 CanCardBeBuffed ===")
for p in glob.glob(str(ROOT / "out" / "bp-*.json")):
    d = json.loads(pathlib.Path(p).read_text(encoding="utf-8"))
    if not isinstance(d, dict):
        continue
    for fn, body in d.items():
        bc = body.get("bytecode") if isinstance(body, dict) else None
        if not isinstance(bc, list):
            continue
        for s in bc:
            t = json.dumps(s, ensure_ascii=False)
            if "CanCardBeBuffed" in t and fn != "CanCardBeBuffed":
                print(f"  {pathlib.Path(p).name} :: {fn}  si={s.get('StatementIndex')}")
                print(f"     {t[:300]}")

print("=== CanAttack 里有没有 Suppress ===")
d = json.loads((ROOT / "out" / "bp-cardscheck.json").read_text(encoding="utf-8"))
n = 0
for s in d["CanAttack"]["bytecode"]:
    t = json.dumps(s, ensure_ascii=False)
    if "isSuppressed" in t or "Suppress" in t:
        n += 1
        print("  si", s.get("StatementIndex"), t[:220])
print(f"  命中 {n} 条")

print("=== CalculateDamageDealt 里的 Suppress 分流 ===")
d2 = json.loads((ROOT / "out" / "bp-cardfn.json").read_text(encoding="utf-8"))
for name in ("CalculateDamageDealt", "ExecuteBeforeReceiveDamage", "ExecuteOnDealDamageAddDamage"):
    body = d2.get(name)
    if not body:
        print(f"  {name}: 不在 dump 里")
        continue
    for s in body["bytecode"]:
        t = json.dumps(s, ensure_ascii=False)
        if "isSuppressed" in t:
            print(f"  {name} si={s.get('StatementIndex')} {t[:200]}")
