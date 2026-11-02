using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static T StateField<T>(GameState state, string name)
        => (T)CloneFields(typeof(GameState)).Single(f => f.Name == name).GetValue(state)!;

    private static string? GameStateCloneFieldCoverage(CardDatabase db)
    {
        // Exact instance-field inventory, including scalar and compiler fields.
        // New fields require an explicit disposition before this guard passes.
        string[] properties = ["ShouldExecuteWaitPlayFromHand", "Database", "CardReservations", "CardBlacklist",
            "Random", "Seed", "GameplayRestrictions", "GameplaySideEffects", "TriggerQueue", "Turn", "ActiveSide",
            "StartingSide", "ClientSide", "DestroyedCardIdsByTurn", "UnitDestroyedThisTurn", "DestroyedCardIdsAll",
            "OperationKreditsSpentThisTurn", "StopAttack", "HasPlayedWeatherCardThisTurn", "FrontlineOwner",
            "StopFurtherActions", "ActiveBondFactions", "FrontlineLimiters", "IsFinished", "Winner", "WinnerReason",
            "ClientIdTurnOverride", "ActionLog", "CardsPlayedThisTurn", "CardsPlayedFromHandByTurn",
            "UnimplementedCalls", "RandomTrace", "CollectRandomTrace", "TrackGeneratedCardTrust", "GeneratedCardIds",
            "VerifiedGeneratedCardIds", "LeftAllyFaction", "RightAllyFaction", "LeftMainFaction", "RightMainFaction",
            "CardMoved", "CardCreated", "CardCreationModifiers"];
        string[] fields = ["_cardsBySide", "_pendingDeckRemovals", "_byCardId", "_battleCardOrder",
            "_autoPlayerCardIds", "_waitPlayFromHandCards", "_buffsToRemoveEndOfTurn", "_kreditSlotsLost",
            "_destroyedBySide", "_kredits", "_maxKredits", "_hqDamagedThisTurn", "_fatigue", "_nextCardId",
            "_cardsCreatedThisTurn", "_createdCountTurn", "_locationCards", "_locationCardIds"];
        if (!properties.Select(n => $"<{n}>k__BackingField").Concat(fields).Order()
            .SequenceEqual(CloneFields(typeof(GameState)).Select(f => f.Name).Order()))
            return "GameState field inventory changed";
        var nested = new[] {
            (typeof(GameState.GameplayRestriction), new[] { "Side", "Type", "SourceCardId", "TurnsRemaining" }),
            (typeof(GameState.GameplaySideEffect), new[] { "Side", "Tag", "SourceCardId", "DurationPolicy", "TurnsRemaining", "EffectValue" }),
            (typeof(GameAction), new[] { "ActionId", "ActionType", "Player", "TurnNumber", "SubActions", "ActionData" }),
            (typeof(SubAction), new[] { "Name", "Values" }),
            (typeof(ActionValue2), new[] { "Name", "Value", "Text" }),
        };
        foreach (var (type, names) in nested)
            if (!names.Select(n => $"<{n}>k__BackingField").Order()
                .SequenceEqual(CloneFields(type).Select(f => f.Name).Order())) return $"{type.Name} field inventory changed";
        // Reviewed immutable policy internals. Sharing is safe because constructors
        // own their buffers and public updates construct a new policy.
        var policyFields = new[] {
            (typeof(CardReservationPolicy), new[] { "_changes", "<ServerUtcTicks>k__BackingField" }),
            (typeof(CardReservationChange), new[] { "_toBeReserved", "_comingBack", "<ActivationTicks>k__BackingField" }),
            (typeof(CardBlacklistPolicy), new[] { "_endTicks", "<ServerUtcTicks>k__BackingField" }),
        };
        foreach (var (type, names) in policyFields)
            if (!names.Order().SequenceEqual(CloneFields(type).Select(f => f.Name).Order()))
                return $"shared policy field inventory changed: {type.Name}";

        var source = new GameState(db, 197);
        SeedCloneScalars(source);
        foreach (var field in CloneFields(typeof(GameState)))
        {
            if (field.FieldType == typeof(byte)) field.SetValue(source, (byte)239);
            else if (field.FieldType == typeof(ulong)) field.SetValue(source, ulong.MaxValue - 19);
        }
        source.WinnerReason = "raw finished state";
        var copy = GameStateClone.Create(source).State;
        foreach (var field in CloneFields(typeof(GameState)))
        {
            if (field.FieldType.IsValueType)
            {
                var type = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
                if (type != typeof(bool) && type != typeof(int) && type != typeof(byte) && type != typeof(ulong) && !type.IsEnum)
                    return $"unreviewed state value type: {field.Name}";
                if (!Equals(field.GetValue(source), field.GetValue(copy))) return $"raw field differs: {field.Name}";
            }
            else if (field.FieldType == typeof(string))
            {
                if (!Equals(field.GetValue(source), field.GetValue(copy))) return "state string differs";
            }
            else if (field.GetValue(source) is { } reference && ReferenceEquals(reference, field.GetValue(copy))
                && field.FieldType != typeof(CardDatabase)) return $"state reference shared: {field.Name}";
        }
        if (GameStateClone.Create(new GameState(db, 0)).State.ClientIdTurnOverride is not null)
            return "absent override became present";
        return null;
    }

    private static string? GameStateCloneGraphIsolation(CardDatabase db)
    {
        var s = new GameState(db, 719);
        var a = s.CreateWithId("card_unit_arado_ar_196", Side.Left, 9, CardLocation.DeckLeft, 0);
        var b = s.CreateWithId("card_unit_arado_ar_196", Side.Right, 3, CardLocation.HandRight, 0);
        var historical = s.CreateWithId("card_unit_arado_ar_196", Side.Left, 21, Side.Left.HqOf(), 0);
        s.Remove(historical);
        var replacement = s.CreateWithId("card_unit_arado_ar_196", Side.Left, 21, CardLocation.HandLeft, 0);
        s.SetLocationCardBySide(Side.Left, historical);
        s.SetLocationCardBySide(Side.Right, b);
        s.TriggerQueue.AddRange([historical, a, historical]);
        s.CardsPlayedThisTurn.AddRange([historical, b]);
        s.CardsPlayedFromHandByTurn[4] = s.CardsPlayedThisTurn;
        a.BlueprintVariables["history"] = s.CardsPlayedThisTurn;
        a.CurrentTarget = historical;
        historical.CurrentTarget = a;
        s.UpdateDestroyedCardsIDs(21, Side.Left);
        s.UpdateDestroyedCardsIDs(21, Side.Left);
        s.DestroyedCardIdsByTurn[4] = s.DestroyedCardIdsAll;
        s.AddAutoPlayCards(9); s.AddWaitPlayFromHandCards(3); s.AddBuffsToRemoveEndOfTurn(7, 21);
        s.SetKredits(Side.Left, 8); s.SetMaxKredits(Side.Right, 12); s.SetFatigue(Side.Right, 4);
        s.RecordKreditSlotLoss(Side.Left); s.UpdateHQDamagedAmountThisTurn(Side.Right, 6);
        s.ActiveBondFactions.UnionWith(["Germany", "Britain"]);
        s.FrontlineLimiters.UnionWith([9, 3]); s.FrontlineLimiters.Remove(9);
        s.GeneratedCardIds.UnionWith([9, 3]); s.VerifiedGeneratedCardIds.Add(3);
        s.UnimplementedCalls["test"] = 2; s.RandomTrace.Add("trace");
        s.CardReservations = new CardReservationPolicy([new(1, [a.Name], [])], 2);
        s.CardBlacklist = new CardBlacklistPolicy(new Dictionary<string, long> { [b.Name] = 3 }, 2);
        s.AddGameplayRestriction(Side.Left, (GameplayRestrictionType)1, 9, 3);
        s.GameplayRestrictions.Add(s.GameplayRestrictions[0]);
        s.ApplyGameplaySideEffect(Side.Right, "test-tag", 3, 2, 4, 8);
        s.GameplaySideEffects.Add(s.GameplaySideEffects[0]);
        var values = new List<ActionValue2> { ActionValue2.Int("id", 9) };
        var sub = new SubAction("test", values);
        var data = new Dictionary<string, object?> { ["history"] = s.CardsPlayedThisTurn, ["card"] = historical,
            ["values"] = values, ["state"] = s, ["random"] = s.Random };
        var action = new GameAction(11, "test", Side.Right, 4, new[] { sub, sub }, data);
        data["action"] = action; s.ActionLog.AddRange([action, action]);
        a.BlueprintVariables["action"] = action;
        int sourceCalls = 0;
        s.CardMoved = (_, _, _, _, _) => sourceCalls++;
        s.CardCreated = _ => sourceCalls++; s.CardCreationModifiers = _ => sourceCalls++;

        var graph = GameStateClone.Create(s); var c = graph.State;
        var ca = graph.Cards[a]; var cb = graph.Cards[b]; var ch = graph.Cards[historical];
        if (graph.Cards.Count != 4 || !ReferenceEquals(c.ById(21), graph.Cards[replacement])
            || ReferenceEquals(c.ById(21), c.Hq(Side.Left)) || !ReferenceEquals(c.Hq(Side.Left), ch)
            || !ReferenceEquals(ch.CurrentTarget, ca) || !ReferenceEquals(ca.CurrentTarget, ch)
            || !ReferenceEquals(c.TriggerQueue[0], ch) || !ReferenceEquals(c.TriggerQueue[2], ch)
            || !ReferenceEquals(c.CardsPlayedFromHandByTurn[4], c.CardsPlayedThisTurn)
            || !ReferenceEquals(ca.BlueprintVariables["history"], c.CardsPlayedThisTurn)
            || !ReferenceEquals(c.DestroyedCardIdsByTurn[4], c.DestroyedCardIdsAll)) return "state graph roots or aliases lost";
        if (!s.AllCardsInBattleOrder.Select(x => graph.Cards[x]).SequenceEqual(c.AllCardsInBattleOrder)
            || !s.CardsUnordered().Select(x => graph.Cards[x]).SequenceEqual(c.CardsUnordered())) return "state index order differs";
        var log = c.ActionLog[0]; var cd = log.ActionData;
        if (ReferenceEquals(log, action) || !ReferenceEquals(log, c.ActionLog[1])
            || !ReferenceEquals(log, cd["action"]) || !ReferenceEquals(log, ca.BlueprintVariables["action"])
            || !ReferenceEquals(log.SubActions[0], log.SubActions[1])
            || !ReferenceEquals(cd["state"], c) || !ReferenceEquals(cd["random"], c.Random)
            || !ReferenceEquals(cd["history"], c.CardsPlayedThisTurn) || !ReferenceEquals(cd["card"], ch)
            || !ReferenceEquals(cd["values"], log.SubActions[0].Values)) return "action graph alias/cycle lost";
        if (!ReferenceEquals(c.CardReservations, s.CardReservations) || !ReferenceEquals(c.CardBlacklist, s.CardBlacklist)
            || !ReferenceEquals(c.Database, db) || c.CardMoved is not null || c.CardCreated is not null
            || c.CardCreationModifiers is not null) return "shared policy/catalog or detached callback contract differs";

        c.GameplayRestrictions[0].TurnsRemaining = 19; c.GameplaySideEffects[0].DurationPolicy = 13;
        if (!ReferenceEquals(c.GameplayRestrictions[0], c.GameplayRestrictions[1])
            || !ReferenceEquals(c.GameplaySideEffects[0], c.GameplaySideEffects[1])
            || s.GameplayRestrictions[0].TurnsRemaining != 3 || s.GameplaySideEffects[0].DurationPolicy != 2)
            return "effect entries shared or internal alias lost";
        ((List<ActionValue2>)log.SubActions[0].Values).Clear();
        ((Dictionary<string, object?>)cd)["copy"] = true;
        c.CardsPlayedThisTurn.Clear(); c.DestroyedCardIdsAll.Clear(); c.TriggerQueue.Clear();
        c.UpdateDestroyedCardsIDs(333, Side.Left);
        c.AddBuffsToRemoveEndOfTurn(7, 999);
        c.ClearAutoPlayCards(); c.ClearWaitPlayFromHandCards(); c.ClearBuffsToRemoveEndOfTurn();
        c.SetKredits(Side.Left, 1); c.SetMaxKredits(Side.Right, 1); c.SetFatigue(Side.Right, 1);
        c.RecordKreditSlotLoss(Side.Left); c.UpdateHQDamagedAmountThisTurn(Side.Right, 1);
        c.UnimplementedCalls.Clear(); c.RandomTrace.Clear(); c.ActiveBondFactions.Clear();
        c.FrontlineLimiters.Clear(); c.GeneratedCardIds.Clear(); c.VerifiedGeneratedCardIds.Clear();
        c.Move(ca, CardLocation.HandLeft); c.ChangeOwner(cb, Side.Left);
        c.Create("card_unit_arado_ar_196", Side.Left, CardLocation.HandLeft, 0);
        if (sourceCalls != 0 || values.Count != 1 || data.ContainsKey("copy") || s.CardsPlayedThisTurn.Count != 2
            || s.DestroyedCardIdsAll.Count != 2 || s.TriggerQueue.Count != 3 || s.GetAutoPlayCards().Length != 1
            || s.DestroyedCardsIdsBySide(Side.Left).Count != 2 || s.DestroyedCardsIdsByTurn(s.Turn).Count != 1
            || s.GetWaitPlayFromHandCards().Length != 1 || s.GetBuffsToRemoveEndOfTurn()[7].Length != 1
            || s.Kredits(Side.Left) != 8 || s.MaxKredits(Side.Right) != 12 || s.Fatigue(Side.Right) != 4
            || s.KreditSlotsLost(Side.Left) != 1 || s.GetHQDamagedAmountThisTurn(Side.Right) != 6
            || s.UnimplementedCalls.Count != 1 || s.RandomTrace.Count != 1 || s.ActiveBondFactions.Count != 2
            || s.FrontlineLimiters.Count != 1 || s.GeneratedCardIds.Count != 2 || s.VerifiedGeneratedCardIds.Count != 1
            || a.Location != CardLocation.DeckLeft || b.Owner != Side.Right || s.AllCards.Count != 3)
            return "copy mutation affected source";
        s.GameplayRestrictions[0].TurnsRemaining = -9; s.ActionLog.Clear(); s.Remove(a);
        s.ClearLocationCardBindings();
        if (c.GameplayRestrictions[0].TurnsRemaining != 19 || c.ActionLog.Count != 2 || c.ById(9) is null)
            return "source mutation affected copy";
        if (!ReferenceEquals(c.Hq(Side.Left), ch) || c.GetLocationCardId(Side.Left) != 21)
            return "source cleared copy HQ bindings";
        return null;
    }

    private static string? GameStateCloneContinuation(CardDatabase db)
    {
        var s = new GameState(db, 0xffff_1234_9876UL) { Turn = 5, CollectRandomTrace = true, TrackGeneratedCardTrust = true };
        s.CreateWithId("card_unit_arado_ar_196", Side.Left, 5002, CardLocation.HandLeft, 0);
        s.NextCardId(Side.Left); s.NextSequentialCardId(Side.Right);
        for (int i = 0; i < 17; i++) s.Random.RandRange(0, 31);
        var clone = GameStateClone.Create(s); var c = clone.State;
        if (c.Random.InitialSeed != s.Random.InitialSeed || c.Random.Seed != s.Random.Seed
            || c.Random.ConsumedCount != s.Random.ConsumedCount) return "RNG cursor differs";
        for (int i = 0; i < 25; i++)
        {
            if (s.Random.RandRange(-17, 43) != c.Random.RandRange(-17, 43)
                || s.NextCardId(Side.Right) != c.NextCardId(Side.Right)
                || s.NextSequentialCardId(Side.Left) != c.NextSequentialCardId(Side.Left)) return "continuation differs";
            if (i == 8) { s.Turn++; c.Turn++; }
            if (i == 16) { s.ClientIdTurnOverride = 3; c.ClientIdTurnOverride = 3; }
        }
        if (!s.UnimplementedCalls.SequenceEqual(c.UnimplementedCalls)) return "allocator diagnostics differ";
        var before = s.Random.ConsumedCount; c.Random.RandRange(0, 4);
        if (s.Random.ConsumedCount != before) return "copy RNG advanced source";
        int created = 0, moved = 0, modifiers = 0;
        c.CardCreated = _ => created++; c.CardCreationModifiers = _ => modifiers++;
        c.CardMoved = (_, _, _, _, _) => moved++;
        var card = c.Create("card_unit_arado_ar_196", Side.Right, CardLocation.HandRight, 0);
        c.Move(card, Side.Right.HqOf());
        if (created != 1 || modifiers != 1 || moved != 1 || s.ById(card.CardId) is not null
            || !c.GeneratedCardIds.Contains(card.CardId) || !c.RandomTrace.Any()) return "rebound state callbacks failed";
        var again = GameStateClone.Create(c).State;
        if (again.CardCreated is not null || again.CardMoved is not null || again.CardCreationModifiers is not null)
            return "repeated clone retained callback";
        return null;
    }

    private static string? GameStateCloneRejection(CardDatabase db)
    {
        var s = new GameState(db, 1);
        bool rejected = false;
        s.WithDeckCardRemoved(Side.Left, 7, () => {
            try { GameStateClone.Create(s); }
            catch (NotSupportedException ex) when (ex.Message.Contains("scoped deck removal")) { rejected = true; }
        });
        if (!rejected) return "transient deck scope accepted";
        GameStateClone.Create(s); // finally restored the source scope
        var data = new Dictionary<string, object?> { ["unknown"] = new Version(1, 2) };
        s.ActionLog.Add(new(1, "test", Side.Left, 1, Array.Empty<SubAction>(), data));
        try { GameStateClone.Create(s); return "unknown action payload accepted"; }
        catch (NotSupportedException ex) when (ex.Message.Contains("System.Version")) { }
        if (s.ActionLog.Count != 1 || !ReferenceEquals(s.ActionLog[0].ActionData, data)) return "failed copy changed source";
        return null;
    }

    private static string? GameStateCloneEngineProduced(CardDatabase db)
    {
        var (engine, source) = EmptyBoard(db);
        source.CollectRandomTrace = true;
        source.CreateWithId("card_unit_arado_ar_196", Side.Left, 9, CardLocation.DeckLeft, 0);
        engine.StartTurn(Side.Left, draw: true);
        engine.EndTurn(Side.Left);
        if (source.ActionLog.Count == 0) return "engine action history not exercised";
        var snapshot = source.SnapshotJson();
        var history = System.Text.Json.JsonSerializer.Serialize(source.ActionLog);
        var graph = GameStateClone.Create(source); var copy = graph.State;
        if (copy.SnapshotJson() != snapshot || System.Text.Json.JsonSerializer.Serialize(copy.ActionLog) != history)
            return "engine-produced state or action data differs";
        if (!StateField<Dictionary<int, CardInstance>>(source, "_byCardId").Keys
            .SequenceEqual(StateField<Dictionary<int, CardInstance>>(copy, "_byCardId").Keys))
            return "engine-produced ID map ordering differs";
        engine.EndTurn(Side.Right);
        if (copy.SnapshotJson() != snapshot || System.Text.Json.JsonSerializer.Serialize(copy.ActionLog) != history)
            return "source engine continuation changed detached state";
        return null;
    }
}
