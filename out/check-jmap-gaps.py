"""1.61 jmap 的两个解析缺口：
  1) `card_unit_p37los` 的 CDO 整个解析不出来；
  2) 51 张卡的 `chooseOneCards` 结构数组被 dump 成字符串（`UAssetAPI...`）。

判据：如果这些资产的 `.uasset` 与 1.60 的解包结果**逐字节相同**，
那么 1.60 dump 里对应的字段对 1.61 依然权威 —— 可以直接补进来，不是猜。

本脚本只做判定与打印，不改文件。
"""
import hashlib
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = r"E:\项目\klink-dotnet"
NEW = os.path.join(ROOT, r"temp\pak-20261031\cards.all.json")
OLD = os.path.join(ROOT, r"temp\pak-20261023\cards.all.json")
EX_NEW = os.path.join(ROOT, r"temp\pak-20261031\kards\Content\Blueprints\Cards")
EX_OLD = os.path.join(ROOT, r"temp\pak-20261023\kards\Content\Blueprints\Cards")

newd = json.load(open(NEW, encoding="utf-8-sig"))["assets"]
oldd = {os.path.basename(k): v for k, v in json.load(open(OLD, encoding="utf-8-sig"))["assets"].items()}


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest() if os.path.exists(path) else None


def asset_bytes(export_root, key):
    return sha(os.path.join(export_root, *key.split("/")))


bad = [k for k, v in newd.items()
       if (v.get("cdo") or {}).get("chooseOneCards")
       and any(not isinstance(x, dict) for x in v["cdo"]["chooseOneCards"])]

same = 0
changed = []
for k in bad:
    a = asset_bytes(EX_OLD, k)
    b = asset_bytes(EX_NEW, k)
    if a and b and a == b:
        same += 1
    else:
        changed.append((os.path.basename(k), a is not None, b is not None))

print(f"chooseOneCards 解析失败的卡: {len(bad)}")
print(f"  其中 .uasset 与 1.60 逐字节相同: {same}")
print(f"  其中 .uasset 变了或缺失: {len(changed)}  {changed[:10]}")

# p37los
k37 = [k for k in newd if os.path.basename(k) == "card_unit_p37los.uasset"]
if k37:
    k = k37[0]
    a, b = asset_bytes(EX_OLD, k), asset_bytes(EX_NEW, k)
    print()
    print("card_unit_p37los.uasset  旧/新 逐字节相同:", a is not None and a == b)
    print("  新 dump 有 cdo:", bool(newd[k].get("cdo")))
    print("  旧 dump 有 cdo:", bool(oldd.get(os.path.basename(k), {}).get("cdo")))
