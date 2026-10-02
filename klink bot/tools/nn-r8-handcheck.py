"""
手牌上限的直接验证：在 NNPlay 的**逐局完整日志**上数手牌。

日志里能直接读到的手牌数只有两处：
  1. `🧠 决策 #N  [T../side]  kredit a/b  手牌 N  场面 N  HQ x/y`  —— **NN 侧**每次决策时的手牌数；
  2. `[开局] T1 行动方 left  L: kredit a/b 手 N 场 N HQ N  |  R: kredit a/b 手 N 场 N HQ N` —— 开局双方。
  3. `│ [T../side] side 手牌已满（N/9），<card> 被弃掉` —— **任一侧**在「抽牌时手牌已满」的直接证据，
     括号里的 `N` 是**抽之前**的手牌数（内核 `preQtyInHand`）。若 N 出现过 > 9，说明手牌确实涨过头了。

所以本脚本报三件事：
  * 每局「NN 侧最大手牌」的分布（口径与上一轮 §3.2 相同）；
  * 全日志里出现过的所有 `手牌 N` 的**最大值**（> 9 就是 bug 还在）；
  * 烧牌行的条数、按侧分布、以及括号里 `preQtyInHand` 的取值分布。

用法：
  python -X utf8 "klink bot/tools/nn-r8-handcheck.py" --logs out/_r8-logs
  python -X utf8 "klink bot/tools/nn-r8-handcheck.py" --logs out/_r7-logs --tag "旧内核"
"""

import argparse
import re
import sys
from collections import Counter
from pathlib import Path

RE_HAND_DEC = re.compile(r"手牌 (\d+)")
RE_BURN = re.compile(r"\[T\d+/(\w+)\] (\w+) 手牌已满（(\d+)/(\d+)）")
RE_OPEN = re.compile(r"L: kredit \d+/\d+ 手 (\d+) .*?R: kredit \d+/\d+ 手 (\d+)")


def scan(path: Path):
    txt = path.read_text(encoding="utf-8", errors="ignore")
    dec = [int(x) for x in RE_HAND_DEC.findall(txt)]
    burns = RE_BURN.findall(txt)
    m = RE_OPEN.search(txt)
    opening = (int(m.group(1)), int(m.group(2))) if m else (0, 0)
    return {
        "dec_max": max(dec) if dec else 0,
        "dec_n": len(dec),
        "all_max": max(dec + [int(b[2]) for b in burns]) if (dec or burns) else 0,
        "burns": len(burns),
        "burn_sides": Counter(b[1] for b in burns),
        "burn_pre": Counter(int(b[2]) for b in burns),
        "opening": opening,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--logs", required=True)
    ap.add_argument("--tag", default="")
    ap.add_argument("--glob", default="*.log")
    a = ap.parse_args()

    root = Path(a.logs)
    files = sorted(root.glob(a.glob))
    if not files:
        raise SystemExit(f"{root} 下没有 {a.glob}")

    L = []
    p = L.append
    p("=" * 118)
    p(f"手牌上限验证  {a.tag}   目录 {a.logs}   局数 {len(files)}")
    p("=" * 118)

    rows = {f.name: scan(f) for f in files}

    dec_hist = Counter(r["dec_max"] for r in rows.values())
    over = {k: v for k, v in rows.items() if v["dec_max"] > 9}
    all_over = {k: v for k, v in rows.items() if v["all_max"] > 9}

    p("")
    p(f"{'NN 侧最大手牌':>14}{'局数':>8}")
    for h in sorted(dec_hist):
        mark = "  ← 超过 9！" if h > 9 else ""
        p(f"{h:>14}{dec_hist[h]:>8}{mark}")

    p("")
    p(f"  全日志里 NN 决策行 `手牌 N` 的最大值 = {max((r['dec_max'] for r in rows.values()), default=0)}")
    p(f"  含烧牌行 `（N/9）` 里的 preQtyInHand 后，全部读到的最大手牌 = "
      f"{max((r['all_max'] for r in rows.values()), default=0)}")
    p(f"  最大手牌 > 9 的局数（NN 决策行口径） = {len(over)} / {len(rows)}")
    p(f"  最大手牌 > 9 的局数（含烧牌行口径）   = {len(all_over)} / {len(rows)}")
    if over:
        p("  超 9 的局：")
        for k, v in sorted(over.items()):
            p(f"    {k}  max={v['dec_max']}")
    if all_over:
        p("  含烧牌行超 9 的局：")
        for k, v in sorted(all_over.items()):
            p(f"    {k}  max={v['all_max']}")

    tb = sum(r["burns"] for r in rows.values())
    sides = Counter()
    pres = Counter()
    for r in rows.values():
        sides.update(r["burn_sides"])
        pres.update(r["burn_pre"])
    p("")
    p(f"  烧牌行总数 = {tb}（{tb / len(rows):.2f} 行/局）")
    p(f"  按侧：{dict(sides)}")
    p(f"  括号里 preQtyInHand 的取值分布：{dict(sorted(pres.items()))}")
    p(f"  有烧牌行的局数 = {sum(1 for r in rows.values() if r['burns'] > 0)} / {len(rows)}")
    p(f"  开局手牌（L,R）分布：{dict(Counter(r['opening'] for r in rows.values()))}")

    txt = "\n".join(L)
    print(txt)
    return 0


if __name__ == "__main__":
    sys.exit(main())
