"""列出 1.60 -> 1.61 之间**真的改了的卡**以及改了什么（CDO 字段 + 函数集合）。"""
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

OLD = r"E:\项目\klink-dotnet\temp\pak-20261023\cards.all.json"
NEW = r"E:\项目\klink-dotnet\temp\pak-20261031\cards.all.json"

old = {os.path.basename(k): v for k, v in json.load(open(OLD, encoding="utf-8-sig"))["assets"].items()}
new = {os.path.basename(k): v for k, v in json.load(open(NEW, encoding="utf-8-sig"))["assets"].items()}

INTERESTING = ("kredits", "attack", "defense", "Type", "Text", "operationCost", "cipher",
               "range", "heavyArmor", "isReserve", "cardSet", "rarity")


def fp(e):
    fns = e.get("functions") or {}
    body = {n: json.dumps(f.get("bytecode"), sort_keys=True, ensure_ascii=False) for n, f in fns.items()}
    return json.dumps({"functions": body, "cdo": e.get("cdo") or {}}, sort_keys=True, ensure_ascii=False)


for base in sorted(new):
    if base not in old:
        continue
    a, b = old[base], new[base]
    if fp(a) == fp(b):
        continue
    name = base[:-len(".uasset")]
    ca, cb = a.get("cdo") or {}, b.get("cdo") or {}
    fields = []
    for k in sorted(set(ca) | set(cb)):
        if k in ("UberGraphFrame", "soundEnterPlay"):
            continue
        if ca.get(k) != cb.get(k):
            va, vb = str(ca.get(k)), str(cb.get(k))
            if len(va) > 90:
                va = va[:90] + "…"
            if len(vb) > 90:
                vb = vb[:90] + "…"
            fields.append("%s: %s -> %s" % (k, va, vb))
    fa, fb = set(a.get("functions") or {}), set(b.get("functions") or {})
    fns = []
    if fb - fa:
        fns.append("+函数 " + ",".join(sorted(fb - fa)))
    if fa - fb:
        fns.append("-函数 " + ",".join(sorted(fa - fb)))
    if fa == fb:
        for f in sorted(fa):
            ba = json.dumps((a["functions"][f]).get("bytecode"), sort_keys=True)
            bb = json.dumps((b["functions"][f]).get("bytecode"), sort_keys=True)
            if ba != bb:
                fns.append("改了函数体 " + f)
    print()
    print(name)
    for x in fields:
        print("    " + x)
    for x in fns:
        print("    " + x)
