# Exact state cloning for candidate evaluation

Status: RNG and supported card-graph/container cloning foundation only. Candidate evaluation
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
- CardCustomJson's two private maps: copied through the slot adapter, preserving
  native ASCII key comparison, removed slots, spelling/order and separate type
  tags. Ordinary CopyFrom retains its existing native merge behavior.

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

Dictionary/HashSet continuation now uses the narrow CollectionSlotClone adapter.
It preserves occupied/removed entry positions, encoded LIFO free chains, capacity,
version and fast-mod metadata, detaches both mutable buffers, and rebuilds hash
buckets from remapped keys without moving entries. The internal comparer is
retained so string hash codes use the same runtime comparer. Cached dictionary
key/value views are discarded because they capture their owner, then created
lazily against the destination. Lists also retain capacity.

The adapter requires .NET 10 x64 and validates exact top-level and entry field
names/types before use. Unknown layouts and invalid slot/free-chain metadata fail
with NotSupportedException; there is no fallback to content-only copying. The
installed .NET 10.0.12 layout was independently inventoried by DeepSeek and checked
by the primary agent. This is intentionally a runtime-specific adapter, not a
general reflective object copier or support for other runtime versions/AOT.
Source state must be quiescent during copying. Active enumerators are not copied.
Validate actual persisted VM payloads and complete the remaining engine boundaries
before using cloning for candidate evaluation; no replay replacement is enabled.

Validation on 2026-11-02: six targeted CardGraphClone cases cover the original
graph checks plus 1,200 deterministic string/JSON mutation steps (three seeds),
900 identity-keyed mutation steps (default and reference comparers), guaranteed
colliding numeric keys under default equality, repeated cloning, cached views,
growth, trim, clear, empty/all-removed states, null set elements and
case-sensitive/insensitive comparers. The new continuation regression
failed against the previous content-only implementation before the fix. The older
standalone probe (`a,d,c` versus `a,c,d`) remains historical evidence of the bug.

Final validation: all six targeted CardGraphClone cases passed, the complete
BotSim selftest passed 1,065 cases, and NNTrain policy-evaluation-selftest passed
all nine cases. These checks do not establish complete PvP rule coverage or an
evaluation throughput improvement.

## VM payload validation

Two additional integration cases exercise the actual KismetVm. One runs a
synthetic program through VM producers for card/null values, heterogeneous
arrays, typed auto-seeded lists, map/set aliases, ability and choice projections,
buff-expiry arrays, board lists and kredit-combination table rows. It checks
reference remapping, nested storage isolation and continued VM list mutation.
The other executes the loaded `card_event_pams.GetChooseSpawnCards` program and
checks its persisted `PossibleCards` list, output alias, candidate order, shared
static template identity and detached mutable candidate payloads.

A deliberately persisted private GameplayTagContainerRef is rejected explicitly.
This owner-linked handle is outside the whitelist; the synthetic rejection case
does not establish that current card IR persists it. A parsed scan of the current
1,709 card IR entries found 1,486 instance assignments with 322 distinct
destination names and no direct assignment source containing GameplayTags.
Assignments alone do not cover in-place mutations or indirect value propagation.

Injected locals/events, every dispatch return and every reachable real card path
are not exhaustively covered. A DeepSeek source-inventory job timed out after
writing a draft; its key producer paths were checked against source, but the draft
is not a completed independent audit. These tests validate simulator engineering,
not authoritative game rules, GameState/engine cloning or evaluation speed.

Validation on 2026-11-02: all eight targeted CardGraphClone cases passed;
the complete BotSim selftest passed all 1,067 cases. This batch adds tests and
documentation only; it does not expand the production payload whitelist.

## Remaining implementation boundaries

1. Card graph: supported container continuation above is implemented. VM producer
   checks below cover selected persisted payloads; exhaustive coverage of actual
   card execution paths and dynamic inputs remains open. Unsupported values fail.
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
