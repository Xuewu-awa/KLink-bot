"""把线上 pak 里解出的 deckCodeIDsTable2 DataTable 转成简单的 {卡组码: 卡名} 映射。

为什么不用现成的 deck_code_ids.json：那份是旧数据（2431 条），
用户给的卡组里 E1/E4/DJ/DR… 这些码在里面根本没有。
线上 pak 里的 deckCodeIDsTable2 有 2518 行，是权威来源。

输入：UAssetCLI dump-datatable 的产物（行名 = 卡名，行内有 card / deck_code_id / ID）
输出：{"<2字符码>": "<卡名>", ...}

用法：
    python convert-deckcode-table.py <dump.json> <输出.json>
"""

import json
import sys
from pathlib import Path


def prop_value(props, wanted_name):
    """从 UAssetAPI 的属性数组里取某个名字的值。"""
    for p in props:
        if p.get("Name") == wanted_name:
            return p.get("Value")
    return None


def main():
    src = Path(sys.argv[1])
    dst = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("deck_code_ids.json")

    print(f"读取 {src} ...")
    root = json.loads(src.read_text(encoding="utf-8-sig"))

    # 顶层可能有多个 DataTable，取第一个
    table_name, table = next(iter(root.items()))
    rows = table["rows"]
    print(f"  表 {table_name}，行数 {len(rows)}")

    result = {}
    conflicts = []
    skipped = 0

    for row_name, row in rows.items():
        props = row.get("Value") or []
        code = prop_value(props, "deck_code_id")
        card = prop_value(props, "card")
        if not code or not card:
            skipped += 1
            continue

        if code in result and result[code] != card:
            conflicts.append((code, result[code], card))
            continue

        result[code] = card

    # 按码排序输出，便于 diff
    ordered = {k: result[k] for k in sorted(result, key=lambda s: (len(s), s))}
    dst.write_text(json.dumps(ordered, ensure_ascii=False, indent=0), encoding="utf-8")

    print(f"  写出 {len(ordered)} 条 → {dst}")
    if skipped:
        print(f"  跳过（缺字段）: {skipped}")
    if conflicts:
        # 注意：Windows 控制台默认 GBK，不要在这里输出非 ASCII 符号
        print(f"  [!] 同码不同卡 {len(conflicts)} 处（保留第一条）:")
        for code, a, b in conflicts[:10]:
            print(f"      {code}: {a} vs {b}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
