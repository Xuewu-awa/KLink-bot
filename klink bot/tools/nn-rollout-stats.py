"""从 NNPlay 日志里抽出行为统计（本轮 vs 上一轮对照用）。

用法：python -X utf8 "klink bot/tools/nn-rollout-stats.py" out/nn-rollout.log out/nn-rollout-v1.log out/nn-rollout-v1b.log
"""

import re
import sys
from collections import Counter, defaultdict

DEC = re.compile(r"🧠 决策 #(\d+)\s+\[T(\d+)/(\w+)\]\s+kredit (\d+)/(\d+)\s+手牌 (\d+)\s+场面 (\d+)\s+HQ (\d+)/(\d+)")
VAL = re.compile(r"当前局面估值 P\((\w+) 胜\) = ([\d.]+)%")
CHO = re.compile(r"→ 选 \[([\d.]+)%\] (.+)$")
ACT = re.compile(r"^#\s*(\d+) \[T(\d+)/(\w+)\] (NN|对手)\s+(.+)$")
RES = re.compile(r"模型平均自评: 决策前 ([\d.]+)%，选中后 ([\d.]+)%")
END = re.compile(r"选中「结束回合」(\d+) 次/(\d+) 次决策")
WIN = re.compile(r"胜负      : (.+)$")


def kind(a):
    for k in ("出牌", "攻击", "移动", "结束", "部署"):
        if a.startswith(k):
            return k
    return a.split()[0] if a.split() else "?"


def main():
    for path in sys.argv[1:]:
        txt = open(path, encoding="utf-8").read()
        lines = txt.splitlines()
        print("=" * 88)
        print(f"{path}")
        print("=" * 88)
        for ln in lines:
            m = WIN.search(ln)
            if m:
                print(f"  结果: {m.group(1)}")
            m = RES.search(ln)
            if m:
                print(f"  自评: 决策前 {m.group(1)}%  选中后 {m.group(2)}%")
            m = END.search(ln)
            if m:
                print(f"  选「结束回合」{m.group(1)}/{m.group(2)} 次决策")

        # 决策块：每块从「🧠 决策」到「→ 选」
        blocks = []
        cur = None
        for ln in lines:
            m = DEC.search(ln)
            if m:
                cur = {"turn": int(m.group(2)), "hand": int(m.group(6)),
                       "board": int(m.group(7)), "hq": (int(m.group(8)), int(m.group(9))),
                       "val": None, "choice": None}
                blocks.append(cur)
                continue
            if cur is not None:
                m = VAL.search(ln)
                if m:
                    cur["val"] = float(m.group(2))
                m = CHO.search(ln)
                if m:
                    cur["choice"] = m.group(2)
                    cur = None

        # NN 的实际动作序列（按回合）
        nn_by_turn = defaultdict(list)
        for ln in lines:
            m = ACT.match(ln)
            if m and m.group(4) == "NN":
                nn_by_turn[int(m.group(2))].append(kind(m.group(5)))

        print(f"  NN 出手回合数: {len(nn_by_turn)}")
        first = Counter()
        for t, acts in sorted(nn_by_turn.items()):
            first[acts[0]] += 1
        print(f"  每个 NN 回合的**第一个动作**分布: {dict(first)}")
        tot = Counter()
        for acts in nn_by_turn.values():
            tot.update(acts)
        print(f"  NN 动作合计: {dict(tot)}")
        idle = sum(1 for acts in nn_by_turn.values() if acts == ["结束"])
        print(f"  整回合什么都没做（唯一动作是结束回合）的回合数: {idle}/{len(nn_by_turn)}")
        # 决策里「结束回合」被选中时的排位
        ed = [b for b in blocks if b["choice"] and kind(b["choice"]) == "结束"]
        print(f"  决策 {len(blocks)} 次；其中 {len(ed)} 次选了结束回合")
        if ed:
            print(f"    这些决策的手牌数分布: {dict(Counter(b['hand'] for b in ed))}")
        # 早期决策
        print("  前 4 次决策:")
        for b in blocks[:4]:
            print(f"    T{b['turn']} 手{b['hand']} 场{b['board']} 估值{b['val']}% → {b['choice']}")
        print()


if __name__ == "__main__":
    main()
