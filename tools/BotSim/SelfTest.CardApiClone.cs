using System.Collections;
using System.Reflection;
using KLink.Bot.Cards;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static T ApiField<T>(CardApi api, string name) => (T)CloneFields(typeof(CardApi))
        .Single(f => f.Name == name).GetValue(api)!;

    private static CardDatabase CopyDatabaseView(CardDatabase source, CardGraphClone memo) => (CardDatabase)typeof(CardDatabase)
        .GetMethod("CloneView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(source, [memo])!;

    private static T ConstructClone<T>(params object[] args)
    {
        try { return (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
            null, args, null)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException)
        { throw ex.InnerException; }
    }

    // Test assembly wiring only. Production has no engine checkpoint yet.
    private static MatchEngine CopyApiComponents(MatchEngine source, bool apiFirst = false)
    {
        var memo = VmCloneContext();
        var db = CopyDatabaseView(source.State.Database, memo);
        var destination = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), 1);
        CardApi? api = apiFirst ? ConstructClone<CardApi>(destination, source.Api, memo) : null;
        var state = CopyVmGraph(memo, source.State);
        CloneFields(typeof(MatchEngine)).Single(f => f.Name == "<State>k__BackingField").SetValue(destination, state);
        api ??= ConstructClone<CardApi>(destination, source.Api, memo);
        typeof(MatchEngine).GetProperty("Api")!.SetValue(destination, api);
        return destination;
    }

    private static string? CardApiCloneFields(CardDatabase db)
    {
        string[] fields = ["_playedCardBroadcastDone", "_playedCardBroadcastDepth", "_resolvingTriggerQueue",
            "_engine", "_triggerBuffers", "_triggerDepth", "_dispatch", "_staticPoolDb", "_staticPool",
            "_staticPoolReservationPolicy", "_staticPoolBlacklistPolicy", "_staticPoolVariants",
            "_staticPoolGeneration", "_gcsPurityCache", "_gcsCache"];
        string[] properties = ["Vm", "TriggerTrace", "AuraTrace"];
        if (!fields.Concat(properties.Select(n => $"<{n}>k__BackingField")).Order()
            .SequenceEqual(CloneFields(typeof(CardApi)).Select(f => f.Name).Order())) return "CardApi field inventory changed";
        string[] dbFields = ["_byName", "_codeByName", "<DeckCodeIds>k__BackingField", "<VariantFallbacks>k__BackingField"];
        if (!dbFields.Order().SequenceEqual(CloneFields(typeof(CardDatabase)).Select(f => f.Name).Order()))
            return "CardDatabase field inventory changed";
        var entry = typeof(CardApi).GetNestedType("GcsCacheEntry", BindingFlags.NonPublic)!;
        if (!new[] { "Generation", "Cards", "MarkAsSeen", "KeepOrder" }.Select(n => $"<{n}>k__BackingField").Order()
            .SequenceEqual(CloneFields(entry).Select(f => f.Name).Order())) return "GCS entry field inventory changed";
        var (engine, state) = EmptyBoard(db);
        engine.Api.TriggerTrace = engine.Api.AuraTrace = engine.Api.Vm.Trace = engine.Api.Vm.StepTrace = state.RandomTrace;
        state.RandomTrace.Add("retained");
        ApiField<HashSet<int>>(engine.Api, "_playedCardBroadcastDone").Add(27);
        CloneFields(typeof(CardApi)).Single(f => f.Name == "_staticPoolGeneration").SetValue(engine.Api, 19);
        engine.Api.Vm.UnimplementedCalls["retained"] = 5;
        var destination = CopyApiComponents(engine);
        var copy = destination.Api;
        if (!ReferenceEquals(copy.TriggerTrace, destination.State.RandomTrace)
            || !ReferenceEquals(copy.TriggerTrace, copy.AuraTrace) || !ReferenceEquals(copy.TriggerTrace, copy.Vm.Trace)
            || !ReferenceEquals(copy.TriggerTrace, copy.Vm.StepTrace) || ReferenceEquals(copy.TriggerTrace, state.RandomTrace)
            || ApiField<int>(copy, "_staticPoolGeneration") != 19
            || !ApiField<HashSet<int>>(copy, "_playedCardBroadcastDone").SetEquals(new[] { 27 }))
            return "raw values or shared trace aliases lost";
        copy.AuraTrace!.Add("copy"); ApiField<HashSet<int>>(copy, "_playedCardBroadcastDone").Add(28);
        copy.Vm.UnimplementedCalls["retained"]++;
        if (state.RandomTrace.Count != 1 || engine.Api.Vm.UnimplementedCalls["retained"] != 5
            || ApiField<HashSet<int>>(engine.Api, "_playedCardBroadcastDone").Count != 1) return "copy mutated source";
        state.RandomTrace.Clear();
        if (copy.TriggerTrace!.Count != 2) return "source mutated copy";
        var again = CopyApiComponents(destination, apiFirst: true);
        if (ReferenceEquals(again.Api.TriggerTrace, copy.TriggerTrace)
            || !ReferenceEquals(again.Api.TriggerTrace, again.State.RandomTrace)) return "repeat/API-first aliases lost";
        return null;
    }

    private static string? CardApiClonePools(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        state.CardReservations = new CardReservationPolicy(Array.Empty<CardReservationChange>(), 10);
        state.CardBlacklist = new CardBlacklistPolicy(new Dictionary<string, long>(), 10);
        EffectContext Ctx(MatchEngine e, CardInstance card) => new() { Engine = e, State = e.State, Self = card, Controller = Side.Left };
        const string probeName = EffectDamageProbeName;
        using var probe = InstallSelectionDrawProbe(probeName, "GetChooseSpawnCards");
        // Controlled local-only IR, executed by the real VM/cache/pool path.
        // This tests engineering isolation, not the card's actual rules or purity.
        var locals = (IDictionary<string, IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(probeName)!.Locals!;
        locals["GetChooseSpawnCards"] = new[] {
            new KismetStep(0, "set", null, [], [], new KismetExpr { Call = "GetAllActiveStaticCards",
                Context = new() { Obj = "cardFunction" }, Args = [new() { Bool = false }, new() { Bool = false }] },
                null, -1, "cards", null),
            new KismetStep(1, "set", null, [], [], new() { Bool = true }, null, -1, "keepOrder", null),
            new KismetStep(2, "set", null, [], [], new() { Bool = true }, null, -1, "markAsSeen", null),
            ProvenanceStep(3, "return") };
        var selecting = state.Create(probeName, Side.Left, CardLocation.HandLeft, 0);
        var candidates = engine.Api.GetChooseSpawnCards(Ctx(engine, selecting), selecting, out bool seen, out bool ordered);
        var cache = ApiField<IDictionary>(engine.Api, "_gcsCache");
        if (candidates.Count == 0 || !cache.Contains(selecting.Name) || !seen || !ordered)
            return "controlled IR did not populate a nonempty candidate cache with flags";
        var pool = ApiField<List<CardInstance>>(engine.Api, "_staticPool");
        var buffers = ApiField<List<List<CardInstance>>>(engine.Api, "_triggerBuffers");
        int bufferOffset = buffers.Count;
        buffers.Add(candidates); buffers.Add(pool);
        selecting.BlueprintVariables["savedPool"] = pool;
        selecting.BlueprintVariables["savedCandidates"] = candidates;
        selecting.BlueprintVariables["buffers"] = buffers;
        selecting.BlueprintVariables["cache"] = cache;
        selecting.BlueprintVariables["variants"] = ApiField<object>(engine.Api, "_staticPoolVariants");
        selecting.BlueprintVariables["purity"] = ApiField<object>(engine.Api, "_gcsPurityCache");
        selecting.BlueprintVariables["cachedEntry"] = cache[selecting.Name];
        candidates[0].BlueprintVariables["owner"] = selecting;
        candidates[0].BlueprintVariables["entry"] = cache[selecting.Name];
        candidates[0].BlueprintVariables["marker"] = 73;
        var destination = CopyApiComponents(engine);
        var copiedSelecting = destination.State.ById(selecting.CardId)!;
        var copiedPool = ApiField<List<CardInstance>>(destination.Api, "_staticPool");
        var copiedCandidates = (List<CardInstance>)copiedSelecting.BlueprintVariables["savedCandidates"]!;
        var copiedCache = ApiField<IDictionary>(destination.Api, "_gcsCache");
        var copiedBuffers = ApiField<List<List<CardInstance>>>(destination.Api, "_triggerBuffers");
        var result = destination.Api.GetChooseSpawnCards(Ctx(destination, copiedSelecting), copiedSelecting, out bool seen2, out bool ordered2);
        var checks = new (string Name, bool Ok)[] {
            ("database remap", ReferenceEquals(ApiField<CardDatabase>(destination.Api, "_staticPoolDb"), destination.State.Database)),
            ("pool root", ReferenceEquals(copiedSelecting.BlueprintVariables["savedPool"], copiedPool)),
            ("buffer root", ReferenceEquals(copiedSelecting.BlueprintVariables["buffers"], copiedBuffers)),
            ("buffer candidates", ReferenceEquals(copiedBuffers[bufferOffset], copiedCandidates)),
            ("buffer pool", ReferenceEquals(copiedBuffers[bufferOffset + 1], copiedPool)),
            ("cache root", ReferenceEquals(copiedSelecting.BlueprintVariables["cache"], copiedCache)),
            ("variant root", ReferenceEquals(copiedSelecting.BlueprintVariables["variants"], ApiField<object>(destination.Api, "_staticPoolVariants"))),
            ("purity root", ReferenceEquals(copiedSelecting.BlueprintVariables["purity"], ApiField<object>(destination.Api, "_gcsPurityCache"))),
            ("reservation identity", ReferenceEquals(ApiField<object>(destination.Api, "_staticPoolReservationPolicy"), state.CardReservations)),
            ("blacklist identity", ReferenceEquals(ApiField<object>(destination.Api, "_staticPoolBlacklistPolicy"), state.CardBlacklist)),
            ("cache generation", ApiField<int>(engine.Api, "_staticPoolGeneration") == ApiField<int>(destination.Api, "_staticPoolGeneration")),
            ("template entry cycle", ReferenceEquals(copiedCandidates[0].BlueprintVariables["entry"], copiedCache[copiedSelecting.Name])),
            ("template owner cycle", ReferenceEquals(copiedCandidates[0].BlueprintVariables["owner"], copiedSelecting)),
            ("entry root", ReferenceEquals(copiedSelecting.BlueprintVariables["cachedEntry"], copiedCache[copiedSelecting.Name])),
            ("template in pool", copiedPool.Contains(copiedCandidates[0])),
            ("template isolation", !ReferenceEquals(copiedCandidates[0], candidates[0])),
            ("returned candidate identities/order", result.SequenceEqual(copiedCandidates)),
            ("flags", seen == seen2 && ordered == ordered2) };
        if (checks.Any(c => !c.Ok)) return "pool/cache clone: " + string.Join(", ", checks.Where(c => !c.Ok).Select(c => c.Name));
        copiedCandidates[0].BlueprintVariables["marker"] = 99;
        if (!Equals(candidates[0].BlueprintVariables["marker"], 73)) return "copy changed original template";
        candidates[0].BlueprintVariables["marker"] = 18;
        if (!Equals(copiedCandidates[0].BlueprintVariables["marker"], 99)) return "original changed copied template";
        var again = CopyApiComponents(destination, apiFirst: true);
        var thirdSelecting = again.State.ById(selecting.CardId)!;
        var third = again.Api.GetChooseSpawnCards(Ctx(again, thirdSelecting), thirdSelecting, out _, out _);
        if (ReferenceEquals(third[0], copiedCandidates[0]) || !Equals(third[0].BlueprintVariables["marker"], 99))
            return "repeated cached template copy lost state/isolation";
        // Policy replacement invalidates only the destination caches.
        destination.State.CardBlacklist = new CardBlacklistPolicy(new Dictionary<string, long> { [candidates[0].Name] = 20 }, 10);
        var after = destination.Api.GetChooseSpawnCards(Ctx(destination, copiedSelecting), copiedSelecting, out _, out _);
        if (after.Any(c => c.Name == candidates[0].Name) || cache.Count == 0
            || !ReferenceEquals(ApiField<List<CardInstance>>(engine.Api, "_staticPool"), pool)) return "branch cache invalidation leaked";
        return null;
    }

    private static string? CardApiCloneBindingAndDatabase(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        // Warm the lazy index and seed a nonzero raw diagnostic without changing the shared test catalog.
        var memo = VmCloneContext();
        var view = CopyDatabaseView(db, memo);
        var code = view.DeckCodeFor(db.All.First().Name);
        var fallbackField = CloneFields(typeof(CardDatabase)).Single(f => f.Name == "<VariantFallbacks>k__BackingField");
        fallbackField.SetValue(view, 13);
        var copiedView = CopyDatabaseView(view, VmCloneContext());
        if (copiedView.VariantFallbacks != 13 || copiedView.DeckCodeFor(db.All.First().Name) != code
            || !ReferenceEquals(copiedView.All.First(), view.All.First())) return "database view raw/catalog contract lost";
        var indexField = CloneFields(typeof(CardDatabase)).Single(f => f.Name == "_codeByName");
        var index = (Dictionary<string, string>)indexField.GetValue(view)!;
        var copiedIndex = (Dictionary<string, string>)indexField.GetValue(copiedView)!;
        copiedIndex["clone-only"] = "xx"; index["source-only"] = "yy";
        fallbackField.SetValue(copiedView, 14);
        if (index.ContainsKey("clone-only") || copiedIndex.ContainsKey("source-only") || view.VariantFallbacks != 13)
            return "database query state shared";
        var names = view.All.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        string fallbackName = names.First(n => !names.Contains(n + "_gold"));
        if (copiedView.Find(fallbackName + "_gold") is null || copiedView.VariantFallbacks != 15 || view.VariantFallbacks != 13)
            return "actual fallback query updated another database view";
        indexField.SetValue(view, null);
        var cold = CopyDatabaseView(view, VmCloneContext());
        if (indexField.GetValue(cold) is not null) return "cold lazy index initialized during copy";
        cold.DeckCodeFor(fallbackName);
        if (indexField.GetValue(view) is not null || indexField.GetValue(cold) is null) return "lazy construction crossed branches";
        var destination = CopyApiComponents(engine);
        var sourceDispatch = ApiField<IDictionary>(engine.Api, "_dispatch");
        var copiedDispatch = ApiField<IDictionary>(destination.Api, "_dispatch");
        if (!sourceDispatch.Keys.Cast<string>().SequenceEqual(copiedDispatch.Keys.Cast<string>())) return "dispatch keys changed";
        // Deliberately supply source context: this particular handler uses its API's engine.
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        string before = state.SnapshotJson();
        destination.Api.InvokeByName("DrawCardFromDeck", null, new object?[] { 1, 1 }, ctx, out bool handled);
        if (!handled || state.SnapshotJson() != before || destination.State.SnapshotJson() == before)
            return "rebuilt dispatch still uses source engine";
        destination.Api.NotifyUnimplemented("copy-only");
        destination.Api.Vm.Run(new KismetProgram([ProvenanceStep(0, "call", "ApiCloneMissing")], 0),
            new() { Engine = destination, State = destination.State, Controller = Side.Left });
        if (state.UnimplementedCalls.ContainsKey("copy-only") || state.UnimplementedCalls.ContainsKey("ApiCloneMissing")
            || !destination.State.UnimplementedCalls.ContainsKey("ApiCloneMissing")) return "API/VM notification binding leaked";
        return null;
    }

    private static string? CardApiCloneActiveRejection(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var (destination, _) = EmptyBoard(db);
        int rejected = 0;
        void Attempt()
        {
            try { ConstructClone<CardApi>(destination, engine.Api, VmCloneContext()); }
            catch (NotSupportedException) { rejected++; }
        }
        using (engine.Api.BeginPlayedCardBroadcast(7)) Attempt();
        foreach (string name in new[] { "_triggerDepth", "_resolvingTriggerQueue" })
        {
            var field = CloneFields(typeof(CardApi)).Single(f => f.Name == name);
            field.SetValue(engine.Api, field.FieldType == typeof(int) ? (object)1 : true);
            Attempt();
            field.SetValue(engine.Api, field.FieldType == typeof(int) ? (object)0 : false);
        }
        var dispatch = ApiField<Dictionary<string, Func<EffectContext, object?, object?[], object?>>>(engine.Api, "_dispatch");
        dispatch["ApiCloneAttempt"] = (_, _, _) => { Attempt(); return null; };
        engine.Api.Vm.Run(new KismetProgram([ProvenanceStep(0, "call", "ApiCloneAttempt")], 0),
            new() { Engine = engine, State = state, Controller = Side.Left });
        if (rejected != 4 || engine.Api.Vm.FaultedPrograms != 0) return "active component was copyable";
        ConstructClone<CardApi>(destination, engine.Api, VmCloneContext());
        // Never carry an injected source dispatch closure into the destination.
        var copy = CopyApiComponents(engine);
        if (ApiField<IDictionary>(copy.Api, "_dispatch").Contains("ApiCloneAttempt")) return "source override copied";
        return null;
    }
}
