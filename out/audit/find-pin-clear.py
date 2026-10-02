#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
取证：`Pinned` / `Suppressed` 到底在什么时机被清除？

两份资料打架：
  · `klink bot/docs/KARDS基础规则参考.md`（社区整理）：
      「压制效果于单位所有者**下个回合结束时**移除。」（只说了压制）
  · `klink bot/docs/模拟器进度基线.md`（引 kards-sim）：
      「**Pinned / Suppressed** 在**该方回合结束时**清除」

只有蓝图/字节码能定案。思路：在 `BP_GameState_Battle` 与 `BP_Logic` 里找
「把 isPinned / isSuppressed 置回 false」的语句，看它挂在哪个函数上、
以及它循环的是「当前回合方」还是「所有卡」。
"""
import json
import re
from pathlib import Path

# 候选文件：全量函数转储
SRC = [
    ('klink bot/out/bp-logic.json', 'bp-logic'),
    ('out/bp-cardfn.json', 'bp-cardfn'),
    ('out/bp-gamestate.json', 'bp-gamestate'),
]
# 也扫 cards.full.json 里的 BP_ 资产
FULL = 'klink bot/decompiled/cards.full.json'

NEEDLES = ('isPinned', 'isSuppressed', 'Pinned', 'Suppressed')


def scan_json(path, label, needles):
    try:
        raw = Path(path).read_text(encoding='utf-8')
    except OSError:
        return None
    try:
        o = json.loads(raw)
    except Exception:
        return None
    hits = []
    # 结构未知，递归找 {函数名: 语句数组}
    def walk(node, path_):
        if isinstance(node, dict):
            for k, v in node.items():
                if isinstance(v, list) and v and isinstance(v[0], dict):
                    blob = json.dumps(v, ensure_ascii=False)
                    if any(n in blob for n in needles):
                        hits.append((k, len(v), blob))
                else:
                    walk(v, f'{path_}/{k}')
        elif isinstance(node, list):
            for i, v in enumerate(node):
                walk(v, f'{path_}[{i}]')
    walk(o, '')
    return hits


def main():
    for path, label in SRC:
        hits = scan_json(path, label, NEEDLES)
        if hits is None:
            print(f'--- {label}: 文件不存在或不是 JSON（{path}）---')
            continue
        print(f'=== {label}（{path}）命中 {len(hits)} 个函数 ===')
        for fn, n, blob in hits[:12]:
            print(f'  fn {fn}  ({n} 条)')
            # 找「写 false」的痕迹
            for m in re.finditer(r'"[^"]*(?:isPinned|isSuppressed|Pinned|Suppressed)[^"]*"', blob):
                print(f'      {m.group(0)}')
        print()

    # cards.full.json 里的 BP_ 资产
    try:
        assets = json.load(open(FULL, encoding='utf-8'))['assets']
    except Exception as e:
        print('cards.full.json 读不到:', e)
        return
    print('=== cards.full.json 里 BP_ 资产中含 Pinned/Suppressed 的函数 ===')
    for asset, body in assets.items():
        stem = asset.split('/')[-1]
        if not stem.startswith(('BP_Logic', 'BP_CardFunctions', 'BP_GameState')):
            continue
        fns = body.get('functions') or {}
        for fn, fb in fns.items():
            blob = json.dumps(fb, ensure_ascii=False)
            if any(n in blob for n in NEEDLES):
                n = len(fb.get('bytecode', [])) if isinstance(fb, dict) else 0
                print(f'  {stem:28s} {fn:44s} {n:5d} 条')


if __name__ == '__main__':
    main()
