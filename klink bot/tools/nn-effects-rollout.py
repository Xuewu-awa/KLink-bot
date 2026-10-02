"""
② 让带效果训练出来的 NN 下场打一批局（不是 1~2 局），并解析行为统计。

为什么要一批：第二轮报告 §7.6 自己写了「NNPlay 只跑了 2 局（各 1 局），行为结论样本量极小」。
一局只要几秒（`--ir` 下约 2~6 s），所以这一轮把它做成可统计的批次。

每个配置跑 seeds 1..N，逐局解析日志里的：
  结果 / 回合数 / NN 动作构成（出牌·攻击·结束回合）/ 决策次数 /
  整回合空过数（该回合 NN 只选了「结束回合」）/ 模型平均自评 /
  重放与实时引擎是否一致（⚠️ 带效果后新出现的失败模式，见报告）

用法：
  python -X utf8 "klink bot/tools/nn-effects-rollout.py" [--jobs 6] [--seeds 20]
"""

import argparse
import concurrent.futures as cf
import json
import re
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
NNPLAY = REPO / "tools/NNPlay/bin/Release/net10.0/NNPlay.dll"

RE_OUT = re.compile(r"NN 是\s+: (\w+)\s+⇒ (.+)")
RE_SUM = re.compile(r"回合数\s+: (\d+)\s+总步数 (\d+)")
RE_KIND = re.compile(r"NN 动作\s+: (.+)")
RE_DEC = re.compile(r"NN 决策\s+: (\d+) 次")
RE_SELF = re.compile(r"模型平均自评: 决策前 ([\d.]+)%，选中后 ([\d.]+)%")
RE_END = re.compile(r"选中「结束回合」(\d+) 次/(\d+) 次决策")
RE_HQ = re.compile(r"HQ\s+: NN (-?\d+)\s+对手 (-?\d+)")
RE_DECISION = re.compile(r"🧠 决策 #(\d+)\s+\[T(\d+)/(\w+)\]")
RE_CHOSEN = re.compile(r"→ 选 \[[\d.]+%\] (.+)")
# ⚠️ 行为统计必须用**实际动作序列**（`#  71 [T19/left] NN   攻击 …`），
#    不能用「决策块里选中了什么」—— 每个回合的最后一次决策必然是「结束回合」，
#    按决策块数会把「出牌+攻击+结束」的回合也算成空过（第一版脚本就是这么错的）。
RE_ACT = re.compile(r"^#\s*(\d+) \[T(\d+)/(\w+)\] (NN|对手)\s+(.+)$", re.M)

CONFIGS = [
    # (标签, 模型, 先/后手, NN 卡组, 对手卡组, 是否加载蓝图 IR, 局数)
    ("A-主模型-先手",   "out/nn-model.bin", "left",  "德芬车",   "德澳老兵", True, 20),
    ("A-主模型-后手",   "out/nn-model.bin", "right", "德芬车",   "德澳老兵", True, 20),
    ("B-主模型-先手",   "out/nn-model.bin", "left",  "日澳快攻", "美英跳",   True, 20),
    ("B-主模型-后手",   "out/nn-model.bin", "right", "日澳快攻", "美英跳",   True, 20),
    ("A-交付模型-先手", "nn-model.bin",     "left",  "德芬车",   "德澳老兵", True, 20),
    ("A-交付模型-后手", "nn-model.bin",     "right", "德芬车",   "德澳老兵", True, 20),
    ("A-主模型-先手-不开IR", "out/nn-model.bin", "left", "德芬车", "德澳老兵", False, 10),
    # ---- 「重放不一致」是**跟边**还是**跟卡组**？这两条用来分离 ----
    ("C-主模型-先手-弱卡组", "out/nn-model.bin", "left",  "德澳老兵", "德芬车", True, 10),
    ("C-主模型-后手-弱卡组", "out/nn-model.bin", "right", "德澳老兵", "德芬车", True, 10),
]


def kind(a):
    """与 nn-rollout-stats.py 逐字相同的动作归类。"""
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
    m = RE_SUM.search(txt); d["turns"] = int(m.group(1)) if m else 0
    m = RE_KIND.search(txt)
    kinds = {}
    if m:
        for part in m.group(1).split():
            k, _, v = part.partition("×")
            kinds[k] = int(v)
    d["play"], d["attack"], d["end"] = kinds.get("出牌", 0), kinds.get("攻击", 0), kinds.get("结束回合", 0)
    m = RE_DEC.search(txt); d["decisions"] = int(m.group(1)) if m else 0
    m = RE_SELF.search(txt); d["self_before"], d["self_after"] = (float(m.group(1)), float(m.group(2))) if m else (0.0, 0.0)
    m = RE_END.search(txt); d["end_chosen"] = int(m.group(1)) if m else 0
    m = RE_HQ.search(txt); d["hq_nn"], d["hq_op"] = (int(m.group(1)), int(m.group(2))) if m else (0, 0)

    # 逐回合行为（按**实际动作**分组，与 nn-rollout-stats.py 同定义）
    # RE_ACT 的组：1=动作号 2=回合 3=side 4=NN|对手 5=动作文本
    by_turn = {}
    for mm in RE_ACT.finditer(txt):
        if mm.group(4) != "NN":
            continue
        by_turn.setdefault(int(mm.group(2)), []).append(kind(mm.group(5)))
    d["nn_turns"] = len(by_turn)
    d["idle_turns"] = sum(1 for a in by_turn.values() if a == ["结束"])
    d["first_end_turns"] = sum(1 for a in by_turn.values() if a and a[0] == "结束")
    d["attack_turns"] = sum(1 for a in by_turn.values() if "攻击" in a)
    d["replay_ok"] = "最终校验  : ✅" in txt
    d["replay_mismatch"] = "重放与实时引擎不一致" in txt
    return d


def run_one(cfg, seed, tmpdir):
    tag, model, side, deck_nn, deck_opp, ir = cfg[:6]
    log = Path(tmpdir) / f"{tag}_{seed}.log"
    cmd = ["dotnet", str(NNPLAY), "play", "--model", model, "--seed", str(seed), "--rollout",
           "--deck-nn", deck_nn, "--deck-opp", deck_opp, "--log", str(log), "--quiet"]
    if side == "right":
        cmd += ["--nn-side", "right"]
    # ⚠️ 第四轮起 NNPlay **默认加载蓝图 IR**（与 NNTrain dump 同分布），
    #    所以这里反过来：ir=False 的配置要显式加 `--no-ir`。
    #    （原来写的是 `if ir: cmd += ["--ir"]` —— 默认不加载时才对。）
    if not ir:
        cmd += ["--no-ir"]
    r = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0 or not log.exists():
        return tag, seed, {"outcome": "error", "result": (r.stderr or "")[-200:]}
    return tag, seed, parse(log)


def deck_baselines():
    """两套「只看卡组身份、完全不看打法」的对照基准。

    1. **头对头原始比例**：数据里该有序对位 Greedy 对 Greedy 的实测胜率。无偏但每个对位只有 8~38 局。
    2. **卡组身份逻辑回归**（左卡组 one-hot + 右卡组 one-hot，44 参数，10,000 局全体拟合）：
       把稀疏的对位比例平滑掉（收缩到边际强弱），给出每个对位的期望胜率。**这是本节用来
       判断「NN 打得比 Greedy 好还是差」的主基准** —— 原始比例在 8 局的格子上噪声太大。
    """
    import numpy as np
    sys.path.insert(0, str(HERE))
    from nn_common import open_data
    import importlib.util
    spec = importlib.util.spec_from_file_location("neb", HERE / "nn-effects-baselines.py")
    neb = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(neb)

    rec, dim, n = open_data(str(REPO / "out/nn-data.bin"))
    y = np.asarray(rec[:, dim], dtype=np.float32)
    gid = np.asarray(rec[:, dim + 1], dtype=np.int64)
    man = json.load(open(REPO / "out/nn-data.bin.decks.json", encoding="utf-8"))
    names = man["decks"]
    GL, GR = {}, {}
    for g, l, r in man["pairs"]:
        GL[g], GR[g] = l, r
    pl = np.array([GL[int(g)] for g in gid], dtype=np.int64)
    pr = np.array([GR[int(g)] for g in gid], dtype=np.int64)

    raw = {}
    for g, l, r in man["pairs"]:
        raw.setdefault((l, r), []).append(g)
    lab = {}
    for g, yy in zip(gid.tolist(), y.tolist()):
        lab[g] = yy

    F = np.zeros((n, 1 + 44), dtype=np.float32)
    F[:, 0] = 1.0
    F[np.arange(n), 1 + pl] = 1.0
    F[np.arange(n), 1 + 22 + pr] = 1.0
    w = neb.fit_logistic(F, y)
    del F, rec

    out = {}
    for nm, (a, b) in {"德芬车→德澳老兵": ("德芬车", "德澳老兵"),
                       "德澳老兵→德芬车": ("德澳老兵", "德芬车"),
                       "日澳快攻→美英跳": ("日澳快攻", "美英跳"),
                       "美英跳→日澳快攻": ("美英跳", "日澳快攻")}.items():
        li, ri = names.index(a), names.index(b)
        games = raw.get((li, ri), [])
        wins = sum(lab[g] for g in games)
        v = np.zeros((1, 45), dtype=np.float32)
        v[0, 0] = 1.0
        v[0, 1 + li] = 1.0
        v[0, 1 + 22 + ri] = 1.0
        p = 1.0 / (1.0 + np.exp(-float((v @ w).reshape(-1)[0])))
        out[nm] = (100.0 * wins / len(games) if games else float("nan"), len(games), 100.0 * p)
    return out


def head_to_head(nn_deck, opp_deck, nn_side):
    """这一对卡组的**头对头**胜率（数据里该有序对位、Greedy 对 Greedy、带效果内核）。

    ⚠️ 比「边际胜率」严格：边际胜率是对所有对手平均，头对头才是这一局的真实基准。
    每个有序对位只有 8~38 局，所以同时给出局数。
    """
    import numpy as np
    sys.path.insert(0, str(HERE))
    from nn_common import open_data
    rec, dim, n = open_data(str(REPO / "out/nn-data.bin"))
    y = np.asarray(rec[:, dim], dtype=np.float64)
    gid = np.asarray(rec[:, dim + 1], dtype=np.int64)
    man = json.load(open(REPO / "out/nn-data.bin.decks.json", encoding="utf-8"))
    names = man["decks"]
    lab = {}
    for g, yy in zip(gid.tolist(), y.tolist()):
        lab[g] = yy
    li, ri = names.index(nn_deck), names.index(opp_deck)
    if nn_side == "right":
        li, ri = ri, li
    w = t = 0
    for g, l, r in man["pairs"]:
        if l == li and r == ri:
            t += 1
            w += lab[g] if nn_side == "left" else (1 - lab[g])
    del rec
    return (100.0 * w / t if t else float("nan")), t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", type=int, default=6)
    ap.add_argument("--dump", default="out/_effects-rollout.json")
    ap.add_argument("--reuse", action="store_true", help="直接读 --dump 的 JSON，不重跑对局")
    a = ap.parse_args()

    tasks = [(c, s) for c in CONFIGS for s in range(1, c[6] + 1)]
    if a.reuse:
        res = {t: {int(k): v for k, v in d.items()}
               for t, d in json.load(open(REPO / a.dump, encoding="utf-8")).items()}
        print(f"复用 {a.dump}")
    else:
        print(f"共 {len(tasks)} 局（{len(CONFIGS)} 个配置）；并发 {a.jobs}")
        res = {}
        with tempfile.TemporaryDirectory() as td:
            with cf.ThreadPoolExecutor(max_workers=a.jobs) as ex:
                futs = [ex.submit(run_one, c, s, td) for c, s in tasks]
                for i, f in enumerate(cf.as_completed(futs), 1):
                    tag, seed, d = f.result()
                    res.setdefault(tag, {})[seed] = d
                    if i % 20 == 0:
                        print(f"  …{i}/{len(tasks)}")

        (REPO / a.dump).write_text(
            json.dumps({t: {str(k): v for k, v in d.items()} for t, d in res.items()},
                       ensure_ascii=False, indent=1), encoding="utf-8")

    db = deck_baselines()

    print()
    print("=" * 126)
    print("② NNPlay 批次结果（每配置 seeds 1..N，逐局解析日志；空格过 = 该回合 NN 唯一动作就是「结束回合」）")
    print("=" * 126)
    hdr = (f"{'配置':<22}{'局数':>5}{'胜':>4}{'负':>4}{'中止':>5}{'有效局胜率':>11}{'对位原始':>10}{'对位平滑':>10}"
           f"{'攻击/局':>8}{'出牌/局':>8}{'空过':>9}{'空过率':>8}{'首动=结束':>10}"
           f"{'自评前':>8}{'自评后':>8}{'重放不一致':>11}")
    print(hdr)
    for cfg in CONFIGS:
        tag, _m, side, dn, do, _ir, _n = cfg
        rows = list(res.get(tag, {}).values())
        if not rows:
            continue
        win = sum(1 for r in rows if r["outcome"] == "win")
        loss = sum(1 for r in rows if r["outcome"] == "loss")
        abort = sum(1 for r in rows if r["outcome"] in ("aborted", "error"))
        valid = win + loss
        n = len(rows)
        atk = sum(r.get("attack", 0) for r in rows) / n
        ply = sum(r.get("play", 0) for r in rows) / n
        idle = sum(r.get("idle_turns", 0) for r in rows)
        turns = sum(r.get("nn_turns", 0) for r in rows)
        fend = sum(r.get("first_end_turns", 0) for r in rows)
        sb = sum(r.get("self_before", 0) for r in rows) / n
        sa = sum(r.get("self_after", 0) for r in rows) / n
        bad = sum(1 for r in rows if r.get("replay_mismatch") or not r.get("replay_ok"))
        h2h, h2h_n = head_to_head(dn, do, side)
        smooth = db.get(f"{dn}→{do}", (float("nan"), 0, float("nan")))[2]
        wr = f"{100.0 * win / valid:.0f}%" if valid else "n/a"
        if valid and valid < 10:
            wr += f"({valid})"
        print(f"{tag:<22}{n:>5}{win:>4}{loss:>4}{abort:>5}{wr:>11}"
              f"{h2h:>7.0f}%({h2h_n}){smooth:>6.0f}%{atk:>8.1f}{ply:>8.1f}{idle:>6}/{turns:<3}"
              f"{(100.0 * idle / turns if turns else 0):>7.1f}%"
              f"{fend:>7}/{turns:<3}"
              f"{sb:>7.1f}%{sa:>7.1f}%{bad:>8}/{n}")
    print("  对位: 原始 = 数据里该有序对位 Greedy 对 Greedy 的实测胜率（括号 = 该对位局数，8~38 局，噪声大）")
    print("        平滑 = 只用「左卡组+右卡组」44 参数逻辑回归在 10,000 局上拟合的期望胜率（本节主基准）")
    print()
    print("自评校准（模型「决策前自评」按实际胜负分组；同一配置里既有胜也有负才有意义）")
    for cfg in CONFIGS:
        tag = cfg[0]
        rows = list(res.get(tag, {}).values())
        w = [r["self_before"] for r in rows if r["outcome"] == "win"]
        l = [r["self_before"] for r in rows if r["outcome"] == "loss"]
        if w and l:
            print(f"  {tag:<22} 胜局自评均值 {sum(w) / len(w):5.1f}%（{len(w)} 局）   "
                  f"负局自评均值 {sum(l) / len(l):5.1f}%（{len(l)} 局）   "
                  f"差 {sum(w) / len(w) - sum(l) / len(l):+5.1f} 点")
        else:
            print(f"  {tag:<22} 结果单一（{len(w)} 胜 / {len(l)} 负）⇒ 无法用它检验校准")
    print()
    print("卡组强弱对照（③ 实测**边际**胜率：随机对手 + 随机左右，Greedy 对 Greedy，带效果内核）：")
    print("  德芬车 73.6%   德澳老兵 27.1%   日澳快攻 60.9%   美英跳 62.0%")
    print()
    print("对比基准（第二轮，无效果模型，各只跑 1 局）：")
    print("  对局A 德芬车(left) vs 德澳老兵   → 输 0:23，攻击 3 次，10/16 回合第一个动作就是结束回合，自评 52.1→54.0%")
    print("  对局B 日澳快攻(left) vs 美英跳   → 赢，攻击 26 次，1/12 回合空过，自评 58.4→59.2%")
    print("  v0 模型 对局A                    → 输 0:20，攻击 4 次，10/16 回合空过，自评 58.2→60.8%")
    print()
    print("中止 = 决策期「重放与实时引擎不一致」而 break（带效果后新出现的失败模式；只出现在 NN 坐 right 时）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
