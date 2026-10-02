"""生成静态卡向量（向量设计的阶段 A + B + 效果标签）。

依据 docs/向量设计方案.md 已拍板的决定：
  · 标量字段每项一个独立通道
  · 关键字位 + **效果标签**（后者是阶段 C 的起点）
  · 「数值越大越差」的字段要取反 —— 编码让「更好」落在数值更高的位置

数据源：ref/kards-sim/cards.json（含 flags / usedTriggers / 完整 raw CDO）

⚠️ 三个已修正的坑：
  1. **raw 的键名大小写敏感** —— 写成 "operationcost" 会读成 0（之前 4727 处）
  2. **heavyArmor 是数值不是布尔位**
  3. **Veteran / Pinned / Suppressed 不在 CDO 里** —— 它们是运行时状态，属于动态向量

用法: python tools/gen-card-vectors.py
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

SRC = r"..\ref\kards-sim\cards.json"
OUT = "docs/card-vectors.json"

# 标量：(名字, raw 里的键, 归一化上限, 是否取反)
# ⚠️ 凡「越低越好」的一律取反 —— 编码让「更好」落在数值更高的位置，
#    不要让模型自己去学方向。
SCALARS = [
    ("kredits",       "kredits",       12, True),    # ⭐ 部署/使用花费：越低越好
    ("attack",        "attack",        20, False),   # 身材：越大越好
    ("defense",       "defense",       20, False),
    ("range",         "range",          3, False),
    ("operationCost", "operationCost",  5, True),    # ⭐ 行动花费：越低越好
    ("heavyArmor",    "heavyArmor",     5, False),
    ("Type",          "Type",          11, False),
    ("faction",       "faction",       10, False),
    ("rarity",        "rarity",         3, False),
]

# CDO 里真实存在的关键字位（11 个）
KEYWORDS = [
    "Guard", "Blitz", "Smokescreen", "Ambush", "Mobilize",
    "Deployment", "Destruction", "Fury", "Covert", "Salvage", "Scrying",
]


# ⭐⭐ 关键字**交互项**（两两相乘的显式特征）
#
# 为什么必须有：像「闪击 + 奋战」这种组合是**乘性**效应 ——
# 一个部署后立刻能行动、而且能攻击两次的单位，强度远超两者之和。
# 只给两个独立 bit，网络得从数据里自己发现这个乘积；样本不够时学不出来，
# 而这类组合恰恰决定胜负。
#
# 所以：**既给单独的 bit，也给显式的乘积项** —— 这就是「结合它们的联系」。
#
# 先全量生成 11 选 2 = 55 项（不靠猜哪些重要），之后可按重要性裁剪。
def _pairs(keys):
    return [(keys[i], keys[j])
            for i in range(len(keys)) for j in range(i + 1, len(keys))]


KEYWORD_PAIRS = _pairs(KEYWORDS)

# 已知「化学反应」特别强的组合 —— 标出来，方便以后单独加权或重点检查
HOT_PAIRS = [("Blitz", "Fury"), ("Blitz", "Ambush"), ("Ambush", "Covert")]

# ⭐ 语义标签 —— 游戏自己给卡打的**效果分类**
EFFECT_TAGS = [
    "isDraw", "isDirectDamage", "isRemoval", "isAttackBuff", "isDefenseBuff",
    "isHQDamage", "isKreditBuff", "isDiscard", "isRepair", "isHQRepair",
    "hasShock", "hasAlpine", "hasPincer",
    "isReserved", "isInPermanentPool",
]
# ⚠️ 这一层只是「特效」的第一层。像「抽两张牌」「无法被位置定向指令指定」
#    这类具体特效，游戏没打标签，**要一个个自己设定** —— 见阶段 C。


def build_cat(cards, field):
    """分类字段：按取值排序给稳定索引（Type/faction/rarity 在 cards.json 顶层是字符串）。"""
    vals = sorted({str((c.get(field) or "")).lower() for c in cards})
    vals = [v for v in vals if v]
    return {v: i for i, v in enumerate(vals)}


def norm(v, cap, invert):
    try:
        v = float(v if v is not None else 0)
    except (TypeError, ValueError):
        v = 0.0
    x = max(0.0, min(1.0, v / cap)) if cap else 0.0
    return round(1.0 - x if invert else x, 4)


def main():
    cards = json.load(open(SRC, encoding="utf-8"))
    print(f"读入 {len(cards)} 张卡")

    # 触发时机：过滤掉枚举哨兵值
    trigs = set()
    for c in cards:
        for t in (c.get("usedTriggers") or []):
            if t and t != "NotAvailable" and not t.endswith("_MAX"):
                trigs.add(t)
    trig_list = sorted(trigs)

    cat_type = build_cat(cards, "type")
    cat_fac  = build_cat(cards, "faction")
    cat_rar  = build_cat(cards, "rarity")
    print("分类映射:")
    print("  type   ", cat_type)
    print("  faction", cat_fac)
    print("  rarity ", cat_rar)

    out, missing = {}, 0
    for c in cards:
        cid = c.get("id")
        if not cid:
            continue
        raw = c.get("raw") or {}

        # Type/faction/rarity 取自顶层字符串；其余取自 raw
        cat = {"Type": cat_type.get(str(c.get("type") or "").lower(), 0),
               "faction": cat_fac.get(str(c.get("faction") or "").lower(), 0),
               "rarity": cat_rar.get(str(c.get("rarity") or "").lower(), 0)}
        s = []
        for _, key, cap, inv in SCALARS:
            if key in cat:
                v = cat[key]
            else:
                v = raw.get(key)
                if v is None:
                    missing += 1
            s.append(norm(v, cap, inv))

        bits = [1 if raw.get("has" + kw) else 0 for kw in KEYWORDS]
        # ⭐ 交互项：两个关键字同时有 → 1。这是「结合它们的联系」那一半。
        inter = [1 if (bits[KEYWORDS.index(a)] and bits[KEYWORDS.index(b)]) else 0
                 for a, b in KEYWORD_PAIRS]
        k = bits + inter + [1 if raw.get(t) else 0 for t in EFFECT_TAGS]

        used = set(c.get("usedTriggers") or [])
        t = [1 if tr in used else 0 for tr in trig_list]

        out[cid] = {"s": s, "k": k, "t": t}

    labels = KEYWORDS + [a+"x"+b for a,b in KEYWORD_PAIRS] + EFFECT_TAGS
    schema = {
        "scalars": [{"name": n, "key": k, "cap": c, "invert": i} for n, k, c, i in SCALARS],
        "keywords": KEYWORDS,
        "keyword_keys": ["has" + k for k in KEYWORDS],
        "keyword_pairs": [[a,b] for a,b in KEYWORD_PAIRS],
        "hot_pairs": [[a,b] for a,b in HOT_PAIRS],
        "interaction_rule": "两关键字同时为 1 时该项为 1（显式乘性特征）",
        "cat_maps": {"type": cat_type, "faction": cat_fac, "rarity": cat_rar},
        "effect_tags": EFFECT_TAGS,
        "triggers": trig_list,
        "dims": {
            "scalars": len(SCALARS),
            "bits": len(labels) - len(KEYWORD_PAIRS),
            "interactions": len(KEYWORD_PAIRS),
            "triggers": len(trig_list),
            "total_static": len(SCALARS) + len(labels) + len(trig_list),
        },
        "note": "阶段 A+B + 效果标签。效果原子槽（阶段 C）尚未展开。",
    }
    json.dump({"schema": schema, "cards": out},
              open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=0)

    print(f"\n=== {OUT} ===")
    print(f"  卡数 {len(out)}   维度 {schema['dims']['total_static']}/卡"
          f"  (标量 {len(SCALARS)} + 位 {len(labels)} + 触发 {len(trig_list)})")
    if missing:
        print(f"  ⚠ 标量缺失 {missing} 处（按 0 处理）")

    for probe in ("card_unit_stug_iii", "card_unit_7_schutzen", "card_unit_10_5_cm_lefh"):
        if probe in out:
            v = out[probe]
            on = [labels[i] for i, x in enumerate(v["k"]) if x]
            ton = [trig_list[i] for i, x in enumerate(v["t"]) if x]
            print(f"\n  {probe}")
            print(f"    标量 {v['s']}")
            print(f"    位   {on or '（无）'}")
            print(f"    触发 {ton or '（无）'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
