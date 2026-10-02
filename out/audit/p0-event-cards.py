"""列出每个 P0 事件在 IR 里有真实程序的卡（取前几个），供自测选样。"""
import json, pathlib, sys

ROOT = pathlib.Path(r"<repo-root>")
d = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))

events = sys.argv[1:] or [
    "OnCardReset", "OnOtherCardReset", "OnSuppressed", "OnOtherCardSuppressed",
    "OnBeforeOtherCardPlayedFromHand", "OnOtherCardDealDamage", "OnCreateCard",
    "OnCardLocationMoved", "OnOtherCardLocationMoved", "OnCardDrawnFromDeck",
    "OnCardSpawnedInHand", "OnBeforeStartOfTurn", "OnAfterLeaveBoard",
    "OnAfterExtraKreditSlotGain", "OnAfterGainDefense", "OnOtherCardAbilitiesChanged",
    "OnFullyRepaired", "OnOtherCardBecomingVeteran", "OnOtherCardDiscarded",
    "OnSurvivedCombat", "OnOtherCardSurvivedCombat", "OnBeforeOtherCardDestroyed",
    "OnAfterOtherCardSuppressed", "OnOtherCardKreditCostChanged", "OnDeckShuffled",
]

out = {}
for ev in events:
    hits = [name for name, v in d.items() if ev in v.get("entrypoints", {})]
    out[ev] = hits
    print(f"{ev:36s} {len(hits):4d}  {', '.join(hits[:4])}")

(ROOT / "out" / "audit" / "p0-event-cards.json").write_text(
    json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
