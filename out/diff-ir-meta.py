"""对比两个 dump 的 out_indices / inputs / outputs 元数据，判断 IR 方言差异来自哪里。"""
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

OLD = r"E:\项目\klink-dotnet\temp\pak-20261023\cards.all.json"
NEW = r"E:\项目\klink-dotnet\temp\pak-20261031\cards.all.json"

old = {os.path.basename(k): v for k, v in json.load(open(OLD, encoding="utf-8-sig"))["assets"].items()}
new = {os.path.basename(k): v for k, v in json.load(open(NEW, encoding="utf-8-sig"))["assets"].items()}


def count(d, field):
    n_assets = 0
    n_funcs = 0
    for v in d.values():
        hit = False
        for fi in (v.get("functions") or {}).values():
            if field in fi:
                n_funcs += 1
                hit = True
        if hit:
            n_assets += 1
    return n_assets, n_funcs


for field in ("out_indices", "inputs", "outputs"):
    print("%-12s old: 资产 %5d / 函数 %6d      new: 资产 %5d / 函数 %6d"
          % ((field,) + count(old, field) + count(new, field)))

# 抽查一张 bytecode 相同的卡：它的函数元数据变了吗
t = "card_event_vanguard_ita.uasset"
print()
print(t)
fo = old[t].get("functions") or {}
fn = new[t].get("functions") or {}
for f in sorted(fo):
    a = {k: fo[f].get(k) for k in ("out_indices", "inputs", "outputs")}
    b = {k: fn[f].get(k) for k in ("out_indices", "inputs", "outputs")}
    if a != b:
        print("  %-50s old=%s" % (f, a))
        print("  %-50s new=%s" % ("", b))
