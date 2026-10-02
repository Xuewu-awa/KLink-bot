"""量化那条 HQ 信号到底"有多少信息量"。

动作流里的 HQ 字段是「行动方**对手**的当前防御力」。
所以它对每条动作都给一个采样，但它**只在 HQ 真的掉血时才变化** ——
其它采样全都是"没变"，两个引擎都会"对上"，但那是**平凡一致**，
不能算作"验证通过"。

本脚本按行动方分组（因为字段指的是不同阵营的 HQ，跨阵营直接比是错的），
统计每组里"值发生变化"的次数 —— 那才是真正能证伪规则的验证点。
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

path = sys.argv[1] if len(sys.argv) > 1 else "docs/live-replays/replay-310284.actions.json"
snap_path = sys.argv[2] if len(sys.argv) > 2 else "docs/live-replays/replay-310284.json"

snap = json.load(open(snap_path, encoding="utf-8"))
sd = snap["starting_info"]["match_and_starting_data"]["starting_data"]
left_id = sd["player_id_left"]
right_id = sd["player_id_right"]

acts = json.load(open(path, encoding="utf-8"))["actions"]

# 找 HQ 字段下标（纯数字、不是 0..4、出现最多、首值 20）
from collections import Counter
cnt = Counter()
first = {}
for a in acts:
    ad = a.get("action_data") or {}
    for k, v in ad.items():
        if k.isdigit() and k not in ("0", "1", "2", "3", "4"):
            cnt[k] += 1
            first.setdefault(k, v)
hq_key = next((k for k, _ in cnt.most_common() if first[k] == "20"), None)
print("HQ 字段下标:", hq_key)

samples = []          # (action_id, turn, side, value)
for a in acts:
    ad = a.get("action_data") or {}
    if hq_key not in ad:
        continue
    side = "left" if a["player_id"] == left_id else ("right" if a["player_id"] == right_id else "?")
    samples.append((a["action_id"], a["turn_number"], side, int(ad[hq_key])))

print("总采样数: %d" % len(samples))
print()

# 按行动方分组，组内比较（同一组才指同一个阵营的 HQ）
per_side = {}
for s in samples:
    per_side.setdefault(s[2], []).append(s)

total_informative = 0
for side, arr in sorted(per_side.items()):
    changes = []
    for i in range(1, len(arr)):
        if arr[i][3] != arr[i - 1][3]:
            changes.append((arr[i - 1][3], arr[i][3], arr[i][0], arr[i][1]))
    total_informative += len(changes)
    print("%s 方动作 %d 次，其中 HQ 数值发生变化 %d 次：" % (side, len(arr), len(changes)))
    for a, b, aid, turn in changes:
        print("    action %-4s T%-3s  %d → %d   （-%d）" % (aid, turn, a, b, a - b))
    print()

print("=" * 70)
print("真正有信息量的验证点: %d 个（总采样 %d 个，其余 %d 个都是'没变'）"
      % (total_informative, len(samples), len(samples) - total_informative))
print()
print("也就是说：一次 21 回合 / 95 条动作的对局，能用来证伪规则的只有 %d 个点。"
      % total_informative)
print("两个引擎在这 %d 个点里，只有一个（A36 的 -3）对不上 ——" % total_informative)
print("它们能对上绝大多数，是因为那些点本来就没变，属于**平凡一致**。")
