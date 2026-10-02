"""
把「全量卡牌用到的所有调用」和「内核已实现的派发项」做差集，按影响面排序。

这是从「一张张撞 bug」转成「按清单补」的关键一步：
  · 输入 A：klink bot/docs/card-effects.json —— 2053 张卡各自调用了哪些函数（解包产物）
  · 输入 B：src/KLink.Bot/Effects/CardApiDispatch.cs 的派发表 —— 内核认识哪些
  · 差集就是**内核的能力缺口**，再乘上每张卡在真实对局里的出现次数，就知道先补谁

同时把「光环」这一类单独挑出来：特征是成对出现 ApplyTheBuff / RemoveTheBuff，
再配 OnEnterPlay + OnLeaveBoardOrOwner / OnEndOfTurn ——
这类卡持续改变**别人**的状态（费用、攻防），是实现里最容易整族遗漏的。
"""
import json
import re
from collections import Counter, defaultdict

EFFECTS = r"klink bot\docs\card-effects.json"
DISPATCH = r"src\KLink.Bot\Effects\CardApiDispatch.cs"
VM = r"src\KLink.Bot\Effects\Blueprint\KismetVm.cs"

effects = json.load(open(EFFECTS, encoding="utf-8"))

# ---- B: 内核认识哪些名字 ----
impl = set()
for path in (DISPATCH, VM):
    try:
        src = open(path, encoding="utf-8").read()
    except OSError:
        continue
    impl |= set(re.findall(r'\[\s*"([A-Za-z_][A-Za-z0-9_]*)"\s*\]\s*=', src))
    impl |= set(re.findall(r'case\s+"([A-Za-z_][A-Za-z0-9_]*)"', src))

print(f"内核派发表里认识的名字: {len(impl)} 个")

# ---- A: 卡牌用到哪些 ----
call_cards = defaultdict(set)      # 调用名 -> 用到它的卡
for name, c in effects.items():
    called = set()
    for fn, lst in (c.get("functions") or {}).items():
        called.update(lst)
    called.update(c.get("calls") or [])
    for x in called:
        if isinstance(x, str) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", x):
            call_cards[x].add(name)

print(f"卡牌总共用到: {len(call_cards)} 个不同的名字")

missing = {k: v for k, v in call_cards.items() if k not in impl}
print(f"其中内核**没有**实现: {len(missing)} 个")
print()

ranked = sorted(missing.items(), key=lambda kv: -len(kv[1]))
print("=== 缺口按「影响的卡数」排序 TOP 30 ===")
print(f"{'调用名':<44}{'卡数':>6}   样例卡")
for k, cards in ranked[:30]:
    sample = sorted(cards)[:2]
    print(f"  {k:<42}{len(cards):>6}   {', '.join(sample)}")

# ---- 光环特征 ----
print()
print("=== 光环类（同时有 ApplyTheBuff 和 RemoveTheBuff）===")
aura = sorted(n for n, c in effects.items()
              if "ApplyTheBuff" in json.dumps(c) and "RemoveTheBuff" in json.dumps(c))
for n in aura:
    fns = sorted((effects[n].get("functions") or {}).keys())
    print(f"  {n:<44} {effects[n].get('text')}")
    print(f"      {fns}")
print(f"  共 {len(aura)} 张")

# ---- 持久型触发点统计（部署后仍生效的卡）----
print()
print("=== 带「离场还原」的卡数（部署后持续生效的强特征）===")
persist = [n for n, c in effects.items() if "OnLeaveBoardOrOwner" in json.dumps(c)]
print(f"  OnLeaveBoardOrOwner: {len(persist)} 张")
for n in sorted(persist)[:12]:
    print(f"      {n}")
