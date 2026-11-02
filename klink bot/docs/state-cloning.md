# Exact state cloning for candidate evaluation

Status: RNG, supported card-graph/container and detached GameState cloning are
implemented. MatchEngine.CloneAtIdle now integrates the state, VM, CardApi and
isolated database query view at a restricted idle boundary. Candidate evaluation still constructs an independent engine and
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
3. MatchEngine: integrate the state/API/VM components below; preserve engine
   counters, action IDs, logs and deck inputs, rebind owned state callbacks and
   reject or explicitly rebuild external choice adapters. A copied delegate may
   still capture the source engine. CardApi component copying alone is insufficient.
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

At the end of the VM batch, CardApi template/cache ownership and rebuilt dispatch
closures remained open; the component batch below addresses those items. External
choice adapters and the complete engine idle gate remain integration work. No throughput gain or
additional PvP rule certification is claimed.


## Idle CardApi component and database view (2026-11-02)

The internal CardApi(destinationEngine, sourceApi, sharedMemo) constructor now
copies the API component, including its VM. It remains inaccessible as a public
engine checkpoint. It requires whole-engine quiescence established by its caller,
a state-enabled memo shared with the copied GameState, and destination state and
callbacks wired by the future engine integration. Direct calls into API methods
must also be quiescent; the four local scope checks are not a complete engine
idle gate or synchronization mechanism.

All 18 API fields (15 explicit fields and three auto-property backing fields),
four database fields and four candidate-cache record fields have exact reflection
inventory guards. Dispositions:

| Fields | Disposition |
| --- | --- |
| `_engine` | Bind supplied destination engine. |
| `_dispatch` | Rebuild through BuildDispatch on destination API; source overrides/delegates are not copied. |
| `Vm` | Copy with destination API and shared memo, retaining VM diagnostics. |
| `_playedCardBroadcastDepth`, `_resolvingTriggerQueue`, `_triggerDepth` | Reject nonzero/active scope; destination starts idle. Active VM execution is also rejected. |
| `_playedCardBroadcastDone`, `_triggerBuffers` | Copy through memo, including retained scratch roots and aliases. |
| `TriggerTrace`, `AuraTrace` | Copy through memo, retaining aliases to VM/state traces. |
| `_staticPool`, `_staticPoolVariants` | Copy lists and mutable template instances through memo; frozen definitions/identity tokens remain shared under the existing catalog contract. |
| `_staticPoolDb` | Resolve through memo so a previously registered database view retains cache-invalidation identity. |
| `_staticPoolReservationPolicy`, `_staticPoolBlacklistPolicy` | Share immutable policy objects, preserving reference-based invalidation semantics. |
| `_staticPoolGeneration` | Copy raw. |
| `_gcsPurityCache`, `_gcsCache` | Copy dictionaries and slot order. Copy each GcsCacheEntry record shell before traversing its Cards list, preserving cycles through card variables. |

CardDatabase.CloneView(memo) creates and registers an internal database view before
state/API traversal. The frozen `_byName` catalog and DeckCodeIds data are shared;
`VariantFallbacks` is copied raw and the mutable lazy `_codeByName` reverse index
is independently copied without initializing a cold index. GameState copying now
consults the memo for its Database reference. Standalone GameStateClone retains
its previous shared-database behavior; it does not silently acquire the isolated
view. Future engine cloning must register the view first and handle/reject any
foreign database references used by external effect contexts.

The state-only whitelist adds the API cache/buffer container shapes, the cache
entry record and the database reference disposition. The public card-only copier
is unchanged. No general delegate or object reflection copier is introduced.
Global GCS hit/miss/bypass counters remain process-wide diagnostics and are not
reset/copied. KismetLibrary.Default and its loaded IR/lazy indexes remain shared;
cloning assumes the library/catalog are frozen, no IR reload and no concurrent
access. Definitions contain mutable nested collections: freezing is a caller
contract, not enforced deep immutability.

Four CardApiClone regressions cover inventories, nonzero retained state,
state/API/VM trace aliases, template/list/cache cycles, both-way isolation,
repeated copying with state-first/API-first traversal, cached candidate flags and
order, branch-local policy invalidation, rebuilt dispatch destination binding,
actual database fallback counting, cold/warm lazy-index isolation, and rejection
during broadcast, trigger, queue resolution and live VM execution. The test-only
wiring deliberately leaves GameState callbacks detached; it is not production
engine restoration.

The current card IR did not produce a nonempty cache in the initial search for
this fixture (PAMS uses a non-cacheable instance-writing path). The cache test
therefore scopes a controlled local-only IR program on an existing definition,
executes the real VM/GetChooseSpawnCards/static-pool implementation and restores
the IR/local-program cache afterward. It proves the engineering copy contract,
not actual-card cacheability or PvP rule semantics. Its retained cached lists and
template mutations are intentionally preserved rather than reconstructed.

DeepSeek supplied a bounded field/mutator inventory of the three pre-change API
partials. The primary agent checked it against source and the runtime field guard;
its counts correctly distinguish 15 explicit fields and three stored properties.
No worker source edits or rule judgments were integrated.

Remaining work: complete engine field dispositions and owned-callback binding,
external choice-adapter policy, full safe-idle gate, then fixed-prefix comparisons
against independent full replay before enabling clone-based candidate evaluation.
There is no new PvP certification or measured throughput gain in this batch.

Validation: all four CardApiClone cases passed, full BotSim selftest passed 1,079
cases, and NNTrain policy-evaluation-selftest passed nine cases (existing CS8632
nullable-context warnings remain). An initial full run exposed legacy fixtures
selecting the sole nonpublic CardDatabase constructor by reflection; CloneView
uses a factory method instead of adding a constructor, and the rerun passed.
The final added actual-fallback/cold-index assertions passed in the targeted run.

## Integrated idle MatchEngine copy (2026-11-02)

MatchEngine.CloneAtIdle() returns an independently mutable engine at an explicitly
restricted boundary. It registers the destination engine and a database view in
one graph memo, then copies GameState, deck inputs, engine log, CardApi and VM.
The three default GameState callbacks are rebound to the destination. Constructor
gameplay setup, Start, rule setters and queue draining are not invoked by copying.

The 27 engine storage fields (12 explicit and 15 auto-property backing fields)
have an exact reflection inventory guard. Raw action ID, current action-log start,
out-of-range rejection counter and both trust configuration flags are retained.
LastPick and LastHandTarget share sealed immutable scalar/string records; their
field layouts are also guarded. Deck inputs support exact string[] and List<string>
runtime types, copied through the common memo to preserve aliases with each other
and with other graph roots. Other IReadOnlyList implementations fail explicitly.
Engine log aliases to state/API/VM payloads are preserved.

The boundary rejects pending selections (including an empty retained queue),
local rule receipts, forced-end requests, trigger/autoplay/wait queues, active
engine/effect/autoplay/forced-end scopes, pending deck removal and active API/VM
execution. The gate does not consume pending work. In particular, a direct Destroy
can leave a receipt that its normal caller must apply before cloning is allowed.
The five external selector properties must be null; no captured selector closure
is retained. All three state callbacks must equal the engine's default owned
delegates; null, custom and multicast replacements are rejected. Destination
selectors can be installed explicitly after cloning when their ownership is known.

All 70 nonstatic block-bodied methods in the original engine file have a disposable
operation-depth scope. Expression-bodied gameplay entry points forward into these
scoped methods; simple record/log updates have no callback. LegalTargets retains its
original deferred predicate timing and guards each predicate invocation separately.
Scopes unwind on exceptions and nest across root calls and callbacks. This is not
synchronization: callers must use one thread, clone between completed operations,
and exclude direct API/state operations in progress. The guard cannot establish
quiescence against arbitrary external mutation or another thread.

Database and pool identities must belong to this engine. Any unregistered database
encountered in the engine graph is rejected rather than silently sharing its mutable
query counters/index. Standalone state/component copies retain their earlier database
contract. Frozen card definitions, catalog indexes, immutable policies and global IR
remain shared. Dispatch is rebuilt with destination ownership; unsupported private
dispatch overrides are not an extension mechanism and are not preserved. IR/catalog
reloads and concurrent mutation remain outside the copy contract.

Six integrated regressions cover the exact field/record inventory, nondefault
scalars, engine/database/log payload aliases, deck aliasing, callback ownership,
two-way isolation, repeated copying, controlled candidate-cache/template cycles,
all listed pending/active/foreign-owner rejection categories, reentrant callback
rejection and exception unwind. Deterministic fixtures compare clone continuation
with both the source and a freshly constructed engine executing the same prefix:
Start, draws, turn changes, RNG samples, play, movement, attack, destruction and
receipt application. Comparisons include snapshots/action history, logs, next action
numbering and VM diagnostics. These are bounded engineering checks, not exhaustive
PvP rule equivalence proofs. The cache fixture uses the same scoped synthetic IR
described in the previous component batch, not a new actual-card rule claim.

DeepSeek delivered the bounded pre-change member inventory; the primary agent
checked its counts against the source and exact runtime field guard. Implementation,
integration, validation and publishing remain with the primary agent.

The default candidate probe remains on independent full replay. The opt-in
clone probe and its bounded measurements are documented below. This engine-copy
batch itself added no PvP certification.

Validation: six MatchEngineClone cases passed, the full BotSim selftest passed
1,085 cases, and NNTrain policy-evaluation-selftest passed nine cases. Existing
NNTrain CS8632 nullable-context warnings remain.


## Opt-in candidate clone probe (2026-11-02)

`CloneActionProbe.Run` is a separate experimental diagnostic. It constructs one
owned baseline from seed and confirmed roots, with the fixed first-index choice
adapter. It disposes that adapter, clones the idle baseline once per candidate,
and attaches an independent first-index adapter to each clone. It never detaches
handlers from the caller's reference engine. Unsupported clone boundaries or graph
payloads remain Unknown with an explicit exception reason; no replay fallback hides
these failures. A bad baseline returns a batch error and no candidate labels.

The separate report contract identifies `owned-prefix-once;idle-clone-per-candidate`.
EnginesCreated counts baseline constructions; ClonesCreated counts successful
copies; PrefixRootsApplied counts actual replay attempts, once for the baseline.
The existing candidate/prefix/product budgets are conservatively retained, even
though the product is no longer the actual number of prefix executions.

Choice adapter counters restart on each branch. This is valid only for the fixed
policy here, which ignores Sequence. Arbitrary stateful policies are unsupported.
Root choice/fault evidence uses deltas, and any completed nested choice makes the
root Unknown. Accepted/Rejected still describe simulator returns, not authoritative
legality, a policy mask, or training admission. Returned reports contain no successor
states. Frozen catalog/IR and single-thread restrictions of CloneAtIdle still apply.
Snapshot/request/trace/diagnostic checks are bounded contamination detectors, not a
proof of equality of every private field or process-wide isolation.

The existing replay probe and default callers are unchanged. A separate command
performs replay/clone classification and root-successor comparisons, then measures
both paths after warmup with alternating order over five repetitions. Timed runs
exclude validation serialization. Allocations use the current-thread GC counter;
these are allocated bytes, not peak memory. Empty card vectors are deliberate for
this execution-only benchmark. Teacher prefixes are engineering fixtures, not game
rules authority. Forward replay and forward/reversed clone reports must agree;
forward root snapshots, action records, engine log, RNG state/consumption and VM
counters must agree. Every candidate must have reached the root observer, so equal
pre-root Unknown failures cannot count as successful successor validation.

```powershell
dotnet run --project tools/BotSim -c Release -- probe-actions-clone --plain --steps 8 --repeats 5 --out out/audit/clone-probe-plain-20261102.json
dotnet run --project tools/BotSim -c Release -- probe-actions-clone --steps 8 --repeats 5 --out out/audit/clone-probe-meta-20261102.json
```

Both runs use seed 1 and eight prefixes (lengths 0 through 7). Measurements below
are totals across five repeats, on this machine; they are not general throughput
claims. Initial runs and repeats showed timing variation. No default switch is
justified by these two bounded fixtures alone.

| Fixture | Candidate comparisons | Replay / clone time (ms) | Replay / clone allocated bytes | Replayed prefix roots per pass |
|---|---:|---:|---:|---:|
| 39 Arado / 39 Arado | 239 | 1910.6 / 1795.7 | 2,887,233,856 / 2,535,984,080 | 1025 / 28 |
| 英日 / 日澳快攻 | 281 | 2930.4 / 2397.0 | 3,757,448,744 / 3,253,951,824 | 1187 / 28 |

There were zero classification/successor mismatches. The plain fixture had 147
Accepted and 92 Rejected candidates. The meta fixture had 57 Accepted, 214 Rejected
and 10 Unknown: six choice-policy-dependent and four unimplemented-call results.
Those Unknown results remain unresolved and add no rule coverage. The latest run
reduced elapsed time by approximately 6% / 18% and allocated bytes by 12% / 13%.
Detailed rows, counters and contracts are in
`out/audit/clone-probe-plain-20261102.json` and
`out/audit/clone-probe-meta-20261102.json`.

Three new regression cases exercise normal play/move/attack prefixes, root successor
equivalence, caller selector ownership, clone accounting, branch mutation isolation,
reverse traversal, VM gaps, pending/nested choices, exceptions, clone-boundary
rejection without fallback, prefix mismatch/rejection, budgets and reference
contamination. All 1,088 BotSim selftests and nine NNTrain policy-evaluation-selftests
passed in a separate publishing snapshot excluding unrelated dirty source edits.
The snapshot preserves unchanged checkout data bytes: Git-normalized cards.live.json
line endings do not match the existing exact native-default card-data hash, so an
archive-only fixture failed IR loading until the original unchanged checkout bytes
were copied and outputs rebuilt. No evidence hash or card rule was rewritten.
Existing NNTrain CS8632 warnings remain.

The DeepSeek worker supplied a bounded selector/counter inventory, checked against
ChoicePolicyAdapter and the replay probe by the primary agent; it did not implement
or publish changes. This engineering batch does not update PvP certification or the
audit completion denominator. Next: broaden prefixes and profile the remaining
snapshot/query/copy allocations before selecting any faster default.

### Longer prefixes and stage profiling (2026-11-02)

The comparison now also checks every reversed-clone root successor against replay,
not just reversed classifications. Four fixtures cover 80 prefixes (lengths 0–15
in three runs and 0–31 in the fourth), using three deck pairs and two seeds. Each
candidate must reach the root observer in all three traversal paths. Reports with
batch errors, missing successors or mismatches fail validation.

| Fixture / seed / prefixes | Candidates | Unknown | Replay / clone ms | Replay / clone allocated bytes |
|---|---:|---:|---:|---:|
| Arado / 1 / 16 | 754 | 0 | 3937.7 / 3063.2 | 6,984,595,648 / 4,918,637,048 |
| 英日–日澳快攻 / 1 / 16 | 860 | 45 | 11639.4 / 10181.6 | 14,845,964,752 / 10,703,612,688 |
| 德芬车–苏澳中速 / 2 / 16 | 818 | 1 | 5813.4 / 3665.6 | 9,025,339,624 / 5,416,632,440 |
| Arado / 2 / 32 | 2114 | 0 | 13979.0 / 9781.8 | 24,877,611,768 / 14,082,420,912 |

These are totals over three timed repetitions, with separate warmup and validation,
on .NET 10.0.12 x64 and empty execution-only card vectors. Across 4,546 candidate
comparisons there were zero classification or observed-successor mismatches:
1,477 Accepted, 3,023 Rejected, and 46 Unknown (37 choice-policy-dependent and nine
unimplemented-call). Unknown remains unresolved. The longer Arado run overlaps
earlier prefixes; these are fixture comparisons, not unique game situations or
additional PvP certification. Equal observed successors do not establish equality
of every private field. Timings vary; measured elapsed reductions were 12.5–36.9%
and allocated-byte reductions 27.9–43.4%. No default execution path changed.

`CloneProbeProfile` is internal, single-thread instrumentation. One separate
profiled clone run per prefix must produce exactly the same report as the normal
run; its overhead is excluded from the timed comparison. It records stage counts,
elapsed time and current-thread allocated bytes. `total` includes all scopes;
`comparison` includes `comparison.state` and `comparison.request`. These parent and
child values must not be summed together. Unlisted work remains in total. Allocation
counts are not retained or peak memory, and sampling instrumentation affects timings.

The stage split changes the optimization priority according to workload:

| Fixture | State/clock/trace comparison time | Candidate rebuild/JSON time | Engine clone time |
|---|---:|---:|---:|
| Arado 16 | 44.7% | 19.5% | 25.5% |
| 英日–日澳快攻 16 | 24.2% | 7.7% | 58.9% |
| 德芬车–苏澳中速 16 | 45.7% | 19.7% | 27.1% |
| Arado 32 | 43.1% | 23.0% | 28.3% |

Next, prototype a lower-allocation exact snapshot comparison behind the experimental
probe, with the existing JSON comparison retained as the test oracle. It must
preserve field coverage, list order, mismatch detection and reference-contamination
checks. Measure snapshot construction versus serialization before selecting the
implementation. For the 英日 fixture, separately split the graph-copy cost before
changing clone ownership or sharing rules. Removing checks or switching defaults
is not justified by this batch.

The DeepSeek worker produced a source-only allocation/loop inventory in a private
packet. The primary agent checked the repeated `DescribeCandidates`, feature-array,
snapshot and serialization sites against source and measured their stage costs.
The inventory's multiplicative candidate-rebuild cost is structural, not a measured
claim that it dominates every deck. The worker made no source or publishing changes.

Reproduce from the repository root with `dotnet run --project tools/BotSim -c Release
-- probe-actions-clone`, adding the options below (run sequentially):

```text
--plain --steps 16 --repeats 3 --out out/audit/clone-profile-plain16-20261102.json
--steps 16 --repeats 3 --out out/audit/clone-profile-meta16-20261102.json
--deck-a 德芬车 --deck-b 苏澳中速 --seed 2 --steps 16 --repeats 3 --out out/audit/clone-profile-gersov16-20261102.json
--plain --seed 2 --steps 32 --repeats 3 --out out/audit/clone-profile-plain32-seed2-20261102.json
```

Profiling regressions cover normal completion, injected root-observer exceptions
and early budget exit, checking unchanged outcomes and scope unwinding. Validation
uses a separate publishing snapshot excluding unrelated dirty probe edits and
preserving unchanged checkout data bytes as described above. This batch changes
engineering diagnostics only; audit matrix scopes and completion estimates remain
unchanged.

All 1,089 BotSim selftests and nine NNTrain policy evaluation selftests passed in
that snapshot; the four targeted CloneActionProbe cases also passed. Existing
NNTrain CS8632 warnings remain.

### Opt-in UTF-8 snapshot comparison (2026-11-02)

`probe-actions-clone --utf8-snapshots` enables an experimental exact-byte snapshot
comparison. Public `CloneActionProbe.Run` and CLI runs without this flag retain
string JSON comparison; the replay path remains unchanged. The snapshot schema,
clock, action trace, candidate request, VM health and final reference-contamination
checks are retained. This is an engineering optimization, not new PvP evidence.

`ProbeSnapshotComparison` builds the same `MatchSnapshot` and uses the same shared
`GameState.SnapshotJsonOptions` and generic snapshot type. It captures an owned
UTF-8 baseline once and serializes each checked snapshot into a batch-local
`MemoryStream`. It compares the entire written byte span with `SequenceEqual`,
without a digest or field subset. Both length and position reset before every
write, including after an earlier serialization exception. No engine state is
restored or cached. The scratch buffer retains its largest capacity until the
synchronous batch ends; it is disposed on normal, budget and exception exits.
Allocated-byte reductions do not establish a peak-memory reduction.

The helper also supports the original string path. Profiling now splits
`snapshot.build` from `snapshot.serialize`; both include initial capture and all
checks. They nest inside the existing comparison/reference scopes where applicable.
Serialization includes equality checking and capture buffer allocation. Do not sum
these scopes with their parents. The ordinary snapshot API and other callers are
unchanged.

With the flag, each benchmark prefix validates replay against forward/reversed
UTF-8 clone successors, compares the complete clone report with the string clone,
and verifies both separately profiled reports. Timed runs rotate replay, UTF-8 clone
and string clone order over three repetitions. The same-run `stringCloneMs` and
`stringCloneBytes` are the control for this optimization; prior reports are not
timing controls. Profiling overhead and validation serialization are excluded from
timed totals. Runtime, fixture restrictions and Unknown semantics remain as above.

Regression cases compare against `GameState.SnapshotJson()` for card scalar edits,
ordered destroyed-ID lists, canonical dictionary order, nested card references,
nulls, Chinese/emoji/escaped strings, scratch growth and shrinkage, and recovery
after a NaN serialization failure. Whole-report comparisons cover root-observer
exceptions, prefix mismatch, budgets, unsupported ops, nested choices, and reference
mutations to snapshot fields, RNG, action trace and VM diagnostics. Forward and
reversed observed successors are checked against replay. These tests preserve the
existing bounded snapshot coverage; they do not prove private-state equivalence.

The DeepSeek worker supplied a source-only field/order/options checklist. The
primary agent verified the relevant snapshot and nested card projections, retained
the existing companion checks, and implemented/ran the regressions. Its proposed
API behaviors were hypotheses to test, not game authority or performance evidence.
The implementation reuses the options instance and generic snapshot type directly;
it does not rely on a manual field inventory or on the worker's overload labels.

Four same-run controls on .NET 10.0.12 x64 (three timed repeats; empty execution-only
vectors) produced the following totals. Add `--utf8-snapshots` to the four commands
above and change output names to `clone-utf8-plain16-20261102.json`,
`clone-utf8-meta16-20261102.json`, `clone-utf8-gersov16-20261102.json` and
`clone-utf8-plain32-seed2-20261102.json` respectively.

| Fixture | String / UTF-8 clone ms | String / UTF-8 clone allocated bytes |
|---|---:|---:|
| Arado / seed 1 / 16 | 2958.5 / 2711.5 | 4,918,566,448 / 4,191,895,472 |
| 英日–日澳快攻 / seed 1 / 16 | 11704.9 / 10245.2 | 10,703,613,024 / 9,859,791,592 |
| 德芬车–苏澳中速 / seed 2 / 16 | 3278.0 / 2996.9 | 5,416,376,720 / 4,612,229,200 |
| Arado / seed 2 / 32 | 9342.5 / 7660.1 | 14,082,501,416 / 12,056,988,080 |

All 4,546 candidate comparisons reached the root observer and matched replay
classification and observed successor state in both clone traversal orders. String
and UTF-8 full reports matched: 1,477 Accepted, 3,023 Rejected and the same 46
unresolved Unknown (37 choice-policy-dependent, nine unimplemented-call). There
were zero batch errors or mismatches. These overlap previous engineering fixtures
and add no unique game-rule coverage. The measured same-run reductions were
8.3–18.0% elapsed and 7.9–14.8% allocated bytes; timing noise remains substantial.

In the separate profiling passes, snapshot serialization/equality/capture allocated
5.3–10.7 MB with the buffer versus 247.5–685.9 MB with strings. Snapshot construction
still allocated 705.2–1,955.8 MB. Both modes construct the full snapshot on every
check, so the buffer does not eliminate the cost of nested projections and sorting.
Next: split graph-copy costs for the expensive 英日 fixture, then evaluate snapshot
construction separately before attempting schema-level changes. Keep the option
experimental while extending workload coverage.

All six targeted CloneActionProbe cases, all 1,091 BotSim selftests and nine NNTrain
policy evaluation selftests passed in a separate publishing snapshot excluding the
pre-existing unrelated dirty probe edits. Tested source bytes match the integrated
files; unchanged checkout data bytes were preserved for native-default hash checks.
Existing NNTrain CS8632 warnings remain. Audit matrix certification is unchanged.


## Coarse engine graph profiling (2026-11-02)

The separate diagnostic pass now splits `engine_clone` into `clone.boundary`,
`clone.database`, `clone.state`, `clone.metadata` and `clone.api`. State children
measure the first `_cardsBySide` traversal, remaining indexes, historical/log data,
and location indexes. API children measure dispatch rebuilding, transient
buffers/traces, static pool and variants, GCS caches, and persistent VM state.
The internal `CloneStageProfile` supplies the same timing/allocation scope used by
`CloneProbeProfile`, without an Engine dependency on Training. The public clone
entry point passes a null profile; ordinary and timed probe runs collect no stages.
No clone payload, whitelist, traversal order, memo, callback or isolation rule changed.

All children include recursively reached objects at that point in the existing
traversal. A later reference to a memoized object is only a lookup, so a stage is
an attribution to the first traversal, not an intrinsic size for that component.
Parent scopes include children, scope bookkeeping and unlisted constructor work;
never sum parents and children. These are elapsed time and current-thread allocated
bytes, not object counts or peak/retained memory. No per-node timers are added.

Run from the publishing snapshot with the same command options as above:

```powershell
dotnet run --project tools/BotSim -c Release --no-build -- probe-actions-clone --utf8-snapshots --steps 16 --repeats 3 --out 'D:/Codex Workshop/KLink-bot/out/audit/clone-graph-meta16-20261102.json'
dotnet run --project tools/BotSim -c Release --no-build -- probe-actions-clone --utf8-snapshots --plain --steps 16 --repeats 3 --out 'D:/Codex Workshop/KLink-bot/out/audit/clone-graph-plain16-20261102.json'
```

The two reports use .NET 10.0.12 x64, seed 1 and empty execution-only vectors.
The table is from one separate UTF-8 profiling run per prefix, not timed-repeat
sums. Parent rows are explicitly marked; remaining small scopes are in the reports.

| Stage | 英日–日澳快攻 ms / allocated bytes | Arado ms / allocated bytes |
|---|---:|---:|
| engine_clone (parent) | 1955.7 / 1,982,482,544 | 231.8 / 492,636,528 |
| clone.database | 218.8 / 338,956,976 | 0.1 / 96,512 |
| clone.state (parent) | 319.8 / 532,476,552 | 213.3 / 457,260,296 |
| clone.state.cards | 271.7 / 438,769,464 | 177.2 / 382,405,328 |
| clone.state.indexes | 15.1 / 20,553,808 | 12.5 / 17,767,440 |
| clone.state.history | 30.9 / 70,868,592 | 21.9 / 55,084,376 |
| clone.api (parent) | 1414.8 / 1,109,320,728 | 16.8 / 33,742,992 |
| clone.api.pool | 1393.2 / 1,071,512,104 | 0.4 / 476,528 |
| clone.api.dispatch | 14.5 / 30,691,680 | 11.4 / 26,908,752 |

For this complex fixture, pool copying accounts for 54.0% of clone allocation
and 71.2% of clone elapsed time; database view copying accounts for 17.1% of
allocation and the first state-card traversal 22.1%. `CloneView` copies the warmed
mutable `_codeByName` dictionary. The Arado control has an unwarmed reverse index
and no materialized static pool at these prefixes, so its state-card traversal
accounts for 77.6% of clone allocation. These workload-dependent differences
explain why snapshot serialization alone could not address the complex fixture.
Timing is noisy and this change makes no speedup claim.

Across 32 prefixes, all 1,614 candidate comparisons matched replay classification
and observed successors with both clone orders, with zero batch errors: 559 Accepted,
1,010 Rejected, 45 Unknown. The complex fixture retains 37 choice-policy-dependent
and eight unimplemented-call Unknown; the control has no Unknown. String/UTF-8 and
profiled/unprofiled reports also matched. These are overlapping engineering
fixtures, not additional PvP certification or an authoritative replay oracle.

The DeepSeek worker provided a packet-only source inventory. The primary checked
its relevant claims against the clone constructors, `EnsureStaticPool`, the slot
adapter and pool isolation regressions. Its suggestion that target dictionaries
resize while filling is incorrect here: the adapter installs copied backing slots
directly. Its suggested per-card/per-container timing boundaries would overlap and
add overhead, so they were not adopted. Unmeasured dominance claims were not used.

Decision: retain deep copying. Pool templates are mutable `CardInstance` objects;
blueprint variables, trigger buffers, filtered lists and cache entries can alias
those same objects and form cycles. Existing regressions exercise mutations in
both directions and repeated cloning, so sharing or rebuilding the pool would
break the supported graph contract. The reverse index also remains independently
mutable. A bounded next optimization is to evaluate reducing delegate/reflection
allocation when copying empty containers while preserving exact capacity, slots,
free chains, comparers and the single memo. This requires its own measurements and
continuation/isolation checks before integration. Snapshot construction remains a
separate measured target; no schema or default probe mode changed.

Validation in `out/audit/clone-graph-publish-20261102` passed all six targeted
CloneActionProbe cases, all 1,091 BotSim selftests and nine NNTrain policy evaluation
checks. The existing observational-profile regression now also checks every graph
stage count and unwinding after both an idle-boundary rejection and unsupported
payload failure inside state traversal. The isolated snapshot excludes unrelated
local probe edits and preserves unchanged checkout data bytes. NNTrain retains its
existing CS8632 warnings. No audit matrix certification was changed.

## Unallocated container clone allocation (2026-11-02)

`CollectionSlotClone` now takes a short path only when the source backing entry
array is null. It validates null buckets, zero count/free count and the reviewed
zero or minus-one free-list sentinel, then copies the exact scalar metadata and
raw comparer. Fresh destination identity and memo registration still happen
before the adapter. Cached dictionary views are cleared so they cannot retain
the source owner. Runtime/layout, exact-type and comparer gates remain in force.
Allocated empty containers (reserved capacity, removed entries or Clear) retain
the existing slot/free-chain copy path; Count alone never selects the short path.

The graph adapter receives its clone context directly. Method-group delegates
are created only after the storage gate, and capturing lambdas remain in separate
helpers so they allocate no closure before that gate. This removes work without
sharing mutable containers, changing traversal order or expanding payload support.

Fresh before/after reports use baseline `b95e967`, .NET 10.0.12 x64, seed 1,
16 prefixes and three timed repeats, with the same UTF-8 snapshot options as the
preceding commands. The control adds `--plain`. Reports are
`out/audit/clone-empty-{meta,plain}16-{before,after}-20261102.json`.

| Timed clone-path allocation, three repeats | Before bytes | After bytes | Reduction |
|---|---:|---:|---:|
| 英日–日澳快攻 | 9,859,619,192 | 8,099,834,128 | 17.85% |
| Arado control | 4,191,910,240 | 3,748,142,240 | 10.59% |

These totals include comparisons and action execution, not just engine cloning.
Separate single profiling runs attribute the changes as follows; parent/child
rows overlap and must not be summed.

| Profile stage | Complex before / after bytes | Control before / after bytes |
|---|---:|---:|
| engine_clone (parent) | 1,982,482,552 / 1,395,831,784 | 492,636,528 / 344,725,632 |
| clone.state.cards | 438,769,568 / 266,471,512 | 382,405,328 / 241,660,096 |
| clone.api.pool | 1,071,512,008 / 666,674,080 | 476,528 / 205,088 |
| clone.database | 338,956,976 / 338,956,960 | 96,512 / 96,512 |

Timed clone-path elapsed totals were 9,891.6 / 8,125.8 ms for the complex
fixture and 2,745.2 / 2,688.8 ms for the control. These sequential runs are
subject to machine load; replay control time also changed, so no causal timing
speedup is claimed. Allocation reductions are workload-specific, not a training
throughput forecast.

All 1,614 candidate comparisons across 32 prefixes retained the same counts,
reasons, engine/root counts and zero batch errors before/after, with zero
mismatches against replay classification and observed successors in both clone
orders. Totals remain 559 Accepted, 1,010 Rejected and 45 Unknown (37 choice
policy dependent, eight unimplemented calls). String/UTF-8 and profiled/unprofiled
comparisons also passed inside the benchmark. These are engineering fixtures,
not authoritative PvP rule evidence or additional rule certification.

The new regression covers three supported string comparers across never-allocated,
reserved, add/remove, Clear and TrimExcess states. It checks exact metadata,
capacity, storage identity, cached views, repeated aliases, subsequent insertion
order, two-way isolation and rejection of malformed metadata. It passed against
both baseline production code and the optimized code. All nine CardGraphClone
checks, the full 1,092 BotSim selftests (including CloneActionProbe), and nine
NNTrain policy evaluation checks passed in the isolated publishing snapshot
`out/audit/clone-empty-publish-20261102`. NNTrain retains existing CS8632 warnings.
Snapshot sources were compared with intended working bytes, with unrelated local
probe edits restored to HEAD in the snapshot and left untouched in the checkout.

The DeepSeek worker supplied a private source-review checklist through the
`deepseek-worker` skill. The primary independently checked its useful metadata,
closure-boundary and alias suggestions against code and regressions. Worker
claims were not used as rule evidence or substitutes for measurements.

Next measured target: the complex fixture's independently mutable database
reverse index still allocates about 339 MB per profiling run. Investigate its
copy cost while preserving capacity, enumeration, mutability and isolation;
sharing or dropping the warmed index is outside the accepted clone contract.


## Immutable string index clone allocation (2026-11-02)

The graph-context `CollectionSlotClone` adapter now specializes exact
`Dictionary<string, string>` instances after the existing unallocated-storage
gate. Strings retain their identities in `CardGraphClone`; copying the entry and
bucket arrays therefore preserves hashes and collision links without per-entry
boxing, remapping or rehashing. The raw comparer, including its randomized hash
seed, scalar metadata, capacity, occupied slots and encoded free chain are
preserved. Both arrays remain independent and cached Keys/Values views are
cleared. Memo registration, exact-type/comparer admission and graph aliases retain
their previous boundaries. The function-transform overload keeps its original
path because supplied transformations need not preserve strings.

Typed expression getters for the validated Entry next/hash fields are compiled
once per cached layout. Runtime and full field-layout checks still require the
reviewed .NET 10 x64 layout. Raw bucket chains are checked for range, cycles,
duplicate or missing slots, free-slot references and hash/bucket agreement;
malformed chains are rejected rather than repaired by rehashing. Active counts
and the exact encoded free chain are also checked. This remains an in-process
clone adapter, not a persistence format. No payload whitelist, frozen catalog
sharing, card data or PvP rule behavior was changed.

Before/after reports use baseline `4d5757b`, .NET 10.0.12 x64, seed 1,
16 prefixes and three timed repeats, with `probe-actions-clone --utf8-snapshots
--steps 16 --repeats 3`; the control adds `--plain`. Reports are
`out/audit/clone-string-{meta,plain}16-{before,after}-20261102.json`.

| Timed clone-path allocation, three repeats | Before bytes | After bytes | Reduction |
|---|---:|---:|---:|
| Complex fixture (英日–日澳快攻) | 8,099,728,664 | 7,330,463,744 | 9.50% |
| Arado control | 3,748,218,792 | 3,718,732,632 | 0.79% |

These totals include comparison and action execution. Separate single profiling
runs report the following allocations; nested parent/child rows overlap and must
not be summed.

| Profile stage | Complex before / after bytes | Control before / after bytes |
|---|---:|---:|
| engine_clone (parent) | 1,395,829,400 / 1,139,619,920 | 344,725,632 / 334,921,408 |
| clone.database | 338,956,960 / 100,117,760 | 96,512 / 96,512 |
| clone.api.pool | 666,674,696 / 656,793,560 | 205,088 / 205,088 |
| snapshot.build | 816,764,576 / 816,764,704 | 705,176,752 / 705,176,752 |

The complex database stage allocates 70.46% less and the engine_clone parent
18.36% less. Whole clone-path elapsed totals were 8,790.9 / 8,883.9 ms for the
complex fixture and 2,586.9 / 2,665.4 ms for the control. No overall timing speedup
is established. Allocation gains are workload-specific; they do not establish
training throughput or peak-memory reductions.

All 1,614 candidate comparisons across 32 prefixes retained identical counts,
reasons, engine/root counts and zero batch errors. Both versions have zero
mismatches against replay classification and observed successors in both clone
orders. Totals remain 559 Accepted, 1,010 Rejected and 45 Unknown (37 choice
policy dependent, eight unimplemented calls). The benchmark's string/UTF-8 and
profiled/unprofiled comparisons also passed. These simulator engineering fixtures
do not certify PvP rules; no audit matrix certification was refreshed.

New regression coverage includes five comparer representations: default,
Ordinal, OrdinalIgnoreCase, reference equality and the runtime's randomized
Ordinal wrapper. It covers never-allocated/reserved/populated/removed/Clear/
TrimExcess storage, collisions, null values, raw slots and bucket equality,
string identity, graph aliases, views, continuation, repeated clones, two-way
isolation and malformed metadata/bucket rejection. Valid-storage coverage also
passes against baseline production code. All six CardApiClone cases, all 1,094
BotSim selftests and nine NNTrain policy evaluation checks passed in
`out/audit/clone-string-publish-20261102`. NNTrain retains existing CS8632 warnings.
The isolated snapshot preserves checkout data bytes and excludes unrelated local
probe edits. Intended source/test bytes were checked against the tested snapshot.

The DeepSeek worker supplied a private bounded source-review checklist through
`deepseek-worker`. The primary independently verified the useful comparer,
metadata, alias and bucket-validation points. Its missing-whitelist inference
was rejected after checking the existing GameStateClone string-map admission;
worker output was not used as authoritative rule evidence.

Next measured target: snapshot.build allocates about 817 MB in the complex
profiling run, exceeding the API pool's 657 MB. Investigate intermediate snapshot
materialization while preserving the exact schema, ordering, null and numeric
encoding and comparison contract. Any implementation requires fresh equivalence
checks and measurements; the default probe mode remains unchanged.


## Empty buff-ledger snapshot projections (2026-11-02)

`CardInstance.Snapshot()` now checks once whether its concrete BuffsBySource
ledger is empty. In that case, its 24 ledger projections return five empty
string arrays, 18 fresh string/int dictionaries and one fresh string/string-array
dictionary directly. This avoids constructing filtering and sorting pipelines
for an input with no elements. Mutable dictionaries remain independent for every
field and every capture, with the same default comparer as the original
ToDictionary calls. No snapshot schema, field order, null handling, numeric or
string encoding, card data or gameplay behavior changed.

All 24 nonempty projection expressions remain byte-identical to baseline,
including explicit zero-entry presence and the three armor maps that retain
source enumeration order. A nonempty ledger whose filtered results are empty
still uses those original expressions. Snapshot capture continues to require
caller-owned state without concurrent mutation; this change adds no atomic or
thread-safe capture contract. Other snapshot projections and record property
initializers are unchanged, so their allocation cost remains.

Fresh before/after benchmarks use baseline `a1cada7`, .NET 10.0.12 x64, seed 1,
16 prefixes and three timed repeats, with `probe-actions-clone --utf8-snapshots
--steps 16 --repeats 3`; the control adds `--plain`. Reports are
`out/audit/snapshot-empty-{meta,plain}16-{before,after}-20261102.json`.
Both isolated snapshots use exact checkout card-data bytes. The final tested
production source differs from the measured implementation only by two comments.

| Timed path allocation, three repeats | Before bytes | After bytes | Reduction |
|---|---:|---:|---:|
| Complex clone path | 7,330,091,488 | 5,918,091,072 | 19.26% |
| Arado clone path | 3,717,919,496 | 2,615,263,400 | 29.66% |
| Complex replay path | 14,848,467,304 | 12,460,160,600 | 16.08% |
| Arado replay path | 6,984,341,640 | 5,881,202,392 | 15.79% |

These totals include comparison and action execution. Separate single profiling
runs attribute the following allocations; nested scopes overlap and cannot be
summed with their parents.

| Profile stage | Complex before / after bytes | Control before / after bytes |
|---|---:|---:|
| snapshot.build | 816,764,672 / 396,429,328 | 705,176,752 / 337,454,512 |
| snapshot.serialize | 5,395,584 / 5,395,584 | 5,279,216 / 5,279,216 |
| engine_clone | 1,139,278,824 / 1,139,460,888 | 334,623,424 / 334,879,360 |
| clone.api.pool (nested in engine_clone) | 656,794,640 / 656,793,440 | 205,088 / 205,088 |

Snapshot construction allocation falls 51.46% / 52.15%. The clone adapter itself
is unchanged. Timed clone totals were 8,460.7 / 7,422.5 ms for the complex fixture
and 2,505.4 / 2,174.3 ms for the control; replay totals were 12,604.1 / 10,776.0 ms
and 3,884.9 / 3,858.4 ms. These sequential measurements are machine-load dependent;
they are observed fixture timings, not a causal throughput estimate, peak-memory
measurement or forecast for AI training.

The 1,614 candidates across 32 prefixes retain identical classifications, reasons,
engine/root counts and zero batch errors before/after. Each run also reports zero
mismatches against simulator replay classification and observed successors in
both clone orders; string/UTF-8 and profiled/unprofiled report checks pass. These
are engineering fixtures, not authoritative replay rule evidence or additional
PvP certification. No audit matrix evidence was refreshed.

The new regression freezes all 24 baseline projection expressions from
`a1cada7`. It checks exact JSON against those projections for empty, sparse and
combined ledgers, independent zero/null presence, negative values, unsorted
sources and both duration slots. It also checks empty array identity, dictionary
comparers, independent captures and mutation isolation in both directions.
The regression passes on baseline production and optimized production. The
final isolated snapshot `out/audit/snapshot-empty-publish-20261102` passes all
1,095 BotSim selftests and nine NNTrain policy evaluation checks; NNTrain retains
existing CS8632 warnings. Unrelated local probe edits remain excluded from that
snapshot and untouched in the checkout.

The DeepSeek worker supplied a private field/type/comparer review through the
`deepseek-worker` skill. The primary verified its 24-field inventory and useful
fresh-dictionary and ordering points against source and regressions. Its claim
of predicate invocations on empty input is incorrect: there are no elements, so
predicates/selectors are not invoked. Its broad exception claim was not adopted;
unchanged snapshot projections can still throw, and their exception behavior is
covered by the existing UTF-8 oracle. The retained empty dictionaries still
allocate; the demonstrated saving is from skipped intermediate LINQ work.

Next measured target: the complex API pool clone still allocates about 657 MB,
now exceeding snapshot.build's 396 MB. Inspect that graph's container traversal
while preserving destination ownership, source aliases, default values and the
existing supported-payload boundaries. Sharing mutable pool entries or expanding
rule certification is outside this performance change.


## Typed collection metadata copies (2026-11-02)

`CollectionSlotClone.Layout` now caches three typed integer getters and one
field-to-field storage-copy action per closed generic layout. They are compiled
only after the existing .NET 10 x64 field-name/type checks. The getters replace
boxed reflection reads of `_count`, `_freeCount` and `_freeList`; the action
copies the five scalar fields (`_fastModMultiplier`, `_count`, `_freeList`,
`_freeCount`, `_version`), the exact raw comparer reference and the supplied
entries/buckets references, then resets map-only cached views to null. Read and
assignment order remain unchanged. Array buffers still belong to the destination;
no mutable pool templates are shared. The existing entry remapping, capacity,
free-chain, occupied-slot and immutable-string bucket checks remain unchanged.

This removes per-container metadata boxing and reflection dispatch, including
on never-allocated containers. It does not remove dictionary/array allocations,
per-entry reflection on the general path, or graph memoization. The accessors are
cached through the existing Lazy layout cache. Compilation now applies to all
admitted layouts, rather than only the existing string-map entry readers. This
batch validates normal .NET 10.0.12 x64 JIT execution. No NativeAOT, trimming or
dynamic-code-disabled compatibility is certified; the repository project files
contain no AOT/trimming configuration. Benchmarks warm both paths, so cold layout
compilation cost and retained compiled-code memory are outside these numbers.

The existing metadata regression now also covers populated maps/sets, deletion
holes, graph remapping and signed version extremes (`int.MinValue` and
`int.MaxValue`). It still checks raw metadata/comparer equality, independent array
buffers, cleared cached views, insertion continuation, aliases, two-way mutation
isolation and malformed unallocated metadata rejection. All nine CardGraphClone
cases pass with this expanded regression on baseline and optimized production.

Fresh before/after reports use baseline `4d4f4d5`, seed 1, .NET 10.0.12 x64,
`probe-actions-clone --utf8-snapshots --steps 16 --repeats 3`; the control adds
`--plain`. Reports: `out/audit/slot-metadata-{meta,plain}16-{before,after}-20261102.json`.
The isolated directories are `out/audit/slot-metadata-before-20261102` and
`out/audit/slot-metadata-publish-20261102`. They use identical checkout catalog and
IR bytes and exclude the unrelated local action-probe edits.

| Timed path, three repeats | Before bytes | After bytes | Reduction |
|---|---:|---:|---:|
| Complex clone path | 5,918,526,824 | 4,678,666,256 | 20.95% |
| Arado clone path | 2,615,379,128 | 2,304,131,352 | 11.90% |
| Complex replay path | 12,470,506,456 | 12,460,724,976 | 0.08% |
| Arado replay path | 5,881,062,632 | 5,881,593,032 | -0.01% |

| Separate profile stage | Complex before / after bytes | Control before / after bytes |
|---|---:|---:|
| clone.api.pool | 656,793,400 / 374,453,008 | 205,088 / 60,320 |
| engine_clone | 1,139,469,312 / 726,311,152 | 334,927,744 / 231,094,272 |
| clone.state.cards | 261,301,728 / 138,957,360 | 233,951,680 / 136,666,816 |
| snapshot.build | 396,429,328 / 396,429,328 | 337,454,512 / 337,454,512 |
| snapshot.serialize | 5,395,584 / 5,395,584 | 5,279,216 / 5,279,216 |

Complex pool-stage allocation falls 42.99%. The metadata change applies to all
admitted graph containers, so state-card cloning also benefits. Profile stages
nest and cannot be added to their parents. Timed complex clone/replay values are
8,422.6 / 6,436.3 ms and 13,519.3 / 11,488.9 ms; control values are
2,331.3 / 1,944.7 ms and 3,728.2 / 3,599.0 ms. These sequential fixture timings
are machine-load dependent: replay time also changes despite near-stable replay
allocation. They are not a causal speedup estimate, peak-memory measurement or
forecast for training throughput.

All 32 invariant rows match before/after: candidate classifications/reasons,
engine/root counts, clone counts and errors. Complex has 815 determinate and
45 Unknown candidates; control has 754 determinate and zero Unknown, totaling
1,614 candidates. Each run independently reports zero observed-successor and
classification mismatches against simulator replay, including reverse clone
order; string/UTF-8 and profiled/unprofiled checks pass. Reports contain no
cross-version full-successor hashes. These engineering fixtures establish no
new PvP rule authority or card certification; no audit matrix was refreshed.

The DeepSeek worker reviewed a private baseline source copy. The primary checked
its inventory against baseline bytes and the implementation. Its comparer,
view-owner, buffer ownership and compilation-scope observations were useful.
Its claim of ten scalar boxes per storage copy double-counts reads and writes:
there are five scalar GetValue boxing operations, and SetValue consumes those
objects. Its warning about wrong-instance exception differences does not apply
to the exact-type internal callers. No new guarantee is made for concurrent
mutation, invalid internal callers or unreviewed runtimes. Worker output is not
authoritative game-rule evidence.

Final validation in the isolated optimized snapshot: all 1,095 BotSim selftests
and nine NNTrain policy evaluation checks pass; NNTrain retains existing CS8632
warnings. The intended production/test bytes match that tested snapshot.

Next, measure the clone probe at a representative training call site before
selecting another micro-optimization. The complex profile now spends about
374 MB in pool cloning and 396 MB in snapshot construction; these measurements
alone do not identify actual training throughput or justify changing the default
probe/admission contract.

### Actual action-label call path measurement (2026-11-02)

Source inspection confirms `ActionDatasetTool.Dump` calls the public
`OracleActionEpisodeRunner.Run`, which rebuilds a fresh planning engine and
replays every confirmed prefix for each decision. It does not call
`CloneActionProbe`. Consequently collection-clone microbenchmarks above are
not evidence of current `dump-actions` throughput. The empty-buff snapshot
change can affect this path directly.

`NNTrain benchmark-action-episodes --seed 1 --repeats 3 --out report.json`
now measures this public runner with the loaded card database, IR and real card
vectors. Each process warms one full episode and then measures three full
episodes, including existing admission and admitted-row UTF-8/gzip into memory.
It uses 39 alternating Arado Ar 196 / light infantry cards per side, copied
from the existing admitted action-data regression fixture. This is a synthetic
engineering fixture, not a legal constructed deck or representative card pool.
No default teacher, admission contract, dataset schema or training policy changes.

The measured total excludes startup, data loading, fingerprints, warm-up,
file I/O, dataset header/manifest and model training. Managed allocation is
current-thread allocated bytes, not resident/peak memory. Canonical uncompressed
label hashes use LF; compressed byte identity is not the equality oracle.
Each repeat must match the warm-up fingerprint, including admission reasons,
all serialized candidate features/commands, observations, chosen indices,
afterstates, final teacher/replay snapshots, full action traces and replay RNG.
Rejected episodes retain diagnostics and exit 2; they cannot emit admitted rows.

Comparison uses isolated sources with identical benchmark host bytes and data.
The baseline has the two affected production files from `a1cada7`; optimized
has production at `c7a4203`. Other source/test files are held at `c7a4203`.
Execution order is seed 1 baseline/optimized, then seed 2 optimized/baseline.
Each table entry is the median of three measured full episodes:

| Seed | Rows / candidates / prefix roots | Total ms before / after | Total managed bytes before / after |
|---|---:|---:|---:|
| 1 | 256 / 6,189 / 32,640 | 5,579.0 / 5,215.4 | 7,852,745,720 / 6,654,095,880 |
| 2 | 135 / 4,073 / 9,045 | 2,243.0 / 1,808.8 | 3,104,897,400 / 2,472,329,552 |

Allocation falls 15.26% and 20.37%; observed elapsed medians fall 6.52% and
19.36%. Sequential timings depend on machine load and are not a causal estimate
or a projection for all decks, data generation or optimizer throughput.
Cross-version fingerprints match exactly for both seeds, and all JSON input
hashes match each other and current local input files. This is simulator
consistency, not comparison against authoritative client execution or proof of
private VM-state equivalence. No PvP certification or audit matrix refresh.

Durable reports are `out/audit/action-episode-seed{1,2}-{before,after}-20261102.json`
and `out/audit/action-episode-comparison-20261102.json`, including host hashes,
source verification counts, sample values and measured scope. The DeepSeek
worker's private call-site inventory was independently checked against source;
it is not game-rule authority.

Validation: all 1,095 BotSim selftests, nine NNTrain policy evaluation checks
and 54 action-dataset checks pass in the isolated optimized snapshot. Invalid
repeat count is rejected. Existing CS8632 warnings remain. Tested production
and benchmark source bytes match the intended published files.

Next priority is the quadratic prefix replay (n(n-1)/2 roots), not another
collection-clone micro-optimization. A non-default full-episode clone planning
experiment must first match the current teacher's admitted labels and outcomes,
retain explicit unsupported-state/rejection handling and demonstrate measured
benefit before any default training-path integration.
