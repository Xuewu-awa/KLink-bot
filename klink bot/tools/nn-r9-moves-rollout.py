"""
nn-r9-moves-rollout.py —— 第九轮：**规则修好之后，模型会不会推进前线**。

背景（`klink bot/docs/内核补全队列.md` 最后两节）
------------------------------------------------
跨前线射程规则修好之后（半场只有 range≥2 能跨），游戏第一次**需要争前线**：
range=1 的步兵/坦克在半场里**谁也打不到**（`CanReachAcrossFrontline` 双方都不在前线 ⇒ 要 range≥2）。
可 `GreedyBot.TryMove` 是空的（`return false;`），而 `tools/NNPlay` 的 `--moves`
**默认关着**（候选动作里不含「移动上前线」）。

所以本脚本跑 **4 组对位 × seeds 1..40 × 左右各 40 局 × `--moves` 开/关**，
统计口径**与 `nn-archetype-rollout.py` 逐字相同**（本脚本 importlib 载入它，只覆盖
`MODEL` 与 `run_one`，正则 / `kind_of` / `agg()` / Wilson / 表 A~I 全部原样复用），
再补一段 `--moves` 专属的「前线推进」统计。

用法（仓库根目录）：
  python -X utf8 "klink bot/tools/nn-r9-moves-rollout.py" --moves --seeds 40 --jobs 8 `
    --model out/nn-model-100k-r9-A-s42.bin `
    --logs out/_r9-logs-mv1 --dump out/_r9-rollout-mv1.json --out out/_r9-rollout-mv1.txt
  # 不带 --moves 就是关的那一组（日志/产物名换成 mv0）
  # 复用已跑结果重算表格：加 --reuse
"""

import importlib.util
import json
import re
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent

spec = importlib.util.spec_from_file_location("nn_arch", HERE / "nn-archetype-rollout.py")
arch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(arch)

# ★ 本轮唯一的模型：A 组（batch 256 / lr 1e-3 / seed 42）
arch.MODEL = "out/nn-model-100k-r9-A-s42.bin"

# ★ 唯一的口径变化：候选动作里要不要含「移动上前线」
MOVES = "--moves" in sys.argv
sys.argv = [a for a in sys.argv if a != "--moves"]

# 逐局日志里的「移动」行（**不是** 🧠 决策块里列的候选 —— 那些也含「前线槽位」）
RE_MOVE = re.compile(r"^#\s*\d+ \[T(\d+)/(\w+)\] (NN|对手)\s+移动 (.+?) → 前线槽位 (\d+)", re.M)
RE_FROM = re.compile(r"@BoardFrontline#(\d+)")


def run_one(group_tag, side, seed, model, outdir):
    """与 arch.run_one 逐字相同，只多两处：`--moves`、以及日志名带 mv0/mv1 前缀（避免两组互踩）。"""
    tag = f"{group_tag}|{side}"
    suf = "mv1" if MOVES else "mv0"
    log = Path(outdir) / f"R9_{suf}_{group_tag}_{side}_{seed}.log"
    dn = next(g[1] for g in arch.GROUPS if g[0] == group_tag)
    do = next(g[3] for g in arch.GROUPS if g[0] == group_tag)
    cmd = ["dotnet", str(arch.NNPLAY), "play", "--model", model, "--seed", str(seed), "--rollout",
           "--deck-nn", dn, "--deck-opp", do, "--log", str(log), "--quiet"]
    # ⚠️ `tools/NNPlay` 的 --moves 默认已改成**开**（2026-10-01，依据就是本脚本测出的结果：
    #    关掉会严重低估模型 —— 主动过牌率 45~69%、快攻胜率 21%；打开后 9~17% / 79%）。
    #    所以「关」那一组现在必须**显式**传 --no-moves，否则两组会跑成同一组。
    cmd.append("--moves" if MOVES else "--no-moves")
    if side == "right":
        cmd += ["--nn-side", "right"]
    r = subprocess.run(cmd, cwd=arch.REPO, capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0 or not log.exists():
        return tag, seed, {"outcome": "error", "result": (r.stderr or "")[-400:], "error_rc": r.returncode,
                           "decs": [], "ir": "?", "ir_loaded": False}
    d = arch.parse(log)
    m = arch.RE_IR.search(r.stdout or "")
    d["ir"] = m.group(1).strip() if m else "?"
    d["ir_loaded"] = ("已加载" in d["ir"])
    return tag, seed, d


arch.run_one = run_one


def frontline_stats(logdir, group_tag, side, seeds):
    """从逐局日志里数「移动上前线」。三类分开数（口径写死，别混）：
       push   = 半场（BoardHqLeft/Right）→ 前线         ← 真正的前线推进
       reslot = 已在前线 → 同槽位（空转，白扔一次移动额度）
       inner  = 已在前线 → 别的槽位（前线内挪位）
    """
    push = reslot = inner = opp_move = 0
    per_game = []
    for s in seeds:
        p = Path(logdir) / f"R9_{'mv1' if MOVES else 'mv0'}_{group_tag}_{side}_{s}.log"
        if not p.exists():
            continue
        txt = p.read_text(encoding="utf-8", errors="ignore")
        g_push = g_reslot = g_inner = 0
        for m in RE_MOVE.finditer(txt):
            # 组: 1=turn 2=side 3=actor 4=源位置 5=目标槽位
            actor, src, slot = m.group(3), m.group(4), int(m.group(5))
            if actor != "NN":
                opp_move += 1
                continue
            f = RE_FROM.search(src)
            if not f:
                g_push += 1
            elif int(f.group(1)) == slot:
                g_reslot += 1
            else:
                g_inner += 1
        push += g_push
        reslot += g_reslot
        inner += g_inner
        per_game.append((g_push, g_reslot, g_inner))
    return dict(push=push, reslot=reslot, inner=inner, opp_move=opp_move, games=len(per_game))


def main():
    # 先把 --moves / --model / --logs / --dump / --out / --seeds 抠出来（给 main 之后的补表用）
    argv = sys.argv[1:]
    def opt(name, default=""):
        if name in argv:
            i = argv.index(name)
            return argv[i + 1] if i + 1 < len(argv) else default
        return default
    logdir = Path(opt("--logs", "out/_arch-logs"))
    if not logdir.is_absolute():
        logdir = arch.REPO / logdir
    dumppath = Path(opt("--dump", "out/_arch-rollout.json"))
    if not dumppath.is_absolute():
        dumppath = arch.REPO / dumppath
    outpath = Path(opt("--out", "out/_arch-rollout.txt"))
    if not outpath.is_absolute():
        outpath = arch.REPO / outpath
    seeds = int(opt("--seeds", "40"))

    rc = arch.main()
    if rc != 0:
        return rc

    L = []
    def say(s=""):
        print(s)
        L.append(s)

    say("=" * 160)
    say(f"表 J  「移动上前线」逐项（--moves {'开' if MOVES else '关'}；"
        f"口径：日志里 `#N [Tx/side] <方> 移动 <卡> → 前线槽位 S` 的**实际执行**行，"
        f"不是 🧠 决策块里列的候选）")
    say("=" * 160)
    say(f"  {'组':<16}{'局数':>5}{'推进前线':>10}{'次/局':>8}{'前线内挪位':>11}"
        f"{'同槽位空转':>11}{'对手移动':>9}{'NN 决策里选 Move':>17}{'Move/全决策':>13}")
    tot = defaultdict(int)
    for gtag, dn, typ, do in arch.GROUPS:
        st = defaultdict(int)
        games = 0
        mv_dec = 0
        n_dec = 0
        for side in ("left", "right"):
            s = frontline_stats(logdir, gtag, side, range(1, seeds + 1))
            for k in ("push", "reslot", "inner", "opp_move"):
                st[k] += s[k]
            games += s["games"]
        for side in ("left", "right"):
            rows = list(json.loads(dumppath.read_text(encoding="utf-8"))
                        .get(f"{gtag}|{side}", {}).values())
            for r in rows:
                for d in r.get("decs", []):
                    n_dec += 1
                    if d.get("chosen") == "Move":
                        mv_dec += 1
        for k in ("push", "reslot", "inner", "opp_move"):
            tot[k] += st[k]
        tot["games"] += games
        tot["mv_dec"] += mv_dec
        tot["n_dec"] += n_dec
        say(f"  {gtag:<16}{games:>5}{st['push']:>10}{st['push'] / games if games else 0:>8.2f}"
            f"{st['inner']:>11}{st['reslot']:>11}{st['opp_move']:>9}"
            f"{mv_dec:>17}{100.0 * mv_dec / n_dec if n_dec else 0:>12.2f}%")
    say(f"  {'合计':<16}{tot['games']:>5}{tot['push']:>10}"
        f"{tot['push'] / tot['games'] if tot['games'] else 0:>8.2f}"
        f"{tot['inner']:>11}{tot['reslot']:>11}{tot['opp_move']:>9}"
        f"{tot['mv_dec']:>17}{100.0 * tot['mv_dec'] / tot['n_dec'] if tot['n_dec'] else 0:>12.2f}%")
    say("")
    say("  ⚠ 「对手移动」恒为 0 是**预期**：`GreedyBot.TryMove` 是 `return false;`（内核不会主动推进前线）。")
    say("     ⇒ 所以哪怕 --moves 开着，前线争夺也只是**单方面**的：NN 推上去，对手永远待在半场。")
    say("     ⇒ 而且训练数据（NNTrain dump 用 GreedyBot 双方）里**一次移动都没有** —— ")
    say("        「移动上前线」这个候选对模型是**分布外**的，它选不选、选得好不好都不能当成模型能力的结论。")
    say("  ⚠ 「同槽位空转」= 卡已经在前线槽位 S，却又执行了一次「→ 前线槽位 S」（白扔一次移动额度）。")
    say("     这是 NnPolicy 的候选生成没排除「原地不动」造成的，不是模型的问题，也不是规则问题。")

    txt = "\n".join(L) + "\n"
    with outpath.open("a", encoding="utf-8") as f:
        f.write(txt)
    print(f"\n已追加表 J 到 {outpath.relative_to(arch.REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
