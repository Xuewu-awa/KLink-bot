"""找出「字节码里根本没有入参、但内核却按入参读」的原语。

原理：扫描全部卡的 Kismet 调用，统计每个外部函数的参数列表形状。
如果某个函数的**所有**调用都满足「参数表里没有任何 `InstanceVariable` /
`Self` / 常量等真正的入参，只有 `LocalVariable` 形态的 out 槽」，
那么内核从 args 里读值就一定读到垃圾（未赋值的 out 槽）。

输出：疑似「零入参」的原语清单 + 每个的一条样例参数名。
"""
import json
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

assets = json.load(open("decompiled/cards.full.json", encoding="utf-8"))["assets"]

# 函数名 -> 参数形状（每种形状的签名）-> 次数
shapes = defaultdict(Counter)


def param_kind(p):
    if not isinstance(p, dict):
        return "?"
    inst = p.get("Inst")
    if inst == "LocalVariable":
        return "local:" + str(p.get("Variable Name"))
    return inst or "?"


def walk(node):
    if isinstance(node, dict):
        inst = node.get("Inst")
        fname = node.get("Function") or node.get("FunctionName")
        if inst in ("FinalFunction", "LocalVirtualFunction", "VirtualFunction") and fname:
            sig = tuple(param_kind(p) for p in (node.get("Parameters") or []))
            shapes[fname][sig] += 1
        for v in node.values():
            walk(v)
    elif isinstance(node, list):
        for v in node:
            walk(v)


walk(assets)

# out 槽的命名惯例：CallFunc_<函数名>_<参数名>
def is_out_slot(fname, kind):
    return kind.startswith("local:CallFunc_%s_" % fname)


zero_input = []
for fname, sigs in shapes.items():
    total = sum(sigs.values())
    if total < 5:
        continue
    all_zero = True
    for sig in sigs:
        for kind in sig:
            if not is_out_slot(fname, kind):
                all_zero = False
                break
        if not all_zero:
            break
    if all_zero and all(len(sig) <= 1 for sig in sigs):
        zero_input.append((total, fname, sigs))

zero_input.sort(reverse=True)
print("=== 疑似「零入参」的原语（共 %d 个）===" % len(zero_input))
for total, fname, sigs in zero_input:
    sig = next(iter(sigs))
    print("  %-40s 调用 %-5d 参数: %s" % (fname, total, ", ".join(sig[1:] if len(sig) > 1 else sig)))
