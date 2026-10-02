"""打印回放里的动作序列（人类可读）。

用法：python dump-replay-actions.py <replay-xxx.actions.json> [起始] [条数]
"""
import json
import sys
from pathlib import Path

p = Path(sys.argv[1])
start = int(sys.argv[2]) if len(sys.argv) > 2 else 0
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 40

d = json.loads(p.read_text(encoding="utf-8-sig"))
arr = d.get("actions", d if isinstance(d, list) else [])
print(f"动作总数: {len(arr)}")
print()

for a in arr[start:start + limit]:
    if not isinstance(a, dict):
        continue
    t = a.get("action_type")
    ad = a.get("action_data")
    sub = a.get("sub_actions")
    val = a.get("value")
    act = a.get("action")
    line = "[{:>3}] T{:<3} pid={:<7} {:<22}".format(
        a.get("action_id", 0), a.get("turn_number", 0), a.get("player_id", 0), str(t))
    if ad:
        line += " data=" + json.dumps(ad, ensure_ascii=False)
    if act:
        line += " action=" + str(act)
    if val:
        line += " value=" + json.dumps(val, ensure_ascii=False)
    print(line)
    if sub:
        print("        sub_actions=" + json.dumps(sub, ensure_ascii=False)[:300])
