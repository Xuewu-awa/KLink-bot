"""
校验「快照 ↔ 回放」的配对是不是真的同一局。

原来的配对判据只是「act 序号覆盖率 ≥95%」，而两局回合数接近时序号集合本来就很像，
104/104、99/99 完全可能是**巧合**。配对错的话，后面所有对拍数字都是假的，
连 DetectFlip 都会跟着错（它就在这个配对好的快照上数卡名）。

这里改用**卡名多重集**比对：同一局的话，快照 act=1 的 78 张卡名（含重复）
必须与回放起始数据里的卡名多重集一致。
"""
import json
import glob
import os
from collections import Counter

CAP = r"<user-home>\AppData\Local\Temp\klink-capture"
REP = r"klink bot\docs\fresh-replays"

SNAPSHOT_NAMES = {
    51: "snapshot-match51.jsonl", 28: "snapshot-match28.jsonl",
    40: "snapshot-match40.jsonl", 19: "snapshot-match19.jsonl",
    64: "snapshot-match64.jsonl", 82: "snapshot-match82.jsonl",
}


def snap_name_counter(path):
    for line in open(path, encoding="utf-8"):
        if not line.strip():
            continue
        r = json.loads(line)
        if r["act"] != 1:
            continue
        c = Counter()
        for card in r["cards"]:
            n = card.get("Name")
            if n and n != "None" and not n.startswith("card_location_"):
                if card.get("location") != "0":
                    c[n] += 1
        return c
    return Counter()


def find_card_list(obj, depth=0):
    """在回放 JSON 里找「一列带名字的卡」—— 结构不确定，所以按形状找。"""
    best = None
    if isinstance(obj, dict):
        for k, v in obj.items():
            r = find_card_list(v, depth + 1)
            if r and (best is None or len(r) > len(best)):
                best = r
    elif isinstance(obj, list) and obj and isinstance(obj[0], dict):
        names = Counter()
        for it in obj:
            for key in ("name", "Name", "card_name", "cardName", "id"):
                v = it.get(key)
                if isinstance(v, str) and v.startswith("card_"):
                    names[v] += 1
                    break
        if names:
            best = names
    return best or Counter()


print("=== 每局：快照 act=1 卡名 vs 回放起始卡名 ===")
for rid_file in sorted(glob.glob(os.path.join(REP, "replay-*.json"))):
    if rid_file.endswith(".actions.json"):
        continue
    rid = os.path.basename(rid_file).replace("replay-", "").replace(".json", "")
    rep = json.load(open(rid_file, encoding="utf-8"))
    rep_names = find_card_list(rep)

    row = [f"replay-{rid}  回放卡名 {sum(rep_names.values())} 张 / {len(rep_names)} 种"]
    best, best_score = None, -1.0
    for mid, fn in SNAPSHOT_NAMES.items():
        p = os.path.join(CAP, fn)
        if not os.path.exists(p):
            continue
        sn = snap_name_counter(p)
        if not sn:
            continue
        inter = sum((sn & rep_names).values())
        union = sum((sn | rep_names).values())
        jac = inter / union if union else 0.0
        if jac > best_score:
            best, best_score = mid, jac
    row.append(f"最佳配对 match{best}  Jaccard {best_score:.3f}")
    print("  " + "  |  ".join(row))

print()
print("=== 全部候选的 Jaccard（越接近 1 越可能是同一局）===")
for rid_file in sorted(glob.glob(os.path.join(REP, "replay-*.json"))):
    if rid_file.endswith(".actions.json"):
        continue
    rid = os.path.basename(rid_file).replace("replay-", "").replace(".json", "")
    rep_names = find_card_list(json.load(open(rid_file, encoding="utf-8")))
    cells = []
    for mid, fn in sorted(SNAPSHOT_NAMES.items()):
        p = os.path.join(CAP, fn)
        sn = snap_name_counter(p) if os.path.exists(p) else Counter()
        inter = sum((sn & rep_names).values())
        union = sum((sn | rep_names).values())
        cells.append(f"m{mid}={inter / union if union else 0:.2f}")
    print(f"  replay-{rid:<8} " + "  ".join(cells))
