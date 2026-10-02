import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
cards = json.loads((ROOT / "klink bot" / "docs" / "cards.live.json").read_text(encoding="utf-8"))
if isinstance(cards, dict):
    cards = cards.get("cards") or list(cards.values())
by_type = {}
for c in cards:
    t = c.get("type") or c.get("Type")
    by_type.setdefault(t, []).append(c.get("name") or c.get("Name"))

for t in ("bomber", "artillery", "fighter", "infantry", "tank"):
    print(t, len(by_type.get(t, [])), (by_type.get(t) or [])[:4])
