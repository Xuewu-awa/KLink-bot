"""把「客户端快照」和「服务器动作流」按对局号对上，检查能否逐动作对齐。

背景：
  · 客户端快照  snapshot-match<NN>.jsonl   每条带 act = 本地已结算动作数
  · 服务器回放  GET /replays/<id>/actions  完整动作流
  · 对局号后两位就是快照文件名的 <NN>（客户端只有 Match_ID_AsTwoDigits）

本脚本只做「对齐检查」，不跑模拟器：
  1. 找出与快照匹配的服务器对局
  2. 取动作流
  3. 把快照的 act 序列和动作流的 action_id 摊在一起看是否自洽

用法: python tools/align-snapshot-replay.py [<NN>]
"""
import glob
import json
import os
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

CAP = os.path.join(os.environ.get("LOCALAPPDATA", "."), "Temp", "klink-capture")
BASE = "http://127.0.0.1:5231"


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=15) as r:
        return json.loads(r.read().decode("utf-8"))


def snapshots():
    out = {}
    for p in glob.glob(os.path.join(CAP, "snapshot-match*.jsonl")):
        nn = os.path.basename(p)[len("snapshot-match"):-len(".jsonl")]
        rows = []
        for line in open(p, encoding="utf-8"):
            line = line.strip()
            if line:
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError:
                    pass
        out[nn] = (p, rows)
    return out


def main():
    try:
        replays = get("/replays?limit=100")["matches"]
    except Exception as e:
        print(f"取不到对局列表: {e}")
        return 1

    snaps = snapshots()
    print(f"本地快照 {len(snaps)} 个   服务器对局 {len(replays)} 局\n")

    only = sys.argv[1] if len(sys.argv) > 1 else None
    for nn, (path, rows) in sorted(snaps.items()):
        if only and nn != only:
            continue
        acts = [r.get("act") for r in rows]
        print("=" * 92)
        print(f"快照 match{nn}   {len(rows)} 条   act 范围 {min(acts)}~{max(acts)}")

        # 按后两位匹配（可能有多个）
        cands = [m for m in replays if str(m["match_id"]).endswith(nn)]
        if not cands:
            print("  ✗ 服务器上没有以此结尾的对局")
            continue

        for m in cands:
            mid = m["match_id"]
            print(f"  → 服务器对局 {mid}   {m['turns']} 回合   {m['action_count']} 动作"
                  f"   胜方={m['winner_side']}   状态={m['status']}")
            try:
                a = get(f"/replays/{mid}/actions?afterActionId=0&limit=2000")["actions"]
            except Exception as e:
                print(f"     取动作失败: {e}")
                continue

            print(f"     动作流 {len(a)} 条")
            types = {}
            for x in a:
                types[x["action_type"]] = types.get(x["action_type"], 0) + 1
            print("     类型分布: " + "  ".join(f"{k}×{v}" for k, v in
                                              sorted(types.items(), key=lambda kv: -kv[1])[:8]))

            # 动作流里的 HQ 采样（字段下标每局不同，用「首值 20、出现最多」推）
            from collections import Counter
            cnt, first = Counter(), {}
            for x in a:
                ad = x.get("action_data") or {}
                for k, v in ad.items():
                    if k.isdigit() and k not in ("0", "1", "2", "3", "4"):
                        cnt[k] += 1
                        first.setdefault(k, v)
            hq = next((k for k, _ in cnt.most_common() if first[k] == "20"), None)
            print(f"     HQ 字段下标 = {hq}")
            if hq:
                seq = [int(x["action_data"][hq]) for x in a if hq in (x.get("action_data") or {})]
                print(f"     HQ 采样 {len(seq)} 个，变化 {sum(1 for i in range(1,len(seq)) if seq[i]!=seq[i-1])} 次")

            # 关键：快照的 act 和动作流的 action_id 是否同量级
            print(f"     对齐检查: 快照 act 最大 {max(acts)}，动作流 action_id 最大 "
                  f"{max(x['action_id'] for x in a)}")
            ids = [x["action_id"] for x in a]
            hit = sum(1 for x in acts if x in ids)
            print(f"     快照的 act 值落在 action_id 集合里的: {hit}/{len(acts)}")
        print()

    return 0


if __name__ == "__main__":
    sys.exit(main())
