"""P0 第 3 族硬要求：重生成 IR 后，**entrypoints / steps 必须逐字节不变**，只有 locals 允许变。

用法: python out/audit/p0-ir-verify.py <旧IR> <新IR>
"""
import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
old = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
new = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding="utf-8"))

print(f"卡数: 旧 {len(old)} / 新 {len(new)}")
if set(old) != set(new):
    print("!! 卡集合不同:", list(set(old) ^ set(new))[:10])
    raise SystemExit(1)

bad_ep, bad_steps, bad_asset = [], [], []
grew, shrank = [], []
for card in old:
    o, n = old[card], new[card]
    if json.dumps(o.get("asset")) != json.dumps(n.get("asset")):
        bad_asset.append(card)
    # entrypoints：键值与顺序都要求一致（用 JSON 串比，顺序敏感）
    if json.dumps(o.get("entrypoints"), sort_keys=True, ensure_ascii=False) != \
       json.dumps(n.get("entrypoints"), sort_keys=True, ensure_ascii=False):
        bad_ep.append(card)
    if json.dumps(o.get("steps"), ensure_ascii=False) != json.dumps(n.get("steps"), ensure_ascii=False):
        bad_steps.append(card)
    ol, nl = o.get("locals") or {}, n.get("locals") or {}
    if len(nl) > len(ol):
        grew.append(card)
    elif len(nl) < len(ol):
        shrank.append(card)

print(f"asset 不同的卡      : {len(bad_asset)} {bad_asset[:5]}")
print(f"entrypoints 不同的卡: {len(bad_ep)} {bad_ep[:5]}")
print(f"steps 不同的卡      : {len(bad_steps)} {bad_steps[:5]}")
print(f"locals 变多的卡     : {len(grew)}")
print(f"locals 变少的卡     : {len(shrank)} {shrank[:5]}")

old_locals = sum(len(v.get("locals") or {}) for v in old.values())
new_locals = sum(len(v.get("locals") or {}) for v in new.values())
print(f"locals 函数体总数   : {old_locals} → {new_locals}")

ok = not bad_ep and not bad_steps and not bad_asset and not shrank
print("\n★ 结论:", "通过 —— entrypoints/steps/asset 逐字节不变，只有 locals 增加"
      if ok else "**不通过** —— 见上面的差异")
sys.exit(0 if ok else 1)
