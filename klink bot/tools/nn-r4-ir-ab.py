"""
第四轮 §1(a) 的 A/B：**同一个模型、同一个对位**，只切「蓝图 IR 默认加载」与 `--no-ir`。

为什么要单独量：`tools/NNPlay/Program.cs` 原先**默认不加载蓝图 IR**（注释还写着
「因为 NNTrain 的 dump 没加载」—— 那段注释在 NNTrain 修好之后就反了）。
于是**默认配置**在做分布外推理。第三轮报告 §4.1 已经量过一次（20 胜 0 负 vs 4 胜 6 负），
本轮把默认值改对之后，用当前模型重跑一遍做前后对照。

用法：
  python -X utf8 "klink bot/tools/nn-r4-ir-ab.py" --model out/nn-model-v2-win.bin --seeds 20 --jobs 6
"""

import argparse
import concurrent.futures as cf
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
NNPLAY = REPO / "tools/NNPlay/bin/Release/net10.0/NNPlay.dll"

RE_OUT = re.compile(r"NN 是\s+: (\w+)\s+⇒ (.+)")
RE_ACT = re.compile(r"^#\s*(\d+) \[T(\d+)/(\w+)\] (NN|对手)\s+(.+)$", re.M)


def run_one(model, use_ir, seed, deck_nn, deck_opp, side, tmpdir):
    tag = "IR默认加载" if use_ir else "--no-ir"
    log = Path(tmpdir) / f"{tag}_{seed}.log"
    cmd = ["dotnet", str(NNPLAY), "play", "--model", model, "--seed", str(seed), "--rollout",
           "--deck-nn", deck_nn, "--deck-opp", deck_opp, "--log", str(log), "--quiet"]
    if side == "right":
        cmd += ["--nn-side", "right"]
    if not use_ir:
        cmd += ["--no-ir"]           # ⚠️ 第四轮起默认是**加载**，对照要显式关掉
    r = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, encoding="utf-8")
    txt = log.read_text(encoding="utf-8", errors="ignore") if log.exists() else ""
    if r.returncode != 0 or not txt:
        return tag, seed, {"outcome": "error", "atk": 0, "play": 0, "end": 0}
    m = RE_OUT.search(txt)
    out = ("win" if "✅" in m.group(2) else "loss" if "❌" in m.group(2) else "abort") if m else "?"
    a = sum(1 for x in RE_ACT.finditer(txt) if x.group(4) == "NN" and x.group(5).startswith("攻击"))
    p = sum(1 for x in RE_ACT.finditer(txt) if x.group(4) == "NN" and x.group(5).startswith("出牌"))
    e = sum(1 for x in RE_ACT.finditer(txt) if x.group(4) == "NN" and x.group(5).startswith("结束"))
    return tag, seed, {"outcome": out, "atk": a, "play": p, "end": e,
                       "ok": "最终校验  : ✅" in txt}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="out/nn-model-v2-win.bin")
    ap.add_argument("--seeds", type=int, default=20)
    ap.add_argument("--jobs", type=int, default=6)
    ap.add_argument("--deck-nn", default="德芬车")
    ap.add_argument("--deck-opp", default="德澳老兵")
    ap.add_argument("--side", default="left")
    a = ap.parse_args()

    tasks = [(ir, s) for ir in (True, False) for s in range(1, a.seeds + 1)]
    res = {}
    with tempfile.TemporaryDirectory() as td:
        with cf.ThreadPoolExecutor(max_workers=a.jobs) as ex:
            futs = [ex.submit(run_one, a.model, ir, s, a.deck_nn, a.deck_opp, a.side, td)
                    for ir, s in tasks]
            for f in cf.as_completed(futs):
                tag, seed, d = f.result()
                res.setdefault(tag, []).append(d)

    print(f"模型 {a.model}   NN={a.deck_nn}({a.side}) vs Greedy={a.deck_opp}   seeds 1..{a.seeds}")
    print(f"{'配置':<16}{'胜':>4}{'负':>4}{'中止':>5}{'胜率':>8}{'攻击/局':>9}{'出牌/局':>9}"
          f"{'结束/局':>9}{'重放一致':>10}")
    for tag in ("IR默认加载", "--no-ir"):
        rows = res.get(tag, [])
        if not rows:
            continue
        w = sum(1 for r in rows if r["outcome"] == "win")
        l = sum(1 for r in rows if r["outcome"] == "loss")
        ab = len(rows) - w - l
        n = len(rows)
        ok = sum(1 for r in rows if r.get("ok"))
        print(f"{tag:<16}{w:>4}{l:>4}{ab:>5}{(100.0 * w / (w + l) if w + l else 0):>7.0f}%"
              f"{sum(r['atk'] for r in rows) / n:>9.1f}{sum(r['play'] for r in rows) / n:>9.1f}"
              f"{sum(r['end'] for r in rows) / n:>9.1f}{ok:>7}/{n}")
    print()
    print("IR默认加载 = 与 NNTrain dump 同一动力学（正确配置）")
    print("--no-ir    = 卡牌效果不执行 ⇒ 分布外推理（旧默认值）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
