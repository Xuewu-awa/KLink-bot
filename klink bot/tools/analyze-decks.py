"""算「目标卡组的完整覆盖率」——决定第一版规则内核要做到什么程度。

这是比「全卡池百分比」有用得多的指标：
一张卡只要有一个外部调用没实现，它在对局里就是错的。
所以真正该问的是：**要实现多少个调用，我这几套牌才能全部跑对？**

输入
----
- 卡组码清单（本脚本内嵌，或由 --decks 传入一个每行一个的文件）
- deck_code_ids.json   2 字符码 -> 卡名
- card-effects.json    gen-card-effects.py 的产物（卡名 -> 外部调用集合）

用法
----
    python analyze-decks.py
    python analyze-decks.py --decks mydecks.txt --out docs/卡组覆盖率.md
"""

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                      # klink bot/
REPO = ROOT.parent                      # 仓库根

# 阵营码：1-9 见 DeckCodeManager.CountryName；字母是后续新增阵营
COUNTRIES = {
    "1": "Germany", "2": "Britain", "3": "Japan", "4": "Soviet", "5": "USA",
    "6": "France", "7": "Italy", "8": "Poland", "9": "Finland",
    "a": "Anzac", "b": "?", "c": "?", "d": "?",
}

# 用户给的卡组（名称 -> 卡组码）。未命名的自动编号。
DECKS = [
    ("德芬车",     "%%19|303NE1gVwCxr;2Z3f3X4AoUrKwE;E4nlxoxpza;ux"),
    ("德澳老兵",   "%%2a|0m19DJDSgvoosQsTtXw2y7yJyO;0leFqYwbyc;1VDTy9yd;DR"),
    ("（未命名1）", "%%19|2S2Z303N4AE3iawCxryQ;3XrK;3f4vE4nloUxoza;ux"),
    ("（未命名2）", "%%1a|3NgUu0xrxVz9zf;4pE3E4y1y9zdzx;3fz8;4wxXzc"),
    ("英苏中立",   "%%24|0mDDgvooq0sQtVtXyEyJyU;DQE0qYxmyGyZzB;DRzC;tTyH"),
    ("米色团",     "%%25|0m19oow7;bqEbohsDtVw0wa;vSwbyw;DRpUsU"),
    ("日波炸槽",   "%%38|5CjQp2pezq;5B5Z6w6xtFztzw;p1p6zpzs;pczi"),
    ("日澳快攻",   "%%3a|5C6B6yhHrktGwXznzqzvzx;DJE7tFy2;7ltKy9zs;7axX"),
    ("Sid日波情报", "%%38|5C6BE7hHmarktCuyx3zmznzqzv;iDphtGtywXzs;7etK;E9pg"),
    ("日波情报",   "%%38|5C6BhHjomarktCuyx3ziznzqzv;E7iDphtywXzs;tGtK;E9pg"),
    ("苏英爆破",   "%%42|8C9bfrjWppqTt3tmvewqwwyUyXz2z5;DQE0tVxmyZz0z1;DRzC;yH"),
    ("苏澳中速",   "%%4a|8C9bfrjWppqTt3tmvewqwwxYyXz5;8NDJE0xWxXz0z1zx;xmy9zC;"),
    ("自残苏",     "%%48|7M8Cwy;9mthu5;8I8UE5gbxmzC;7Hz0zi"),
    ("美澳跳",     "%%5a|bCbEbibmcPDBDCdkfGmPtYv6vYy7ycyv;DJv7zx;bPy9yd;bKgg"),
    ("美英跳",     "%%52|bCbEbmcPDBDCdkfGr4rctYu8v6v7vUvXvYw2yv;qYw8;bKbPlgyH;DR"),
    ("美澳极限快", "%%5a|bBDBtYv6vYy3;bqDHjyoh;bOxcyw;DGglvSxX"),
    ("（未命名3）", "%%15|2L2Q2Z31343b3j3m3N3O3YbPgggUlUnDnsoTtdtgu0xrz6;j1nwofoPvh;bKmI;"),
    ("（未命名4）", "%%23|0m0z195NgvoosTwbyEyJ;qYw8;0d1V78oxp3tTyH;5B"),
    ("（未命名5）", "%%25|0j0m0w0z191xgFgvjzkplroosQsTw2w7w8yJ;08bPj1mIofqYwb;mU;bK"),
    ("（未命名6）", "%%35|5C5R5x636B6Y6Z7b7s7xlfnPrk;5z666CbPjOoeohp4p9x3;bKgL;"),
    ("（未命名7）", "%%36|5C636Bzn;2h2l5z6XEagJnNnQp9tKwh;7a7eg3;2p"),
    ("（未命名8）", "%%29|0B0D0d0meFgvjen8ngoopUq0rKsQw7;0l1VnjqYrLrPsOsRtWw8wbwc;;"),
]

MULTIPLIERS = (1, 2, 3, 4)


def parse_deck_code(code: str):
    """返回 (main_country, ally_country, {import_id: count})，格式与 DeckCodeManager.ParseDeckCode 一致。"""
    code = code.strip()
    if not code.startswith("%%"):
        raise ValueError("卡组码必须以 %% 开头")
    body = code[2:]
    parts = body.split("|")
    if len(parts) < 2:
        raise ValueError("卡组码缺少牌组部分")
    country, cards = parts[0], parts[1]
    if "~" in cards:
        cards = cards[:cards.index("~")]

    groups = cards.split(";")
    counts = Counter()
    for gi, group in enumerate(groups):
        if gi >= len(MULTIPLIERS):
            break
        for j in range(0, len(group) - 1, 2):
            counts[group[j:j + 2]] += MULTIPLIERS[gi]
    return country[0:1], country[1:2], counts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--decks", help="每行一个卡组码的文件（可选）")
    ap.add_argument("--deck-ids",
                    default=str(REPO / "src/KLink.Server/Assets/kards-server/deck_code_ids.json"))
    ap.add_argument("--effects", default=str(ROOT / "docs/card-effects.json"))
    ap.add_argument("--out", default=str(ROOT / "docs/卡组覆盖率.md"))
    args = ap.parse_args()

    decks = list(DECKS)
    if args.decks:
        extra = [l.strip() for l in Path(args.decks).read_text(encoding="utf-8").splitlines() if l.strip().startswith("%%")]
        decks = [(f"（文件{i+1}）", c) for i, c in enumerate(extra)]

    print("读取卡牌映射与效果表 ...")
    ids = json.loads(Path(args.deck_ids).read_text(encoding="utf-8-sig"))
    effects = json.loads(Path(args.effects).read_text(encoding="utf-8"))

    # 全局频次（用于判断某个调用属于长尾的哪一段）
    freq = Counter()
    for v in effects.values():
        for c in v.get("external_calls", []):
            freq[c] += 1
    rank = {c: i + 1 for i, (c, _) in enumerate(freq.most_common())}

    # 归类：这张卡是否有「外部调用」
    def calls_of(card_name):
        e = effects.get(card_name)
        if e is None:
            return None                    # 效果表里没有
        return e.get("external_calls", [])

    per_deck = []
    all_needed = Counter()
    all_missing_cards = Counter()

    for name, code in decks:
        try:
            main, ally, counts = parse_deck_code(code)
        except Exception as ex:
            print(f"  [跳过] {name}: {ex}")
            continue

        unknown_ids, unknown_cards, no_effect, with_effect = [], [], 0, 0
        needed = set()
        for iid, cnt in counts.items():
            entry = ids.get(iid)
            if not entry:
                unknown_ids.append(iid)
                continue
            card = entry.get("card") if isinstance(entry, dict) else None
            if not card:
                unknown_ids.append(iid)
                continue
            cl = calls_of(card)
            if cl is None:
                unknown_cards.append(card)
                continue
            if not cl:
                no_effect += 1
                continue
            with_effect += 1
            needed.update(cl)

        for c in needed:
            all_needed[c] += 1
        for c in unknown_cards:
            all_missing_cards[c] += 1

        per_deck.append({
            "name": name, "code": code,
            "main": COUNTRIES.get(main, main), "ally": COUNTRIES.get(ally, ally),
            "total": sum(counts.values()), "unique": len(counts),
            "with_effect": with_effect, "no_effect": no_effect,
            "unknown_ids": unknown_ids, "unknown_cards": unknown_cards,
            "needed_calls": sorted(needed),
        })

    # ---------- 汇总 ----------
    union = set(all_needed)
    union_ranks = sorted((rank.get(c, 10**9), c) for c in union)
    tail = [c for r, c in union_ranks if r > 300]

    md = []
    A = md.append
    A("# 目标卡组覆盖率分析")
    A("")
    A("> 输入：用户提供的 " + str(len(per_deck)) + " 套卡组码")
    A("> 数据：`deck_code_ids.json`（码→卡名）+ `card-effects.json`（卡名→效果调用）")
    A("")
    A("**为什么要算这个**：一张卡只要有一个外部调用没实现，它在对局里就是错的。")
    A("所以真正决定工期的是「要覆盖这些卡组，得实现多少个调用」，不是全卡池百分比。")
    A("")

    A("## 1. 逐套卡组")
    A("")
    A("| 卡组 | 主国 | 盟国 | 张数 | 唯一卡 | 有效果 | 无效果 | 需要调用数 | 找不到的卡 |")
    A("|---|---|---|---|---|---|---|---|---|")
    for d in per_deck:
        A(f"| {d['name']} | {d['main']} | {d['ally']} | {d['total']} | {d['unique']} | "
          f"{d['with_effect']} | {d['no_effect']} | **{len(d['needed_calls'])}** | "
          f"{len(d['unknown_cards'])+len(d['unknown_ids'])} |")
    A("")

    A("## 2. ⭐ 结论：要实现多少个调用")
    A("")
    A(f"这些卡组用到的调用**并集 = {len(union)} 个**（全局共 {len(freq)} 个不同调用）。")
    A("")
    A("| 范围 | 调用数 | 说明 |")
    A("|---|---|---|")
    A(f"| 全部所需 | **{len(union)}** | 实现这些 → 上表所有卡组 100% 正确 |")
    A(f"| 其中在全局前 100 | {sum(1 for r,c in union_ranks if r<=100)} | 高频，几乎所有卡都用 |")
    A(f"| 其中在全局前 300 | {sum(1 for r,c in union_ranks if r<=300)} | 主力 |")
    A(f"| **其中在全局 300 名之后** | **{len(tail)}** | ⚠️ 长尾：只为某一套牌服务 |")
    A("")

    if tail:
        A("### 2.1 长尾调用（全局排名 > 300，但仍被目标卡组需要）")
        A("")
        A("这些是**必须单独实现**的，不能靠「实现高频调用」顺带覆盖。")
        A("")
        A("| 调用 | 全局排名 | 被几套牌需要 |")
        A("|---|---|---|")
        for c in sorted(tail, key=lambda x: rank.get(x, 10**9)):
            A(f"| `{c}` | {rank.get(c,'—')} | {all_needed[c]} |")
        A("")

    A("## 3. 调用被多少套牌需要（复用度）")
    A("")
    A("复用度高的先做，收益最大。")
    A("")
    A("| 调用 | 被几套牌需要 | 全局排名 |")
    A("|---|---|---|")
    for c, n in all_needed.most_common(50):
        A(f"| `{c}` | {n} | {rank.get(c,'—')} |")
    A("")

    missing = {c: n for c, n in all_missing_cards.items()}
    if missing:
        A("## 4. 效果表里找不到的卡")
        A("")
        A("多半是 HQ / 特殊卡 / 非卡牌资产 —— 需要单独确认。")
        A("")
        A("| 卡名 | 出现在几套牌 |")
        A("|---|---|")
        for c, n in Counter(missing).most_common(60):
            A(f"| `{c}` | {n} |")
        A("")

    out = Path(args.out)
    out.write_text("\n".join(md), encoding="utf-8")

    print()
    print(f"卡组数: {len(per_deck)}")
    print(f"所需调用并集: {len(union)}  (全局共 {len(freq)})")
    print(f"  前100内: {sum(1 for r,c in union_ranks if r<=100)}")
    print(f"  前300内: {sum(1 for r,c in union_ranks if r<=300)}")
    print(f"  300名之后(长尾): {len(tail)}")
    if all_missing_cards:
        print(f"效果表缺失卡: {len(all_missing_cards)} 种")
    print(f"已写出: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
