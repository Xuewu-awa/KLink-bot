"""检查回放快照的原始 JSON 结构（PowerShell 的 ConvertFrom-Json 会折叠数组）。"""
import json
import sys
from pathlib import Path

p = Path(sys.argv[1] if len(sys.argv) > 1
         else Path(__file__).resolve().parent.parent / "docs" / "live-replays" / "replay-310284.json")

d = json.loads(p.read_text(encoding="utf-8-sig"))
sd = d["starting_info"]["match_and_starting_data"]["starting_data"]

for k in ("location_card_left", "starting_hand_left", "deck_left", "deck_right"):
    v = sd.get(k)
    kind = type(v).__name__
    n = len(v) if isinstance(v, list) else "-"
    print(f"--- {k}  ({kind}, len={n}) ---")
    if isinstance(v, list):
        for c in v[:3]:
            print("   ", json.dumps(c, ensure_ascii=False))
        print(f"    ... 共 {len(v)} 张")
    else:
        print("   ", json.dumps(v, ensure_ascii=False))
    print()
