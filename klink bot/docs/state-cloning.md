# Exact state cloning for candidate evaluation

Status: foundation only. Candidate evaluation still constructs an independent
engine and replays the committed action tape. No speedup is claimed by this work.

## Implemented

`UeRandomStream.Clone()` copies InitialSeed, current Seed and ConsumedCount into
an independent stream without drawing. Initialize and Reset retain their existing
cumulative-counter behavior. BotSim's `FRandomStream` regressions cover reference
vectors, shuffle consumption and clone continuation/isolation across seed
truncation, empty/advanced streams, reset and reseeding.

## Remaining implementation boundaries

1. Card graph: allocate all card shells first, then remap CurrentTarget,
   BlueprintVariables/Sets and any nested mutable payloads. Preserve aliases,
   collection ordering, buff ledgers and suppression restoration data. Reject
   unsupported runtime payload types explicitly; do not silently share them.
2. GameState: retain side/id indexes, battle order, HQ bindings, queues,
   histories, allocators, policies, RNG and admission diagnostics. Rebind callbacks
   to the destination engine. SnapshotJson is an observation format, not a restore
   contract.
3. MatchEngine/CardApi/VM: preserve persistent state and diagnostic counters;
   rebuild dispatch delegates against the destination API. Review static template
   ownership and pool caches before sharing anything. Bind choice adapters to the
   destination. A copied delegate may still capture the source engine.
4. Define and enforce an idle decision boundary. Pending selections include
   closures, so checking PendingSelectionCount alone does not establish that all
   effect, receipt, autoplay and forced-end-turn work is quiescent. Mid-resolution
   cloning remains outside the initial implementation scope.

## Integration gates

- Inventory every instance field of each cloned type, with an explicit copy,
  remap, rebuild, immutable-share or idle-only disposition. Add a field-coverage
  guard so new fields cannot silently evade this contract.
- Verify source isolation in both directions, alias preservation inside each
  copy, nonempty ledgers/queues at permitted boundaries, RNG cursor and health
  diagnostics. Matching public snapshots alone is insufficient.
- Compare full replay and clone-based evaluation on fixed action prefixes:
  candidate acceptance, resulting state, subsequent action sequences, choices,
  RNG consumption, diagnostics and final result. Retain full replay as reference.
- Benchmark equivalent runs only after correctness gates pass. Keep clone mode
  disabled until the engine graph and adapters meet these gates.

This is an engineering acceptance plan, not a certification of PvP rule coverage.
