"""判定 Kismet 的 `CodeOffset` 到底是「字节偏移」还是「StatementIndex」。

背景：KismetVm 里跳转语义一直有疑问。
- 按 StatementIndex 解释 → card_event_aans 完美（jump->252 正好是 Return 的下标）
- 但在别的卡上出现向后跳转，而 Blueprint 的 ForEachLoop/WhileLoop **本来就**会
  产生向后跳转，所以「向后跳转 = 错」这个判据不成立

所以直接量化：对全部跳转指令，看 CodeOffset 落在
  (a) 本函数语句的字节偏移集合（bo，由 UAssetCLI 用 WriteExpression 往返量出）里的比例
  (b) 本函数语句的 StatementIndex 集合里的比例
哪个高就是哪个。

用法：python analyze-jump-offsets.py [cards.full.json]
"""
import json
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
src = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "decompiled" / "cards.full.json"

print(f"读取 {src} ...")
assets = json.loads(src.read_text(encoding="utf-8-sig"))["assets"]

stats = Counter()
by_inst = Counter()
examples = []

for path, info in assets.items():
    if not info.get("ok"):
        continue
    for fname, finfo in (info.get("functions") or {}).items():
        if not isinstance(finfo, dict):
            continue
        bc = finfo.get("bytecode")
        if not bc:
            continue

        # 两种候选目标集合
        byte_offsets = {e["bo"] for e in bc if "bo" in e}
        stmt_indexes = {e["StatementIndex"] for e in bc if "StatementIndex" in e}
        has_bo = len(byte_offsets) > 0

        for e in bc:
            inst = e.get("Inst", "")
            if inst not in ("Jump", "JumpIfNot", "JumpIf"):
                continue
            t = e.get("Offset")
            if t is None:
                continue

            stats["total"] += 1
            by_inst[inst] += 1
            if has_bo and t in byte_offsets:
                stats["hit_byte"] += 1
            if t in stmt_indexes:
                stats["hit_stmt"] += 1
            if has_bo and t not in byte_offsets and t not in stmt_indexes:
                stats["hit_neither"] += 1
                if len(examples) < 8:
                    examples.append((Path(path).stem, fname, t,
                                     max(byte_offsets) if byte_offsets else 0,
                                     sorted(byte_offsets)[:4]))

tot = max(1, stats["total"])
print()
print(f"跳转指令总数: {stats['total']}   {dict(by_inst)}")
print()
print(f"  落在**字节偏移**集合里: {stats['hit_byte']:>7}  ({stats['hit_byte']/tot:.1%})")
print(f"  落在 StatementIndex 集合里: {stats['hit_stmt']:>7}  ({stats['hit_stmt']/tot:.1%})")
print(f"  两者都不在:            {stats['hit_neither']:>7}  ({stats['hit_neither']/tot:.1%})")
print()
for c, f, t, mx, head in examples:
    print(f"  {c} / {f}: 目标 {t}，但脚本最大字节偏移只有 {mx}，前几个 bo={head}")
