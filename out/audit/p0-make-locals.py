"""把 private-fns-cards-only.txt 里的私有函数名解析出来，生成 LOCAL_FUNCTIONS 的 Python 字面量。

用法: python out/audit/p0-make-locals.py            # 全部 76 个
      python out/audit/p0-make-locals.py --missing  # 只取"派发表无"的那些（低风险落地）
"""
import pathlib
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
txt = (ROOT / "out" / "audit" / "private-fns-cards-only.txt").read_text(encoding="utf-8")

rows = []
for line in txt.splitlines():
    m = re.match(r"^(.+?)\s+卡数=\s*(\d+)\s+(.*)$", line)
    if m:
        rows.append((m.group(1).strip(), int(m.group(2)), m.group(3).strip()))

only_missing = "--missing" in sys.argv
picked = [n for n, c, st in rows if (not only_missing) or ("派发表无" in st)]
print(f"# 解析到 {len(rows)} 个；本次取 {len(picked)} 个（only_missing={only_missing}）")
print("LOCAL_FUNCTIONS = {")
for n in picked:
    print(f'    {n!r},')
print("}")
