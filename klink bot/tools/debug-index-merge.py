"""调试索引合并为什么找不到新增条目。"""
import re
from pathlib import Path

GEN = Path("ref/kards-sim/KardsSim/Generated")
NEW = Path("out/Generated-gap2/_index.g.cs")

entry_re = re.compile(r'^\s*\["([^"]+)"\]\s*=\s*([A-Za-z0-9_]+)\.Registry\.Fns,\s*$')

old_text = (GEN / "_index.g.cs").read_text(encoding="utf-8")
new_text = NEW.read_text(encoding="utf-8")

old_entries = {m.group(1) for line in old_text.splitlines() if (m := entry_re.match(line))}
new_entries = {m.group(1) for line in new_text.splitlines() if (m := entry_re.match(line))}

have_files = {f.stem for f in GEN.rglob("*.g.cs") if not f.name.startswith("_")}

print(f"旧索引条目 {len(old_entries)}")
print(f"新索引条目 {len(new_entries)}")
print(f"Generated/ 下 .g.cs 文件 {len(have_files)}")
print(f"新索引有、旧索引没有: {len(new_entries - old_entries)}")
print(f"  其中在 have_files 里的: {len((new_entries - old_entries) & have_files)}")

print()
for probe in ["card_event_pams", "card_event_the_rock_of_gibraltar", "card_unit_2nd_west_africa"]:
    print(f"  {probe}:")
    print(f"    在新索引 = {probe in new_entries}")
    print(f"    在旧索引 = {probe in old_entries}")
    print(f"    have_files = {probe in have_files}")
    p = GEN / "Britain" / "OceaniaStorm" / "events" / f"{probe}.g.cs"
    print(f"    文件落地 = {p.exists()}  ({p})")

print()
print("前 10 个真正该补的:")
for k in sorted((new_entries - old_entries) & have_files)[:10]:
    print(f"    {k}")
