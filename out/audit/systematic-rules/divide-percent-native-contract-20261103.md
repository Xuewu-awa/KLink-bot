# Divide_IntInt / Percent_IntInt native contract (2026-11-03)

The current KARDS executable (`b6f4b6af2af3f9a596182df82abc4e87a2cb51abaff417f558d68e7bbb201305`) exposes both functions on `/Script/Engine.KismetMathLibrary`. Their captured wrappers write `0` when the signed divisor is zero. For nonzero divisors they execute `cdq; idiv ecx`: `Divide_IntInt` stores EAX, while `Percent_IntInt` stores EDX. This establishes truncation toward zero for the quotient and a remainder with the dividend's sign for representable int32 operands.

The offline VM regression covers positive/negative operand combinations, signed endpoints, and zero divisors. The full BotSim suite passed 1109 tests, the focused regression passed, and the Release solution build completed with zero warnings or errors. Static IR contains 37 Divide sites across 26 card records and 14 Percent sites across 5 records; this inventory does not establish PvP reachability or certify those cards.

`INT_MIN / -1` is excluded because native signed `idiv` raises a processor exception for that pair and game-level handling is unresolved. Full UFunction marshaling, malformed frames, and complete card behavior remain outside this contract. Machine-code capture, cases, hashes, and per-site inventory are recorded in [divide-percent-native-contract-20261103.json](divide-percent-native-contract-20261103.json).
