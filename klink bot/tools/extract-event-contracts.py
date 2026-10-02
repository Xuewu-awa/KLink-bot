"""从原始字节码里把「事件契约」抽出来。

每个事件桩函数（OnAfterAttack / OnAfterOtherCardAttacks / OnMoveToFrontline …）
的形状是：

    LetValueOnPersistentFrame  K2Node_Event_attackerCard = <局部 attackerCard>
    LetValueOnPersistentFrame  K2Node_Event_damageToDefender = <局部 damageToDefender>
    LocalFinalFunction ExecuteUbergraph_<卡>  <入口下标>

也就是说：**事件参数 → 持久帧槽位** 的映射在桩函数里，
而 ubergraph 里用 `槽位.成员`（IR 里的 ctx 限定读取）去取。

这份脚本输出：事件名 → 有序参数列表（局部名 + 类型 + 目标槽位）。
它就是 FireTrigger 必须提供的载荷契约。

用法: python tools/extract-event-contracts.py [输出json]
"""
import json
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

assets = json.load(open("decompiled/cards.full.json", encoding="utf-8"))["assets"]

# 事件名 -> 参数形状 -> 卡数
shapes = defaultdict(Counter)
# 事件名 -> 槽位名 -> {局部名, 类型}
slots = defaultdict(dict)


def type_of(pin):
    if not isinstance(pin, dict):
        return "?"
    obj = pin.get("PinSubCategoryObject") or ""
    if obj:
        return obj.rsplit(".", 1)[-1]
    return pin.get("PinCategory") or "?"


for path, entry in assets.items():
    funcs = entry.get("functions") or {}
    if not isinstance(funcs, dict):
        continue
    for fname, f in funcs.items():
        if fname.startswith("ExecuteUbergraph") or fname.startswith("UserConstructionScript"):
            continue
        bc = (f or {}).get("bytecode") or []
        params = []
        for s in bc:
            if s.get("Inst") != "LetValueOnPersistentFrame":
                continue
            slot = s.get("Property Name") or ""
            ex = s.get("Expression") or {}
            local = ex.get("Variable Name") or ""
            outer = ex.get("Variable Outer") or {}
            params.append((local, type_of(outer), slot))

        if params:
            shapes[fname][tuple(p[1] for p in params)] += 1
            for local, ty, slot in params:
                slots[fname][slot] = {"local": local, "type": ty}

print("=== 事件契约（共 %d 个事件）===" % len(shapes))
for fname in sorted(shapes, key=lambda k: -sum(shapes[k].values())):
    total = sum(shapes[fname].values())
    print("\n### %s   （%d 张卡注册）" % (fname, total))
    for shape, cnt in shapes[fname].most_common():
        print("    形状(%d 参) ×%d: %s" % (len(shape), cnt, ", ".join(shape)))
    for slot, info in sorted(slots[fname].items()):
        print("      %-46s ← 局部 %-24s %s" % (slot, info["local"], info["type"]))

if len(sys.argv) > 1:
    out = {fn: {"slots": slots[fn],
                "shapes": [{"types": list(s), "cards": c} for s, c in shapes[fn].most_common()]}
           for fn in shapes}
    with open(sys.argv[1], "w", encoding="utf-8") as fp:
        json.dump(out, fp, ensure_ascii=False, indent=1)
    print("\n已写出 %s" % sys.argv[1])
