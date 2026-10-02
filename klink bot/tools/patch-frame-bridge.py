"""修复参照实现的「持久帧」断桥。

问题：事件桩把形参写进**宿主全局变量**
    H.SetVar("K2Node_Event_destroyedInCombat", ...)
而 ubergraph 读的是**它自己的函数局部变量**
    GetLocal(L, "K2Node_Event_destroyedInCombat")   → 永远 Nothing

于是所有「读事件参数做判断」的效果静默走空分支 —— 905 个文件受影响。

改法：把生成代码里对 K2Node_Event_* 的**读取**改成走宿主
    GetLocal(L, "K2Node_Event_X")  →  H.GetVar("K2Node_Event_X")

这是机械替换，模式无歧义：
  - 事件桩里只写 `H.SetVar("K2Node_Event_X", ...)`，不读
  - ubergraph 里只读 `GetLocal(L, "K2Node_Event_X")`，不写
  - 生成的每个方法都有 `IHost H` 形参，所以 H 一定在作用域内

用法:
  python tools/patch-frame-bridge.py --dry-run   # 只统计
  python tools/patch-frame-bridge.py             # 就地修改
  python tools/patch-frame-bridge.py --revert    # 改回来
"""
import argparse
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

GEN = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "..", "..", "ref", "kards-sim", "KardsSim", "Generated")

READ = re.compile(r'GetLocal\(L, "(K2Node_Event_[A-Za-z0-9_]+)"\)')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--revert", action="store_true")
    args = ap.parse_args()

    if not os.path.isdir(GEN):
        print("找不到生成目录:", GEN)
        return 1

    files = []
    for root, _, names in os.walk(GEN):
        for n in names:
            if n.endswith(".g.cs"):
                files.append(os.path.join(root, n))

    changed = 0
    total_hits = 0
    for path in files:
        text = open(path, encoding="utf-8").read()
        if args.revert:
            new = re.sub(r'H\.GetVar\("(K2Node_Event_[A-Za-z0-9_]+)"\)',
                         r'GetLocal(L, "\1")', text)
        else:
            new = READ.sub(r'H.GetVar("\1")', text)

        if new != text:
            hits = len(READ.findall(text)) if not args.revert else 0
            total_hits += hits
            changed += 1
            if not args.dry_run:
                open(path, "w", encoding="utf-8", newline="").write(new)

    verb = "将修改" if args.dry_run else ("已还原" if args.revert else "已修改")
    print(f"{verb} {changed} / {len(files)} 个文件，替换 {total_hits} 处读取")
    return 0


if __name__ == "__main__":
    sys.exit(main())
