"""
nn-archetype-decks.py —— 卡组性质对照（**只读，不改任何东西**）。

目的：把「快攻卡组过牌少」的两种解释分开 ——
  (a) 快攻卡组**费用低** ⇒ 早期本来就打得起的牌多 ⇒ 被迫过牌少（卡组性质）
  (b) 快攻卡组让**模型更愿意出牌**（模型偏好）

为此对每个卡组统计：
  · 卡牌费用分布（含重复的份数口径 + 去重的种类口径）
  · 平均费用
  · 单位卡（type == unit）占比
  · 前 4 个回合「理论上打得起的牌数」—— 用 kredit 曲线 1/2/3/4：
      第 t 回合可用 kredit = t ⇒ 卡组里 cost ≤ t 的**份数**就是这一回合能支付的最大牌数
      （不是「手上一定有」，是**卡组供给上限**；再看「按比例折算到 4 张起手」的期望值）

数据来源（与 NNPlay 同一份，绝不另写一套卡组解析）：
  · 卡组码        : src/KLink.Bot/Cards/MetaDecks.cs（正则抓取，保持同步）
  · 卡组码→卡名   : <Data>/deck_code_ids.json
  · 卡名→费用/类型: <Data>/cards.json
  卡组码展开规则与 src/KLink.Bot/Cards/DeckCodeParser.cs **逐条一致**：
     %%<主国><盟国>|<牌组>|<HQ>，牌组按 ';' 拆 4 组，第 i 组每个码计 i+1 份。

用法:
  python -X utf8 "klink bot/tools/nn-archetype-decks.py" --out out/_arch-decks.txt
"""

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DATA = REPO / "tools/NNPlay/bin/Release/net10.0/Data"
METADECKS = REPO / "src/KLink.Bot/Cards/MetaDecks.cs"

# 与 DeckCodeParser.Multipliers 一致
MULT = [1, 2, 3, 4]

# 关注这几个卡组（任务书指定的 4 组里的 2 个 NN 卡组 + 2 个对手卡组）
FOCUS = ["德芬车", "美澳跳", "英日", "德澳老兵"]

# 与 CardDatabase.VariantSuffixes 一致：卡组码表里的 xxx_bal / xxx_vet 复用基础卡数据
VARIANT_SUFFIXES = ["_bal", "_vet", "_alt", "_gold"]

# 「单位卡」= 会部署到场上单位的类型（与 KLink.Bot 的 type 字段取值一致）
UNIT_TYPES = {"infantry", "tank", "fighter", "bomber", "artillery"}


def resolve(name, cards):
    """复刻 CardDatabase.Find 的变体回退。返回 (卡名, 卡定义 or None, 是否走了回退)。"""
    if name in cards:
        return name, cards[name], False
    for suf in VARIANT_SUFFIXES:
        if name.endswith(suf) and name[: -len(suf)] in cards:
            return name[: -len(suf)], cards[name[: -len(suf)]], True
    return name, None, False


def parse_metadecks():
    txt = METADECKS.read_text(encoding="utf-8")
    out = {}
    for m in re.finditer(r'\("([^"]+)",\s*"([^"]+)"\)', txt):
        out[m.group(1)] = m.group(2)
    return out


def expand(code, ids):
    """复刻 DeckCodeParser.Parse + Expand。返回 [(卡名, 份数)] 与 unknown 列表。"""
    assert code.startswith("%%"), code
    body = code[2:]
    parts = body.split("|")
    cards = parts[1]
    tilde = cards.find("~")
    if tilde >= 0:
        cards = cards[:tilde]
    groups = cards.split(";")
    counts = Counter()
    for gi, group in enumerate(groups[:len(MULT)]):
        for j in range(0, len(group) - 1, 2):
            counts[group[j:j + 2]] += MULT[gi]
    unknown = []
    items = []
    for import_id, cnt in counts.items():
        name = ids.get(import_id)
        if name is None:
            unknown.append(import_id)
            continue
        items.append((name, cnt))
    return items, unknown


def stats(items, cards):
    """一份卡组的费用/类型统计。items = [(卡名, 份数)]"""
    per_card = []
    missing = []
    fallback = []
    for name, cnt in items:
        base, c, fb = resolve(name, cards)
        if c is None:
            missing.append(name)
            continue
        if fb:
            fallback.append(f"{name}→{base}")
        per_card.append((name, cnt, int(c.get("kredits", -1)), str(c.get("type", "?"))))

    copies = sum(cnt for _n, cnt, _k, _t in per_card)
    kinds = len(per_card)
    cost_copies = Counter()
    cost_kinds = Counter()
    for _n, cnt, k, _t in per_card:
        cost_copies[k] += cnt
        cost_kinds[k] += 1

    mean_cost_copies = (sum(k * cnt for _n, cnt, k, _t in per_card) / copies) if copies else 0.0
    mean_cost_kinds = (sum(k for _n, _c, k, _t in per_card) / kinds) if kinds else 0.0
    unit_copies = sum(cnt for _n, cnt, _k, t in per_card if t in UNIT_TYPES)
    unit_kinds = sum(1 for _n, _c, _k, t in per_card if t in UNIT_TYPES)
    order_copies = sum(cnt for _n, cnt, _k, t in per_card if t == "order")

    le = {}
    for t in (1, 2, 3, 4):
        le[t] = sum(cnt for _n, cnt, k, _t in per_card if 0 <= k <= t)

    return dict(
        copies=copies, kinds=kinds,
        cost_copies=dict(sorted(cost_copies.items())),
        cost_kinds=dict(sorted(cost_kinds.items())),
        mean_cost_copies=mean_cost_copies, mean_cost_kinds=mean_cost_kinds,
        unit_copies=unit_copies, unit_kinds=unit_kinds, order_copies=order_copies,
        le=le, per_card=sorted(per_card, key=lambda x: (x[2], x[0])),
        missing=missing, fallback=fallback,
    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--decks", default="", help="逗号分隔；默认全部内置卡组")
    ap.add_argument("--out", default="")
    a = ap.parse_args()

    ids = json.loads((DATA / "deck_code_ids.json").read_text(encoding="utf-8"))
    cards = json.loads((DATA / "cards.json").read_text(encoding="utf-8"))
    meta = parse_metadecks()

    names = [x for x in a.decks.split(",") if x] if a.decks else list(meta)

    L = []

    def say(s=""):
        print(s)
        L.append(s)

    say("=" * 110)
    say("卡组性质对照（费用 / 类型 / 理论打得起的牌数）—— 只读，不改任何东西")
    say("=" * 110)
    say(f"数据 : {DATA}")
    say("口径 : 卡组码展开与 src/KLink.Bot/Cards/DeckCodeParser.cs 逐条一致（组 i 每码计 i+1 份）")
    say("")

    results = {}
    for nm in names:
        code = meta.get(nm)
        if code is None:
            say(f"⚠ 找不到卡组 {nm}")
            continue
        items, unknown = expand(code, ids)
        st = stats(items, cards)
        st["unknown"] = unknown
        results[nm] = st

    # ---- 表 1：费用分布 ----
    say("=" * 110)
    say("表 1  费用分布（**份数**口径；30 张牌里每个费用各有几张）")
    say("=" * 110)
    maxc = max((max(st["cost_copies"]) for st in results.values() if st["cost_copies"]), default=0)
    hdr = f"{'卡组':<10}{'总份数':>6}{'种类':>5}" + "".join(f"{c:>5}" for c in range(0, maxc + 1))
    hdr += f"{'≤2费':>7}{'≤3费':>7}{'均费':>7}{'单位份数':>9}{'单位占比':>9}{'命令占比':>9}"
    say(hdr)
    for nm, st in results.items():
        cc = st["cost_copies"]
        row = f"{nm:<10}{st['copies']:>6}{st['kinds']:>5}"
        row += "".join(f"{cc.get(c, 0):>5}" for c in range(0, maxc + 1))
        row += (f"{sum(v for k, v in cc.items() if k <= 2):>7}"
                f"{sum(v for k, v in cc.items() if k <= 3):>7}"
                f"{st['mean_cost_copies']:>7.2f}"
                f"{st['unit_copies']:>9}"
                f"{st['unit_copies'] / st['copies']:>9.1%}"
                f"{st['order_copies'] / st['copies']:>9.1%}")
        say(row)
    say("")
    say("  费用行标题 = 费用 0,1,2,...,%d（份数）" % maxc)
    say("  「单位」= type ∈ {infantry, tank, fighter, bomber, artillery}；「命令」= type == order。")

    # ---- 表 2：理论打得起的牌数 ----
    say("")
    say("=" * 110)
    say("表 2  前 4 回合「理论上打得起的牌数」（kredit 曲线 1/2/3/4）")
    say("=" * 110)
    say(f"{'卡组':<10}{'T1(1费)':>9}{'T2(2费)':>9}{'T3(3费)':>9}{'T4(4费)':>9}"
        f"{'T1占比':>8}{'T2占比':>8}{'T3占比':>8}{'T4占比':>8}")
    for nm, st in results.items():
        le = st["le"]
        n = st["copies"]
        say(f"{nm:<10}{le[1]:>9}{le[2]:>9}{le[3]:>9}{le[4]:>9}"
            f"{le[1] / n:>8.1%}{le[2] / n:>8.1%}{le[3] / n:>8.1%}{le[4] / n:>8.1%}")
    say("")
    say("  「打得起的牌数」= 卡组里 cost ≤ t 的**份数**（= 第 t 回合 kredit=t 时单张可支付的最大牌数供给）。")
    say("  这是**卡组供给上限**，不是「手上一定有」—— 起手 4~5 张的期望见下面表 3。")

    # ---- 表 3：起手期望 ----
    say("")
    say("=" * 110)
    say("表 3  起手 4 张（先手）里「打得起的牌」的期望张数 —— 超几何均值 = 4 × (cost≤t 的比例)")
    say("=" * 110)
    say(f"{'卡组':<10}{'T1':>8}{'T2':>8}{'T3':>8}{'T4':>8}   （先手 T1 只有 1 点 kredit）")
    for nm, st in results.items():
        n = st["copies"]
        le = st["le"]
        say(f"{nm:<10}" + "".join(f"{4 * le[t] / n:>8.2f}" for t in (1, 2, 3, 4)))
    say("  起手 5 张（后手）把 4 乘换成 5 即可：T1 期望 = 5 × 占比。")

    # ---- 表 4：≤2 费 / ≤3 费 的**种类**口径 ----
    say("")
    say("=" * 110)
    say("表 4  低费牌的**种类**口径（去重，回答「有几张不同的牌 ≤2/≤3 费」）")
    say("=" * 110)
    say(f"{'卡组':<10}{'≤2费种类':>10}{'≤3费种类':>10}{'总种类':>8}{'≤2费种类占比':>14}{'≤3费种类占比':>14}")
    for nm, st in results.items():
        ck = st["cost_kinds"]
        k = st["kinds"]
        say(f"{nm:<10}{sum(v for c, v in ck.items() if c <= 2):>10}"
            f"{sum(v for c, v in ck.items() if c <= 3):>10}{k:>8}"
            f"{sum(v for c, v in ck.items() if c <= 2) / k:>14.1%}"
            f"{sum(v for c, v in ck.items() if c <= 3) / k:>14.1%}")

    # ---- 明细：重点卡组逐张 ----
    say("")
    say("=" * 110)
    say("表 5  重点卡组逐张明细（按费用升序）")
    say("=" * 110)
    for nm in FOCUS:
        st = results.get(nm)
        if st is None:
            continue
        say("")
        say(f"【{nm}】{st['copies']} 张 / {st['kinds']} 种，均费 {st['mean_cost_copies']:.2f}，"
            f"单位 {st['unit_copies']} 张（{st['unit_copies'] / st['copies']:.0%}）")
        say(f"   {'费用':>4} {'份数':>4} {'类型':<10} 卡名")
        for name, cnt, k, t in st["per_card"]:
            say(f"   {k:>4} {cnt:>4} {t:<10} {name}")
        if st["fallback"]:
            say(f"   变体回退（复用基础卡数据）: {st['fallback']}")
        if st["unknown"]:
            say(f"   ⚠ 未知卡组码: {st['unknown']}")
        if st["missing"]:
            say(f"   ⚠ cards.json 里连基础卡都没有: {st['missing']}")

    txt = "\n".join(L) + "\n"
    if a.out:
        (REPO / a.out).parent.mkdir(parents=True, exist_ok=True)
        (REPO / a.out).write_text(txt, encoding="utf-8")
        print(f"\n已写入 {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
