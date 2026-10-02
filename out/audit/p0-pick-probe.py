import json, pathlib, sys
sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
d = json.loads((ROOT / "klink bot" / "docs" / "card-ir.json").read_text(encoding="utf-8"))
evs = ["OnCreateCard", "OnBecomingVeteran", "OnSurvivedCombat", "OnOtherCardSurvivedCombat",
       "OnOtherCardBecomingVeteran", "OnAfterOtherCardSuppressed", "OnBeforeOtherCardDestroyed",
       "OnOtherCardDiscarded", "OnOtherCardAbilitiesChanged", "OnOtherCardDealDamage",
       "OnBeforeOtherCardPlayedFromHand", "OnFullyRepaired", "OnAfterGainDefense",
       "OnAfterExtraKreditSlotGain", "OnBeforeStartOfTurn", "OnOtherCardReset", "OnCardReset",
       "OnSuppressed", "OnOtherCardSuppressed", "OnAfterLeaveBoard", "OnCardLocationMoved",
       "OnOtherCardLocationMoved", "OnCardSpawnedInHand", "OnCardDrawnFromDeck",
       "OnBeforeDestroyed", "OnDestroyed", "OnOtherCardDestroyed"]
for ev in evs:
    hits = [k for k, v in d.items() if ev in v.get("entrypoints", {})]
    units = [k for k in hits if k.startswith("card_unit_")]
    print("%-34s %3d  %s" % (ev, len(hits), (units[0] if units else (hits[0] if hits else "-"))))
