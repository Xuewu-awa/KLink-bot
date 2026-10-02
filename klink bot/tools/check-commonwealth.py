"""查 card_event_the_commonwealth（英联邦）内核有没有实现。"""
import json
import re
import os

K = "card_event_the_commonwealth"

print("=" * 76)
print("① 卡面数据")
print("=" * 76)
eff = json.load(open(r"klink bot/docs/card-effects.json", encoding="utf-8"))
if isinstance(eff, list):
    eff = {c.get("id") or c.get("asset", "").split("/")[-1]: c for c in eff}
c = eff.get(K) or {}
print(f"  title  = {c.get('title')!r}")
print(f"  kredits= {c.get('kredits')}   type={c.get('type')}   faction={c.get('faction')}")
print(f"  text   = {c.get('text')!r}")
print(f"  functions = {list((c.get('functions') or {}).keys())}")
calls = c.get("calls") or []
print(f"  calls  ({len(calls)} 个):")
for x in calls:
    print(f"      {x}")

print()
print("=" * 76)
print("② card-ir.json 里这张卡的步骤")
print("=" * 76)
ir = json.load(open(r"klink bot/docs/card-ir.json", encoding="utf-8"))
node = ir.get(K)
if node is None:
    # 可能是 list 或别的结构
    print(f"  顶层类型 {type(ir).__name__}，键样例 {list(ir)[:5] if isinstance(ir, dict) else 'n/a'}")
else:
    s = json.dumps(node, ensure_ascii=False)
    print(f"  长度 {len(s)} 字符")
    steps = node.get("steps") if isinstance(node, dict) else node
    if isinstance(steps, list):
        print(f"  共 {len(steps)} 步")
        names = []
        for st in steps:
            n = st.get("call") or st.get("func") or st.get("name") if isinstance(st, dict) else None
            if n:
                names.append(n)
        from collections import Counter
        for n, cnt in Counter(names).most_common(30):
            print(f"      {cnt:>3}×  {n}")

print()
print("=" * 76)
print("③ 内核有没有认这些调用名（CardApiDispatch 的派发表）")
print("=" * 76)
src = ""
for root, _, files in os.walk(r"src/KLink.Bot"):
    for f in files:
        if f.endswith(".cs"):
            src += open(os.path.join(root, f), encoding="utf-8", errors="ignore").read()

for name in (c.get("calls") or [])[:20]:
    hit = f'"{name}"' in src or f"case \"{name}\"" in src
    print(f"      {'✓' if hit else '✗'}  {name}")
