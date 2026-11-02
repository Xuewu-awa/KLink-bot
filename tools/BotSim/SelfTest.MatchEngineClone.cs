using System.Collections;
using System.Reflection;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static FieldInfo EngineCloneField(string name) => CloneFields(typeof(MatchEngine)).Single(f => f.Name == name);

    private static string? MatchEngineCloneFields(CardDatabase db)
    {
        string[] fields = ["_pendingSelections", "_effectResolutionDepth", "_processingForcedEndTurn",
            "_pendingForcedEndTurnSide", "_pendingForcedEndTurnInstigator", "_db", "_actionId",
            "_pendingLocalRuleReceipts", "_processingAutoPlayCards", "_processingQueuedAutoPlayActions",
            "_currentActionLogStart", "_operationDepth"];
        string[] props = ["State", "LeftDeckList", "RightDeckList", "Api", "OutOfRangeAttacksRejected",
            "PickCardToDraw", "ChooseSpawnCard", "PickHandTarget", "PickHandTargetId", "ChooseHandTarget",
            "LastHandTarget", "LastPick", "Log", "EnforceGeneratedCardTrust", "BlockUntrustedOrders"];
        if (!fields.Concat(props.Select(n => $"<{n}>k__BackingField")).Order()
            .SequenceEqual(CloneFields(typeof(MatchEngine)).Select(f => f.Name).Order())) return "engine field inventory changed";
        foreach (var (type, names) in new[] {
            (typeof(MatchEngine.HandTargetRecord), new[] { "SelectingCardId", "ChosenCardId" }),
            (typeof(MatchEngine.PickRecord), new[] { "SelectingCardId", "Index", "Code", "ChosenName" }) })
            if (!names.Select(n => $"<{n}>k__BackingField").Order().SequenceEqual(CloneFields(type).Select(f => f.Name).Order()))
                return "immutable pick record inventory changed";
        var decks = new List<string> { "card_unit_arado_ar_196" };
        var e = new MatchEngine(db, decks, decks, 123);
        e.EnforceGeneratedCardTrust = false; e.BlockUntrustedOrders = false;
        e.RecordPick(4, 2, "code", "name"); e.RecordHandTargetPick(5, 6);
        e.Log.Add("retained"); e.Api.TriggerTrace = e.Api.Vm.Trace = e.Log;
        var card = e.State.CreateWithId("card_unit_arado_ar_196", Side.Left, 9, CardLocation.HandLeft, 0);
        card.BlueprintVariables["engine"] = e;
        card.BlueprintVariables["database"] = db;
        card.BlueprintVariables["log"] = e.Log;
        card.BlueprintVariables["pick"] = e.LastPick;
        EngineCloneField("_actionId").SetValue(e, 73);
        EngineCloneField("_currentActionLogStart").SetValue(e, 12);
        EngineCloneField("<OutOfRangeAttacksRejected>k__BackingField").SetValue(e, 8);
        var c = e.CloneAtIdle();
        var copiedCard = c.State.ById(9)!;
        if (!ReferenceEquals(copiedCard.BlueprintVariables["engine"], c)
            || !ReferenceEquals(copiedCard.BlueprintVariables["database"], c.State.Database)
            || !ReferenceEquals(copiedCard.BlueprintVariables["log"], c.Log)
            || !ReferenceEquals(copiedCard.BlueprintVariables["pick"], c.LastPick)) return "cross-component payload aliases lost";
        foreach (var field in CloneFields(typeof(MatchEngine)).Where(f => f.FieldType.IsValueType))
            if (!Equals(field.GetValue(e), field.GetValue(c))) return "scalar differs: " + field.Name;
        if (c.LastPick != e.LastPick || c.LastHandTarget != e.LastHandTarget
            || !ReferenceEquals(c.LeftDeckList, c.RightDeckList) || ReferenceEquals(c.LeftDeckList, decks)
            || !ReferenceEquals(c.Log, c.Api.TriggerTrace) || !ReferenceEquals(c.Log, c.Api.Vm.Trace)
            || ReferenceEquals(c.State.Database, db) || ReferenceEquals(c.Log, e.Log)) return "engine aliases or values lost";
        if (!ReferenceEquals(c.State.CardMoved!.Target, c) || !ReferenceEquals(c.State.CardCreated!.Target, c)
            || !ReferenceEquals(c.State.CardCreationModifiers!.Target, c)) return "callback owner was not rebound";
        ((List<string>)c.LeftDeckList).Add("branch"); c.Log.Add("branch");
        if (decks.Count != 1 || e.Log.Count != 1) return "branch mutated source";
        decks.Clear(); e.Log.Clear();
        if (c.LeftDeckList.Count != 2 || c.Log.Count != 2) return "source mutated branch";
        var again = c.CloneAtIdle();
        if (ReferenceEquals(again.Log, c.Log) || !ReferenceEquals(again.Api.TriggerTrace, again.Log)
            || !ReferenceEquals(again.LeftDeckList, again.RightDeckList)) return "reclone lost aliases";
        return null;
    }

    private static string? MatchEngineCloneContinuation(CardDatabase db)
    {
        MatchEngine Fresh() => new(db, Enumerable.Repeat("card_unit_arado_ar_196", 14).ToArray(),
            Enumerable.Repeat("card_unit_arado_ar_196", 14).ToArray(), 773);
        string Fingerprint(MatchEngine e) => e.State.SnapshotJson() + "\n" + JsonSerializer.Serialize(e.State.ActionLog)
            + "\n" + string.Join("\n", e.Log) + "\n" + string.Join("\n", e.State.RandomTrace)
            + $"/{e.Api.Vm.ProgramsExecuted}/{e.Api.Vm.StepsExecuted}/{e.Api.Vm.FaultedPrograms}/"
            + JsonSerializer.Serialize(e.Api.Vm.UnimplementedCalls) + "/" + JsonSerializer.Serialize(e.Api.Vm.UnsupportedOps)
            + "/" + EngineCloneField("_actionId").GetValue(e) + "/" + EngineCloneField("_currentActionLogStart").GetValue(e);
        var source = Fresh(); source.State.CollectRandomTrace = true;
        var cloneBeforeStart = source.CloneAtIdle();
        var replay = Fresh(); replay.State.CollectRandomTrace = true;
        source.Start(); cloneBeforeStart.Start(); replay.Start();
        if (Fingerprint(source) != Fingerprint(cloneBeforeStart) || Fingerprint(source) != Fingerprint(replay))
            return "unstarted clone/start differs from independent construction";
        // A deterministic action prefix is an engineering fixture, not rule authority.
        source.EndTurn(Side.Left); replay.EndTurn(Side.Left);
        var clone = source.CloneAtIdle();
        for (int turn = 0; turn < 5; turn++)
        {
            var before = Fingerprint(source);
            var side = source.State.ActiveSide;
            clone.DrawCard(side);
            clone.EndTurn(side);
            if (Fingerprint(source) != before) return "branch continuation changed original";
            source.DrawCard(side); source.EndTurn(side);
            replay.DrawCard(side); replay.EndTurn(side);
            if (Fingerprint(source) != Fingerprint(clone) || Fingerprint(source) != Fingerprint(replay))
                return "continuation diverged at turn " + turn;
            int sample = source.State.Random.RandRange(0, 9999);
            if (sample != clone.State.Random.RandRange(0, 9999)
                || sample != replay.State.Random.RandRange(0, 9999))
                return "random continuation differs";
            clone = clone.CloneAtIdle();
        }
        return null;
    }

    private static string? MatchEngineCloneRejection(CardDatabase db)
    {
        var (e, s) = EmptyBoard(db);
        bool Reject(string contains)
        {
            try { e.CloneAtIdle(); return false; }
            catch (NotSupportedException ex) { return ex.Message.Contains(contains, StringComparison.Ordinal); }
        }
        foreach (string field in new[] { "_operationDepth", "_effectResolutionDepth", "_pendingForcedEndTurnInstigator",
            "_processingForcedEndTurn", "_processingAutoPlayCards", "_processingQueuedAutoPlayActions", "_pendingForcedEndTurnSide" })
        {
            var f = EngineCloneField(field); var old = f.GetValue(e);
            f.SetValue(e, f.FieldType == typeof(bool) ? true : f.FieldType == typeof(Side?) ? Side.Left : 1);
            if (!Reject("idle")) return "active field accepted: " + field;
            f.SetValue(e, old);
        }
        EngineCloneField("_pendingSelections").SetValue(e, new Queue<Func<bool>>());
        if (!Reject("idle")) return "empty but owned selection scope accepted";
        EngineCloneField("_pendingSelections").SetValue(e, null);
        var receiptType = typeof(MatchEngine).GetNestedType("LocalRuleReceipt", BindingFlags.NonPublic)!;
        var receiptKind = typeof(MatchEngine).GetNestedType("LocalReceiptKind", BindingFlags.NonPublic)!;
        var receipt = Activator.CreateInstance(receiptType, Enum.ToObject(receiptKind, 0), 0, false, Side.NotAvailable, null)!;
        var queue = EngineCloneField("_pendingLocalRuleReceipts").GetValue(e)!;
        queue.GetType().GetMethod("Enqueue")!.Invoke(queue, [receipt]);
        if (!Reject("idle") || ((ICollection)queue).Count != 1) return "pending receipt accepted or drained";
        queue.GetType().GetMethod("Clear")!.Invoke(queue, null);
        s.AddAutoPlayCards(2); if (!Reject("idle")) return "autoplay accepted"; s.ClearAutoPlayCards();
        s.AddWaitPlayFromHandCards(2); if (!Reject("idle")) return "waiting selection accepted"; s.ClearWaitPlayFromHandCards();
        s.TriggerQueue.Add(s.Hq(Side.Left)); if (!Reject("idle")) return "trigger queue accepted"; s.TriggerQueue.Clear();
        e.PickCardToDraw = (_, _, _) => null; if (!Reject("selectors")) return "draw selector accepted"; e.PickCardToDraw = null;
        e.ChooseSpawnCard = (_, _) => null; if (!Reject("selectors")) return "spawn selector accepted"; e.ChooseSpawnCard = null;
        e.PickHandTarget = (_, _) => null; if (!Reject("selectors")) return "hand selector accepted"; e.PickHandTarget = null;
        e.PickHandTargetId = (_, _) => null; if (!Reject("selectors")) return "ID selector accepted"; e.PickHandTargetId = null;
        e.ChooseHandTarget = (_, _) => null; if (!Reject("selectors")) return "legacy selector accepted"; e.ChooseHandTarget = null;
        var moved = s.CardMoved; s.CardMoved += (_, _, _, _, _) => { };
        if (!Reject("callbacks")) return "multicast callback accepted"; s.CardMoved = moved;
        var created = s.CardCreated; s.CardCreated = null;
        if (!Reject("callbacks")) return "detached callback accepted"; s.CardCreated = created;
        var modifiers = s.CardCreationModifiers; s.CardCreationModifiers = _ => { };
        if (!Reject("callbacks")) return "custom callback accepted"; s.CardCreationModifiers = modifiers;
        s.Hq(Side.Left).BlueprintVariables["foreign"] = CopyDatabaseView(db, VmCloneContext());
        if (!Reject("unregistered database")) return "foreign database payload accepted";
        s.Hq(Side.Left).BlueprintVariables.Clear();
        foreach (var (name, active) in new (string, object)[] { ("_triggerDepth", 1),
            ("_playedCardBroadcastDepth", 1), ("_resolvingTriggerQueue", true) })
        {
            var field = CloneFields(e.Api.GetType()).Single(f => f.Name == name);
            var old = field.GetValue(e.Api); field.SetValue(e.Api, active);
            if (!Reject("effect execution")) return "active API accepted: " + name;
            field.SetValue(e.Api, old);
        }
        var vmDepth = CloneFields(e.Api.Vm.GetType()).Single(f => f.Name == "_executionDepth");
        vmDepth.SetValue(e.Api.Vm, 1);
        if (!Reject("effect execution")) return "executing VM accepted";
        vmDepth.SetValue(e.Api.Vm, 0);
        var poolDb = CloneFields(e.Api.GetType()).Single(f => f.Name == "_staticPoolDb");
        poolDb.SetValue(e.Api, CopyDatabaseView(db, VmCloneContext()));
        if (!Reject("foreign pool database")) return "foreign static pool database accepted";
        poolDb.SetValue(e.Api, null);
        bool deckScopeBlocked = false;
        s.WithDeckCardRemoved(Side.Left, 9, () => { deckScopeBlocked = Reject("scoped deck removal"); });
        if (!deckScopeBlocked) return "scoped deck removal accepted";
        var unsupportedDeck = new MatchEngine(db, Array.AsReadOnly(Array.Empty<string>()), Array.Empty<string>(), 1);
        try { unsupportedDeck.CloneAtIdle(); return "unknown deck wrapper accepted"; }
        catch (NotSupportedException ex) when (ex.Message.Contains("decks")) { }
        e.CloneAtIdle();
        return null;
    }

    private static string? MatchEngineCloneReentrancy(CardDatabase db)
    {
        var e = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), 1);
        var s = e.State;
        var created = s.CardCreated; bool blocked = false;
        s.CardCreated = c => {
            try { e.CloneAtIdle(); }
            catch (NotSupportedException ex) when (ex.Message.Contains("idle")) { blocked = true; }
            created!(c);
            throw new InvalidOperationException("clone-unwind-probe");
        };
        try { e.Start(); return "callback exception not exercised"; }
        catch (InvalidOperationException ex) when (ex.Message == "clone-unwind-probe") { }
        finally { s.CardCreated = created; }
        if (!blocked || (int)EngineCloneField("_operationDepth").GetValue(e)! != 0) return "reentry accepted or scope leaked";
        e.CloneAtIdle();
        return null;
    }

    private static string? MatchEngineCloneActions(CardDatabase db)
    {
        MatchEngine Setup()
        {
            var (engine, state) = EmptyBoard(db);
            state.Turn = 3; state.ActiveSide = Side.Left;
            state.SetMaxKredits(Side.Left, 24); state.SetKredits(Side.Left, 24);
            state.CreateWithId("card_unit_arado_ar_196", Side.Left, 9, CardLocation.HandLeft, 0);
            return engine;
        }
        var source = Setup(); var replay = Setup(); var branch = source.CloneAtIdle();
        string Print(MatchEngine e) => e.State.SnapshotJson() + JsonSerializer.Serialize(e.State.ActionLog)
            + string.Join("\n", e.Log) + "/" + EngineCloneField("_actionId").GetValue(e)
            + $"/{e.Api.Vm.ProgramsExecuted}/{e.Api.Vm.StepsExecuted}/{e.Api.Vm.FaultedPrograms}";
        var engines = new[] { source, replay, branch };
        foreach (var e in engines)
            if (!e.PlayCard(e.State.ById(9)!)) return "play fixture was rejected";
        if (Print(source) != Print(branch) || Print(source) != Print(replay)) return "play diverged";
        foreach (var e in engines) { e.EndTurn(Side.Left); e.EndTurn(Side.Right); }
        foreach (var e in engines)
            if (!e.MoveUnit(e.State.ById(9)!, 0, out var reason)) return "move fixture was rejected: " + reason;
        if (Print(source) != Print(branch) || Print(source) != Print(replay)) return "move diverged";
        foreach (var e in engines) { e.EndTurn(Side.Left); e.EndTurn(Side.Right); }
        foreach (var e in engines)
            if (!e.Attack(e.State.ById(9)!, e.State.Hq(Side.Right), out var reason)) return "attack fixture was rejected: " + reason;
        if (Print(source) != Print(branch) || Print(source) != Print(replay)) return "attack diverged";
        var before = Print(source);
        branch.Destroy(branch.State.ById(9)!);
        if (Print(source) != before) return "branch destruction mutated original";
        source.Destroy(source.State.ById(9)!); replay.Destroy(replay.State.ById(9)!);
        if (Print(source) != Print(branch) || Print(source) != Print(replay)) return "destruction diverged";
        foreach (var e in engines) e.ApplyPendingLocalRuleReceipts();
        if (Print(source) != Print(branch) || Print(source) != Print(replay)) return "destruction receipt diverged";
        branch.CloneAtIdle();
        return null;
    }
}
