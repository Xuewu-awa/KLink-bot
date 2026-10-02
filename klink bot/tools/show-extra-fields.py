"""打印每局回放里那个"额外状态字段"（不是 0..4 的键）的取值轨迹。

目的：判断它是不是 HQ 防御力（会出现 20 递减）。
"""
import glob
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

for path in sorted(glob.glob("docs/live-replays/replay-*.actions.json")):
    acts = json.load(open(path, encoding="utf-8"))["actions"]
    extra = {}
    for x in acts:
        ad = x.get("action_data") or {}
        for k, v in ad.items():
            if k not in ("0", "1", "2", "3", "4", "side", "reason", "winner_side", "playerID"):
                extra.setdefault(k, []).append(v)
    print("### %s" % path.split("\\")[-1])
    for k, vals in extra.items():
        print("   key %-3s  n=%-3d  取值: %s" % (k, len(vals), " ".join(map(str, vals))))
    if not extra:
        print("   （没有额外字段）")
