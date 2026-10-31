"""把 1.61 dump 里「jmap 解析失败」的字段，用 1.60 dump 的同一字段补回。

判据（可证伪、不是猜）
--------------------
`dump-batch` 解析不了某个 struct/enum 时，会把值写成**类名占位符**
（实测形态：`"UAssetAPI.PropertyTypes.Objects.ArrayPropertyData"`、
`"UASSETAPI_INVALID_ENUM_IDX_13"` 之类）。
对每个这样的字段，如果该资产的 `.uasset` 与 1.60 的解包结果**逐字节相同**，
那么 1.60 dump 里那个字段就是**同一个文件字节的同一次解析结果** ⇒ 对 1.61 权威。
字节不同则不补，只报告（那是真的改了，需要更新 jmap 才能解）。

用法：python fix-jmap-gaps.py [--write]
不加 `--write` 时只统计。
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

WRITE = "--write" in sys.argv


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest() if os.path.exists(path) else None


def is_placeholder(v):
    if isinstance(v, str):
        return v.startswith("UAssetAPI.") or v.startswith("UASSETAPI_") or v.startswith("UAssetAPI_")
    if isinstance(v, list):
        return bool(v) and all(isinstance(x, str) and is_placeholder(x) for x in v)
    return False


doc = json.load(open(NEW, encoding="utf-8-sig"))
newd = doc["assets"]
oldd = {os.path.basename(k): v for k, v in json.load(open(OLD, encoding="utf-8-sig"))["assets"].items()}

fixed = {}
unfixable = []
preexisting = 0
for key, entry in newd.items():
    cdo = entry.get("cdo")
    if not cdo:
        continue
    base = os.path.basename(key)
    old_cdo = (oldd.get(base) or {}).get("cdo") or {}
    # 只有「旧 dump 解析得出、新 dump 变成占位符」才算**新增**缺口。
    # 两边都是占位符的（如 `UberGraphFrame`、`historyPhotoSize`）是本来就有的，不动。
    fields = [f for f, v in cdo.items() if is_placeholder(v) and not is_placeholder(old_cdo.get(f))]
    preexisting += sum(1 for f, v in cdo.items()
                       if is_placeholder(v) and is_placeholder(old_cdo.get(f)))
    if not fields:
        continue
    identical = sha(os.path.join(EX_OLD, *key.split("/"))) == sha(os.path.join(EX_NEW, *key.split("/")))
    for f in fields:
        if identical and f in old_cdo:
            cdo[f] = old_cdo[f]
            fixed.setdefault(f, []).append(base)
        else:
            unfixable.append((base, f, str(cdo[f])[:60]))

print(f"新增缺口：可补 {sum(len(v) for v in fixed.values())} 处，不可补 {len(unfixable)} 处"
      f"（另有 {preexisting} 处是两版都有的占位符，不动）")
for f, names in sorted(fixed.items()):
    print(f"  已补 {f}: {len(names)} 张卡（例 {names[:3]}）")
for u in unfixable[:20]:
    print("  ✗ 不可补:", u)

# `card_unit_p37los` 是「整个 cdo 缺失」，单独处理
k37 = [k for k in newd if os.path.basename(k) == "card_unit_p37los.uasset"]
if k37 and not newd[k37[0]].get("cdo"):
    old = oldd.get(os.path.basename(k37[0]), {}).get("cdo")
    if old:
        newd[k37[0]]["cdo"] = old
        print("  已补 card_unit_p37los 的整个 cdo:", len(old), "个字段")

if WRITE:
    with open(NEW, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=1)
    print("已写回", NEW, os.path.getsize(NEW), "B")
else:
    print("（未写回；加 --write 生效）")
