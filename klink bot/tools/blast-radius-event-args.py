"""量化「触发载荷只能传 2 个参数」这个缺口的爆炸半径。

背景：kards-sim 的 CardDispatch.Fire 是

    var args = subject is null ? new[] { self } : new[] { self, Val.Ref(host.Obj(subject)) };

也就是最多传 **2 个**实参。但真实事件的参数表有 1~6 个（见 docs/event-contracts.json）。
直译产物用 `args.Length > n` 守卫，所以缺的参数会变成 Val.Nothing，
而效果里的 `if (!destroyedInCombat)` 这类守卫会直接短路 → **整段效果被跳过**。

本脚本统计：有多少事件参数 > 2、这些事件被多少张卡注册。
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")

contracts = json.load(open("docs/event-contracts.json", encoding="utf-8"))

rows = []
for fn, v in contracts.items():
    cards = sum(s["cards"] for s in v["shapes"])
    nparams = max(len(s["types"]) for s in v["shapes"])
    rows.append((nparams, cards, fn, v["shapes"][0]["types"]))

rows.sort(key=lambda r: (-r[0], -r[1]))

print("=== 参数个数 > 2 的事件（这些在只传 2 个参数时会缺参）===")
over = [r for r in rows if r[0] > 2]
total_cards = sum(r[1] for r in over)
print("  共 %d 个事件，覆盖 %d 张卡注册" % (len(over), total_cards))
print()
for nparams, cards, fn, types in over:
    print("  %-38s %d 参  %4d 张卡" % (fn, nparams, cards))
    print("        %s" % ", ".join(types))

print()
print("=== 统计口径 ===")
print("  全部事件:            %d" % len(rows))
print("  参数 = 1 的:         %d" % len([r for r in rows if r[0] == 1]))
print("  参数 = 2 的:         %d" % len([r for r in rows if r[0] == 2]))
print("  参数 > 2 的（受影响）: %d" % len(over))
