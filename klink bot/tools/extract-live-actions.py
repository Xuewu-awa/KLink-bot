"""从 fyserver 控制台日志里抽出真实客户端动作，还原线上协议。

日志形如：
    [23:34:22] [fyserver] 解密结果：{"action_type": "PC", "player_id": 892257,
        "action_data": {"0":"64","1":"3","2":"0","3":"0","4":"jJ","84":"11"},
        "action_id": 49, "local_subactions": 1}

这是**唯一能证明内核协议理解是否正确的东西**，所以把它整理成结构化数据。

用法：
    python extract-live-actions.py <日志> [输出.json]
"""

import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent

log_path = Path(sys.argv[1])
out_path = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "docs" / "live-actions.json"

text = log_path.read_text(encoding="utf-8", errors="replace")

actions = []
for m in re.finditer(r'解密结果：(\{.*?\})\s*$', text, re.MULTILINE):
    raw = m.group(1)
    try:
        obj = json.loads(raw)
    except Exception:
        continue
    actions.append(obj)

print(f"抽出解密动作: {len(actions)}")

# ---- 动作类型分布 ----
by_type = Counter(a.get("action_type") for a in actions)
print()
print("action_type 分布:")
for t, n in by_type.most_common():
    print(f"  {t:<24} {n}")

# ---- 每种动作的 action_data 键形状 ----
shapes = defaultdict(Counter)
for a in actions:
    t = a.get("action_type")
    ad = a.get("action_data") or {}
    shapes[t][tuple(sorted(ad.keys(), key=lambda s: int(s) if s.isdigit() else 999))] += 1

print()
print("各动作的 action_data 键:")
for t in by_type:
    print(f"  {t}:")
    for keys, n in shapes[t].most_common(4):
        print(f"      {list(keys)}  ×{n}")

# ---- 值域：哪些键装的是卡组码 ----
deck_ids = {}
ids_file = ROOT / "docs" / "deck_code_ids.live.json"
if ids_file.exists():
    deck_ids = json.loads(ids_file.read_text(encoding="utf-8"))

print()
print("各键的取值样本（判断哪个键是卡组码 / 哪个是数值）:")
key_values = defaultdict(Counter)
for a in actions:
    for k, v in (a.get("action_data") or {}).items():
        key_values[k][str(v)] += 1

for k in sorted(key_values, key=lambda s: int(s) if s.isdigit() else 999):
    vals = key_values[k]
    looks_like_code = sum(n for v, n in vals.items() if v in deck_ids)
    sample = ", ".join(f"{v}×{n}" for v, n in vals.most_common(6))
    tag = f"  ← 其中 {looks_like_code} 次命中卡组码表" if looks_like_code else ""
    print(f"  [{k:>3}] {sample}{tag}")

# ---- 输出 ----
out = {
    "source_log": str(log_path),
    "action_count": len(actions),
    "action_types": dict(by_type),
    "actions": actions,
}
out_path.parent.mkdir(parents=True, exist_ok=True)
out_path.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
print()
print(f"已写出: {out_path}")
