"""`GetDeckByside` 的全部调用点与用法清单（2026-10-02）。

用途：改 `GetDeckBySide` 的返回形状之前，先证明"元素是卡 ID，不是卡实例"。

结论（跑一遍就能复现）：
  * 46 张卡的 IR 里出现 `GetDeckByside`；
  * `Array_Get(deckCardIDs, i)` 出来的元素，消费者**全部**是整数语义：
      GetCardFromID / Greater_IntInt / JSON_SetInt / DiscardCardFromDeck /
      DrawSpecificCardFromDeckBySide / RandomIntFromRangeWithStream / Array_Resize …
  * **没有一个**消费者把元素当卡实例（当接收者、当卡参数）。

用法：`python out/audit/p2-getdeckbyside-callers.py`
"""
import json
import pathlib
import sys
from collections import defaultdict

ROOT = pathlib.Path(__file__).resolve().parents[2]
IR = ROOT / 'klink bot' / 'docs' / 'card-ir.json'


def expr_vars(e, acc):
    if isinstance(e, dict):
        if 'var' in e and isinstance(e['var'], str):
            acc.add(e['var'])
        for v in e.values():
            expr_vars(v, acc)
    elif isinstance(e, list):
        for v in e:
            expr_vars(v, acc)


def programs(p):
    return [('uber', p.get('steps') or [])] + \
           [('local:' + k, v) for k, v in (p.get('locals') or {}).items()]


def main():
    d = json.loads(IR.read_text(encoding='utf-8'))
    callers = {}
    consumers = defaultdict(set)

    for card, p in d.items():
        for where, steps in programs(p):
            deck_slots = set()
            for s in steps:
                if s.get('fn') == 'GetDeckByside':
                    for o in s.get('outs') or []:
                        if 'slot' in o:
                            deck_slots.add(o['slot'])
            if not deck_slots:
                continue

            callers.setdefault(card, []).append((where, len(deck_slots)))

            # 传递闭包：deck 数组 → set 的局部变量 → Array_Get 出来的元素
            taint = set(deck_slots)
            changed = True
            while changed:
                changed = False
                for s in steps:
                    acc = set()
                    expr_vars(s, acc)
                    if s.get('op') == 'set' and (acc & taint):
                        dst = s.get('dst')
                        if dst and dst not in taint:
                            taint.add(dst)
                            changed = True
                    if s.get('fn') == 'Array_Get':
                        a = s.get('args') or []
                        if a and isinstance(a[0], dict) and a[0].get('var') in taint:
                            for o in s.get('outs') or []:
                                if 'slot' in o and o['slot'] not in taint:
                                    taint.add(o['slot'])
                                    changed = True

            for s in steps:
                acc = set()
                expr_vars(s, acc)
                if not (acc & taint):
                    continue
                fn = s.get('fn') or s.get('op')
                if fn in ('Array_Get', 'GetDeckByside'):
                    continue
                consumers[fn].add(card)

    print(f'调用 GetDeckByside 的卡：{len(callers)} 张')
    for card in sorted(callers):
        print(f'  {card}  {callers[card]}')
    print()
    print('牌库数组的（传递）消费者 —— 全部是整数语义：')
    for fn in sorted(consumers, key=lambda k: -len(consumers[k])):
        print(f'  {fn:<38} {len(consumers[fn]):2d} 张')
    return 0


if __name__ == '__main__':
    sys.exit(main())
