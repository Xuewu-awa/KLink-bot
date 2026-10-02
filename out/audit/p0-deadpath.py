"""P0 收益量化：**"至少一条效果路径是死的"卡数** 的前后对比。

两层口径（与审计 §1 一致）：
  ① 事件层：卡订阅了某个**内核从不派发**的事件名（IR 的 entrypoints）
  ② 原语层：卡在**玩法路径**上调用了**派发表里没有**的原语（IR 的 call fn）

基线 = `out/_p0-baseline-src`（本轮开工前的源码快照，与 src/.git 的 7c9596a 逐字节一致）
现状 = `src/KLink.Bot`

用法: python out/audit/p0-deadpath.py
"""
import json
import pathlib
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = pathlib.Path(r"<repo-root>")
_ir_arg = None
for _i, _a in enumerate(sys.argv):
    if _a == "--ir":
        _ir_arg = sys.argv[_i + 1]
    if _a == "--src":
        _src_arg = sys.argv[_i + 1]
IR_PATH = pathlib.Path(_ir_arg) if _ir_arg else (ROOT / "klink bot" / "docs" / "card-ir.json")
SRC_PATH = pathlib.Path(_src_arg) if "--src" in sys.argv else (ROOT / "src" / "KLink.Bot")
IR = json.loads(IR_PATH.read_text(encoding="utf-8"))

# 不算"缺口"的名字：UI/引擎回调/战役/入口点本身
NON_GAP_EVENTS = {
    "OnPlayedFromHand", "CustomEvent", "ReceiveBeginPlay", "ReceiveTick", "ReceiveDestroyed",
    "ReceiveEndPlay", "LoadRedrawSettings",
}
NON_GAP_PREFIX = ("OnActor", "Campaign", "OnCreateCardApplyCampaignUpgrades")


def fired_names(src: pathlib.Path):
    """内核真正派发的事件名。

    ⚠️ 必须按**调用**取，不能按行取：`FireTrigger(...)` 常常跨行写，
    程序名（`otherProgramName`）落在下一行 —— 按行扫会漏掉
    `OnOtherCardLeaveBoardOrOwner` 这种（40 张订阅者，一漏就把它算成"从不派发"）。
    """
    out = set()
    for p in src.rglob("*.cs"):
        txt = p.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r"FireTrigger\s*\(", txt):
            window = txt[m.end():m.end() + 600]
            for lit in re.finditer(r'"((?:On)[A-Za-z0-9_]+)"', window):
                out.add(lit.group(1))
        for m in re.finditer(r"RunTriggerProgram\s*\(", txt):
            window = txt[m.end():m.end() + 400]
            for lit in re.finditer(r'"((?:On)[A-Za-z0-9_]+)"', window):
                out.add(lit.group(1))
    return out


def dispatch_keys(src: pathlib.Path):
    keys = set()
    for p in src.rglob("*.cs"):
        txt = p.read_text(encoding="utf-8", errors="replace")
        keys |= set(re.findall(r'\["([A-Za-z0-9_ .:]+)"\]\s*=', txt))
    return keys


def is_gap(name: str) -> bool:
    return name not in NON_GAP_EVENTS and not name.startswith(NON_GAP_PREFIX)


def report(label: str, src: pathlib.Path):
    fired = fired_names(src)
    keys = dispatch_keys(src)

    ev_dead_cards = set()      # 订阅了从不派发的事件
    pr_dead_cards = set()      # 调用了没进派发表的原语
    any_dead = set()
    all_dead = set()

    for card, body in IR.items():
        eps = [e for e in (body.get("entrypoints") or {}) if is_gap(e)]
        calls = {s["fn"] for s in (body.get("steps") or []) if s.get("op") == "call" and s.get("fn")}
        # 第 3 族之后：卡内私有函数由 IR 的 locals 覆盖（派发表优先、locals 兜底），
        # 所以"这张卡自己定义了同名函数体"也算通。
        # ⚠️ 注意 locals 是**每张卡自己的**：`ApplyBuff` 只对定义了它的那 4 张卡算通。
        locals_ = set((body.get("locals") or {}).keys())

        dead_eps = [e for e in eps if e not in fired]
        dead_calls = [c for c in calls
                      if c not in keys and c not in locals_ and not c.startswith("math:")]

        if dead_eps:
            ev_dead_cards.add(card)
        if dead_calls:
            pr_dead_cards.add(card)
        if dead_eps or dead_calls:
            any_dead.add(card)
        if eps and not dead_eps and calls and not dead_calls:
            pass
        elif not eps and not calls:
            pass
        else:
            # "全部路径都死" = 所有事件入口都死（且没有活的原语路径）
            if eps and len(dead_eps) == len(eps) and not calls:
                all_dead.add(card)

    print(f"--- {label} ---")
    print(f"  派发的事件名 {len([f for f in fired if f.startswith('On')])} 个；派发表键 {len(keys)} 个")
    print(f"  ① 订阅了从不派发事件的卡      : {len(ev_dead_cards)}")
    print(f"  ② 调用了未进派发表原语的卡    : {len(pr_dead_cards)}")
    print(f"  ★ 至少一条效果路径是死的      : {len(any_dead)}  / {len(IR)} = {len(any_dead)/len(IR):.0%}")
    # 宽口径：把 UI/引擎回调入口也算进去（审计的 901 用的是这一类统计，但没写清口径）
    loose = sum(1 for card, body in IR.items()
                if any(e not in fired for e in (body.get("entrypoints") or {})))
    print(f"  （宽口径：含 UI/引擎回调入口也算缺口 : {loose} —— 审计的 901 与该口径同量级）")
    return ev_dead_cards, pr_dead_cards, any_dead


base = report("基线 out/_p0-baseline-src（本轮开工前）", ROOT / "out" / "_p0-baseline-src")
if "--src" in sys.argv or "--ir" in sys.argv:
    now = report(f"现状 {SRC_PATH} + IR {IR_PATH.name}", SRC_PATH)
else:
    now = report("现状 src/KLink.Bot（四族之后）", SRC_PATH)

ev_fixed = base[0] - now[0]
pr_fixed = base[1] - now[1]
any_fixed = base[2] - now[2]
print("--- 差量 ---")
print(f"  ① 被修好的卡: {len(ev_fixed)}（剩下 {len(now[0])}）")
print(f"  ② 被修好的卡: {len(pr_fixed)}（剩下 {len(now[1])}）")
print(f"  ★ 至少一条路径从死变活: {len(any_fixed)} 张  ⇒ {len(base[2])} → {len(now[2])}")

# 仍然死的 top 事件（有订阅者但内核不派发）
sub = {}
for card, body in IR.items():
    for e in (body.get("entrypoints") or {}):
        if is_gap(e):
            sub[e] = sub.get(e, 0) + 1
now_fired = fired_names(ROOT / "src" / "KLink.Bot")
rest = sorted(((n, c) for n, c in sub.items() if n not in now_fired), key=lambda t: -t[1])
print(f"--- 仍然从不派发、但仍有订阅者的事件（前 20 / 共 {len(rest)}）---")
for n, c in rest[:20]:
    print(f"  {c:5d}  {n}")
