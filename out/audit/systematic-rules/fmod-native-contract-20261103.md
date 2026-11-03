# FMod native contract (2026-11-03)

The current executable (`b6f4b6af2af3f9a596182df82abc4e87a2cb51abaff417f558d68e7bbb201305`) uses a zero-divisor branch that writes remainder `0` and quotient `0`. For finite in-range operands, the wrapper computes the quotient by division followed by truncation toward zero and delegates remainder production to the captured helper at RVA `0x12002d0`; the four signed finite regression cases pass in BotSim.

Two static IR sites occur in `card_location_british_scen5.OnStartOfGame` (steps 4935 and 5556). This is a local semantic contract only; malformed floating inputs, conversion overflow, complete argument marshaling, PvP reachability, and full card certification remain open. See the machine-readable evidence in `fmod-native-contract-20261103.json`.
