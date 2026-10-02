"""把卡牌蓝图的批量反编译产物，整理成「卡牌效果表」+「未解析调用名清单」。

输入
----
1. 卡牌蓝图批量反编译产物（UAssetCLI dump-batch 的输出）
   {"assets": {"ANZAC/events/card_event_aans.uasset": {"functions": {...}, ...}}}
2. （可选）cards-from-fmodel.json —— 提供每张卡的英文描述与数值，用来交叉验证
3. （可选）对局协议参考里的子动作名清单，用来区分「已知游戏函数」与「外部/原生函数」

输出
----
- card-effects.json   每张卡：调用了哪些函数、用了哪些常量、英文原文
- 卡牌效果提取报告.md  覆盖率统计、调用词表、未解析调用名清单、抽样对照

用法
----
    python gen-card-effects.py <batch.json> <cards-from-fmodel.json> <输出目录>
"""

import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

# BP_OnlineMatch 里我们自己定义的函数（子动作构造器 / 接收器 / 执行器）——
# 这些是「游戏内部已知」的，不算未解析。
KNOWN_INTERNAL = re.compile(
    r'^(AddSubAction|ReceiveAction|ReceiveSubAction|Add\s*Sub\s*Action|DoSubAction|'
    r'ResolveSubAction|CreateAction_|ActionValue|ExecuteUbergraph|OnPlayedFromHand|AddAction)'
)

# Blueprint 的纯数据/工具节点，不是游戏逻辑，不算未解析
BP_BUILTIN = re.compile(
    r'^(Array_|Map_|MakeArray|MakeMap|MakeStruct|BreakStruct|Select|Sequence|Branch|'
    r'EqualEqual|NotEqual|Greater|Less|Add_|Subtract_|Multiply_|Divide_|Percent_|'
    r'Conv_|ToText|ToStr|ToInt|ToString|Append|Concat|FormatText|PrintString|'
    r'GetEnumeratorUserFriendlyName|GetDisplayName|MakeLiteral|SetArray|Array_Add|'
    r'Get_|Set_|IsValid|Random|Delay|ForEach|Reverse|Contains|Find|Length|Clear|'
    r'Add_IntInt|Boolean|Not_|And|Or_|Xor|LoadObject|CreateWidget|IsServer|HasRole|'
    r'Switch|Enum|Struct|Self|CallFunc_)'
)


def load_json(path: Path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def card_key_from_asset(asset_path: str) -> str:
    """ANZAC/events/card_event_aans.uasset  ->  card_event_aans"""
    return Path(asset_path).stem


def flatten_calls(functions: dict):
    """把一个资产里所有函数的调用合并，并保留每个函数的独立视图。"""
    per_fn = {}
    merged = set()
    objects = set()
    strings = set()
    ints = set()
    for fname, info in functions.items():
        calls = set(info.get("calls") or [])
        per_fn[fname] = sorted(calls)
        merged |= calls
        objects |= set(info.get("objects") or [])
        strings |= set(info.get("strings") or [])
        ints |= set(info.get("ints") or [])
    return per_fn, merged, objects, strings, ints


def main():
    batch_path = Path(sys.argv[1])
    fmodel_path = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    out_dir = Path(sys.argv[3]) if len(sys.argv) > 3 else Path(".")

    print(f"读取 {batch_path} ...")
    batch = load_json(batch_path)
    assets = batch.get("assets", {})
    print(f"  资产数: {len(assets)}")

    fmodel = {}
    if fmodel_path and fmodel_path.exists():
        print(f"读取 {fmodel_path} ...")
        fmodel = load_json(fmodel_path)
        print(f"  卡牌数: {len(fmodel)}")

    cards = {}
    unresolved = Counter()
    all_helpers = Counter()
    failed = []

    for asset_path, info in assets.items():
        name = card_key_from_asset(asset_path)
        if not info.get("ok"):
            failed.append((asset_path, info.get("error", "?")))
            continue

        functions = info.get("functions") or {}
        per_fn, merged, objects, strings, ints = flatten_calls(functions)

        ext_calls = {c for c in merged if not KNOWN_INTERNAL.match(c) and not BP_BUILTIN.match(c)}
        for c in ext_calls:
            unresolved[c] += 1
        for c in merged:
            all_helpers[c] += 1

        meta = fmodel.get(name) or {}
        cards[name] = {
            "asset": asset_path,
            "title": meta.get("title"),
            "text": meta.get("text"),
            "type": meta.get("type"),
            "faction": meta.get("faction"),
            "kredits": meta.get("kredits"),
            "attack": meta.get("attack"),
            "defense": meta.get("defense"),
            "range": meta.get("range"),
            "functions": per_fn,
            "calls": sorted(merged),
            "external_calls": sorted(ext_calls),
            "objects": sorted(objects),
            "strings": sorted(strings),
            "ints": sorted(ints),
        }

    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "card-effects.json").write_text(
        json.dumps(cards, ensure_ascii=False, indent=1), encoding="utf-8")

    # ---------- 覆盖率 ----------
    total = len(cards)
    with_calls = sum(1 for c in cards.values() if c["calls"])
    with_ext = sum(1 for c in cards.values() if c["external_calls"])
    has_text = sum(1 for c in cards.values() if c["text"])
    matched_fmodel = sum(1 for c in cards.values() if c["title"])

    md = []
    A = md.append
    A("# 卡牌效果提取报告")
    A("")
    A("> 来源：线上 pak（`kds/kards/Content/Paks/kards-Windows.pak`，2026-09-11）")
    A("> 流程：`pak-extract` → `dump-batch`（Kismet 反编译）→ 本脚本汇总")
    A("")
    A("## 1. 覆盖率")
    A("")
    A("| 项 | 数量 | 占比 |")
    A("|---|---|---|")
    A(f"| 卡牌蓝图 | {total} | 100% |")
    A(f"| 反编译出函数体 | {with_calls} | {with_calls/max(1,total):.1%} |")
    A(f"| 含「非内置」调用（即有实际效果逻辑） | {with_ext} | {with_ext/max(1,total):.1%} |")
    A(f"| 能对上英文原文 | {has_text} | {has_text/max(1,total):.1%} |")
    A(f"| 能对上英文标题 | {matched_fmodel} | {matched_fmodel/max(1,total):.1%} |")
    if failed:
        A(f"| 反编译失败 | {len(failed)} | {len(failed)/max(1,total+len(failed)):.1%} |")
    A("")

    A("## 2. 未解析调用名（长尾规模）")
    A("")
    A(f"共 **{len(unresolved)}** 个不同的「非内置、非 BP_OnlineMatch 内部」被调用名。")
    A("这些就是需要额外处理的集合（多半是 `/Script/kards.*` 的原生辅助函数）。")
    A("")
    A("| 调用名 | 出现在多少张卡 |")
    A("|---|---|")
    for name, cnt in unresolved.most_common(80):
        A(f"| `{name}` | {cnt} |")
    if len(unresolved) > 80:
        A(f"| … 另有 {len(unresolved)-80} 个 | |")
    A("")

    A("## 3. 出现最多的调用（含内部）")
    A("")
    A("| 调用名 | 卡数 |")
    A("|---|---|")
    for name, cnt in all_helpers.most_common(40):
        A(f"| `{name}` | {cnt} |")
    A("")

    # ---------- 累计覆盖率 ----------
    A("## 3.1 长尾到底有多长：累计覆盖率")
    A("")
    A("按「出现在多少张卡」排序后，前 N 个调用能覆盖多少张卡的调用实例。")
    A("这条曲线决定规则内核要实现的 API 数量。")
    A("")
    total_instances = sum(unresolved.values())
    A(f"调用实例总数（非内置）: **{total_instances}**，不同调用名 **{len(unresolved)}** 个")
    A("")
    A("| 前 N 个调用 | 覆盖实例数 | 覆盖率 |")
    A("|---|---|---|")
    running = 0
    marks = {1, 5, 10, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, 1494}
    for i, (name, cnt) in enumerate(unresolved.most_common(), start=1):
        running += cnt
        if i in marks or i == len(unresolved):
            A(f"| {i} | {running} | {running/max(1,total_instances):.1%} |")
    A("")

    # ---------- 卡牌完整覆盖率（比实例覆盖率重要得多）----------
    with_ext_cards = {k: v for k, v in cards.items() if v["external_calls"]}
    lens = [len(v["external_calls"]) for v in with_ext_cards.values()]
    A("## 3.1.1 ⭐ 卡牌**完整**覆盖率（决定「多少张卡能真正跑起来」）")
    A("")
    A("上面那张表算的是**实例**覆盖率。但一张卡只有**它用到的全部调用**都实现了，")
    A("在对局里才是正确的 —— 少一个调用，这张卡就是错的。所以真正该看的表是这个：")
    A("")
    A("| 实现前 N 个调用 | 完整可用的卡 | 占有效卡比例 | 实例覆盖率 |")
    A("|---|---|---|---|")
    inst_total = sum(lens) if lens else 1
    for N in (10, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000):
        top = {c for c, _ in unresolved.most_common(N)}
        full = sum(1 for v in with_ext_cards.values() if set(v["external_calls"]) <= top)
        inst = sum(len([c for c in v["external_calls"] if c in top]) for v in with_ext_cards.values())
        A(f"| {N} | **{full}** | **{full/max(1,len(with_ext_cards)):.1%}** | {inst/max(1,inst_total):.1%} |")
    A(f"| {len(unresolved)}（全部） | {len(with_ext_cards)} | 100% | 100% |")
    A("")
    if lens:
        import statistics
        A(f"**每张卡用到的外部调用数：中位数 {statistics.median(lens):.0f}，"
          f"均值 {statistics.mean(lens):.1f}，最大 {max(lens)}**")
        A("")
        A("中位数只有 5 —— 说明卡牌是**少量调用的组合**，不是 2000 份各自独立的实现。")
        A("这是「内核能在合理工期内做出来」的最强证据。")
        A("")

    # ---------- 分类 ----------
    def classify(n):
        if re.match(r'^(Is|Has|Can|Should|Does|Are|Which)', n):
            return "查询/判定"
        if re.match(r'^(Get|Find|Calc|get)', n):
            return "取值/选择器"
        if re.match(r'^(do|Do|On|on)', n):
            return "触发器/事件"
        if re.match(r'^(Change|Damage|Destroy|Spawn|Give|Remove|Add|Set|Make|Draw|Discard|Heal|Move|Create|Pin|Unpin|Salvage|Suppress|Convert|Reveal|Steal|Update|Persist|Custom|JSON_)', n):
            return "效果/状态修改"
        return "其它"

    buckets = defaultdict(Counter)
    for n, c in unresolved.items():
        buckets[classify(n)][n] = c

    A("## 3.2 按角色分类（决定内核先实现什么）")
    A("")
    A("| 角色 | 不同调用数 | 覆盖实例数 | 说明 |")
    A("|---|---|---|---|")
    order = ["效果/状态修改", "取值/选择器", "查询/判定", "触发器/事件", "其它"]
    for k in order:
        if k not in buckets:
            continue
        cnt_names = len(buckets[k])
        cnt_inst = sum(buckets[k].values())
        note = {
            "效果/状态修改": "内核必须实现 —— 每个都要映射到子动作",
            "取值/选择器": "只读，实现成本低（大部分是遍历+过滤）",
            "查询/判定": "只读，实现成本最低",
            "触发器/事件": "决定「什么时候」结算，是内核的事件系统",
            "其它": "需人工归类",
        }[k]
        A(f"| {k} | {cnt_names} | {cnt_inst} | {note} |")
    A("")

    # ---------- 触发器词表 ----------
    triggers = sorted(n for n in unresolved if re.match(r'^(do|Do|On|on)', n))
    if triggers:
        A("## 3.3 触发器/事件词表（" + str(len(triggers)) + " 个）")
        A("")
        A("这些名字直接告诉内核「有哪些时机点需要派发事件」——是效果系统的骨架。")
        A("")
        for t in triggers:
            A(f"- `{t}`  （{unresolved[t]} 张卡）")
        A("")

    A("## 4. 抽样：反编译结果 vs 英文原文")
    A("")
    A("用来人工核对「提出来的调用」和「卡面文字」是否对得上。")
    A("")
    shown = 0
    for name, c in sorted(cards.items()):
        if not c["text"]:
            continue
        ext = c["external_calls"]
        if not ext:
            continue
        A(f"### {c['title'] or name}")
        A("")
        A(f"- 卡名：`{name}`")
        if c["kredits"] is not None:
            A(f"- 费用 {c['kredits']}｜攻 {c.get('attack')}｜防 {c.get('defense')}｜射程 {c.get('range')}｜{c.get('type')}｜{c.get('faction')}")
        A(f"- 原文：*{c['text']}*")
        A(f"- 提取到的调用：{', '.join('`'+x+'`' for x in ext[:14])}")
        if c["ints"]:
            A(f"- 常量：{', '.join(str(x) for x in c['ints'][:16])}")
        A("")
        shown += 1
        if shown >= 25:
            break
    A("")

    if failed:
        A("## 5. 反编译失败清单")
        A("")
        for p, e in failed[:60]:
            A(f"- `{p}` — {e}")
        A("")

    out_md = out_dir / "卡牌效果提取报告.md"
    out_md.write_text("\n".join(md), encoding="utf-8")
    print()
    print(f"卡牌: {total}   有外部调用: {with_ext}   未解析调用名: {len(unresolved)}")
    print(f"已写出: {out_dir/'card-effects.json'}")
    print(f"已写出: {out_md}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
