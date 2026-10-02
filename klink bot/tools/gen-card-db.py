"""从线上 pak 的卡牌蓝图 CDO 生成**权威卡牌数据库**。

为什么要重做一份：cards-from-fmodel.json 是较早的导出，缺 34 张线上已有的卡
（用户卡组里就有，例如 card_event_kettenkrad_home_bal）。
CDO（Default__Xxx_C）里才是这张卡的权威字段，而且覆盖全部 2053 张。

输出形状与 cards-from-fmodel.json 兼容，这样 KLink.Bot 可以直接换源。

用法：
    python gen-card-db.py <cards.all.json> <输出.json> [cards-from-fmodel.json 作为补充]
"""

import json
import re
import sys
from pathlib import Path

# CDO 字段名 → 输出键。
# 注意大小写：CDO 里是 operationCost（大写 C），不是 operationcost。
FIELD_MAP = {
    "kredits": "kredits",
    "operationCost": "operationcost",
    "attack": "attack",
    "defense": "defense",
    "range": "range",
    "heavyArmor": "heavyArmor",
    "rarity": "rarity",
    "cardSet": "cardSet",
    "faction": "faction",
    "Type": "type",
    "EffectType": "effectType",
}

# 布尔字段单独处理
BOOL_FIELDS = {
    "hasGuard": "hasGuard",
}


def strip_enum(value: str) -> str:
    """'ETypeEnum::order' -> 'order'；'ERarityEnum::Uncommon' -> 'Uncommon'"""
    if not isinstance(value, str):
        return value
    if "::" in value:
        return value.split("::", 1)[1]
    return value


def localize(value: str) -> str:
    """
    游戏的本地位字符串是 `Base, <pkg>, <key>, <正文>` 形式，取出**正文**。

    ⚠️⚠️ **不能按"最后一个 ', ' 切、取最后一段"** —— 正文自己就含逗号，
    那样会把正文的前半截吃掉（正是本函数原来 `rsplit(", ", 1)[-1]` 的写法）。

    实例（`card_unit_m3a3_honey_desert`）：
        'Base, card_britain, card_unit_m3a3_honey_desert_text, When you draw a card, your HQ gains +1 defense.'
    旧写法切在 `card, ` 上 ⇒ 只剩 'your HQ gains +1 defense.'，
    **触发条件「When you draw a card」整个丢掉**。

    影响面（`out/audit/text-truncation-probe.py` 实测）：**1868 张里 468 张被截断**，
    丢的全是触发条件 / 条件从句，例如：
        'When this unit becomes Veteran'   剩余 'draw 2 cards.'
        'When the enemy deploys a unit'    剩余 'reduce the cost of all orders in your hand by 1'
        'When your HQ is damaged'          剩余 'deal that amount of damage to this unit instead.'

    正确做法：以本地化 key（`<...>_text`）当锚点，取它之后的**全部**内容。
    key 里不含 `", "`，所以锚点是可靠的。
    """
    if not isinstance(value, str):
        return value or ""

    for marker in ("_text, ", "_title, ", "_desc, ", "_flavor, "):
        idx = value.find(marker)
        if idx >= 0:
            return value[idx + len(marker):].strip()

    # 没有可识别的 key：退化成「按 ', ' 切 3 段取第 4 段」，**绝不取最后一段**
    parts = value.split(", ", 3)
    return parts[3].strip() if len(parts) > 3 else value.strip()


def to_int(value, default=0):
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return int(value)
    if isinstance(value, str):
        v = value.strip()
        if v.startswith("E") and "::" in v:      # 枚举
            return default
        try:
            return int(float(v))
        except ValueError:
            return default
    return default


def main():
    src = Path(sys.argv[1])
    dst = Path(sys.argv[2])
    supplement_path = Path(sys.argv[3]) if len(sys.argv) > 3 else None

    print(f"读取 {src} ...")
    batch = json.loads(src.read_text(encoding="utf-8-sig"))
    assets = batch.get("assets", {})
    print(f"  资产 {len(assets)}")

    supplement = {}
    if supplement_path and supplement_path.exists():
        supplement = json.loads(supplement_path.read_text(encoding="utf-8-sig"))
        print(f"  补充源 {supplement_path.name}: {len(supplement)} 张")

    cards = {}
    no_cdo = []
    skipped_non_card = []
    field_seen = {}

    # Cards/ 目录下混着 UI / 渲染辅助蓝图，不是可打的卡
    NON_CARD_PREFIXES = ("BP_", "WBP_", "M_", "Render", "BPI_", "E_", "DT_")

    for asset_path, info in assets.items():
        if not info.get("ok"):
            continue
        name = Path(asset_path).stem
        if name.startswith(NON_CARD_PREFIXES):
            skipped_non_card.append(name)
            continue
        cdo = info.get("cdo")
        if not cdo:
            no_cdo.append(name)
            continue

        for k in cdo:
            field_seen[k] = field_seen.get(k, 0) + 1

        entry = {"name": name}
        for src_key, out_key in FIELD_MAP.items():
            if src_key not in cdo:
                continue
            raw = cdo[src_key]
            if out_key in ("kredits", "operationcost", "attack", "defense", "range", "heavyArmor"):
                entry[out_key] = to_int(raw)
            else:
                entry[out_key] = strip_enum(raw)

        for src_key, out_key in BOOL_FIELDS.items():
            if src_key in cdo:
                entry[out_key] = str(cdo[src_key]).strip().lower() in ("true", "1")

        # 名称与文本
        if "title" in cdo:
            entry["title"] = localize(cdo["title"])
        if "Text" in cdo:
            entry["text"] = localize(cdo["Text"])
        if "flavorText" in cdo:
            entry["flavorText"] = localize(cdo["flavorText"])

        # 用补充源补齐 CDO 里没有的字段（主要是显示名）
        sup = supplement.get(name)
        if sup:
            for k in ("title", "cardSet", "rarity"):
                entry.setdefault(k, sup.get(k))
            if not entry.get("text"):
                entry["text"] = sup.get("text")

        entry["source_pak"] = asset_path
        cards[name] = entry

    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(json.dumps(cards, ensure_ascii=False, indent=0), encoding="utf-8")

    # 统计
    with_stats = sum(1 for c in cards.values() if c.get("attack") is not None)
    with_text = sum(1 for c in cards.values() if c.get("text"))
    types = {}
    for c in cards.values():
        types[c.get("type", "?")] = types.get(c.get("type", "?"), 0) + 1

    print(f"  写出 {len(cards)} 张 → {dst}")
    if skipped_non_card:
        print(f"  跳过非卡牌蓝图: {len(skipped_non_card)}（{skipped_non_card[:4]} …）")
    if no_cdo:
        print(f"  [!] 没有 CDO 的资产 {len(no_cdo)} 个（前 5: {no_cdo[:5]}）")
    print(f"  带攻防的（单位）: {with_stats}")
    print(f"  带文本的: {with_text}")
    print(f"  类型分布: {dict(sorted(types.items(), key=lambda kv: -kv[1]))}")
    missing_keys = [k for k in ("kredits", "attack", "defense", "operationcost", "range")
                    if field_seen.get(k, 0) == 0]
    if missing_keys:
        print(f"  [!] CDO 里从未出现的字段: {missing_keys}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
