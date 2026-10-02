# -*- coding: utf-8 -*-
"""在**全部**回放里找「直接携带 kredit」的 action_data 槽位。

判据（不依赖"花费反推"）：
  · HQ 键：出现最频繁且首个取值是 20（复刻 ReplayData.InferHqKey）。
  · 候选 kredit 槽位：
      (A) **槽位数**：单调不减、步长 <= +2、取值落在 1..24、至少出现 8 次；
      (B) **当前 kredit**：同一回合内出现下降、回合边界回升（锯齿）。
"""
import json, os, sys, collections

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)

DIRS = ['out/_server-replays', 'klink bot/docs/fresh-replays', 'klink bot/docs/live-replays']
seen = set()
files = []
for dd in DIRS:
    if not os.path.isdir(dd):
        continue
    for f in sorted(os.listdir(dd)):
        if f.endswith('.actions.json') and f not in seen:
            seen.add(f)
            files.append(os.path.join(dd, f))

print(f'共 {len(files)} 局\n')


def hq_key(acts):
    counts = collections.Counter()
    first = {}
    for a in acts:
        for k, v in (a.get('action_data') or {}).items():
            if k.isdigit() and k not in ('0', '1', '2', '3', '4'):
                counts[k] += 1
                first.setdefault(k, v)
    cands = [k for k in counts if first[k] == '20']
    if not cands:
        return None
    return max(cands, key=lambda k: (counts[k], -len(k), k))


def as_int(v):
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


for path in files:
    d = json.load(open(path, encoding='utf-8'))
    acts = d['actions']
    hq = hq_key(acts)
    keys = collections.Counter()
    for a in acts:
        for k in (a.get('action_data') or {}):
            if k.isdigit() and k not in ('0', '1', '2', '3', '4'):
                keys[k] += 1
    others = [k for k in keys if k != hq]
    name = os.path.basename(path).replace('.actions.json', '')
    print('=' * 96)
    print(f'### {name}  动作 {len(acts)}  HQ键={hq}  其它键={others}')

    for k in others:
        seq = [(a['action_id'], a['turn_number'], a['action_type'],
                a['player_id'], as_int((a.get('action_data') or {}).get(k)))
               for a in acts if k in (a.get('action_data') or {})]
        vals = [s[4] for s in seq if s[4] is not None]
        if not vals:
            print(f'  key={k}: 非整数取值')
            continue
        # 走势摘要
        inc = all(vals[i] <= vals[i + 1] for i in range(len(vals) - 1))
        rng = (min(vals), max(vals))
        distinct = sorted(set(vals))
        # 找「同一回合内下降」
        drops = 0
        by_turn = collections.defaultdict(list)
        for aid, t, ty, pid, v in seq:
            if v is not None:
                by_turn[(t, pid)].append(v)
        for tv in by_turn.values():
            for i in range(len(tv) - 1):
                if tv[i + 1] < tv[i]:
                    drops += 1
        flag = []
        if inc and rng[1] <= 24 and len(vals) >= 8:
            flag.append('★单调不减且<=24 ⇒ 槽位候选')
        if drops >= 2 and rng[1] <= 24:
            flag.append('★回合内下降 ⇒ 当前kredit候选')
        print(f'  key={k:>3} n={len(vals):>3} 范围={rng} 单调不减={inc} 回合内下降={drops} '
              f'取值={distinct[:14]}{" …" if len(distinct) > 14 else ""}  {" ".join(flag)}')
