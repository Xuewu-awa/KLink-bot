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
