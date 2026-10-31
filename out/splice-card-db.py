"""把 1.61 pak 的**真实 CDO 数值**逐字段并进现有卡表 —— 只动「游戏数据真的改了的卡」。

为什么不能整份重生成
--------------------
`cards.live.json` 原本是从「pak 定义清单（manifest）+ 运行时 CDO 标量捕获」生成的，
那份清单带**已解析的继承默认值**；而 `UAssetCLI dump-batch` 的单文件形态**不带**。
实测：用单文件形态整份重生成后，几乎每张卡的 `operationcost / attack / defense /
range / heavyArmor / effectType / cipher / targetTax / hasGuard` 都从 `0`/`NotAvailable`
退化成 `None` —— 等于把 2019 张卡的默认值全清空。

所以采用与 IR 相同的策略：**只有 CDO 或字节码真的变了的卡**才更新，
且只写「新 CDO 里确实有值」的字段；其余一律保留现有值。

用法：python splice-card-db.py [--write]
"""
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = r"E:\项目\klink-dotnet"
CUR = os.path.join(ROOT, r"rel\data\fyserver\BotData\cards.json")
DUMPS = [(os.path.join(ROOT, r"temp\pak-20261023\cards.all.json"), "old"),
         (os.path.join(ROOT, r"temp\pak-20261031\cards.all.json"), "new")]
OUT = os.path.join(ROOT, r"temp\pak-20261031\cards.live.json")

WRITE = "--write" in sys.argv

FIELD_MAP = {
    "kredits": "kredits", "operationCost": "operationcost", "attack": "attack",
    "defense": "defense", "range": "range", "heavyArmor": "heavyArmor",
    "rarity": "rarity", "cardSet": "cardSet", "faction": "faction", "Type": "type",
    "EffectType": "effectType", "cipher": "cipher",
    "KreditsTax_AsEnemyTarget": "targetTax",
}
# ⚠️ **故意不写 `spawnCardName`**：当前卡表里 0 张卡有这个键（内核不读它），
# 而 1.61 的 CDO 里这个字段在某些卡上是**分号拼接的多值**
#（例 `card_unit_yokosuka_e14y` 是 `card_event_sunny1_blue_sky;card_event_rain1_mist;…`），
# 直接写进去会把"天气池"当成"生成卡名"。dry-run 时抓到，已排除。
BOOL_FIELDS = {
    "hasGuard": "hasGuard",
    "waitWithOnOtherCardPlayedUntilHandTargetSelected": "waitForHandTarget",
}
INT_OUT = {"kredits", "operationcost", "attack", "defense", "range", "heavyArmor",
           "cipher", "targetTax"}


def strip_enum(v):
    if isinstance(v, str) and "::" in v:
        return v.split("::", 1)[1]
    return v


def to_int(v):
    if isinstance(v, bool):
        return int(v)
    if isinstance(v, (int, float)):
        return int(v)
    if isinstance(v, str):
        s = v.strip()
        if s.startswith("E") and "::" in s:
            return 0
        try:
            return int(float(s))
        except ValueError:
            return 0
    return 0


def localize(v):
    if isinstance(v, dict):
        return v.get("source_text", v.get("CultureInvariantString", ""))
    if not isinstance(v, str):
        return v or ""
    for marker in ("_text, ", "_title, ", "_desc, ", "_flavor, "):
        i = v.find(marker)
        if i >= 0:
            return v[i + len(marker):].strip()
    parts = v.split(", ", 3)
    return parts[3].strip() if len(parts) > 3 else v.strip()


def bad(v):
    return v is None or (isinstance(v, str)
                         and ("UAssetAPI" in v or "UASSETAPI" in v or v == "None"))


def load_dump(path):
    return {os.path.basename(k): v
            for k, v in json.load(open(path, encoding="utf-8-sig"))["assets"].items()}


def fp(entry):
    fns = entry.get("functions") or {}
    body = {n: json.dumps(f.get("bytecode"), sort_keys=True, ensure_ascii=False)
            for n, f in fns.items()}
    return json.dumps({"functions": body, "cdo": entry.get("cdo") or {}},
                      sort_keys=True, ensure_ascii=False)


old, new = load_dump(DUMPS[0][0]), load_dump(DUMPS[1][0])
changed = sorted(b[:-len(".uasset")] for b in new
                 if b in old and fp(old[b]) != fp(new[b]) and b.endswith(".uasset"))
print(f"游戏数据真有变化的卡：{len(changed)} 张")

cur = json.load(open(CUR, encoding="utf-8-sig"))
touched = 0
for name in changed:
    base = name + ".uasset"
    cdo = new.get(base, {}).get("cdo") or {}
    if not cdo:
        print(f"  ! {name}: 新 dump 没有 CDO，跳过")
        continue
    entry = cur.get(name)
    if entry is None:
        print(f"  ! {name}: 卡表里没有这张卡，跳过")
        continue
    delta = {}
    for src, dst in FIELD_MAP.items():
        if src not in cdo or bad(cdo[src]):
            continue
        val = to_int(cdo[src]) if dst in INT_OUT else strip_enum(cdo[src])
        if bad(val):
            continue
        if entry.get(dst) != val:
            delta[dst] = (entry.get(dst), val)
            entry[dst] = val
    for src, dst in BOOL_FIELDS.items():
        if src not in cdo or bad(cdo[src]):
            continue
        val = str(cdo[src]).strip().lower() in ("true", "1")
        if entry.get(dst) != val:
            delta[dst] = (entry.get(dst), val)
            entry[dst] = val
    for f in ("customName1", "customName2"):
        if f in cdo and not bad(cdo[f]) and entry.get(f) != cdo[f]:
            delta[f] = (entry.get(f), cdo[f])
            entry[f] = cdo[f]
    for src, dst in (("title", "title"), ("Text", "text"), ("flavorText", "flavorText")):
        if src in cdo and not bad(cdo[src]):
            val = localize(cdo[src])
            if val and entry.get(dst) != val:
                delta[dst] = (entry.get(dst), val)
                entry[dst] = val
    if str(cdo.get("selectTargetOnPlayedFromHand", False)).lower() in ("true", "1"):
        if entry.get("selectTargetOnPlayedFromHand") is not True:
            delta["selectTargetOnPlayedFromHand"] = (entry.get("selectTargetOnPlayedFromHand"), True)
            entry["selectTargetOnPlayedFromHand"] = True
    if delta:
        touched += 1
        print(f"  {name}")
        for k, (a, b) in delta.items():
            sa, sb = str(a), str(b)
            if len(sa) > 70:
                sa = sa[:70] + "…"
            if len(sb) > 70:
                sb = sb[:70] + "…"
            print("      %-26s %s  ->  %s" % (k, sa, sb))

print(f"\n实际改动 {touched} 张卡")
if WRITE:
    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(cur, fh, ensure_ascii=False, indent=0)
    print("已写出", OUT, os.path.getsize(OUT), "B")
else:
    print("（未写出；加 --write 生效）")
