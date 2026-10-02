"""
nn-early-logscan.py —— 扫 NNPlay 对局日志，量「NN 前期不出牌」。

**只读日志，不改任何东西。**

统计口径（两种都给，别混）：
  · 决策级空过率 = 选中「结束回合」的决策 / 总决策
  · 回合级空过率 = 该回合 NN 的动作序列**只有**「结束回合」的回合 / NN 出过手的回合
    （任务书里的 35.3% / 24.0% 是这个口径）

同时给 H1（同分并列）与 H5（候选构成）需要的数字。

用法:
  python "klink bot/tools/nn-early-logscan.py" "out/_r8-logs" "out/_r7-logs" ...
  python "klink bot/tools/nn-early-logscan.py" --glob "out/_r8-logs/*.log" --out out/_early-logscan.txt
"""

import argparse
import glob
import os
import re
import sys
from collections import defaultdict

DEC = re.compile(r"^🧠 决策 #(\d+)\s+\[T(\d+)/(\w+)\]\s+kredit (\d+)/(\d+)\s+"
                 r"手牌 (\d+)\s+场面 (\d+)\s+HQ (\d+)/(\d+)")
EVAL = re.compile(r"^\s*当前局面估值 P\((\w+) 胜\) = ([\d.]+)%\s+"
                  r"（枚举 (\d+) 个，合法 (\d+) 个，内核拒绝 (\d+) 个）")
CAND = re.compile(r"^\s*(✔?)\s*([\d.]+)%\s+(.+?)\s*$")
MORE = re.compile(r"…还有 (\d+) 个候选没列")
PICK = re.compile(r"^\s*→ 选 \[([\d.]+)%\]\s+(.+)$")
MODEL = re.compile(r"^模型\s*:\s*(.+?)\s*$")
ROLLOUT = re.compile(r"^试算\s*:\s*(.+?)\s*$")
NNWIN = re.compile(r"^NN 是\s*:\s*(\S+)\s*⇒\s*(.+?)\s*$")
TURNS = re.compile(r"^回合数\s*:\s*(\d+)")
NNDEC = re.compile(r"^NN 决策\s*:\s*(\d+) 次")

NOTE_SUFFIX = "（已用 GreedyBot 走完本回合后估值）"


def kind_of(desc: str) -> str:
    d = desc.replace(NOTE_SUFFIX, "").strip()
    if d.startswith("结束") and "回合" in d:
        return "EndTurn"
    if d.startswith("出牌"):
        return "Play"
    if d.startswith("攻击"):
        return "Attack"
    if d.startswith("移动"):
        return "Move"
    return "?"


def parse(path):
    """返回该局日志的决策列表 + 元信息。"""
    decisions = []
    meta = {"path": path, "model": None, "rollout": None, "win": None, "turns": None}
    cur = None
    cands = []
    more = 0

    def flush():
        if cur is not None:
            cur["cands"] = cands
            cur["more"] = more
            decisions.append(cur)

    with open(path, encoding="utf-8", errors="replace") as f:
        for ln in f:
            ln = ln.rstrip("\n")
            m = MODEL.match(ln)
            if m:
                meta["model"] = m.group(1)
                continue
            m = ROLLOUT.match(ln)
            if m:
                meta["rollout"] = m.group(1)
                continue
            m = NNWIN.match(ln)
            if m:
                meta["win"] = m.group(2)
                continue
            m = TURNS.match(ln)
            if m:
                meta["turns"] = int(m.group(1))
                continue
            m = DEC.match(ln)
            if m:
                flush()
                g = m.groups()
                cur = dict(idx=int(g[0]), turn=int(g[1]), side=g[2],
                           kredit=int(g[3]), kmax=int(g[4]),
                           hand=int(g[5]), board=int(g[6]),
                           hq=int(g[7]), hq_opp=int(g[8]),
                           enum=None, legal=None, rejected=None,
                           cur_eval=None, chosen=None, chosen_score=None)
                cands = []
                more = 0
                continue
            m = EVAL.match(ln)
            if m and cur is not None:
                cur["cur_eval"] = float(m.group(2))
                cur["enum"] = int(m.group(3))
                cur["legal"] = int(m.group(4))
                cur["rejected"] = int(m.group(5))
                continue
            m = MORE.search(ln)
            if m and cur is not None:
                more = int(m.group(1))
                continue
            m = PICK.match(ln)
            if m and cur is not None:
                cur["chosen_score"] = float(m.group(1))
                cur["chosen"] = kind_of(m.group(2))
                continue
            m = CAND.match(ln)
            if m and cur is not None and not ln.strip().startswith("→"):
                mark, pct, desc = m.groups()
                cands.append(dict(chosen=bool(mark.strip()), score=float(pct),
                                  kind=kind_of(desc), desc=desc))
                continue
    flush()
    return meta, decisions


def analyze(paths):
    rows = []
    for p in paths:
        try:
            meta, dec = parse(p)
        except Exception as ex:                      # noqa: BLE001
            print(f"  跳过 {p}: {ex}", file=sys.stderr)
            continue
        if not dec:
            continue
        rows.append((meta, dec))
    return rows


def report(rows, out):
    def say(s=""):
        print(s)
        out.append(s)

    # ---- 按模型分组 ----
    by_model = defaultdict(list)
    for meta, dec in rows:
        by_model[meta["model"]].append((meta, dec))

    say("=" * 90)
    say("一、空过率（按模型分组）")
    say("=" * 90)
    say(f"{'模型':<42} {'局数':>5} {'决策数':>7} {'决策级空过':>10} {'回合级空过':>10} {'胜率':>7}")
    for model, group in sorted(by_model.items(), key=lambda kv: -len(kv[1])):
        n_dec = 0
        n_end_dec = 0
        n_turns = 0
        n_empty_turns = 0
        wins = 0
        played = 0
        for meta, dec in group:
            # 决策级
            n_dec += len(dec)
            n_end_dec += sum(1 for d in dec if d["chosen"] == "EndTurn")
            # 回合级：把同一回合的决策聚起来
            per_turn = defaultdict(list)
            for d in dec:
                per_turn[d["turn"]].append(d["chosen"])
            for t, kinds in per_turn.items():
                n_turns += 1
                if all(k == "EndTurn" for k in kinds):
                    n_empty_turns += 1
            if meta["win"] and "✅" in meta["win"]:
                wins += 1
                played += 1
            elif meta["win"] and "❌" in meta["win"]:
                played += 1
        say(f"{str(model):<42} {len(group):>5} {n_dec:>7} "
            f"{(n_end_dec / n_dec if n_dec else 0):>9.1%} "
            f"{(n_empty_turns / n_turns if n_turns else 0):>9.1%} "
            f"{(wins / played if played else 0):>6.1%}")

    # ---- 空过率按回合段（用样本量最大的那个模型）----
    main_model = max(by_model, key=lambda m: len(by_model[m]))
    group = by_model[main_model]
    say("")
    say(f"（下面全部用局数最多的模型：{main_model}，{len(group)} 局）")

    say("")
    say("=" * 90)
    say("二、空过率随回合变化（早期回合 vs 后期）")
    say("=" * 90)
    buckets = [(1, 3), (4, 6), (7, 9), (10, 12), (13, 15), (16, 18), (19, 21), (22, 99)]
    say(f"{'回合段':<12} {'决策数':>7} {'决策级空过':>10} {'平均手牌':>9} {'平均kredit':>10} {'平均场面':>9}")
    for lo, hi in buckets:
        ds = [d for _, dec in group for d in dec if lo <= d["turn"] <= hi]
        if not ds:
            continue
        ne = sum(1 for d in ds if d["chosen"] == "EndTurn")
        say(f"T{lo}-{hi if hi < 99 else '∞':<8} {len(ds):>7} {ne / len(ds):>9.1%} "
            f"{sum(d['hand'] for d in ds) / len(ds):>9.2f} "
            f"{sum(d['kredit'] for d in ds) / len(ds):>10.2f} "
            f"{sum(d['board'] for d in ds) / len(ds):>9.2f}")

    # ---- H1：同分并列 ----
    say("")
    say("=" * 90)
    say("三、H1 同分并列：日志里**列出来的**候选之间的并列情况")
    say("=" * 90)
    say("⚠ 日志默认只列 --top 6 个 + 「结束回合」，所以这是**下界**：")
    say("  真正并列的候选可能有一部分没被打印出来。")
    tot = 0
    tie_at_max = 0
    tie_chose_endturn = 0
    tie_chose_first = 0
    tie_chose_nonfirst = 0
    ties_any = 0
    for _, dec in group:
        for d in dec:
            cs = d["cands"]
            if not cs:
                continue
            tot += 1
            mx = max(c["score"] for c in cs)
            top = [c for c in cs if c["score"] == mx]
            if len(top) > 1:
                tie_at_max += 1
                ch = next((c for c in cs if c["chosen"]), None)
                if ch is not None and ch["score"] == mx:
                    if ch is top[0]:
                        tie_chose_first += 1
                    else:
                        tie_chose_nonfirst += 1
                    if ch["kind"] == "EndTurn":
                        tie_chose_endturn += 1
            # 任意两个候选同分
            if len({c["score"] for c in cs}) < len(cs):
                ties_any += 1
    say(f"  参与统计的决策（有候选行的）        : {tot}")
    say(f"  最高分**并列**的决策                : {tie_at_max}  ({tie_at_max / tot:.1%})")
    say(f"  任意两个候选同分的决策              : {ties_any}  ({ties_any / tot:.1%})")
    say(f"  并列且选中了第一个（枚举序最前）    : {tie_chose_first}")
    say(f"  并列但选中的不是第一个              : {tie_chose_nonfirst}")
    say(f"  并列且选中的是「结束回合」          : {tie_chose_endturn}")
    say("  ⚠ 代码事实：NnPolicy.Choose 用 `s.WinProb > chosen.WinProb` 严格大于 ⇒")
    say("     同分取**先枚举到的**；而 EndTurnAction 是 Enumerate 里**最后一个**加的。")
    say("     ⇒ 同分时「结束回合」天然**输**给任何出牌/攻击/移动。")

    # ---- H1b：结束回合 vs 最佳出牌的分差 ----
    say("")
    say("=" * 90)
    say("四、H1b 选中「结束回合」时，它比最佳**非**结束回合候选高多少")
    say("=" * 90)
    gaps = []
    for _, dec in group:
        for d in dec:
            cs = [c for c in d["cands"] if c["kind"] != "EndTurn"]
            et = next((c for c in d["cands"] if c["kind"] == "EndTurn"), None)
            if d["chosen"] == "EndTurn" and cs and et is not None:
                gaps.append(et["score"] - max(c["score"] for c in cs))
    if gaps:
        gaps_sorted = sorted(gaps)
        n = len(gaps_sorted)
        say(f"  样本 {n} 次「选了结束回合且日志里有出牌候选」的决策")
        say(f"  分差（结束回合 − 最佳出牌）: 平均 {sum(gaps) / n:+.2f} 点，"
            f"中位 {gaps_sorted[n // 2]:+.2f} 点")
        for thr in (0.0, 0.5, 1.0, 2.0, 5.0, 10.0):
            k = sum(1 for g in gaps if g <= thr)
            say(f"    分差 ≤ {thr:>4.1f} 点 : {k:>6}  ({k / n:.1%})")

    # ---- H5：候选构成 ----
    say("")
    say("=" * 90)
    say("五、H5 候选集构成：枚举出的候选里有没有「出牌」")
    say("=" * 90)
    say(f"{'回合段':<12} {'决策数':>7} {'枚举均':>7} {'合法均':>7} {'有出牌候选':>11} {'有攻击候选':>11} {'纯结束回合':>11}")
    for lo, hi in buckets:
        ds = [d for _, dec in group for d in dec if lo <= d["turn"] <= hi]
        if not ds:
            continue
        has_play = sum(1 for d in ds if any(c["kind"] == "Play" for c in d["cands"]))
        has_atk = sum(1 for d in ds if any(c["kind"] == "Attack" for c in d["cands"]))
        only_end = sum(1 for d in ds if d["legal"] == 1)
        say(f"T{lo}-{hi if hi < 99 else '∞':<8} {len(ds):>7} "
            f"{sum(d['enum'] or 0 for d in ds) / len(ds):>7.2f} "
            f"{sum(d['legal'] or 0 for d in ds) / len(ds):>7.2f} "
            f"{has_play / len(ds):>10.1%} {has_atk / len(ds):>10.1%} {only_end / len(ds):>10.1%}")
    say("  ⚠ 「有出牌候选」只反映**日志里列出来的**候选（--top 6 + 结束回合），是下界。")
    say("     但「纯结束回合」（合法=1）是精确的。")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dirs", nargs="*", default=[])
    ap.add_argument("--glob", default="")
    ap.add_argument("--out", default="")
    ap.add_argument("--limit", type=int, default=0, help="每个目录最多读多少局（0=全部）")
    args = ap.parse_args()

    paths = []
    if args.glob:
        paths += sorted(glob.glob(args.glob))
    for d in args.dirs:
        if os.path.isdir(d):
            paths += sorted(glob.glob(os.path.join(d, "*.log")))
        else:
            paths.append(d)
    if args.limit:
        paths = paths[:args.limit]
    print(f"读入 {len(paths)} 个日志文件")

    rows = analyze(paths)
    print(f"解析成功 {len(rows)} 局\n")

    out = []
    report(rows, out)
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as f:
            f.write("\n".join(out) + "\n")
        print(f"\n已写入 {args.out}")


if __name__ == "__main__":
    main()
