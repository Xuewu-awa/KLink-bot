"""把新 pak 的 IR 与旧 IR 做**逐卡拼接**：只替换「原始字节码或 CDO 真的变了」的卡。

为什么必须拼接（2026-10-31 实测）
----------------------------------
`UAssetCLI dump-batch --full` 产出的是 `{"assets": {...}}` 单文件形态，
而**旧 IR 是用另一种 dump 形态生成的** —— `gen-kismet-ir.py` 的 `load_assets()`
第二条分支（带 `functions_file` / `metadata_file` 的「pak 定义索引」）。
那条分支会从函数导出的 `properties[].PropertyFlags`（`CPF_Parm` / `CPF_OutParm`）
推出 `inputs` / `outputs` / `out_indices`，写进 IR 的 `local_inputs` / `local_outputs`。

单文件形态**没有**这些元数据 ⇒ 直接用它生成的 IR 会丢掉 `local_inputs`/`local_outputs`，
实测 selftest 从「724 通过」退化成 5 条真失败
（`card_event_vanguard_ita` 的同名来源谓词、海上入侵/大规模部署的生成链）。

而这两条 dump 的**原始字节码**对 2046 张卡是逐字节相同的 —— 差异只是 `bo` 偏移重编号
和上面那组元数据。所以正确做法不是「整份换掉」，而是：
  原始字节码 或 CDO 有变化的卡  →  用新 IR 那一条
  其余卡                        →  保留旧 IR 那一条（元数据、偏移都不动）

用法：python splice-ir.py <旧ir.json> <新ir.json> <旧dump.json> <新dump.json> <输出.json>
"""
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

if len(sys.argv) != 6:
    print(__doc__)
    sys.exit(2)

old_ir_p, new_ir_p, old_dump_p, new_dump_p, out_p = sys.argv[1:]

old_ir = json.load(open(old_ir_p, encoding="utf-8-sig"))
new_ir = json.load(open(new_ir_p, encoding="utf-8-sig"))
old_dump = {os.path.basename(k): v for k, v in json.load(open(old_dump_p, encoding="utf-8-sig"))["assets"].items()}
new_dump = {os.path.basename(k): v for k, v in json.load(open(new_dump_p, encoding="utf-8-sig"))["assets"].items()}


def fingerprint(entry):
    """一张卡在 dump 里的「游戏数据指纹」：函数字节码 + CDO。"""
    fns = entry.get("functions") or {}
    body = {name: json.dumps(fi.get("bytecode"), sort_keys=True, ensure_ascii=False) for name, fi in fns.items()}
    return json.dumps({"functions": body, "cdo": entry.get("cdo") or {}}, sort_keys=True, ensure_ascii=False)


changed = []
missing = []
for card in new_ir:
    base = os.path.basename((new_ir[card].get("asset") or "") or (card + ".uasset"))
    if base in new_dump and base in old_dump:
        if fingerprint(old_dump[base]) != fingerprint(new_dump[base]):
            changed.append(card)
    elif card not in old_ir:
        missing.append(card)

changed.sort()
result = {}
took_new = []
for card in new_ir:
    if card in changed or card not in old_ir:
        result[card] = new_ir[card]
        took_new.append(card)
    else:
        result[card] = old_ir[card]

with open(out_p, "w", encoding="utf-8", newline="\n") as fh:
    json.dump(result, fh, ensure_ascii=False, separators=(",", ":"))

print(f"旧 IR {len(old_ir)} 张 / 新 IR {len(new_ir)} 张")
print(f"游戏数据真有变化的卡：{len(changed)} 张")
for c in changed:
    print("   ", c)
if missing:
    print(f"新 IR 有、旧 IR 没有：{len(missing)} 张 -> {missing[:8]}")
print(f"采用新条目 {len(took_new)} 张，其余 {len(result) - len(took_new)} 张保留旧条目")
print(f"输出 {out_p}  ({os.path.getsize(out_p):,} B)")
