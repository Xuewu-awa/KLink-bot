import pathlib
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
t = pathlib.Path(r"<repo-root>\src\KLink.Bot\Cards\CardInnateTable.cs").read_text(encoding="utf-8")
hits = re.findall(r'\["(card_[a-z0-9_]+)"\]\s*=\s*\(\[([^\]]*)\],\s*(\d+)\)', t)
armored = [(n, a, k) for n, k, a in hits if a != "0"]
print("entries", len(hits), "armored", len(armored))
for n, a, k in armored[:12]:
    print(n, a, k)
