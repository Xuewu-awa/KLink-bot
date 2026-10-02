"""
第七轮：**收敛后的 NN 下场**（`out/_r6-model-S3-adam-100k.bin`）+ 与「同数据、同编码、只差优化器」的
未收敛对照模型（`out/nn-model-win-100k-ep150.bin`）在同一批 seed 上的行为对照。

本脚本**只测量、不改任何东西**（不改模型 / 编码 / NNPlay 策略 / `src/KLink.Bot/`）。
它做的三件事：
  ① 对每个 (模型 × 对位 × 边) 跑 seeds 1..N，逐局解析 NNPlay 的完整日志；
  ② 汇总「胜率 / 攻击次数每局 / 空过回合比例 / 首动=结束回合 / 自评」；
  ③ 自评校准：把每局的「决策前平均自评」与**该局实际胜负**对齐 ——
     AUC / Brier / 分桶实际胜率 / 胜局 vs 负局自评差。

指标定义与第三轮 `nn-effects-rollout.py` **逐字相同**（同一套正则、同一套「空过」定义：
一个回合里 NN 的实际动作序列只有「结束」⇒ 空过），这样新旧数字可以直接并列。

用法（仓库根目录）：
  python -X utf8 "klink bot/tools/nn-r7-rollout.py" --seeds 40 --jobs 10 `
    --dump out/_r7-rollout.json --out out/_r7-rollout.txt
  # 复用已跑结果重算表格：
  python -X utf8 "klink bot/tools/nn-r7-rollout.py" --reuse --dump out/_r7-rollout.json
"""

import argparse
import concurrent.futures as cf
import json
import math
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
NNPLAY = REPO / "tools/NNPlay/bin/Release/net10.0/NNPlay.dll"

# ---------------- 下面这一组正则与第三轮 nn-effects-rollout.py **逐字相同** ----------------
RE_OUT = re.compile(r"NN 是\s+: (\w+)\s+⇒ (.+)")
RE_SUM = re.compile(r"回合数\s+: (\d+)\s+总步数 (\d+)")
RE_KIND = re.compile(r"NN 动作\s+: (.+)")
RE_DEC = re.compile(r"NN 决策\s+: (\d+) 次")
RE_SELF = re.compile(r"模型平均自评: 决策前 ([\d.]+)%，选中后 ([\d.]+)%")
RE_END = re.compile(r"选中「结束回合」(\d+) 次/(\d+) 次决策")
RE_HQ = re.compile(r"HQ\s+: NN (-?\d+)\s+对手 (-?\d+)")
RE_ACT = re.compile(r"^#\s*(\d+) \[T(\d+)/(\w+)\] (NN|对手)\s+(.+)$", re.M)
RE_MISS = re.compile(r"⚠ 有 (\d+) 次决策存在「一步直接获胜」的候选，但模型没选它")
RE_IMM = re.compile(r"⚑ 直接获胜")
# ----------------------------------------------------------------------------------

NEW = "out/_r6-model-S3-adam-100k.bin"
OLD = "out/nn-model-win-100k-ep150.bin"        # 第五轮控制配方 / 10 万局 / 150 epoch / 留出 73.90%
OLD4 = "out/_r4-model-win-ep120.bin"           # 第四轮 1 万局 / 120 epoch / 留出 71.91%（更早的未收敛）

MODELS = [
    ("R6-adam-100k", NEW, "81.63%（收敛）"),
    ("R5-ctl-100k", OLD, "73.90%（同数据·未收敛）"),
    ("R4-10k-ep120", OLD4, "71.91%（1 万局·未收敛）"),
]

MATCHUPS = [
    # (标签, NN 卡组, 对手卡组)  —— 基准见 out/_r7-baseline-100k.txt（10 万局 Greedy 对 Greedy）
    ("A-强牌", "德芬车", "德澳老兵"),      # 头对头 96.2%(左) / 91.3%(右)：NN 拿强牌
    ("B-历史均势", "日澳快攻", "美英跳"),  # 头对头 56.0%(左) / 38.2%(右)：第三轮那个均势对位
    ("C-真均势", "美澳跳", "英日"),        # 头对头 51.4%(左) / 45.7%(右)：两个方向都贴 50%
]

# 哪个模型只跑哪些对位（省时间）：不列 = 全部对位
MODEL_MATCHUPS = {"R4-10k-ep120": ["A-强牌"]}


def build_configs(matchups):
    out = []
    for mtag, mpath, mnote in MODELS:
        allow = MODEL_MATCHUPS.get(mtag)
        for gtag, dn, do in matchups:
            if allow is not None and gtag not in allow:
                continue
            for side in ("left", "right"):
                out.append((f"{mtag}|{gtag}|{side}", mpath, mnote, side, dn, do))
    return out


CONFIGS = build_configs(MATCHUPS)


def kind(a):
    """与 nn-rollout-stats.py / nn-effects-rollout.py 逐字相同的动作归类。"""
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

    by_turn = {}
    for mm in RE_ACT.finditer(txt):
        if mm.group(4) != "NN":
            continue
        by_turn.setdefault(int(mm.group(2)), []).append(kind(mm.group(5)))
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
    return d


def run_one(cfg, seed, outdir):
    tag, model, _note, side, deck_nn, deck_opp = cfg
    log = Path(outdir) / f"{tag.replace('|', '_')}_{seed}.log"
    cmd = ["dotnet", str(NNPLAY), "play", "--model", model, "--seed", str(seed), "--rollout",
           "--deck-nn", deck_nn, "--deck-opp", deck_opp, "--log", str(log), "--quiet"]
    if side == "right":
        cmd += ["--nn-side", "right"]
    r = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0 or not log.exists():
        return tag, seed, {"outcome": "error", "result": (r.stderr or "")[-300:], "error_rc": r.returncode}
    return tag, seed, parse(log)


# ---------------- 统计小工具 ----------------

def wilson(k, n, z=1.96):
    """二项比例 Wilson 95% 区间（%）。"""
    if n == 0:
        return (float("nan"), float("nan"))
    p = k / n
    d = 1 + z * z / n
    c = p + z * z / (2 * n)
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return (100 * (c - h) / d, 100 * (c + h) / d)


def auc(pairs):
    """(score, label) 列表的 AUC（Mann-Whitney，含并列取 0.5）。"""
    pos = [s for s, y in pairs if y == 1]
    neg = [s for s, y in pairs if y == 0]
    if not pos or not neg:
        return float("nan")
    tot = 0.0
    for a in pos:
        for b in neg:
            tot += 1.0 if a > b else 0.5 if a == b else 0.0
    return tot / (len(pos) * len(neg))


def brier(pairs):
    return sum((s / 100.0 - y) ** 2 for s, y in pairs) / len(pairs) if pairs else float("nan")


def bucket_table(pairs, edges=(0, 40, 50, 60, 70, 80, 101)):
    rows = []
    for lo, hi in zip(edges, edges[1:]):
        sel = [(s, y) for s, y in pairs if lo <= s < hi]
        if sel:
            rows.append((f"{lo}~{hi - 1}%", len(sel), sum(y for _s, y in sel),
                         100.0 * sum(y for _s, y in sel) / len(sel),
                         sum(s for s, _y in sel) / len(sel)))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", type=int, default=10)
    ap.add_argument("--seeds", type=int, default=40)
    ap.add_argument("--dump", default="out/_r7-rollout.json")
    ap.add_argument("--out", default="out/_r7-rollout.txt")
    ap.add_argument("--logs", default="out/_r7-logs")
    ap.add_argument("--models", default="", help="只跑这些模型标签（逗号分隔）")
    ap.add_argument("--matchups", default="", help='覆盖对位，如 "C-新:日波情报:德澳老兵2"')
    ap.add_argument("--reuse", action="store_true")
    ap.add_argument("--detail", action="store_true", help="打印逐局明细")
    a = ap.parse_args()

    global CONFIGS, MATCHUPS
    if a.matchups:
        MATCHUPS = []
        for spec in a.matchups.split(","):
            lab, dn, do = spec.split(":")
            MATCHUPS.append((lab, dn, do))
        CONFIGS = build_configs(MATCHUPS)
    if a.models:
        keep = set(a.models.split(","))
        CONFIGS = [c for c in CONFIGS if c[0].split("|")[0] in keep]

    tasks = [(c, s) for c in CONFIGS for s in range(1, a.seeds + 1)]
    if a.reuse:
        res = {t: {int(k): v for k, v in d.items()}
               for t, d in json.load(open(REPO / a.dump, encoding="utf-8")).items()}
        print(f"复用 {a.dump}")
    else:
        outdir = REPO / a.logs
        outdir.mkdir(parents=True, exist_ok=True)
        print(f"共 {len(tasks)} 局（{len(CONFIGS)} 个配置 × {a.seeds} seeds）；并发 {a.jobs}")
        print(f"逐局完整日志写到 {a.logs}/")
        res = {}
        with cf.ThreadPoolExecutor(max_workers=a.jobs) as ex:
            futs = [ex.submit(run_one, c, s, str(outdir)) for c, s in tasks]
            for i, f in enumerate(cf.as_completed(futs), 1):
                tag, seed, d = f.result()
                res.setdefault(tag, {})[seed] = d
                if i % 40 == 0 or i == len(tasks):
                    print(f"  …{i}/{len(tasks)}")
        res = {t: {str(k): v for k, v in d.items()} for t, d in res.items()}   # 与 --reuse 分支同一口径（键 = 字符串 seed）
        (REPO / a.dump).write_text(
            json.dumps({t: {str(k): v for k, v in d.items()} for t, d in res.items()},
                       ensure_ascii=False, indent=1), encoding="utf-8")

    L = []
    p = L.append
    p("=" * 150)
    p("① NNPlay 批次结果（每配置 seeds 1..N；`--rollout`；IR 默认加载；NN 动作按**实际执行的动作序列**统计）")
    p("=" * 150)
    hdr = (f"{'配置(模型|对位|边)':<28}{'局数':>5}{'胜':>4}{'负':>4}{'中止':>5}{'胜率(95%CI)':>18}{'攻击/局':>8}"
           f"{'出牌/局':>8}{'空过':>10}{'空过率':>8}{'首动=结束':>11}{'自评前':>8}{'自评后':>8}{'重放不一致':>11}{'漏杀':>6}")
    p(hdr)
    for cfg in CONFIGS:
        tag = cfg[0]
        rows = list(res.get(tag, {}).values())
        if not rows:
            continue
        win = sum(1 for r in rows if r["outcome"] == "win")
        loss = sum(1 for r in rows if r["outcome"] == "loss")
        abort = sum(1 for r in rows if r["outcome"] in ("aborted", "error"))
        valid = win + loss
        n = len(rows)
        lo, hi = wilson(win, valid) if valid else (float("nan"), float("nan"))
        atk = sum(r.get("attack", 0) for r in rows) / n
        ply = sum(r.get("play", 0) for r in rows) / n
        idle = sum(r.get("idle_turns", 0) for r in rows)
        turns = sum(r.get("nn_turns", 0) for r in rows)
        fend = sum(r.get("first_end_turns", 0) for r in rows)
        sb = sum(r.get("self_before", 0) for r in rows) / n
        sa = sum(r.get("self_after", 0) for r in rows) / n
        bad = sum(1 for r in rows if r.get("replay_mismatch") or not r.get("replay_ok"))
        miss = sum(r.get("missed_wins", 0) for r in rows)
        wr = f"{100.0 * win / valid:.0f}%" if valid else "n/a"
        ci = f"[{lo:.0f},{hi:.0f}]" if valid else ""
        p(f"{tag:<28}{n:>5}{win:>4}{loss:>4}{abort:>5}{(wr + ' ' + ci):>18}{atk:>8.1f}{ply:>8.1f}"
          f"{idle:>6}/{turns:<4}{(100.0 * idle / turns if turns else 0):>7.1f}%"
          f"{fend:>8}/{turns:<4}{sb:>7.1f}%{sa:>7.1f}%{bad:>8}/{n}{miss:>6}")
    p("")
    p("  胜率 = 有效局（排除中止）里的胜率，括号是 Wilson 95% 置信区间（n 小 ⇒ 区间很宽，别用它下强结论）")
    p("  空过 = 该回合 NN 的**实际动作序列只有「结束回合」**（与第三轮同定义）")
    p("  首动=结束 = 该回合 NN 的第一个动作就是「结束回合」")
    p("  漏杀 = 存在「一步直接获胜」的候选但模型没选它的决策次数合计")

    # ---------------- 跨配置汇总（按模型、按对位、按边） ----------------
    p("")
    p("=" * 150)
    p("② 汇总（同模型合并；同一 seed 序列、同一内核、同一 IR）")
    p("=" * 150)

    def agg(rows):
        win = sum(1 for r in rows if r["outcome"] == "win")
        loss = sum(1 for r in rows if r["outcome"] == "loss")
        abort = np_ = len(rows) - win - loss
        idle = sum(r.get("idle_turns", 0) for r in rows)
        turns = sum(r.get("nn_turns", 0) for r in rows)
        return dict(n=len(rows), win=win, loss=loss, abort=abort,
                    atk=sum(r.get("attack", 0) for r in rows) / len(rows),
                    atk_turns=sum(r.get("attack_turns", 0) for r in rows) / len(rows),
                    idle=idle, turns=turns,
                    idle_rate=100.0 * idle / turns if turns else 0.0,
                    sb=sum(r.get("self_before", 0) for r in rows) / len(rows),
                    sa=sum(r.get("self_after", 0) for r in rows) / len(rows),
                    dec=sum(r.get("decisions", 0) for r in rows) / len(rows))

    groups = {}
    for cfg in CONFIGS:
        tag = cfg[0]
        mtag, gtag, side = tag.split("|")
        rows = list(res.get(tag, {}).values())
        if not rows:
            continue
        groups.setdefault(("模型", mtag), []).extend(rows)
        groups.setdefault(("模型×对位", f"{mtag}|{gtag}"), []).extend(rows)
        groups.setdefault(("模型×边", f"{mtag}|{side}"), []).extend(rows)

    p(f"{'分组':<26}{'局数':>5}{'胜':>4}{'负':>4}{'中止':>5}{'胜率(95%CI)':>18}{'攻击/局':>8}"
      f"{'有攻击的回合/局':>15}{'空过':>10}{'空过率':>8}{'自评前':>8}{'自评后':>8}{'决策/局':>8}")
    for key in sorted(groups, key=lambda k: (k[0], k[1])):
        g = agg(groups[key])
        lo, hi = wilson(g["win"], g["win"] + g["loss"]) if g["win"] + g["loss"] else (float("nan"), float("nan"))
        wr = f"{100.0 * g['win'] / (g['win'] + g['loss']):.0f}%" if g["win"] + g["loss"] else "n/a"
        p(f"{key[1]:<26}{g['n']:>5}{g['win']:>4}{g['loss']:>4}{g['abort']:>5}"
          f"{(wr + f' [{lo:.0f},{hi:.0f}]'):>18}{g['atk']:>8.1f}{g['atk_turns']:>15.1f}"
          f"{g['idle']:>6}/{g['turns']:<4}{g['idle_rate']:>7.1f}%{g['sb']:>7.1f}%{g['sa']:>7.1f}%{g['dec']:>8.1f}")

    # ---------------- ③ 自评校准 ----------------
    p("")
    p("=" * 150)
    p("③ 自评校准（每局「决策前平均自评」 vs 该局实际胜负；同一模型跨 4 个配置合并）")
    p("=" * 150)
    for mtag, _mp, mnote in MODELS:
        rows = groups.get(("模型", mtag), [])
        if not rows:
            continue
        pairs = [(r["self_before"], 1 if r["outcome"] == "win" else 0)
                 for r in rows if r["outcome"] in ("win", "loss")]
        w = [s for s, y in pairs if y == 1]
        l = [s for s, y in pairs if y == 0]
        if not pairs:
            continue
        p("")
        p(f"  【{mtag}】{mnote}   局数 {len(pairs)}（{len(w)} 胜 / {len(l)} 负）")
        if w and l:
            p(f"    胜局自评均值 {sum(w) / len(w):6.1f}%      负局自评均值 {sum(l) / len(l):6.1f}%      "
              f"差 {sum(w) / len(w) - sum(l) / len(l):+6.1f} 点")
        else:
            p(f"    结果单一（{len(w)} 胜 / {len(l)} 负）⇒ 无法用它检验校准")
        p(f"    AUC(自评→胜负) = {auc(pairs):.3f}   （0.5 = 毫无区分力；1.0 = 完全可分）"
          f"   Brier = {brier(pairs):.4f}   （越小越好；常数 0.5 猜测 = 0.25）")
        p(f"    {'自评区间':<12}{'局数':>6}{'实际胜':>8}{'实际胜率':>10}{'平均自评':>10}")
        for name, n_, k_, wr_, mean_ in bucket_table(pairs):
            p(f"    {name:<12}{n_:>6}{k_:>8}{wr_:>9.1f}%{mean_:>9.1f}%")

    # ---------------- ④ 成对比较：新 vs 旧（同 seed） ----------------
    p("")
    p("=" * 150)
    p("④ 同一 seed 序列上的成对比较（新 R6-adam vs 旧 R5-ctl；只比「同为有效局」的 seed）")
    p("=" * 150)
    for gtag, _dn, _do in MATCHUPS:
        for side in ("left", "right"):
            new = res.get(f"R6-adam-100k|{gtag}|{side}", {})
            old = res.get(f"R5-ctl-100k|{gtag}|{side}", {})
            if not new or not old:
                continue
            seeds = sorted(set(int(s) for s in new) & set(int(s) for s in old))
            both = [(s, new[str(s)], old[str(s)]) for s in seeds]
            nw = sum(1 for _s, a_, _b in both if a_["outcome"] == "win")
            nl = sum(1 for _s, a_, _b in both if a_["outcome"] == "loss")
            ow = sum(1 for _s, _a, b_ in both if b_["outcome"] == "win")
            ol = sum(1 for _s, _a, b_ in both if b_["outcome"] == "loss")
            only_new = sum(1 for _s, a_, b_ in both if a_["outcome"] == "win" and b_["outcome"] != "win")
            only_old = sum(1 for _s, a_, b_ in both if b_["outcome"] == "win" and a_["outcome"] != "win")
            p(f"  {gtag}·{side:<5}  n={len(seeds):<4} 新 {nw}胜{nl}负   旧 {ow}胜{ol}负   "
              f"新赢旧没赢 {only_new}   旧赢新没赢 {only_old}")

    # ---------------- ⑤ 逐局明细 ----------------
    if a.detail:
        p("")
        p("=" * 150)
        p("⑤ 逐局明细")
        p("=" * 150)
        p(f"{'配置':<28}{'seed':>5}{'结果':>8}{'回合':>5}{'决策':>5}{'出牌':>5}{'攻击':>5}{'结束':>5}"
          f"{'NN回合':>7}{'空过':>5}{'自评前':>8}{'自评后':>8}{'HQ 自/敌':>12}{'漏杀':>5}")
        for cfg in CONFIGS:
            tag = cfg[0]
            for s in sorted(res.get(tag, {}), key=lambda x: int(x)):
                r = res[tag][s]
                p(f"{tag:<28}{s:>5}{r.get('outcome', '?'):>8}{r.get('turns', 0):>5}{r.get('decisions', 0):>5}"
                  f"{r.get('play', 0):>5}{r.get('attack', 0):>5}{r.get('end', 0):>5}{r.get('nn_turns', 0):>7}"
                  f"{r.get('idle_turns', 0):>5}{r.get('self_before', 0):>7.1f}%{r.get('self_after', 0):>7.1f}%"
                  f"{str(r.get('hq_nn', 0)) + '/' + str(r.get('hq_op', 0)):>12}{r.get('missed_wins', 0):>5}")

    txt = "\n".join(L)
    print()
    print(txt)
    (REPO / a.out).write_text(txt, encoding="utf-8")
    print(f"\n已写入 {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
