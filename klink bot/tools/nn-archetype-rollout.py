"""
nn-archetype-rollout.py —— **卡组原型对照**：同一模型、同一条件，把「慢速控制卡组」换成「快攻卡组」再测一遍。

**只测量，不改任何东西**（不改策略 / 内核 / 编码 / 超参）。它只做一件事：
跑 NNPlay 的 `play` 子命令并把日志按**与上一轮 `nn-r7-rollout.py` / `nn-early-logscan.py` 逐字相同的口径**统计。

为什么要有这一轮
----------------
上一轮的诊断（`klink bot/docs/NN不出牌诊断.md`）只测了**一个对位**：`美澳跳`（NN） vs `英日`。
`美澳跳` 是慢速控制卡组 —— 在那种卡组里囤牌、攒资源本来就可能是合理的。
所以「NN 倾向过牌」可能是**卡组特性**而不是**模型偏好**。本脚本用快攻卡组 `德芬车` 做同样的事来分开这两者。

四组（每组 NN 先手 / 后手各 40 局，seeds 1..40 ⇒ 80 局/组）
----------------------------------------------------------
  慢速(基线)   美澳跳  控制   vs 英日
  快攻         德芬车  快攻   vs 英日
  快攻(换对手) 德芬车  快攻   vs 德澳老兵
  慢速(换对手) 美澳跳  慢速   vs 德澳老兵

口径（**与上一轮对齐，逐条注明**）
--------------------------------
  · 回合级空过率   = 「该回合 NN 的**实际动作序列只有『结束回合』**」的回合 / NN 出过手的回合
                     ← 上一轮 §2 的那个 35.0% / 24.0%，来自 `nn-r7-rollout.py` 的 `idle_turns`
  · 决策级空过率   = 选中「结束回合」的决策 / 总决策        ← `nn-early-logscan.py` 口径
  · 被迫           = 该决策 `合法 == 1`（只有「结束回合」一个合法动作）
  · 主动过牌率     = (选中「结束回合」且 合法>1) / **总决策**
                     ← 上一轮 §1 报的 15.0%(r6) / 6.6%(★ r8) 就是这个数
  · 剔被迫后过牌率 = (选中「结束回合」且 合法>1) / (合法>1 的决策)     ← 「剔掉被迫后」的字面口径
                     两个都给，因为上一轮只报了前一个
  · 空过回合里「整个回合每一步都被迫」的占比 = 上一轮 §1 的 31.6% / 48.0%
  · 攻击/局、出牌/局、NN 回合/局、自评前/后 = 与 `nn-r7-rollout.py` 的 RE_* 正则逐字相同
  · 手牌最大值 / kredit 最大值 = 该组所有 **NN 决策点**上 `手牌` / `kredit` 的最大值（日志 🧠 决策行）
  · 胜率 = 有效局（排除中止）里的胜率 + Wilson 95% CI。**n=80 ⇒ ±10 点，不下强结论。**

用法（仓库根目录）：
  python -X utf8 "klink bot/tools/nn-archetype-rollout.py" --seeds 40 --jobs 10 `
    --logs out/_arch-logs --dump out/_arch-rollout.json --out out/_arch-rollout.txt
  # 复用已跑结果重算表格：
  python -X utf8 "klink bot/tools/nn-archetype-rollout.py" --reuse `
    --dump out/_arch-rollout.json --out out/_arch-rollout.txt
"""

import argparse
import concurrent.futures as cf
import json
import math
import re
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
NNPLAY = REPO / "tools/NNPlay/bin/Release/net10.0/NNPlay.dll"

MODEL = "out/nn-model-100k-handfix.bin"          # ★ 最新收敛模型（留出 81.45%）

# (组标签, NN 卡组, 类型, 对手卡组)
GROUPS = [
    ("慢速-基线", "美澳跳", "控制", "英日"),
    ("快攻", "德芬车", "快攻", "英日"),
    ("快攻-换对手", "德芬车", "快攻", "德澳老兵"),
    ("慢速-换对手", "美澳跳", "控制", "德澳老兵"),
]

# ---------------- 与 nn-r7-rollout.py 逐字相同的正则 ----------------
RE_OUT = re.compile(r"NN 是\s+: (\w+)\s+⇒ (.+)")
RE_SUM = re.compile(r"回合数\s+: (\d+)\s+总步数 (\d+)")
RE_KIND = re.compile(r"NN 动作\s+: (.+)")
RE_DEC = re.compile(r"NN 决策\s+: (\d+) 次")
RE_SELF = re.compile(r"模型平均自评: 决策前 ([\d.]+)%，选中后 ([\d.]+)%")
RE_END = re.compile(r"选中「结束回合」(\d+) 次/(\d+) 次决策")
RE_HQ = re.compile(r"HQ\s+: NN (-?\d+)\s+对手 (-?\d+)")
RE_ACT = re.compile(r"^#\s*(\d+) \[T(\d+)/(\w+)\] (NN|对手)\s+(.+)$", re.M)
RE_MISS = re.compile(r"⚠ 有 (\d+) 次决策存在「一步直接获胜」的候选，但模型没选它")
# -------------------------------------------------------------------

# ---------------- 与 nn-early-logscan.py 逐字相同的正则 ----------------
DEC = re.compile(r"^🧠 决策 #(\d+)\s+\[T(\d+)/(\w+)\]\s+kredit (\d+)/(\d+)\s+"
                 r"手牌 (\d+)\s+场面 (\d+)\s+HQ (\d+)/(\d+)")
EVAL = re.compile(r"^\s*当前局面估值 P\((\w+ 胜)\) = ([\d.]+)%\s+"
                  r"（枚举 (\d+) 个，合法 (\d+) 个，内核拒绝 (\d+) 个）")
PICK = re.compile(r"^\s*→ 选 \[([\d.]+)%\]\s+(.+)$")
RE_IR = re.compile(r"蓝图 IR\s*:\s*(.+)")
# ---------------------------------------------------------------------

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


def kind_short(a: str) -> str:
    """nn-r7-rollout.py 的 kind()，逐字相同（用于 NN 动作序列统计）。"""
    for k in ("出牌", "攻击", "移动", "结束", "部署"):
        if a.startswith(k):
            return k
    return a.split()[0] if a.split() else "?"


def parse(path):
    txt = Path(path).read_text(encoding="utf-8", errors="ignore")
    d = {}
    m = RE_OUT.search(txt)
    d["result"] = m.group(2).strip() if m else "?"
    d["outcome"] = ("win" if "✅" in (m.group(2) if m else "") else
                    "loss" if "❌" in (m.group(2) if m else "") else "aborted")
    m = RE_SUM.search(txt)
    d["turns"] = int(m.group(1)) if m else 0
    d["steps"] = int(m.group(2)) if m else 0
    m = RE_KIND.search(txt)
    kinds = {}
    if m:
        for part in m.group(1).split():
            k, _, v = part.partition("×")
            try:
                kinds[k] = int(v)
            except ValueError:
                pass
    d["play"], d["attack"], d["end"] = kinds.get("出牌", 0), kinds.get("攻击", 0), kinds.get("结束回合", 0)
    m = RE_DEC.search(txt)
    d["decisions"] = int(m.group(1)) if m else 0
    m = RE_SELF.search(txt)
    d["self_before"], d["self_after"] = (float(m.group(1)), float(m.group(2))) if m else (0.0, 0.0)
    m = RE_END.search(txt)
    d["end_chosen"] = int(m.group(1)) if m else 0
    m = RE_HQ.search(txt)
    d["hq_nn"], d["hq_op"] = (int(m.group(1)), int(m.group(2))) if m else (0, 0)
    # ⚠ 日志文件里**没有**启动横幅（那几行走 Console.WriteLine）—— IR 由 run_one 从 stdout 补
    d["ir"] = "?"
    d["ir_loaded"] = False

    by_turn = {}
    for mm in RE_ACT.finditer(txt):
        if mm.group(4) != "NN":
            continue
        by_turn.setdefault(int(mm.group(2)), []).append(kind_short(mm.group(5)))
    d["nn_turns"] = len(by_turn)
    d["idle_turns"] = sum(1 for a in by_turn.values() if a == ["结束"])
    d["first_end_turns"] = sum(1 for a in by_turn.values() if a and a[0] == "结束")
    d["attack_turns"] = sum(1 for a in by_turn.values() if "攻击" in a)
    d["noattack_turns"] = sum(1 for a in by_turn.values() if "攻击" not in a)
    d["replay_ok"] = "最终校验  : ✅" in txt
    d["replay_mismatch"] = "重放与实时引擎不一致" in txt
    m = RE_MISS.search(txt)
    d["missed_wins"] = int(m.group(1)) if m else 0
    d["immediate_win_actions"] = txt.count("⚑ 直接获胜")

    # ---- 逐决策（nn-early-logscan.py 口径）----
    decs = []
    cur = None
    for ln in txt.splitlines():
        m = DEC.match(ln)
        if m:
            if cur is not None:
                decs.append(cur)
            g = m.groups()
            # g[0]=决策号 g[1]=Turn g[2]=边 g[3]=kredit g[4]=kredit上限 g[5]=手牌 g[6]=场面 g[7]/g[8]=HQ
            cur = dict(turn=int(g[1]), kredit=int(g[3]), kmax=int(g[4]),
                       hand=int(g[5]), board=int(g[6]),
                       legal=None, enum=None, cur_eval=None, chosen=None, chosen_score=None)
            continue
        if cur is None:
            continue
        m = EVAL.match(ln)
        if m:
            cur["cur_eval"] = float(m.group(2))
            cur["enum"] = int(m.group(3))
            cur["legal"] = int(m.group(4))
            continue
        m = PICK.match(ln)
        if m:
            cur["chosen_score"] = float(m.group(1))
            cur["chosen"] = kind_of(m.group(2))
            continue
    if cur is not None:
        decs.append(cur)
    d["decs"] = decs
    return d


def run_one(group_tag, side, seed, model, outdir):
    tag = f"{group_tag}|{side}"
    log = Path(outdir) / f"ARCH_{group_tag}_{side}_{seed}.log"
    dn = next(g[1] for g in GROUPS if g[0] == group_tag)
    do = next(g[3] for g in GROUPS if g[0] == group_tag)
    cmd = ["dotnet", str(NNPLAY), "play", "--model", model, "--seed", str(seed), "--rollout",
           "--deck-nn", dn, "--deck-opp", do, "--log", str(log), "--quiet"]
    if side == "right":
        cmd += ["--nn-side", "right"]
    r = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0 or not log.exists():
        return tag, seed, {"outcome": "error", "result": (r.stderr or "")[-400:], "error_rc": r.returncode,
                           "decs": [], "ir": "?", "ir_loaded": False}
    d = parse(log)
    # ⚠ 启动横幅（含「蓝图 IR : 已加载（1636 张卡）」）只走 Console，**不写日志文件** ——
    #    NNPlay 的 Log.Write 只在 !quiet 时打屏幕，而这几行是 Console.WriteLine 直接打的。
    #    所以 IR 自检必须从 stdout 抓，不能从日志抓。
    m = RE_IR.search(r.stdout or "")
    d["ir"] = m.group(1).strip() if m else "?"
    d["ir_loaded"] = ("已加载" in d["ir"])
    return tag, seed, d


# ---------------- 统计小工具 ----------------

def wilson(k, n, z=1.96):
    if n == 0:
        return (float("nan"), float("nan"))
    p = k / n
    dd = 1 + z * z / n
    c = p + z * z / (2 * n)
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return (100 * (c - h) / dd, 100 * (c + h) / dd)


def agg(rows):
    """把一组对局聚成行为指标。rows = 该组的逐局 dict 列表。"""
    n = len(rows)
    win = sum(1 for r in rows if r.get("outcome") == "win")
    loss = sum(1 for r in rows if r.get("outcome") == "loss")
    abort = n - win - loss

    decs = [d for r in rows for d in r.get("decs", [])]
    n_dec = len(decs)
    dec_end = sum(1 for d in decs if d["chosen"] == "EndTurn")
    dec_forced = sum(1 for d in decs if d["legal"] == 1)
    dec_cond = [d for d in decs if d["legal"] is not None and d["legal"] > 1]
    dec_cond_end = sum(1 for d in dec_cond if d["chosen"] == "EndTurn")

    # 回合级：该回合 NN 的动作序列只有「结束」
    per_turn = defaultdict(list)
    for r in rows:
        for d in r.get("decs", []):
            per_turn[(id(r), d["turn"])].append(d)
    n_turns = len(per_turn)
    empty_turns = 0
    empty_forced_turns = 0
    for v in per_turn.values():
        if all(x["chosen"] == "EndTurn" for x in v):
            empty_turns += 1
            if all(x["legal"] == 1 for x in v):
                empty_forced_turns += 1

    # nn-r7-rollout.py 口径的回合级空过（用实际动作序列，与上面等价但独立算一遍做自检）
    idle = sum(r.get("idle_turns", 0) for r in rows)
    nn_turns_log = sum(r.get("nn_turns", 0) for r in rows)

    hands = [d["hand"] for d in decs]
    kred = [d["kredit"] for d in decs]
    kmax = [d["kmax"] for d in decs]

    return dict(
        n=n, win=win, loss=loss, abort=abort,
        decisions=n_dec, dec_end=dec_end, dec_forced=dec_forced,
        dec_cond=len(dec_cond), dec_cond_end=dec_cond_end,
        n_turns=n_turns, empty_turns=empty_turns, empty_forced_turns=empty_forced_turns,
        idle_log=idle, nn_turns_log=nn_turns_log,
        play=sum(r.get("play", 0) for r in rows) / n if n else 0,
        attack=sum(r.get("attack", 0) for r in rows) / n if n else 0,
        end_act=sum(r.get("end", 0) for r in rows) / n if n else 0,
        attack_turns=sum(r.get("attack_turns", 0) for r in rows) / n if n else 0,
        first_end=sum(r.get("first_end_turns", 0) for r in rows),
        self_before=sum(r.get("self_before", 0) for r in rows) / n if n else 0,
        self_after=sum(r.get("self_after", 0) for r in rows) / n if n else 0,
        turns=sum(r.get("turns", 0) for r in rows) / n if n else 0,
        missed=sum(r.get("missed_wins", 0) for r in rows),
        bad_replay=sum(1 for r in rows if r.get("replay_mismatch") or not r.get("replay_ok")),
        ir_loaded=sum(1 for r in rows if r.get("ir_loaded")),
        hand_max=max(hands) if hands else 0, hand_mean=sum(hands) / len(hands) if hands else 0,
        kred_max=max(kred) if kred else 0, kred_mean=sum(kred) / len(kred) if kred else 0,
        kmax_max=max(kmax) if kmax else 0, kmax_mean=sum(kmax) / len(kmax) if kmax else 0,
    )


def pct(a, b):
    return f"{100.0 * a / b:.1f}%" if b else "n/a"


def diff_ci(k1, n1, k2, n2, z=1.96):
    """两比例之差 (p2 − p1) 的 Wald 95% 区间（百分点）。n 小的时候只当参考。"""
    if not n1 or not n2:
        return (float("nan"),) * 3
    p1, p2 = k1 / n1, k2 / n2
    se = math.sqrt(p1 * (1 - p1) / n1 + p2 * (1 - p2) / n2)
    d = p2 - p1
    return 100 * d, 100 * (d - z * se), 100 * (d + z * se)


def counts(rows, lo=1, hi=999, mode="all"):
    """在窗口 [lo,hi] 内数 (分子, 分母)。
    mode='all'   → 主动过牌 / 全部决策
    mode='cond'  → 主动过牌 / 有替代的决策
    """
    decs = [d for r in rows for d in r.get("decs", []) if lo <= d["turn"] <= hi]
    cond = [d for d in decs if d["legal"] is not None and d["legal"] > 1]
    ce = sum(1 for d in cond if d["chosen"] == "EndTurn")
    return (ce, len(cond) if mode == "cond" else len(decs))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=40)
    ap.add_argument("--jobs", type=int, default=10)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--logs", default="out/_arch-logs")
    ap.add_argument("--dump", default="out/_arch-rollout.json")
    ap.add_argument("--out", default="out/_arch-rollout.txt")
    ap.add_argument("--reuse", action="store_true")
    a = ap.parse_args()

    configs = [(g[0], side) for g in GROUPS for side in ("left", "right")]

    if a.reuse:
        raw = json.loads((REPO / a.dump).read_text(encoding="utf-8"))
        res = {t: {int(k): v for k, v in d.items()} for t, d in raw.items()}
        print(f"复用 {a.dump}")
    else:
        outdir = REPO / a.logs
        outdir.mkdir(parents=True, exist_ok=True)
        tasks = [(t, side, s) for (t, side) in configs for s in range(1, a.seeds + 1)]
        print(f"共 {len(tasks)} 局（{len(configs)} 个配置 × {a.seeds} seeds）；并发 {a.jobs}")
        print(f"模型 {a.model}；--rollout；IR 默认加载；逐局日志写到 {a.logs}/")
        res = {}
        with cf.ThreadPoolExecutor(max_workers=a.jobs) as ex:
            futs = [ex.submit(run_one, t, side, s, a.model, str(outdir)) for (t, side, s) in tasks]
            for i, f in enumerate(cf.as_completed(futs), 1):
                tag, seed, d = f.result()
                res.setdefault(tag, {})[seed] = d
                if i % 20 == 0 or i == len(tasks):
                    print(f"  …{i}/{len(tasks)}")
        (REPO / a.dump).write_text(
            json.dumps({t: {str(k): v for k, v in d.items()} for t, d in res.items()},
                       ensure_ascii=False), encoding="utf-8")

    # ---------------- 报告 ----------------
    L = []

    def say(s=""):
        print(s)
        L.append(s)

    say("=" * 160)
    say("NN 卡组原型对照 —— 同一模型（★ out/nn-model-100k-handfix.bin）、同条件（--rollout、IR 加载、seeds 1..40、左右各 40）")
    say("=" * 160)

    # IR 自检
    irs = defaultdict(int)
    for t, d in res.items():
        for r in d.values():
            irs[r.get("ir", "?")] += 1
    say("IR 自检（每局日志开头那行「蓝图 IR」）：")
    for k, v in sorted(irs.items(), key=lambda kv: -kv[1]):
        say(f"    {v:>4} 局   {k}")
    n_bad = sum(v for k, v in irs.items() if "已加载" not in k)
    say(f"    ⇒ {'✅ 全部局都加载了蓝图 IR' if n_bad == 0 else f'❌ 有 {n_bad} 局没加载 IR，下面数字不可比'}")
    say("")

    # ---- 表 A：四组逐项对齐 ----
    say("=" * 160)
    say("表 A  四组行为对比（口径与上一轮对齐；回合级空过 = 该回合 NN 的动作序列只有「结束回合」）")
    say("=" * 160)
    hdr = (f"{'组(NN卡组 vs 对手)':<30}{'局数':>5}{'胜':>4}{'负':>4}{'胜率(95%CI)':>16}"
           f"{'回合级空过':>12}{'决策级空过':>11}{'被迫决策':>10}"
           f"{'主动过牌/全决策':>16}{'剔被迫后过牌':>13}")
    say(hdr)
    tableA = {}
    for gtag, dn, typ, do in GROUPS:
        rows = []
        for side in ("left", "right"):
            rows += list(res.get(f"{gtag}|{side}", {}).values())
        g = agg(rows)
        tableA[gtag] = g
        lo, hi = wilson(g["win"], g["win"] + g["loss"]) if g["win"] + g["loss"] else (float("nan"),) * 2
        wr = f"{100.0 * g['win'] / (g['win'] + g['loss']):.0f}%" if g["win"] + g["loss"] else "n/a"
        ci = f"[{lo:.0f},{hi:.0f}]" if g["win"] + g["loss"] else ""
        c1 = f"{pct(g['empty_turns'], g['n_turns'])} {g['empty_turns']}/{g['n_turns']}"
        c2 = f"{pct(g['dec_end'], g['decisions'])} {g['dec_end']}/{g['decisions']}"
        c3 = f"{pct(g['dec_forced'], g['decisions'])} {g['dec_forced']}/{g['decisions']}"
        c4 = f"{pct(g['dec_cond_end'], g['decisions'])} {g['dec_cond_end']}/{g['decisions']}"
        c5 = f"{pct(g['dec_cond_end'], g['dec_cond'])} {g['dec_cond_end']}/{g['dec_cond']}"
        say(f"{gtag + '  ' + dn + ' vs ' + do:<30}{g['n']:>5}{g['win']:>4}{g['loss']:>4}"
            f"{(wr + ' ' + ci):>16}{c1:>12}{c2:>11}{c3:>10}{c4:>16}{c5:>13}")
    say("")
    say("  ⚠ 胜率 n=80 ⇒ Wilson 95% 区间约 ±10 点，**不下强结论**；重点看行为指标。")
    say("  ⚠ 「主动过牌/全决策」= 上一轮 §1 报的 15.0%(r6) / 6.6%(★ r8) 那个口径。")
    say("     「剔被迫后过牌」= 同一个分子 ÷ (合法>1 的决策)，是「剔掉被迫后」的字面口径 —— 上一轮没报，本轮补上。")

    # ---- 表 B：逐边拆开 ----
    say("")
    say("=" * 160)
    say("表 B  逐边拆开（先手 / 后手分开；先手 T1 只有 1 点 kredit）")
    say("=" * 160)
    say(f"{'组|边':<26}{'局数':>5}{'胜':>4}{'负':>4}{'胜率':>7}{'回合级空过':>12}{'决策级空过':>11}"
        f"{'被迫':>10}{'主动过牌/全决策':>16}{'剔被迫后':>10}{'攻击/局':>8}{'出牌/局':>8}{'NN回合/局':>10}")
    for gtag, dn, typ, do in GROUPS:
        for side in ("left", "right"):
            rows = list(res.get(f"{gtag}|{side}", {}).values())
            if not rows:
                continue
            g = agg(rows)
            wr = f"{100.0 * g['win'] / (g['win'] + g['loss']):.0f}%" if g["win"] + g["loss"] else "n/a"
            c1 = f"{pct(g['empty_turns'], g['n_turns'])} {g['empty_turns']}/{g['n_turns']}"
            c2 = f"{pct(g['dec_end'], g['decisions'])} {g['dec_end']}/{g['decisions']}"
            c4 = f"{pct(g['dec_cond_end'], g['decisions'])} {g['dec_cond_end']}/{g['decisions']}"
            say(f"{(gtag + '|' + side):<26}{g['n']:>5}{g['win']:>4}{g['loss']:>4}{wr:>7}"
                f"{c1:>12}{c2:>11}{pct(g['dec_forced'], g['decisions']):>10}{c4:>16}"
                f"{pct(g['dec_cond_end'], g['dec_cond']):>10}"
                f"{g['attack']:>8.1f}{g['play']:>8.1f}{g['nn_turns_log'] / g['n']:>10.1f}")

    # ---- 表 C：手牌 / kredit / 自评 ----
    say("")
    say("=" * 160)
    say("表 C  手牌、kredit、自评、局面长度（都取 NN 的**决策点**；最大值 = 该组所有决策点上的最大）")
    say("=" * 160)
    say(f"{'组':<16}{'手牌max':>8}{'手牌均值':>9}{'kredit max':>11}{'kredit均值':>11}"
        f"{'kredit上限max':>13}{'攻击/局':>8}{'出牌/局':>8}{'NN回合/局':>10}{'总回合/局':>10}"
        f"{'自评前':>8}{'自评后':>8}{'漏杀':>6}{'重放不一致':>11}")
    for gtag, dn, typ, do in GROUPS:
        rows = []
        for side in ("left", "right"):
            rows += list(res.get(f"{gtag}|{side}", {}).values())
        g = tableA[gtag]
        say(f"{gtag:<16}{g['hand_max']:>8}{g['hand_mean']:>9.2f}{g['kred_max']:>11}{g['kred_mean']:>11.2f}"
            f"{g['kmax_max']:>13}{g['attack']:>8.1f}{g['play']:>8.1f}"
            f"{g['nn_turns_log'] / g['n']:>10.1f}{g['turns']:>10.1f}"
            f"{g['self_before']:>7.1f}%{g['self_after']:>7.1f}%{g['missed']:>6}"
            f"{str(g['bad_replay']) + '/' + str(g['n']):>11}")

    # ---- 表 D：按回合段 ----
    say("")
    say("=" * 160)
    say("表 D  按回合段拆开：决策级空过 / 被迫 / 有替代却过牌（回答「前期不出牌到底是被迫还是选择」）")
    say("=" * 160)
    buckets = [(1, 1), (2, 3), (4, 6), (7, 9), (10, 12), (13, 99)]
    for gtag, dn, typ, do in GROUPS:
        rows = []
        for side in ("left", "right"):
            rows += list(res.get(f"{gtag}|{side}", {}).values())
        decs = [d for r in rows for d in r.get("decs", [])]
        say("")
        say(f"【{gtag}】{dn}（{typ}） vs {do}   —— 决策 {len(decs)} 个")
        say(f"  {'回合段':<10}{'决策数':>7}{'决策级空过':>11}{'被迫':>10}{'有替代':>8}"
            f"{'有替代却过牌':>13}{'占全决策':>10}{'占(有替代)':>12}{'平均手牌':>9}{'平均kredit':>11}")
        for lo, hi in buckets:
            ds = [d for d in decs if lo <= d["turn"] <= hi]
            if not ds:
                continue
            nd = len(ds)
            ne = sum(1 for d in ds if d["chosen"] == "EndTurn")
            nf = sum(1 for d in ds if d["legal"] == 1)
            cond = [d for d in ds if d["legal"] is not None and d["legal"] > 1]
            ce = sum(1 for d in cond if d["chosen"] == "EndTurn")
            name = f"T{lo}" if lo == hi else (f"T{lo}-{hi}" if hi < 99 else f"T{lo}+")
            say(f"  {name:<10}{nd:>7}{pct(ne, nd):>11}{pct(nf, nd):>10}{len(cond):>8}"
                f"{ce:>13}{pct(ce, nd):>10}{pct(ce, len(cond)):>12}"
                f"{sum(d['hand'] for d in ds) / nd:>9.2f}{sum(d['kredit'] for d in ds) / nd:>11.2f}")

    # ---- 表 E：空过回合的构成 ----
    say("")
    say("=" * 160)
    say("表 E  空过回合的构成（上一轮 §1 的口径：整个回合每一步都被迫的占多少）")
    say("=" * 160)
    say(f"{'组':<16}{'空过回合':>9}{'NN回合':>8}{'回合级空过率':>13}"
        f"{'整回合全被迫':>13}{'占空过回合':>11}{'真正主动空过':>13}{'占NN回合':>10}")
    for gtag, dn, typ, do in GROUPS:
        g = tableA[gtag]
        say(f"{gtag:<16}{g['empty_turns']:>9}{g['n_turns']:>8}{pct(g['empty_turns'], g['n_turns']):>13}"
            f"{g['empty_forced_turns']:>13}{pct(g['empty_forced_turns'], g['empty_turns']):>11}"
            f"{g['empty_turns'] - g['empty_forced_turns']:>13}"
            f"{pct(g['empty_turns'] - g['empty_forced_turns'], g['n_turns']):>10}")

    # ---- 自检：两种回合级口径必须一致 ----
    say("")
    say("=" * 160)
    say("自检：回合级空过的两种算法必须一致")
    say("=" * 160)
    say(f"{'组':<16}{'决策行聚合':>12}{'NN动作序列(idle)':>18}{'NN回合(决策行)':>15}{'NN回合(动作行)':>15}{'一致?':>8}")
    for gtag, dn, typ, do in GROUPS:
        g = tableA[gtag]
        ok = (g["empty_turns"] == g["idle_log"]) and (g["n_turns"] == g["nn_turns_log"])
        say(f"{gtag:<16}{g['empty_turns']:>12}{g['idle_log']:>18}{g['n_turns']:>15}{g['nn_turns_log']:>15}"
            f"{'✅' if ok else '❌':>8}")

    # ---- 表 F：关键对照 ----
    say("")
    say("=" * 160)
    say("表 F  关键对照：快攻 vs 慢速（同一对手 = 英日，唯一变量是 NN 卡组）")
    say("=" * 160)
    slow = tableA["慢速-基线"]
    fast = tableA["快攻"]
    say(f"{'指标':<28}{'慢速 美澳跳':>14}{'快攻 德芬车':>14}{'差值(快攻−慢速)':>18}")
    items = [
        ("回合级空过率", slow["empty_turns"] / slow["n_turns"], fast["empty_turns"] / fast["n_turns"], "%"),
        ("决策级空过率", slow["dec_end"] / slow["decisions"], fast["dec_end"] / fast["decisions"], "%"),
        ("被迫决策率", slow["dec_forced"] / slow["decisions"], fast["dec_forced"] / fast["decisions"], "%"),
        ("主动过牌率(占全决策)", slow["dec_cond_end"] / slow["decisions"], fast["dec_cond_end"] / fast["decisions"], "%"),
        ("剔被迫后过牌率", slow["dec_cond_end"] / slow["dec_cond"], fast["dec_cond_end"] / fast["dec_cond"], "%"),
        ("真正主动空过回合率", (slow["empty_turns"] - slow["empty_forced_turns"]) / slow["n_turns"],
         (fast["empty_turns"] - fast["empty_forced_turns"]) / fast["n_turns"], "%"),
        ("攻击/局", slow["attack"], fast["attack"], ""),
        ("出牌/局", slow["play"], fast["play"], ""),
        ("NN回合/局", slow["nn_turns_log"] / slow["n"], fast["nn_turns_log"] / fast["n"], ""),
        ("手牌最大值", slow["hand_max"], fast["hand_max"], ""),
        ("kredit 最大值", slow["kred_max"], fast["kred_max"], ""),
        ("胜率", slow["win"] / (slow["win"] + slow["loss"]) if slow["win"] + slow["loss"] else 0,
         fast["win"] / (fast["win"] + fast["loss"]) if fast["win"] + fast["loss"] else 0, "%"),
    ]
    for name, s, f_, unit in items:
        d = f_ - s
        if unit == "%":
            say(f"{name:<28}{100 * s:>13.1f}%{100 * f_:>13.1f}%{100 * d:>+17.1f}点")
        else:
            say(f"{name:<28}{s:>14.2f}{f_:>14.2f}{d:>+18.2f}")
    say("")
    say("  口径提醒：胜率一行的「点」= 百分点，但 n=80 ⇒ 区间 ±10 点，**不作为结论**。")

    # ---- 表 G：同一回合窗口内对照（排掉「快攻局更长 ⇒ 决策构成不同」这个混淆）----
    say("")
    say("=" * 160)
    say("表 G  同一回合窗口内对照（排掉「快攻的局更长 ⇒ 决策的回合构成不同」这个混淆）")
    say("=" * 160)
    say(f"{'组':<16}{'窗口':<10}{'决策数':>7}{'决策级空过':>11}{'被迫':>10}{'有替代':>8}"
        f"{'有替代却过牌':>13}{'占全决策':>10}{'占(有替代)':>12}")
    windows = [("T1-3", 1, 3), ("T4-6", 4, 6), ("T7-9", 7, 9), ("T10-12", 10, 12),
               ("T4-12", 4, 12), ("T13+", 13, 999)]
    for gtag, dn, typ, do in GROUPS:
        rows = []
        for side in ("left", "right"):
            rows += list(res.get(f"{gtag}|{side}", {}).values())
        decs = [d for r in rows for d in r.get("decs", [])]
        for name, lo, hi in windows:
            ds = [d for d in decs if lo <= d["turn"] <= hi]
            if not ds:
                continue
            nd = len(ds)
            ne = sum(1 for d in ds if d["chosen"] == "EndTurn")
            nf = sum(1 for d in ds if d["legal"] == 1)
            cond = [d for d in ds if d["legal"] is not None and d["legal"] > 1]
            ce = sum(1 for d in cond if d["chosen"] == "EndTurn")
            say(f"{gtag:<16}{name:<10}{nd:>7}{pct(ne, nd):>11}{pct(nf, nd):>10}{len(cond):>8}"
                f"{ce:>13}{pct(ce, nd):>10}{pct(ce, len(cond)):>12}")
        say("")

    # ---- 表 I：空过率的加法分解（回答「原始差到底是卡组还是模型」）----
    say("")
    say("=" * 160)
    say("表 I  回合级空过率的**加法分解**：空过率 = 全被迫回合率 + 主动空过回合率")
    say("=" * 160)
    say("  这是回答「原始差到底是卡组特性还是模型偏好」的关键：只有**主动**那一项才可能是模型偏好。")
    say("")
    say(f"{'组':<16}{'回合级空过率':>13}{'= 全被迫回合率':>15}{'+ 主动空过回合率':>17}"
        f"{'决策/回合':>11}{'空过回合/局':>12}")
    for gtag, dn, typ, do in GROUPS:
        g = tableA[gtag]
        forced_rate = g["empty_forced_turns"] / g["n_turns"] if g["n_turns"] else 0
        active_rate = (g["empty_turns"] - g["empty_forced_turns"]) / g["n_turns"] if g["n_turns"] else 0
        say(f"{gtag:<16}{pct(g['empty_turns'], g['n_turns']):>13}{pct(g['empty_forced_turns'], g['n_turns']):>15}"
            f"{pct(g['empty_turns'] - g['empty_forced_turns'], g['n_turns']):>17}"
            f"{g['decisions'] / g['n_turns'] if g['n_turns'] else 0:>11.2f}"
            f"{g['empty_turns'] / g['n']:>12.2f}")
    say("")
    s_, f_ = tableA["慢速-基线"], tableA["快攻"]
    drop = (s_["empty_turns"] / s_["n_turns"] - f_["empty_turns"] / f_["n_turns"]) * 100
    d_forced = (s_["empty_forced_turns"] / s_["n_turns"] - f_["empty_forced_turns"] / f_["n_turns"]) * 100
    d_active = ((s_["empty_turns"] - s_["empty_forced_turns"]) / s_["n_turns"]
                - (f_["empty_turns"] - f_["empty_forced_turns"]) / f_["n_turns"]) * 100
    say(f"  慢速 − 快攻（同一对手 英日）：回合级空过率 {drop:+.1f} 点 "
        f"= 被迫部分 {d_forced:+.1f} 点 + 主动部分 {d_active:+.1f} 点")
    if drop:
        say(f"    ⇒ 原始差里 **{100 * d_forced / drop:.0f}% 来自「被迫」**（卡组便宜 ⇒ 打得起牌），"
            f"{100 * d_active / drop:.0f}% 来自「有替代却过牌」。")
    say("")
    say("  逐对位的「主动空过回合 / NN 回合」差值（回合是比决策更干净的单位 —— 快攻每回合决策点更多）：")
    for ts, tf, opp in (("慢速-基线", "快攻", "英日"), ("慢速-换对手", "快攻-换对手", "德澳老兵")):
        a_, b_ = tableA[ts], tableA[tf]
        k1 = a_["empty_turns"] - a_["empty_forced_turns"]
        k2 = b_["empty_turns"] - b_["empty_forced_turns"]
        d, l, h = diff_ci(k1, a_["n_turns"], k2, b_["n_turns"])
        say(f"    vs {opp:<6} 慢速 {pct(k1, a_['n_turns'])} {k1}/{a_['n_turns']}   "
            f"快攻 {pct(k2, b_['n_turns'])} {k2}/{b_['n_turns']}   "
            f"差 {d:+.1f} 点  95% 区间 [{l:+.1f},{h:+.1f}]")

    # ---- 表 H：匹配窗口内的差值 + 置信区间（这才是有判别力的那张表）----
    say("=" * 160)
    say("表 H  匹配窗口内的「快攻 − 慢速」差值 + 95% 区间（**判别 A/B/C 就靠这张**）")
    say("=" * 160)
    say("  对照组按「同一对手」配对，唯一变量是 NN 卡组。")
    say("")
    for opp_tag_slow, opp_tag_fast, opp in [("慢速-基线", "快攻", "英日"),
                                            ("慢速-换对手", "快攻-换对手", "德澳老兵")]:
        slow_rows, fast_rows = [], []
        for side in ("left", "right"):
            slow_rows += list(res.get(f"{opp_tag_slow}|{side}", {}).values())
            fast_rows += list(res.get(f"{opp_tag_fast}|{side}", {}).values())
        say(f"【对手 = {opp}】")
        say(f"  {'口径':<34}{'慢速':>16}{'快攻':>16}{'差值(快攻−慢速)':>18}{'95% 区间':>20}")
        specs = [
            ("主动过牌 / 全部决策（全回合）", "all", 1, 999),
            ("主动过牌 / 有替代的决策（全回合）", "cond", 1, 999),
            ("主动过牌 / 全部决策（T4–12 匹配窗）", "all", 4, 12),
            ("主动过牌 / 有替代的决策（T4–12 匹配窗）", "cond", 4, 12),
            ("主动过牌 / 全部决策（T1–3）", "all", 1, 3),
            ("主动过牌 / 有替代的决策（T1–3）", "cond", 1, 3),
            ("主动过牌 / 全部决策（T13+）", "all", 13, 999),
            ("主动过牌 / 有替代的决策（T13+）", "cond", 13, 999),
        ]
        for label, mode, lo, hi in specs:
            k1, n1 = counts(slow_rows, lo, hi, mode)
            k2, n2 = counts(fast_rows, lo, hi, mode)
            d, l, h = diff_ci(k1, n1, k2, n2)
            s1 = f"{100 * k1 / n1:.1f}% {k1}/{n1}" if n1 else "n/a"
            s2 = f"{100 * k2 / n2:.1f}% {k2}/{n2}" if n2 else "n/a"
            say(f"  {label:<34}{s1:>16}{s2:>16}{d:>+16.1f}点{f'[{l:+.1f},{h:+.1f}]':>20}")
        say("")

    txt = "\n".join(L) + "\n"
    if a.out:
        (REPO / a.out).parent.mkdir(parents=True, exist_ok=True)
        (REPO / a.out).write_text(txt, encoding="utf-8")
        print(f"\n已写入 {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
