# Dynamic text boundary audit (2026-10-31)

- Authoritative IR: `D:\Codex Workshop\KLink-bot\klink bot\docs\card-ir.json` (SHA-256 `726b259f86ba9f5abbadd35d1262b8432f0ff3b9d7290c0b7261817c9888c56b`)
- SDK evidence: UE4SS CXXHeaderDump under `D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump`
- Replays: not used.

## Indexed calls

- `SpawnCardInHandBySide`: **232**
- `AddNumberToText`: **32**
- `UpdateCardText`: **0**
- `AppendNumberToCardText`: **0**
- `math:Format`: **57** (nested and standalone IR shapes)
- `math:PrintString`: **2**

Of the 232 indexed `SpawnCardInHandBySide` calls, **230** pass literal `newCardText=""`. Two PvP generated-card calls (`card_event_seize_the_initiative` and `card_event_carrier_battle`) pass a variable-derived `Conv_TextToString(Format(...))` value. The current IR does not preserve the `FormatArgumentData` member values, so the exact formatted string is unresolved. All calls use `SkipDrawAnimation=false` and `fromOppositeSide=false`.

The two `PrintString` nodes are in `card_location_german_scen5` and `card_unit_75th_rifle_regiment.locals.SpawnCopy`. They are diagnostic/logging calls with no gameplay output slot.

## Decision

The SDK exposes a presentation chain (`AddNumberToText` -> online-match sub-action/notifier -> visual controller) and static card text in `FCardData`; no separate dynamic rule-state field was identified. The two generated-card cards explicitly apply their numeric rule changes through `DamageCard`/`ChangeKreditCost` and persist the level, while `Format` only supplies replacement display text. For `card_unit_115th_separate_btn`, the rule state is expressed by `ChangeAttack`/`ChangeDefense`; `Format`/`AddNumberToText` only formats the displayed number. The headless adapters therefore remain no-ops for these display calls.

`DoSpawnInHand` still leaves visual/campaign text payloads unmodeled. This is recorded as an adapter-completeness item; current IR evidence does not demonstrate a PvP rule mismatch.

This report does not prove complete native `/Script/kards` or inherited Blueprint equivalence.
