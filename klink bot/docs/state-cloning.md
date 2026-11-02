# Exact state cloning for candidate evaluation

Status: RNG, supported card-graph/container and detached GameState cloning are
implemented. An internal idle VM component copy is also implemented; complete
MatchEngine/CardApi cloning remains open. Candidate evaluation still constructs an independent engine and
replays the committed action tape.
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

## Detached GameState graph

`GameStateClone.Create(source)` returns a detached state and a read-only card
identity map. It uses the card copier's memo across every state root, including
history-only cards, queue entries and HQ bindings outside the current ID index.
Distinct objects with the same card ID remain distinct. The state constructor
copies raw scalars rather than calling gameplay setters, firing triggers or
rebuilding indexes from a public snapshot. Nonempty trigger/autoplay/wait lists
and historical ledgers are retained; this does not authorize running a copied
engine while resolution is pending.

The reviewed GameState inventory contains 61 instance fields (including compiler
backing fields). The exact-name regression guard requires review on additions:

| Disposition | Fields |
| --- | --- |
| Copy scalar/string (24) | ShouldExecuteWaitPlayFromHand, Seed, Turn, ActiveSide, StartingSide, ClientSide, UnitDestroyedThisTurn, OperationKreditsSpentThisTurn, StopAttack, HasPlayedWeatherCardThisTurn, FrontlineOwner, StopFurtherActions, IsFinished, Winner, WinnerReason, _cardsCreatedThisTurn, _createdCountTurn, ClientIdTurnOverride, CollectRandomTrace, TrackGeneratedCardTrust, LeftAllyFaction, RightAllyFaction, LeftMainFaction, RightMainFaction |
| Shared frozen catalog (1) | Database; its nested definitions and runtime caches are not made immutable by this API. The future engine integration must review catalog queries/cache ownership. |
| Shared immutable policy (2) | CardReservations, CardBlacklist. Their constructors own their buffers; public changes return new policies. Nested CardReservationChange records own private sets. Policy field names are guarded in tests. |
| Clone cursor (1) | Random, preserving InitialSeed, Seed and ConsumedCount without drawing |
| Recursive copy/remap (30) | _cardsBySide, _pendingDeckRemovals, _byCardId, _battleCardOrder, _autoPlayerCardIds, _waitPlayFromHandCards, _buffsToRemoveEndOfTurn, _kreditSlotsLost, GameplayRestrictions, GameplaySideEffects, TriggerQueue, DestroyedCardIdsByTurn, DestroyedCardIdsAll, _destroyedBySide, _kredits, _maxKredits, _hqDamagedThisTurn, _fatigue, ActiveBondFactions, FrontlineLimiters, _nextCardId, ActionLog, CardsPlayedThisTurn, CardsPlayedFromHandByTurn, UnimplementedCalls, RandomTrace, GeneratedCardIds, VerifiedGeneratedCardIds, _locationCards, _locationCardIds |
| Clear for later destination binding (3) | CardMoved, CardCreated, CardCreationModifiers |

The state-only payload extension admits the concrete list/array/map/set types in
GameStateClone.cs, mutable GameplayRestriction/GameplaySideEffect entries and
GameAction/SubAction graphs. One memo preserves repeated entries, cross-store
aliases and cycles through ActionData. ActionValue2 instances are shared as sealed
immutable string/int records; their field inventory is guarded. Read-only action
interfaces do not imply immutable backing storage, so their supported concrete
containers are copied. Unknown action payloads or container implementations fail
explicitly. The standalone CardGraphClone whitelist is unchanged by this extension.

An active WithDeckCardRemoved scope is rejected: its source-stack finally cannot
continue on a detached state. Clearing three callback properties prevents source
engine captures, but callers must still establish source quiescence and bind the
destination engine. GameStateClone does not construct a MatchEngine or copy its
API/VM; the separate internal VM operation is described below.

Five GameStateClone regressions cover raw scalar/nullable presence, exact state
and nested field inventories, mutable-buffer isolation in both directions,
history/HQ roots with duplicate IDs, indexes/order, repeated effect entries,
cyclic action data, policy sharing, nonempty queues/ledgers, RNG/allocator
continuation across turn/override changes, callback clearing/rebinding, repeated
cloning and rejection without source mutation. One case clones state/action
history produced by actual engine turn processing and checks that further source
engine execution leaves the copy unchanged. It does not continue a cloned engine.
The DeepSeek field inventory was independently checked against source and the
61-field reflection guard; its unverified policy/HQ interpretations are not used
as the copy contract.

Validation on 2026-11-02: all five GameStateClone cases and all eight existing
CardGraphClone cases passed; the full BotSim selftest passed 1,072 cases and
NNTrain policy-evaluation-selftest passed all nine cases. The NNTrain build still
reports nullable-context warnings in its existing source files. No engine clone
or throughput improvement is claimed.

## Remaining implementation boundaries

1. Card graph: supported container continuation above is implemented. VM producer
   checks below cover selected persisted payloads; exhaustive coverage of actual
   card execution paths and dynamic inputs remains open. Unsupported values fail.
2. GameState: detached copying below is implemented. Destination-engine callback
   binding and the complete idle-boundary gate remain integration work.
   SnapshotJson is an observation format, not a restore contract.
3. MatchEngine/CardApi: preserve persistent state and diagnostic counters;
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

## Idle VM component copy (2026-11-02)

The internal KismetVm(destinationApi, sourceVm, sharedMemo) constructor copies
only a quiescent VM component. It is not a public engine checkpoint. The caller
must provide a destination CardApi and a state-enabled CardGraphClone memo shared
with all other destination roots. Full engine integration is still disabled.

All 17 instance fields have explicit dispositions, enforced by a reflection
inventory in BotSim:

| Fields / auto-properties | Disposition |
| --- | --- |
| `_api` | Bind to supplied destination API; never retain source API. |
| `_executionDepth` | Require zero; destination starts at zero. |
| `Trace`, `StepTrace` | Copy via shared memo, preserving aliases to each other and other state roots. |
| `ProgramsExecuted`, `StepsExecuted`, `UnsupportedInstructions`, `FaultedPrograms`, `UnresolvedJumps`, `FlowReturns`, `StepLimitHits` | Copy raw cumulative counters. |
| `UnimplementedCalls`, `UnsupportedOps` | Copy dictionary storage/slots via shared memo. |
| `CaptureMissingCallSites`, `MissingCallSitesOmitted`, `MissingCallSiteCaptureFailures` | Copy diagnostic configuration and cumulative counters. |
| `_missingCallSites` | Copy dictionary via shared memo; share sealed immutable KismetMissingCallSite string/integer records. |

The state-only payload whitelist now admits that diagnostic dictionary and its
immutable keys. The card-only whitelist is unchanged. All RunCore paths wrap the
whole frame operation in a depth increment/finally decrement, including nested
local/event execution, missing entries, interpreter faults and failures before
the interpreter catch. Active stack frames/locals/flow stacks are not copied.
This guard is not thread synchronization: callers must prevent concurrent access.

Three targeted regressions cover the exact field inventory, nonzero raw counters,
trace aliases across state roots, two-way isolation, repeated copies, real missing
call provenance continuation, destination API notification binding and copy
rejection at nesting depths 1/2/1 followed by successful copying after unwind.
Both interpreted faults and exceptions before the interpreter catch are checked.

The initial DeepSeek engine/API/VM inventory job timed out without an artifact;
no conclusions from that job are used. The primary review found and included
KismetMissingCallSites.cs despite its different filename. A narrower DeepSeek
review then checked those three VM files. Its member table and depth-guard review
were independently verified; its prose incorrectly says 15 auto-properties
(the actual breakdown is 14 auto-properties plus 3 explicit fields). Its claim
about deep copying was explicitly outside its scope; the primary graph review
and isolation/alias tests establish the supported behavior described here.

Validation: all three KismetVmClone cases passed; the full BotSim selftest passed
1,075 cases, and NNTrain policy-evaluation-selftest passed nine cases. NNTrain
continues to emit existing CS8632 nullable-context warnings.

CardApi template/cache
ownership, rebuilt dispatch closures, external choice adapters and the complete
engine idle gate remain the next integration boundary. No throughput gain or
additional PvP rule certification is claimed.
