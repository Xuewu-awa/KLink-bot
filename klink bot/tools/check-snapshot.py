"""用客户端真实快照校验卡库与初始状态。

为什么这一步最该先做：
  · 快照里的 `act` 是本地已结算动作数，act 最小的那条就是**初始状态**
  · 它带着**真实的手牌/牌库划分** —— 这正是之前一直拿不到的东西
    （fyserver 的手牌划分不可信，我用的是"按需注入手牌"的权宜之计）
  · 每张卡的攻/防/费用/关键字都在里面，可以直接和我的卡库对拍

用法:
  python tools/check-snapshot.py                 # 扫 %TEMP%\\klink-capture 下全部快照
  python tools/check-snapshot.py <文件>          # 只看一个
"""
import glob
import json
import os
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

CAP = os.path.join(os.environ.get("LOCALAPPDATA", "."), "Temp", "klink-capture")

# location 枚举 —— 从快照实测值反推（5=左HQ 6=右HQ 3=左手 4=右手 …）
LOC_HINT = {
    "1": "牌库?", "2": "?", "3": "左手", "4": "右手", "5": "左HQ",
    "6": "右HQ", "7": "前线?", "8": "弃牌", "9": "牌库?",
}

# 我的卡库（用于对拍数值）
MINE = json.load(open("docs/cards.live.json", encoding="utf-8"))
# kards-sim 的卡库（含 has* 位，作为第二参照）
try:
    THEIRS = {c["id"]: c for c in json.load(
        open(r"..\ref\kards-sim\cards.json", encoding="utf-8"))}
except Exception:
    THEIRS = {}


def load(path):
    rows = []
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return rows


def main():
    if len(sys.argv) > 1:
        files = sys.argv[1:]
    else:
        files = glob.glob(os.path.join(CAP, "snapshot-match*.jsonl"))
    if not files:
        print(f"没找到快照。目录: {CAP}")
        return 1

    for path in files:
        rows = load(path)
        if not rows:
            print(f"\n### {os.path.basename(path)}  （空文件，跳过）")
            continue

        print("=" * 96)
        print(f"### {os.path.basename(path)}   快照 {len(rows)} 条"
              f"   动作序号 {[r.get('act') for r in rows]}")

        # 最早的一条 = 初始状态
        first = min(rows, key=lambda r: r.get("act", 0))
        cards = [c for c in first.get("cards", []) if c.get("Name")]
        print(f"\n--- 初始状态（act={first.get('act')}）：{len(cards)} 张卡 ---")

        by_loc = defaultdict(list)
        for c in cards:
            by_loc[c.get("location", "?")].append(c)
        print("\n区域分布:")
        for loc in sorted(by_loc, key=lambda x: (len(x), x)):
            names = by_loc[loc]
            uniq = len({c["Name"] for c in names})
            print(f"  location={loc:<3} ({LOC_HINT.get(loc,'?'):<6}) "
                  f"{len(names):>3} 张，{uniq} 种")

        # HQ
        print("\nHQ:")
        for c in cards:
            if c.get("Name", "").startswith("card_location"):
                print(f"  cardID={c.get('cardID'):<4} defense={c.get('defense'):<4} "
                      f"location={c.get('location')} (owner={str(c.get('owner'))[:28]})")

        # 数值对拍
        print("\n--- 卡库对拍 ---")
        miss, diff = [], []
        for c in cards:
            n = c["Name"]
            m = MINE.get(n)
            if m is None:
                miss.append(n)
                continue
            for field, key in (("attack", "attack"), ("defense", "defense"),
                               ("kredits", "kredits")):
                try:
                    a = int(c.get(field) or 0)
                except ValueError:
                    continue
                b = int(m.get(key) or 0)
                if a != b:
                    diff.append((n, field, a, b))
        print(f"  不在我的卡库: {len(set(miss))} 种" +
              (f"  → {sorted(set(miss))[:8]}" if miss else ""))
        print(f"  数值不一致:   {len(diff)} 处")
        for n, f, a, b in diff[:12]:
            print(f"      {n:<42} {f:<8} 游戏={a}  我的={b}")

        # 关键字位抽查（用 kards-sim 的 has* 做第二参照）
        if THEIRS:
            bad = 0
            for c in cards:
                t = THEIRS.get(c["Name"])
                if not t:
                    continue
                raw = t.get("raw") or {}
                g = c.get("hasBlitz")
                if g is not None and raw.get("hasBlitz") is not None:
                    if (g == "true") != bool(raw.get("hasBlitz")):
                        bad += 1
            print(f"  hasBlitz 与 kards-sim 不一致: {bad} 处")

        # 全部动作的规模，便于判断采集是否完整
        print(f"\n--- 各快照规模 ---")
        for r in rows[:12]:
            print(f"  act={str(r.get('act')):<4} count={r.get('count')}")
        print()

    return 0


if __name__ == "__main__":
    sys.exit(main())
