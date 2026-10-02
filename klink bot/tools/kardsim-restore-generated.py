"""
恢复被误删的 Generated/ 根级文件。

背景：我为了清掉自己误平铺到 Generated/ 根下的 75 个文件，用
    Get-ChildItem Generated -File -Filter *.g.cs | Where-Object { $_.Name -notlike "_*" } | Remove-Item
删了 76 个 —— 但**他原本就有一批函数库资产放在根下**（BP_CardFunctions 等），
被一起删掉了。编译报 `_index.g.cs(21,32): 名称"BP_CardFunctions"不存在`。

恢复办法：`out/Generated-gap2/_deps/` 是一份全量转译产物（1737 个），
把其中**当前 Generated/ 下任何位置都没有**的补进 `Generated/_deps/`。
只补缺的，绝不覆盖 —— 否则会 CS0101 重复定义。

（C# 的命名空间来自文件内容而不是目录，所以补到 `_deps/` 下不影响
 `KardsSim.Generated.BP_CardFunctions` 的引用。）
"""
import shutil
from pathlib import Path

GEN = Path("ref/kards-sim/KardsSim/Generated")
DEPS_SRC = Path("out/Generated-gap2/_deps")
DEST = GEN / "_deps"


def bare(p: Path) -> str:
    return p.name.removesuffix(".g.cs")


existing = {bare(f) for f in GEN.rglob("*.g.cs") if not f.name.startswith("_")}
print(f"Generated/ 现有 {len(existing)} 个类")

DEST.mkdir(parents=True, exist_ok=True)
added, skipped = [], 0
for f in sorted(DEPS_SRC.glob("*.g.cs")):
    if f.name.startswith("_"):
        continue
    name = bare(f)
    if name in existing:
        skipped += 1
        continue
    shutil.copy2(f, DEST / f.name)
    existing.add(name)
    added.append(name)

print(f"补入 {len(added)} 个 → {DEST}")
print(f"跳过（已存在）{skipped} 个")
print()
print("补入的前 25 个:")
for n in added[:25]:
    print(f"    {n}")
