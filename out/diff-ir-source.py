"""对比 1.60 / 1.61 两次 dump 的原始字节码，判断 IR 变化是「游戏改了」还是「我们的管线换了解释」。

用法：python diff-ir-source.py [关键词...]
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

OLD = r"E:\项目\klink-dotnet\temp\pak-20261023\cards.all.json"
NEW = r"E:\项目\klink-dotnet\temp\pak-20261031\cards.all.json"

old = json.load(open(OLD, encoding="utf-8-sig"))["assets"]
new = json.load(open(NEW, encoding="utf-8-sig"))["assets"]
print("old assets", len(old), " new assets", len(new))

# 两次 dump 的 `--input-dir` 不同 ⇒ 资产键一个带 pak 前缀、一个是相对路径。
# 按 basename 归一化后再配对。
import os
def bybase(d):
    return {os.path.basename(k): v for k, v in d.items()}
old = bybase(old)
new = bybase(new)

keys = sys.argv[1:] or ["vanguard_ita", "amphibious_assault", "cruiser_scouts", "panzer_iii_f"]

for needle in keys:
    hits = [k for k in new if needle in k]
    for t in hits:
        print()
        print("=" * 70)
        print(t)
        if t not in old:
            print("  NEW-ONLY（旧 dump 里没有这个资产）")
            continue
        fo = old[t].get("functions") or {}
        fn = new[t].get("functions") or {}
        print("  函数集合相同:", set(fo) == set(fn), "| old", len(fo), "new", len(fn))
        for f in sorted(set(fo) & set(fn)):
            so = json.dumps(fo[f].get("bytecode"), sort_keys=True, ensure_ascii=False)
            sn = json.dumps(fn[f].get("bytecode"), sort_keys=True, ensure_ascii=False)
            flag = "相同" if so == sn else "**不同**"
            print("    %-55s expr %s->%s  原始bytecode %s"
                  % (f, fo[f].get("expr_count"), fn[f].get("expr_count"), flag))
        added = sorted(set(fn) - set(fo))
        removed = sorted(set(fo) - set(fn))
        if added:
            print("    新增函数:", added)
        if removed:
            print("    删除函数:", removed)
        co = old[t].get("cdo") or {}
        cn = new[t].get("cdo") or {}
        if co != cn:
            diff = {k: (co.get(k), cn.get(k)) for k in set(co) | set(cn) if co.get(k) != cn.get(k)}
            print("    cdo 变化:", diff)
        else:
            print("    cdo 未变")
