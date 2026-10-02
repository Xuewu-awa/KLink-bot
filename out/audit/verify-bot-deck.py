#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
验证 fyserver 里那副**硬编码的 bot 卡组**能不能被正确解析。

出处：`tem/fyserver/Services/MatchManagerService.cs:500`
    deck_code = "%%21|4v32323232sTgv0z0C0C0C0CoBoBoBoB0Y0Y101010hShShShS1902020202030303ououpRpRpRsU;;;~;;;|0N1b"

服务端的解析逻辑（`GetCardsFromDeck`）：
    · HQ 卡 = `deckCode[^4..^2]`，即**倒数第 4~3 位那两个字符**（这里是 "0N"）
    · 牌 = `deckCode.Remove(0,5)` 之后按 ';' 分 4 组，第 index 组重复 index+1 次，
      每 2 字符一个码

⚠️ 这是**接真对局最关键的前置**：HQ 的 cardID 如果认不出来，
   内核重建局面时就没有 HQ，一切对不上。

输出：码 → 卡名，以及有没有未知码。
"""
import json
import sys
from pathlib import Path

DECK_CODE = "%%21|4v32323232sTgv0z0C0C0C0CoBoBoBoB0Y0Y101010hShShShS1902020202030303ououpRpRpRsU;;;~;;;|0N1b"


def load_table():
    """内核用哪份就用哪份（BotData 里那份 = deck_code_ids.live.json）。"""
    for p in ('tem/fyserver/bin/Release/net10.0/BotData/deck_code_ids.json',
              'klink bot/docs/deck_code_ids.live.json',
              'tem/fyserver/library/deckCodeIDsTable2.json'):
        path = Path(p)
        if not path.exists():
            continue
        d = json.load(open(path, encoding='utf-8'))
        table = {}
        if isinstance(d, dict):
            for k, v in d.items():
                table[k] = v if isinstance(v, str) else (v.get('card') or v.get('name'))
        elif isinstance(d, list):
            for it in d:
                table[it.get('deck_code_id')] = it.get('card')
        if table:
            print(f'用表: {p}  （{len(table)} 条）')
            return table
    raise SystemExit('找不到卡组码表')


def main():
    table = load_table()
    code = DECK_CODE
    print(f'卡组码: {code}')
    print(f'  长度 {len(code)}')

    # --- HQ：倒数第 4~3 位 ---
    hq_key = code[-4:-2]
    hq_name = table.get(hq_key)
    print(f'  HQ 码 = "{hq_key}"  →  {hq_name}')

    # --- 主体 ---
    body = code[5:]
    groups = body.split(';')[:4]
    print(f'  主体分组 {len(groups)} 组：' + ' | '.join(repr(g[:20]) for g in groups))

    total = 0
    unknown = []
    names = []
    for idx, g in enumerate(groups):
        chunks = [g[i:i + 2] for i in range(0, len(g), 2)]
        for ch in chunks:
            if len(ch) != 2:
                continue
            name = table.get(ch)
            if not name:
                unknown.append(ch)
                continue
            for _ in range(idx + 1):
                names.append(name)
                total += 1

    print(f'  解析出牌 {total} 张（+HQ 1 张 = {total + 1}）')
    print(f'  未知码 {len(unknown)} 个' + (f'：{unknown[:12]}' if unknown else ''))

    from collections import Counter
    print('  重复最多的：')
    for n, c in Counter(names).most_common(6):
        print(f'     {c}× {n}')

    # 期望 39 张（含 HQ）
    ok = (total + 1) == 39 and not unknown
    print()
    print('✅ 解析正常（39 张、零未知码）' if ok else f'⚠️ 有问题：总 {total + 1} 张、未知 {len(unknown)} 个')


if __name__ == '__main__':
    main()
