"""Read either dump-batch assets or the complete, resolved pak card manifest.

The manifest's explicit overrides omit inherited native CDO fields. For that
input, use the adjacent read-only runtime scalar capture for inherited values;
keep pak enum names and structured properties. Never export object addresses.
"""
import json
from pathlib import Path


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def load_assets(source, *, functions=False):
    source = Path(source)
    data = read_json(source)
    if isinstance(data, dict) and "assets" in data:
        return data["assets"]
    if not isinstance(data, list):
        raise ValueError("Expected dump-batch assets or cards.definitions.json")
    scalar_path = source.with_name("runtime-card-cdos.scalars.json")
    scalars = {}
    if scalar_path.exists():
        scalars = {c["card_id"]: c["fields"] for c in read_json(scalar_path)["cards"]}
    assets = {}
    enum_fields = {"Type", "faction", "rarity", "cardSet", "EffectType", "exileNation"}
    for card in data:
        cdo = dict(scalars.get(card["name"], {}))
        # The native inherited default for these enums is zero. Nonzero enum
        # values must have a pak override; do not guess names from old mappings.
        for field in enum_fields:
            if cdo.get(field) == 0:
                cdo[field] = "NotAvailable"
        cdo.update(card["explicit_overrides"])
        info = {"ok": True, "cdo": cdo}
        if functions and card.get("functions_file"):
            file = Path(card["functions_file"])
            if not file.is_absolute():
                file = source.parent / file
            info["functions"] = {
                name: summarize_function(fn)
                for name, fn in read_json(file).items()
            }
        assets[card["asset"]] = info
    return assets


def summarize_function(fn):
    if "bytecode" not in fn:
        return fn
    found = {"calls": set(), "objects": set(), "strings": set(), "ints": set()}
    def walk(value):
        if isinstance(value, dict):
            inst = value.get("Inst", "")
            if "Function" in inst or inst == "CallMath":
                call = value.get("FunctionName") or value.get("Function")
                if call:
                    found["calls"].add(call)
            if inst == "ObjectConst" and isinstance(value.get("Object"), str):
                found["objects"].add(value["Object"])
            if inst in ("StringConst", "UnicodeStringConst", "NameConst"):
                literal = value.get("Value")
                if isinstance(literal, str):
                    found["strings"].add(literal)
            if inst in ("IntConst", "IntConstByte", "ByteConst", "Int64Const"):
                literal = value.get("Value")
                if isinstance(literal, int):
                    found["ints"].add(literal)
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)
    walk(fn["bytecode"])
    return {k: sorted(v) for k, v in found.items()}


def innate_abilities(cdo):
    result = {}
    raw = cdo.get("receivedAbilitiesFromCards", [])
    # ⚠️ `dump-batch` 的**单文件形态**里，这个 map 属性有时整块是占位符
    #（实测 `["UAssetAPI.PropertyTypes.Objects.MapPropertyData"]`）⇒ 旧实现
    # `for key, value in raw` 直接 `ValueError: not enough values to unpack`。
    # 占位符的含义是"这次解析不出来"，跳过即可 —— 上层会保留旧表里已经解析好的值。
    if not isinstance(raw, list):
        return result
    for item in raw:
        if not (isinstance(item, (list, tuple)) and len(item) == 2):
            continue
        key, value = item
        if not (isinstance(key, dict) and isinstance(value, dict)):
            continue
        name = key.get("Value")
        members = value.get("Value")
        if name is None or not isinstance(members, list):
            continue
        givers = next((p.get("Value") for p in members
                       if isinstance(p, dict) and p.get("Name") == "CardsGivingAbility"), None)
        if not isinstance(givers, list):
            continue
        result[name] = [int(g["Value"]) for g in givers
                        if isinstance(g, dict) and "Value" in g]
    return result
