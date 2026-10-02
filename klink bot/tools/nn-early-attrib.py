"""
nn-early-attrib.py —— 读 NNEarlyProbe 倒出来的「每个候选真正被打分的那个局面」，做归因。

**只读，不改任何东西。**

回答三个不同的问题（别混）：
  Q0 那些「空过」里，有多少是**被迫**的（该决策只有「结束回合」一个合法动作）？
  Q1 主动过牌时，模型给「结束回合」比最佳替代高多少分？
  Q2 「出牌」候选为什么得分低？—— 对比「结束回合」的局面与「最佳出牌」的局面，
     用**精确路径分解**把 ΔP 分摊到编码的各特征组上。

归因办法：C# 侧对每个候选与「结束回合」候选做精确路径分解
（逐组覆盖编码向量，各组 ΔP 之和 = 两端 P 之差；正反两个顺序取平均）。
这里只做汇总与展示。

用法:
  python "klink bot/tools/nn-early-attrib.py" out/_early-states-r8-*.jsonl \
      --verify-log "out/_r8-logs/R8-100k-handfix_C-真均势_left_1.log" \
      --out out/_early-attrib-r8.txt
"""

import argparse
import json
import os
import re
from collections import defaultdict

# AtomicAction.Kind 是中文（"结束回合" / "出牌" / "攻击" / "移动"）
END = "结束回合"
PLAY = "出牌"

EVAL_KEYS = ["turn", "activeIsPerspective", "cardsPlayedThisTurn",
             "p_hqDef", "p_kredits", "p_maxKredits", "p_hand", "p_deck", "p_board",
             "p_zHand", "p_zFront", "p_zHalf", "p_zDisc",
             "o_hqDef", "o_kredits", "o_maxKredits", "o_hand", "o_deck", "o_board"]

BLOCKS = ["p_handVec", "p_frontVec", "p_halfVec", "p_discVec",
          "o_handVec", "o_frontVec", "o_halfVec", "o_discVec"]

EXEC = re.compile(r"^#\s*\d+\s+\[T(\d+)/(\w+)\]\s+NN\s+(.+?)\s*$")
LOG_DECOR = "（已用 GreedyBot 走完本回合后估值）"


def _norm(s: str) -> str:
    s = s.replace(LOG_DECOR, "")
    # NNPlay 还会追加「⚑ 直接获胜（…）」这类标注，也不是动作本身
    i = s.find("⚑")
    if i >= 0:
        s = s[:i]
    return s.strip()


def load(paths):
    if isinstance(paths, str):
        paths = [paths]
    rows = []
    for si, p in enumerate(paths):
        with open(p, encoding="utf-8-sig") as f:
            for ln in f:
                ln = ln.strip()
                if ln:
                    r = json.loads(ln)
                    r["_src"] = si
                    rows.append(r)
    return rows


def verify(rows, logpath, say):
    """把本工具选的动作与 NNPlay 日志里 `#  N [T../left] NN   <desc>` 逐条对照。"""
    acts = []
    with open(logpath, encoding="utf-8", errors="replace") as f:
        for ln in f:
            m = EXEC.match(ln)
            if m:
                acts.append((int(m.group(1)), m.group(2), _norm(m.group(3))))
    say("=" * 88)
    say("零、一致性核对：本工具选的动作 vs NNPlay 日志")
    say("=" * 88)
    say(f"  日志 {logpath}：{len(acts)} 条 NN 动作；探针 {len(rows)} 次决策")
    n = min(len(acts), len(rows))
    bad = 0
    for i in range(n):
        t, side, desc = acts[i]
        mine = _norm(rows[i]["cands"][rows[i]["chosen"]]["desc"])
        if desc != mine:
            bad += 1
            if bad <= 5:
                say(f"  ✗ #{i + 1} T{t}: 日志「{desc}」 vs 探针「{mine}」")
    if len(acts) != len(rows):
        say(f"  ✗ 动作条数不同：日志 {len(acts)} vs 探针 {len(rows)}")
    say(f"  逐条一致 {n - bad}/{n}"
        + ("   ✅ 探针复刻了 NNPlay 的决策（同种子同对位）" if bad == 0
           else "   ⚠ 不一致 —— 下面的归因不能代表 NNPlay"))
    if len(rows) > len(acts):
        say(f"  （探针比日志多 {len(rows) - len(acts)} 次决策 = 多跑的局；只核对了日志那一局）")
    say("")


def best_of(row, target_kind):
    """按得分取「某一类」候选里最佳的那个索引；没有则 None。
    target_kind=None 表示「任意非结束回合候选」。"""
    def ok(c):
        if c["terminal"]:
            return False
        return c["kind"] != END if target_kind is None else c["kind"] == target_kind

    idx = [i for i, c in enumerate(row["cands"]) if ok(c)]
    if not idx:
        return None
    return max(idx, key=lambda i: row["cands"][i]["score"])


def gap_stats(rows, target_kind, say, label):
    """EndTurn vs 「某一类最佳替代」的分差统计（单位：**百分点**）。"""
    gaps = []
    for r in rows:
        if r["cands"][r["chosen"]]["kind"] != END:
            continue
        j = best_of(r, target_kind)
        if j is None:
            continue
        gaps.append((r, (r["cands"][r["chosen"]]["score"] - r["cands"][j]["score"]) * 100, j))
    say("")
    say("=" * 88)
    say(f"Q1 主动过牌时，「结束回合」比{label}高多少分")
    say("=" * 88)
    if not gaps:
        say("  无样本")
        return gaps
    g = sorted(x[1] for x in gaps)
    n = len(g)
    say(f"  样本 {n}   （单位：百分点 = 模型输出的概率差 × 100）")
    say(f"  平均 {sum(g) / n:+.2f}   中位 {g[n // 2]:+.2f}   "
        f"最小 {g[0]:+.2f}   最大 {g[-1]:+.2f}")
    for thr in (0.0, 0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0):
        k = sum(1 for x in g if x <= thr)
        say(f"    分差 ≤ {thr:>5.1f} 点 : {k:>5} ({k / n:>6.1%})")
    return gaps


def feat_diffs(gaps, say):
    say("")
    say("=" * 88)
    say("Q2a 主动过牌时，「结束回合」局面 vs「最佳出牌」局面，差在哪些维度")
    say("=" * 88)
    say(f"  （{len(gaps)} 个样本；值是 结束回合局面 − 出牌局面 的平均）")
    say(f"   {'特征':<24} {'平均差':>9} {'非零比例':>9}")
    acc = defaultdict(list)
    for r, _, j in gaps:
        et = r["cands"][r["chosen"]]["eval"]
        pl = r["cands"][j]["eval"]
        for k in EVAL_KEYS:
            acc[k].append(et[k] - pl[k])
    for k in EVAL_KEYS:
        v = acc[k]
        if not v:
            say(f"   {k:<24} {'—':>9} {'—':>9}")
            continue
        say(f"   {k:<24} {sum(v) / len(v):>+9.3f} "
            f"{sum(1 for x in v if x != 0) / len(v):>8.1%}")

    say("")
    say(f"   {'卡向量块':<14} {'结束回合|L1|':>13} {'出牌|L1|':>11} {'不同的比例':>11}")
    accb = defaultdict(list)
    for r, _, j in gaps:
        et = r["cands"][r["chosen"]]["blocks"]
        pl = r["cands"][j]["blocks"]
        for k in BLOCKS:
            accb[k].append((et[k], pl[k]))
    for k in BLOCKS:
        if not accb[k]:
            continue
        a = sum(x for x, _ in accb[k]) / len(accb[k])
        b = sum(y for _, y in accb[k]) / len(accb[k])
        ch = sum(1 for x, y in accb[k] if abs(x - y) > 1e-6) / len(accb[k])
        say(f"   {k:<14} {a:>13.2f} {b:>11.2f} {ch:>10.1%}")


def attrib_summary(gaps, say, label):
    say("")
    say("=" * 88)
    say(f"Q2b 归因：把「结束回合 − {label}」的 ΔP 分摊到编码特征组（精确路径分解）")
    say("=" * 88)
    say("  （正 = 该组特征有利于「结束回合」；正反两序取平均）")
    gsum = defaultdict(float)
    gabs = defaultdict(float)
    gtot = 0.0
    gn = 0
    for r, _, j in gaps:
        if r.get("attribBase") is None:
            continue
        ent = next((a for a in r["attrib"] if a["idx"] == j), None)
        if ent is None:
            continue
        gn += 1
        gtot += ent["total"]
        for name, pair in ent["groups"].items():
            gsum[name] += pair[0]
            gabs[name] += abs(pair[0])
    if gn == 0:
        say("  无样本")
        return
    say(f"  样本 {gn}   平均总分差（分解之和）{gtot / gn * 100:+.2f} 百分点")
    say(f"   {'特征组':<16} {'平均ΔP(点)':>11} {'平均|ΔP|(点)':>13} {'占总|ΔP|':>10}")
    tot_abs = sum(gabs.values()) or 1.0
    for name in sorted(gsum, key=lambda k: -gabs[k]):
        say(f"   {name:<16} {gsum[name] / gn * 100:>+11.2f} {gabs[name] / gn * 100:>13.2f} "
            f"{gabs[name] / tot_abs:>9.1%}")


def ablation_summary(gaps, say, label):
    say("")
    say("=" * 88)
    say(f"Q2c 消融：把某组特征换成训练集均值（= 抹掉这组信息），")
    say(f"     「结束回合 − {label}」的分差还剩多少")
    say("=" * 88)
    say("  （原始分差 100% = 什么都没抹；掉到 0 ⇒ 这个分差**全靠**这一组特征）")
    full = []
    per = defaultdict(list)
    for r, _, j in gaps:
        if r.get("attribBase") is None:
            continue
        ent = next((a for a in r["attrib"] if a["idx"] == j), None)
        if ent is None or "ablate" not in ent:
            continue
        full.append(ent["total"])
        for name, v in ent["ablate"].items():
            per[name].append(v)
    if not full:
        say("  无样本")
        return
    fmean = sum(full) / len(full)
    say(f"  样本 {len(full)}   原始平均分差 {fmean * 100:+.2f} 百分点")
    say(f"   {'抹掉的特征组':<18} {'剩余分差(点)':>13} {'保留比例':>9} {'|剩余|均值':>11}")
    rows = []
    for name, v in per.items():
        m = sum(v) / len(v)
        rows.append((name, m, abs(m) / abs(fmean) if fmean else float("nan"),
                     sum(abs(x) for x in v) / len(v)))
    for name, m, keep, am in sorted(rows, key=lambda t: t[3]):
        say(f"   {name:<18} {m * 100:>+13.2f} {keep:>8.0%} {am * 100:>11.2f}")


def saturation(rows, say):
    """线索①：候选得分**完全同分**（rollout 之后局面被抹平）的规模。全精度，非日志的 1 位小数。"""
    say("")
    say("=" * 88)
    say("Q0c 得分饱和：候选之间得分**完全相等**的规模（全精度，不经过日志的 1 位小数舍入）")
    say("=" * 88)
    n = len(rows)
    tie_max = 0
    tie_any = 0
    play_all_same = 0
    play_multi = 0
    tie_max_has_end = 0
    tie_max_chose_first = 0
    for r in rows:
        cs = [c for c in r["cands"] if not c["terminal"]]
        if not cs:
            continue
        sc = [c["score"] for c in cs]
        mx = max(sc)
        top = [i for i, c in enumerate(r["cands"]) if not c["terminal"] and c["score"] == mx]
        if len(top) > 1:
            tie_max += 1
            if any(r["cands"][i]["kind"] == END for i in top):
                tie_max_has_end += 1
            if r["chosen"] == top[0]:
                tie_max_chose_first += 1
        if len(set(sc)) < len(sc):
            tie_any += 1
        plays = [c["score"] for c in r["cands"] if c["kind"] == PLAY and not c["terminal"]]
        if len(plays) > 1:
            play_multi += 1
            if len(set(plays)) == 1:
                play_all_same += 1
    say(f"  决策数                              : {n}")
    say(f"  最高分**并列**的决策                : {tie_max} ({tie_max / n:.1%})")
    say(f"    并列里含「结束回合」的            : {tie_max_has_end} ({tie_max_has_end / max(1, tie_max):.1%})")
    say(f"    并列时选中的是枚举序第一个的      : {tie_max_chose_first} "
        f"({tie_max_chose_first / max(1, tie_max):.1%})   ← 代码是严格 `>`，同分必取第一个")
    say(f"  任意两个候选同分的决策              : {tie_any} ({tie_any / n:.1%})")
    say(f"  有 ≥2 个「出牌」候选的决策          : {play_multi}")
    say(f"    其中所有出牌候选**得分完全相同**  : {play_all_same} "
        f"({play_all_same / max(1, play_multi):.1%})")
    say("  （出牌候选全同分 = rollout 之后它们落到**同一个局面** —— GreedyBot 把本回合剩下的牌")
    say("    全打完，先出哪张不再影响回合交界时的局面。）")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl", nargs="+")
    ap.add_argument("--out", default="")
    ap.add_argument("--max-turn", type=int, default=0, help="只看 Turn ≤ N 的决策（0=全部）")
    ap.add_argument("--examples", type=int, default=6)
    ap.add_argument("--verify-log", default="")
    args = ap.parse_args()

    rows = load(args.jsonl)
    L = []

    def say(s=""):
        print(s)
        L.append(s)

    if args.verify_log:
        verify(rows, args.verify_log, say)

    say(f"文件     : {args.jsonl}")
    say(f"决策数   : {len(rows)}   来自 {len({(r['_src'], r['game']) for r in rows})} 局")

    # ---------- Q0：被迫过牌 vs 主动过牌 ----------
    per_turn = defaultdict(list)
    for r in rows:
        per_turn[(r["_src"], r["game"], r["turn"])].append(r)
    n_dec = len(rows)
    n_end = sum(1 for r in rows if r["cands"][r["chosen"]]["kind"] == END)
    forced = sum(1 for r in rows if r["nLegal"] == 1)
    cond = [r for r in rows if r["nLegal"] > 1]
    cond_end = sum(1 for r in cond if r["cands"][r["chosen"]]["kind"] == END)

    n_turns = len(per_turn)
    n_empty = sum(1 for k, v in per_turn.items()
                  if all(x["cands"][x["chosen"]]["kind"] == END for x in v))
    # 空过回合里，是否每个决策都只有一个合法动作（= 被迫）
    empty_forced = 0
    for k, v in per_turn.items():
        if all(x["cands"][x["chosen"]]["kind"] == END for x in v):
            if all(x["nLegal"] == 1 for x in v):
                empty_forced += 1

    say("")
    say("=" * 88)
    say("Q0 空过里有多少是「被迫」的（该决策只有「结束回合」一个合法动作）")
    say("=" * 88)
    say(f"  决策总数                        : {n_dec}")
    say(f"  只有「结束回合」一个合法动作的决策 : {forced}  ({forced / n_dec:.1%})  ← 被迫")
    say(f"  有替代可选的决策                : {len(cond)}  ({len(cond) / n_dec:.1%})")
    say(f"    其中仍然选了「结束回合」        : {cond_end}  ({cond_end / len(cond):.1%} of 有替代)"
        f"  = {cond_end / n_dec:.1%} of 全部决策  ← 主动过牌")
    say(f"  决策级空过率（选中结束回合）      : {n_end}/{n_dec} = {n_end / n_dec:.1%}")
    say(f"  回合级空过率（该回合 NN 只结束）  : {n_empty}/{n_turns} = {n_empty / n_turns:.1%}")
    say(f"    其中**整个回合每一步都被迫**的  : {empty_forced}/{n_empty} = "
        f"{empty_forced / max(1, n_empty):.1%}")
    say(f"    ⇒ 真正「主动过牌」的回合        : {n_empty - empty_forced}/{n_turns} = "
        f"{(n_empty - empty_forced) / n_turns:.1%}")

    # ---------- 饱和 ----------
    saturation(rows, say)

    # ---------- H3：被估值的局面里 cardsPlayedThisTurn 是几 ----------    say("")
    say("=" * 88)
    say("Q0b H3 检查：**被估值的局面**里 cardsPlayedThisTurn 到底是不是 0")
    say("=" * 88)
    cpt_all = defaultdict(int)
    cpt_roll = defaultdict(int)
    cpt_noroll = defaultdict(int)
    for r in rows:
        cpt_all[r["decisionPoint"]["cardsPlayedThisTurn"]] += 1
        for c in r["cands"]:
            (cpt_roll if c["rolledOut"] else cpt_noroll)[c["eval"]["cardsPlayedThisTurn"]] += 1
    say(f"  决策点本身            : {dict(sorted(cpt_all.items()))}")
    say(f"  试算后经 rollout 的候选: {dict(sorted(cpt_roll.items()))}")
    say(f"  试算后未 rollout 的候选: {dict(sorted(cpt_noroll.items()))}")
    say("  （若后两行只有 0 ⇒ H3「出牌后状态 cardsPlayedThisTurn≥1 属分布外」不成立）")

    # ---------- Q1 ----------
    gaps_any = gap_stats(rows, None, say, "任意非结束回合候选")
    gaps_play = gap_stats(rows, PLAY, say, "最佳**出牌**候选")

    if gaps_play:
        feat_diffs(gaps_play, say)
        attrib_summary(gaps_play, say, "最佳出牌")
        ablation_summary(gaps_play, say, "最佳出牌")
    if gaps_any:
        attrib_summary(gaps_any, say, "最佳任意替代")

    # ---------- 早期回合 ----------
    if args.max_turn:
        say("")
        say("=" * 88)
        say(f"Q3 只看 Turn ≤ {args.max_turn}")
        say("=" * 88)
        sub = [r for r in rows if r["turn"] <= args.max_turn]
        se = sum(1 for r in sub if r["cands"][r["chosen"]]["kind"] == END)
        sf = sum(1 for r in sub if r["nLegal"] == 1)
        sc = [r for r in sub if r["nLegal"] > 1]
        sce = sum(1 for r in sc if r["cands"][r["chosen"]]["kind"] == END)
        say(f"  决策 {len(sub)}；决策级空过 {se}/{len(sub)} = {se / len(sub):.1%}")
        say(f"  被迫过牌 {sf} ({sf / len(sub):.1%})；有替代却过牌 {sce}/{len(sc)} = "
            f"{sce / max(1, len(sc)):.1%}")

    # ---------- 例子 ----------
    say("")
    say("=" * 88)
    say(f"Q4 明细例子（前 {args.examples} 个「主动过牌」的决策）")
    say("=" * 88)
    for r, gap, j in (gaps_play or gaps_any)[:args.examples]:
        dp = r["decisionPoint"]
        say("")
        say(f"  局{r['game']} T{r['turn']}  决策点: 手牌 {dp['p_hand']} kredit {dp['p_kredits']}"
            f" 场面 {dp['p_board']}  HQ {dp['p_hqDef']}/{dp['o_hqDef']}"
            f"  对手手牌 {dp['o_hand']} 对手场面 {dp['o_board']}"
            f"  （枚举 {r['nEnum']} 合法 {r['nLegal']}）")
        say(f"    {'':<2}{'候选':<44} {'得分':>7}  {'T':>3} {'act':>3} {'cpt':>3} "
            f"{'手':>3} {'kred':>4} {'场':>3} {'弃':>3} {'半':>3}")
        for i, c in enumerate(r["cands"]):
            e = c["eval"]
            mark = "✔" if i == r["chosen"] else ("→" if i == j else " ")
            say(f"    {mark:<2}{c['desc'][:42]:<44} {c['score'] * 100:>6.1f}%  {e['turn']:>3} "
                f"{e['activeIsPerspective']:>3} {e['cardsPlayedThisTurn']:>3} "
                f"{e['p_hand']:>3} {e['p_kredits']:>4} {e['p_board']:>3} "
                f"{e['p_zDisc']:>3} {e['p_zHalf']:>3}")
        if r.get("attribBase") is not None:
            ent = next((a for a in r["attrib"] if a["idx"] == j), None)
            if ent:
                top = sorted(ent["groups"].items(), key=lambda kv: -abs(kv[1][0]))[:6]
                say(f"      归因（{ent['total'] * 100:+.2f} 点）: "
                    + "  ".join(f"{n}={v[0] * 100:+.2f}" for n, v in top))

    txt = "\n".join(L) + "\n"
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(txt)
        print(f"\n已写入 {args.out}")


if __name__ == "__main__":
    main()
