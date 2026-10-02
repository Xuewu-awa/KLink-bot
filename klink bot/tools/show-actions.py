"""把某局回放的动作流按行打印出来（诊断用）。"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

path = sys.argv[1]
acts = json.load(open(path, encoding="utf-8"))["actions"]
for x in acts:
    ad = x["action_data"]
    keep = {k: v for k, v in ad.items() if k not in ("side", "reason")}
    body = json.dumps(keep, ensure_ascii=False)
    print("%3d T%-3s %-8s %-22s %s" % (
        x["action_id"], x["turn_number"], x["player_id"], x["action_type"], body))
