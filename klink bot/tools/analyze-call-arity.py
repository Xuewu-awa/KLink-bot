"""扫描全部卡的 Kismet 字节码，统计每个外部函数的"参数列表形状"。

目的：判断 `GetOppositeSide` 这种「只有一个 out 参数」的调用，
是**函数本身就只声明了一个参数**，还是**dump 里把入参省掉了**。
"""
import json
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

assets = json.load(open("decompiled/cards.full.json", encoding="utf-8"))["assets"]

shapes = defaultdict(Counter)      # 函数名 -> {参数个数: 次数}
samples = defaultdict(dict)        # 函数名 -> {参数个数: 一条样例}

wanted = {"GetOppositeSide", "GetLocationCardBySide", "GetCardFromID", "DamageCard",
          "GetTargetedCard", "IsSideActive", "GetCardsOnBoardBySide", "DrawCardsFromDeckBySide"}


def walk(node, fn_names):
    if isinstance(node, dict):
        inst = node.get("Inst")
        fname = node.get("Function") or node.get("FunctionName")
        if inst in ("FinalFunction", "LocalVirtualFunction", "VirtualFunction") and fname:
            params = node.get("Parameters") or []
            fn_names[fname][len(params)] += 1
            samples[fname].setdefault(len(params), node)
        for v in node.values():
            walk(v, fn_names)
    elif isinstance(node, list):
        for v in node:
            walk(v, fn_names)


walk(assets, shapes)

for fn in sorted(wanted):
    if fn not in shapes:
        continue
    print("### %s" % fn)
    for n, cnt in sorted(shapes[fn].items()):
        desc = []
        for p in (samples[fn][n].get("Parameters") or []):
            if isinstance(p, dict) and p.get("Inst") == "LocalVariable":
                desc.append(p.get("Variable Name"))
            elif isinstance(p, dict):
                desc.append(p.get("Inst"))
            else:
                desc.append(str(p))
        print("    参数 %d 个 ×%-5d 例: %s" % (n, cnt, ", ".join(desc)))
