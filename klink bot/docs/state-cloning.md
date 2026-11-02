# Exact state cloning for candidate evaluation

Status: RNG clone and logical card-graph copy foundation only. Candidate evaluation
still constructs an independent engine and replays the committed action tape.
No speedup is claimed by this work.

## Implemented

`UeRandomStream.Clone()` copies InitialSeed, current Seed and ConsumedCount into
an independent stream without drawing. Initialize and Reset retain their existing
cumulative-counter behavior. BotSim's `FRandomStream` regressions cover reference
vectors, shuffle consumption and clone continuation/isolation across seed
truncation, empty/advanced streams, reset and reseeding.

`CardGraphClone.Create(roots)` now copies reachable CardInstance, CardBuff and
CardCustomJson objects using one reference-identity memo. It retains duplicate
roots, distinct objects with equal card IDs, cross-card targets, shared containers
and cycles, and exposes a read-only original-to-copy card map. Memberwise shells
retain every scalar/backing field without invoking projected setters. No live
game memory, authoritative rule data or card IR is changed by this feature.

Reference-field dispositions:

- Name, SalvageFaction and SuppressStrippedCustomAbility: immutable strings shared.
- Definition and StaticTemplateIdentity: shared catalog identity, under an explicit
  read-only contract. CardDefinition contains nested collections and is not deeply
  immutable; callers must not mutate the catalog through either graph.
- CurrentTarget: remapped to the reachable copy, including self/cross-card cycles.
- SuppressionExceptionTriggers, SuppressStrippedKeywords, SuppressStrippedBuffs,
  Keywords, ReceivedAbilitiesFromCards, SuppressStrippedAbilities,
  RemovedGameplayTags, CustomJson, BlueprintSets, BlueprintVariables, BuffsBySource
  and CardsBuffedByThisCard: copied recursively through the same memo.
- CardBuff's custom-ability details set: copied through that memo; every scalar
  retains its raw value, including zero-presence and nullable ledger metadata.
- CardCustomJson's two private maps: rebuilt through CopyFrom, preserving native
  ASCII key comparison, key spelling/order and separate JSON type tags.

Supported payloads are explicit in CardGraphClone.cs: boxed primitives/enums,
strings, cards/buffs/JSON, the listed VM list/map/set/array types and ledger pairs.
Only default, ordinal, ordinal-ignore-case and reference-identity equality
comparers are shared. Unsupported types, custom comparers and collection subclasses
throw NotSupportedException. This is fail-closed coverage, not a claim that every
possible VM payload is supported. No unrestricted reflective object copier is used.

BotSim's CardGraphClone regressions cover cycles/aliases, card-keyed maps, typed
arrays, source/copy isolation, suppression ledgers, raw private values, JSON tags,
comparers and unsupported payloads. Reflection-based test inventories require a
review when reference fields or unfamiliar value-field types are added.

Important remaining card-container boundary: rebuilding a Dictionary or HashSet
retains current enumeration order but does not retain removed-entry slots. A later
insertion may enumerate at a different position from the source. Capacity and
enumerator version are also not copied. Therefore this logical graph copy is not
yet an exact continuation contract and must not be used for candidate evaluation.
Resolve and regression-test remove/add continuation (including identity-keyed
containers), then validate actual persisted VM payloads before closing card cloning.

Validation on 2026-11-02: 4 targeted CardGraphClone tests and the complete 1,063
BotSim selftests passed; NNTrain policy-evaluation-selftest passed all 9 checks.
A .NET 10 remove/insert probe reproduced the open ordering boundary for both
Dictionary and HashSet: source `a,d,c`, rebuilt copy `a,c,d` after deleting `b`
from `a,b,c` and then inserting `d`. DeepSeek supplied a read-only source inventory;
the primary agent checked the field dispositions and implemented/reviewed the code.

## Remaining implementation boundaries

1. Card graph: logical copying above is implemented. Exact mutable-container
   continuation and coverage of actual persisted VM payloads remain open.
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
