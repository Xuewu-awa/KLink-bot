"""
统计 IR 里 call 步骤的「参数是否包含 self 占位」——
这决定了 CardApiDispatch 里 a[0] 到底是第一个实参还是接收者。

背景（来自 CCB-TEAM 的 Kismet 直译模拟器文档「05 · 坑与复盘」第 9 条）：
    原语读到错位的参数 | a[0] 是接收者，实参从 a[1] 起 | 调用约定

如果同一份 IR 里两种约定混用，那么任何 a[0] 都可能取到接收者，
而 `SideArg(r, a, 0)` 在解析不出来时会**静默退化成 NotAvailable**（→ 返回空/0），
正是那份文档里第 12 条「未实现调用静默变成 0」。
"""
import json
import collections

d = json.load(open(r"klink bot\docs\card-ir.json", encoding="utf-8"))

with_self = collections.Counter()      # fn -> 次数（args[0] 是 self 占位）
without_self = collections.Counter()
self_at_other_pos = collections.Counter()
total_calls = 0

for card, prog in d.items():
    for s in prog["steps"]:
        if s.get("op") != "call":
            continue
        total_calls += 1
        fn = s.get("fn", "?")
        args = s.get("args", [])
        if not args:
            without_self[fn] += 1
            continue
        if isinstance(args[0], dict) and args[0].get("self") is True:
            with_self[fn] += 1
        elif any(isinstance(a, dict) and a.get("self") is True for a in args[1:]):
            self_at_other_pos[fn] += 1
        else:
            without_self[fn] += 1

print(f"IR 里 call 步骤总数: {total_calls}")
print(f"  args[0] 是 self 占位    : {sum(with_self.values())}  ({len(with_self)} 种函数)")
print(f"  args[0] 是普通实参      : {sum(without_self.values())}  ({len(without_self)} 种函数)")
print(f"  self 出现在非 0 位      : {sum(self_at_other_pos.values())}")

both = sorted(set(with_self) & set(without_self))
print(f"\n★ 同一个函数两种约定都出现过: {len(both)} 种")
for fn in both[:25]:
    print(f"    {fn:<40} self在前 {with_self[fn]:5}   self在后/无 {without_self[fn]:5}")

print(f"\n只出现过 self 在 args[0] 的函数（这些调用的 a[0] 一定取错）:")
for fn, n in with_self.most_common(25):
    if fn not in without_self:
        print(f"    {fn:<40} {n:5}")
