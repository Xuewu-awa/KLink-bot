# -*- coding: utf-8 -*-
"""从修复后的分类表生成 `out/audit/没修的.md`（本轮**没做**的缺口清单）。

为什么要有这份清单：`CardApiDispatch.cs` 里有一条注释指到这里
（`// ["ShouldGotchaTrigger"] = …（见 out/audit/没修的.md）`）——
「我故意没注册」这个决定必须留下书面理由，否则下一个人会以为那是漏掉的。
"""
import json, os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)
rows = json.load(open('out/audit/missing-keys-classify2-after.json', encoding='utf-8'))
gaps = [r for r in rows if r['realCalls'] > 0]

o = []
o.append('# 本轮**没修**的缺口清单（2026-10-02）')
o.append('')
o.append('数据来源：`out/audit/missing-keys-classify2-after.json`'
         '（判据见 `out/audit/missing-keys-classify2.py` 的模块注释，'
         '总报告见 `out/audit/missing-keys-report.md`）。')
o.append('')
o.append('口径：**真缺口调用点** = 派发表没有 **且** 调用方那张卡的 `locals` 里也没有'
         '（locals 兜底见 `KismetVm.cs:583`）。')
o.append('')
o.append(f'当前真缺口合计 **{len(gaps)} 种 / {sum(r["realCalls"] for r in gaps)} 个调用点**；'
         f'冻结基线在 `tools/BotSim/DispatchGap.cs`（540 种，只降不升）。')
o.append('')

for c, title in (('A', '(A) 纯 UI / 战役 / 表现层 —— **对局无影响，不打算修**'),
                 ('C', '(C) 玩法相关 —— 分「有理由不做」与「下一轮首选」两档')):
    sub = sorted((r for r in gaps if r['class'] == c), key=lambda r: -r['realCalls'])
    o.append(f'## {title}：{len(sub)} 种 / {sum(r["realCalls"] for r in sub)} 调用点')
    o.append('')
    if c == 'A':
        o.append('这些键的调用者全是 UI 蓝图 / 战役卡，天梯对局里不会走。')
        o.append('（完整清单见 `missing-keys-classify2-after.txt` 的 (A) 段，'
                 '这里只列前 40 个。）')
        o.append('')
        o.append('| 键 | 真缺口调用点 | 判据 |')
        o.append('|---|---:|---|')
        for r in sub[:40]:
            o.append(f'| `{r["fn"]}` | {r["realCalls"]} | {r["rule"]} |')
    else:
        o.append('| 键 | 真缺口调用点 | 涉及卡 | 判据 | 为什么没做 |')
        o.append('|---|---:|---:|---|---|')
        reasons = {
            'GotchaTriggered': '**故意不做**：Gotcha 是整条子系统（`gotchaActivated` 状态位 + `RearrangeLocation` + `SetCardsSeenByCipher` + Covert 揭示位 + cipher/intel），本内核一个都没建模；半吊子实现比不实现更糟',
            'ShouldGotchaTrigger': '**故意不注册**：`gotcha` 标记在本内核没有写入方，注册成 `false` 只会把 ⑥ 的计数刷绿而行为不变',
            'IsGotcha': '同 `ShouldGotchaTrigger`（`covert && gotcha`，`gotcha` 未建模）',
            'MakeCardRetreat': '要新增「退回手牌」原语并接好 `OnAfterLeaveBoard` / 前线归属重算；可照 `DestroyCard` 的结构做，本轮时间不够',
            'FullyHealCard': '相对简单（`HealCard(card, MaxDefense - Defense)` + out `HealedAmount`），但 `MaxDefense` 是否含 buff 要先确认 —— **下一轮首选**',
            'ConvertCard': '函数体 400+ 行（`CreateCard` + 牌库/手牌/场上三来源 + `OnOtherCardConverted` + salvaged 结构体），风险最高',
        }
        for r in sub:
            why = reasons.get(r['fn'], '')
            if not why:
                if r['rule'] == 'C-easy-ref':
                    why = '参考实现里有 ⇒ 语义已查明，**下一轮批量做的首选**'
                elif r['rule'] == 'C-easy-wrap':
                    why = '纯包装（只调已注册原语）⇒ 写层适配即可'
                elif r['fn'].startswith('Campaign'):
                    why = '其实是战役模式（天梯不走）⇒ 归 (A) 更合适，脚本因名字含玩法动词误归 C'
                else:
                    why = '待排期'
            o.append(f'| `{r["fn"]}` | {r["realCalls"]} | {r["realCards"]} | {r["rule"]} | {why} |')
    o.append('')

o.append('## 下一轮的推荐顺序（按「成本 ÷ 收益」）')
o.append('')
o.append('1. **`C-easy-ref` / `C-easy-wrap` 批量做**（~40 种，语义都有出处）：'
         '`getCardsBuffedByThisCard`(25) / `SetCountdown`(15) / `getKreditTempBuffAmount`(11) / '
         '`RemovePin`(10) / `getAttackTempBuffAmount`(9) / `GetCardsPlayedFromHandLastTurn`(10) / '
         '`GetSupportLineLocationBySide`(19) / `DiscardRandomCardFromHand`(20) / `LoseKreditSlot`(16) …')
o.append('2. **`FullyHealCard`(33)** —— 最简单的一个「真实现」。')
o.append('3. **`MakeCardRetreat`(36) + `GetAllCardsInFrontline`(8)** —— 一起做，结构照 `DestroyCard`。')
o.append('4. **Gotcha 子系统**（`GotchaTriggered` 54 + `ShouldGotchaTrigger` 53 + `IsGotcha` 15）'
         '—— 收益最大但要做**状态机**（Covert 揭示位 + cipher），单独排一轮。')
o.append('5. **`ConvertCard`(26)** —— 最后做，函数体最长。')
o.append('')
o.append('⚠️ 不管做哪个，**做完都要更新 `tools/BotSim/DispatchGap.cs` 的两个基线常量**'
         '（`dotnet run --project tools\\BotSim -c Release -- dispatch-gap`），'
         '否则守卫会（正确地）失败。')

open('out/audit/没修的.md', 'w', encoding='utf-8').write('\n'.join(o))
print('\n'.join(o[:14]))
print(f'\n→ out/audit/没修的.md（{len(o)} 行）')
