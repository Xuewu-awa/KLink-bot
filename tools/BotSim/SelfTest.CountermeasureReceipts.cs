using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? RepeatedCancellationNotice(CardDatabase db)
    {
        // Probe repeated notifier serialization directly; this does not assert
        // that two real countermeasures necessarily fire in one effect chain.
        var notify = typeof(MatchEngine).GetMethod("NotifyStopFurtherActions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (Side side in new[] { Side.Left, Side.Right })
        {
            var (engine, state) = EmptyBoard(db);
            engine.RecordAction("historical", side, new());
            engine.FireSubAction("ZActionPlayCardFromHand", new[] { ActionValue2.Int("cardID", 44) });
            var history = state.ActionLog[^1];
            engine.RecordAction("current", side, new());
            var first = ActionValue2.Int("cardID", 70);
            var second = ActionValue2.Int("cardID", 71);
            var marker = ActionValue2.Int("actionCancelled", 1);
            engine.FireSubAction("ZActionPlayCardFromHand", new[] { first });
            engine.FireSubAction("unrelated", new[] { ActionValue2.Int("cardID", 90) });
            var untouched = state.ActionLog[^1];
            engine.FireSubAction("ZActionPlayCardFromHand", new[] { second });
            notify.Invoke(engine, null);
            var once = state.ActionLog[^1].SubActions[0].Values.ToArray();
            notify.Invoke(engine, null);
            var plays = state.ActionLog.SelectMany(a => a.SubActions).Where(a => a.Name == "ZActionPlayCardFromHand").ToArray();
            if (!once.SequenceEqual(new[] { first, marker, second, marker })
                || !plays[1].Values.SequenceEqual(new[] { first, marker, marker })
                || !plays[2].Values.SequenceEqual(new[] { first, marker, marker, first, marker, second, marker, marker })
                || !ReferenceEquals(state.ActionLog[1], history) || !ReferenceEquals(state.ActionLog[4], untouched))
                return $"repeat must append markers and copy cumulative values while preserving other records: {side}";
            var previous = state.ActionLog.ToArray();
            engine.RecordAction("next", side, new());
            notify.Invoke(engine, null);
            if (!previous.SequenceEqual(state.ActionLog.Take(previous.Length)))
                return "next empty batch must not recancel prior records";
        }
        return null;
    }

    private static string? EndTurnStopBoundary(CardDatabase db)
    {
        const string name = "card_unit_m36_destroyer";
        // Synthetic event checks the boundary, not this card's live ability.
        using var probe = InstallSelectionDrawProbe(name, "OnEndOfTurn");
        var locals = (IDictionary<string, IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(name)!.Locals!;
        KismetStep Observe(int index, string key, string getter) => new(index, "call", "JSON_SetBool",
            new[] { new KismetExpr { Int = 60 }, new KismetExpr { Str = key }, new KismetExpr { Call = getter } },
            Array.Empty<int>(), null, null, -1, null, null) { ByteOffset = index };
        locals["OnEndOfTurn"] = new[] {
            Observe(0, "audit_stop", "GetStopFurtherActions"), Observe(1, "audit_attack", "GetStopAttack"),
            new KismetStep(2, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null)
                { ByteOffset = 2 }
        };
        foreach (Side side in new[] { Side.Left, Side.Right })
        {
            var (engine, state) = EmptyBoard(db); state.ActiveSide = side;
            var observer = state.CreateWithId(name, side, 60, side.HqOf(), 1);
            observer.Definition = new CardDefinition { Name = name, Type = "tank", UsedTriggers = new[] { "OnEndOfTurn" } };
            state.StopFurtherActions = true; state.StopAttack = true;
            engine.EndTurn(side);
            if (!observer.CustomJson.ContainsKey("audit_stop") || !observer.CustomJson.ContainsKey("audit_attack")
                || engine.Api.JsonGetBool(observer, "audit_stop") || engine.Api.JsonGetBool(observer, "audit_attack")
                || engine.Api.Vm.FaultedPrograms != 0)
                return $"end-turn callback observed stale stop flags: {side}";
        }
        return null;
    }

    private static string? SelectionReplyStopBoundary(CardDatabase db)
    {
        const string name = "card_unit_m36_destroyer";
        // A synthetic callback observes phase ordering, not this card's live effect.
        using var probe = InstallSelectionDrawProbe(name, "OnHandTargetSelected");
        var locals = (IDictionary<string, IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(name)!.Locals!;
        KismetStep Observe(int index, string key, string getter) => new(index, "call", "JSON_SetBool",
            new[] { new KismetExpr { Int = 60 }, new KismetExpr { Str = key }, new KismetExpr { Call = getter } },
            Array.Empty<int>(), null, null, -1, null, null) { ByteOffset = index };
        locals["OnHandTargetSelected"] = new[] {
            Observe(0, "audit_stop", "GetStopFurtherActions"), Observe(1, "audit_attack", "GetStopAttack"),
            new KismetStep(2, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null)
                { ByteOffset = 2 }
        };
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int route in new[] { 0, 1, 2 })
        {
            var (engine, state) = EmptyBoard(db); state.ActiveSide = side;
            var source = state.CreateWithId(name, side, 60, side.HqOf(), 1);
            state.CreateWithId("card_event_interception", side, 80, side.HandOf(), 0);
            source.ExtraPlayTriggers = 1;
            state.StopFurtherActions = true; state.StopAttack = true;
            bool accepted = false, clean = false; int effects = 0;
            engine.PickHandTargetId = (_, _) => accepted ? 80 : null;
            CardEffectScripts.Register(name, _ => {
                effects++; clean = !state.StopFurtherActions && !state.StopAttack;
                // A flag raised within the reply must survive its remaining tail.
                state.StopFurtherActions = true; state.StopAttack = true;
            });
            try
            {
                if (route == 1)
                {
                    if (engine.Api.DevelopChosenCard(source, PlainUnit) is null) return "develop reply missing";
                }
                else if (route == 2)
                {
                    var chosen = state.CreateWithId(PlainUnit, side, 90, side.DeckOf(), 0);
                    if (engine.Api.DrawChosenCardToHand(source, chosen) is null) return "deck reply missing";
                }
                else
                {
                    engine.Api.InvokeByName("selectTargetFromHand", source, new object?[] { 60 },
                        new EffectContext { Engine = engine, State = state, Self = source, Controller = side }, out _);
                    if (engine.PendingSelectionCount != 1 || effects != 0
                        || !state.StopFurtherActions || !state.StopAttack)
                        return "unanswered selection changed stop flags or consumed callback";
                    accepted = true;
                    if (!engine.ResumePendingSelections()) return "accepted reply did not resume";
                }
                if (!source.CustomJson.ContainsKey("audit_stop") || !source.CustomJson.ContainsKey("audit_attack")
                    || engine.Api.JsonGetBool(source, "audit_stop") || engine.Api.JsonGetBool(source, "audit_attack")
                    || effects != 1 || !clean || !state.StopFurtherActions || !state.StopAttack
                    || engine.PendingSelectionCount != 0 || engine.Api.Vm.FaultedPrograms != 0)
                    return $"reply must clear old stops before callback/extra effect and retain new stops: {side}/{route}/{effects}/{clean}";
            }
            finally { CardEffectScripts.Unregister(name); }
        }
        return null;
    }

    private static string? CountermeasureCancellationBatch(CardDatabase db)
    {
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int route in new[] { 0, 1, 2, 3, 4 })
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = side; state.SetKredits(side, 12);
            engine.RecordAction("XActionPlayCardFromHand", side, new());
            engine.FireSubAction("ZActionPlayCardFromHand", new[] { ActionValue2.Int("cardID", 44) });
            var historical = state.ActionLog[^1];
            if (route == 1)
                engine.RecordAction("XActionPlayCardFromHand", side, new());
            // Same-batch prefix must be included only for in-action autoplay.
            if (route == 1)
                engine.FireSubAction("ZActionPlayCardFromHand", new[] { ActionValue2.Int("cardID", 45) });
            var order = state.CreateWithId("card_event_war_bonds", side, 70, side.HandOf(), 0);
            var gotcha = MakeGotcha(state, "card_event_ultra", side.Opposite(), 1500, side.Opposite().HandOf(), 0);
            gotcha.GotchaActivated = 1;
            state.CreateWithId("card_unit_panzer_35_t", side, 91, side.DeckOf(), 0);
            state.CreateWithId("card_unit_panzer_35_t", side.Opposite(), 92, side.Opposite().DeckOf(), 0);
            if (route == 0)
            {
                if (!MainActionPolicyAdapter.ApplyRoot(engine, side, new(MainActionKind.Play, 70))) return "cancel root rejected";
            }
            else
            {
                state.AddAutoPlayCards(70);
                if (route == 1) engine.ExecuteAutoPlayCards();
                else if (route == 2) engine.ExecuteQueuedAutoPlayActions();
                else if (route == 3) engine.ResolveHandSelection(order, () => { engine.ExecuteAutoPlayCards(); return true; }, false);
                else engine.EndTurn(side);
            }
            var plays = state.ActionLog.SelectMany(a => a.SubActions).Where(a => a.Name == "ZActionPlayCardFromHand").ToArray();
            var current = plays[^1];
            var marker = ActionValue2.Int("actionCancelled", 1);
            if (current.Values.LastOrDefault() != marker || order.EnteredPlayOnTurn != 0)
                return $"cancelled play missing exact trailing marker: {side}/{route}";
            if (!ReferenceEquals(state.ActionLog[1], historical) || plays[0].Values.Count != 1)
                return "cancellation mutated a previous action";
            if (route == 1 && (plays[1].Values.Count != 2 || plays[1].Values[^1] != marker
                || current.Values[0] != ActionValue2.Int("cardID", 45) || current.Values[1] != marker
                || current.Values.Count(v => v.Name == "actionCancelled") != 2))
                return "same-batch play values must accumulate across matching subactions";
            if (state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name.Contains("StopFurther"))
                || engine.Api.Vm.FaultedPrograms != 0) return "cancellation must rewrite existing plays only";
        }
        return null;
    }

    private static string? CountermeasureTriggerNotice(CardDatabase db)
    {
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (bool stop in new[] { false, true })
        foreach (bool skip in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var gotcha = MakeGotcha(state, "card_event_interception", side, 1500, side.HandOf(), 0);
            gotcha.GotchaActivated = 1;
            var order = state.CreateWithId("card_event_war_bonds", side.Opposite(), 2000, CardLocation.Discard, 3);
            order.EnteredPlayOnTurn = 55;
            var tigers = state.CreateWithId("card_unit_the_tigers", side, 1600, side.HqOf(), 0);
            int attack = tigers.Attack, defense = tigers.Defense;
            engine.Api.GotchaTriggered(gotcha, order.CardId, stop, skip);
            var notices = state.ActionLog.SelectMany(a => a.SubActions).ToArray();
            var notice = notices.SingleOrDefault(a => a.Name == "ZActionGotchaTriggered");
            if (notice is null || notice.Values.Count != 3
                || !notice.Values.Select(v => v.Name).SequenceEqual(new[] { "cardID", "instigatorID", "StopCard" })
                || !notice.Values.Select(v => v.Value).SequenceEqual(new[] { 1500, 2000, stop ? 1 : 0 })
                || notice.Values.Any(v => v.Text != ""))
                return $"missing/extraneous Gotcha trigger payload: {side}/{stop}/{skip}";
            if (Array.IndexOf(notices, notice) != 0 || tigers.Attack != attack + 1 || tigers.Defense != defense + 1)
                return "trigger notice must precede real THE TIGERS T21 effect";
            if (gotcha.Location != CardLocation.Discard || gotcha.GotchaActivated != 0
                || state.StopFurtherActions != stop || order.EnteredPlayOnTurn != (stop ? 0 : 55)
                || order.CustomJson.ContainsKey("cancelOrderRemove") != skip
                || order.Location != CardLocation.Discard || order.LocationNumber != 3)
                return "stop/preview marker must preserve logical discard and source slot";
            engine.ApplyPendingLocalRuleReceipts();
            if (tigers.Attack != attack + 1 || tigers.Defense != defense + 1 || engine.Api.Vm.FaultedPrograms != 0)
                return "local receipt must not rerun T21";
        }
        foreach (Side side in new[] { Side.Left, Side.Right })
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = side; state.SetKredits(side, 12);
            var order = state.CreateWithId("card_event_war_bonds", side, 70, side.HandOf(), 3);
            var gotcha = MakeGotcha(state, "card_event_ultra", side.Opposite(), 1500, side.Opposite().HandOf(), 0);
            gotcha.GotchaActivated = 1;
            state.CreateWithId("card_unit_panzer_35_t", side, 91, side.DeckOf(), 0);
            var drawn = state.CreateWithId("card_unit_panzer_35_t", side.Opposite(), 92, side.Opposite().DeckOf(), 0);
            if (!MainActionPolicyAdapter.ApplyRoot(engine, side, new(MainActionKind.Play, 70))) return "ULTRA root rejected";
            if (!state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionGotchaTriggered")
                || order.Location != CardLocation.Discard || order.EnteredPlayOnTurn != 0
                || state.ById(91)!.Location != side.DeckOf() || drawn.Location != side.Opposite().HandOf()
                || engine.Api.Vm.FaultedPrograms != 0)
                return "real ULTRA must cancel order effect and draw for countermeasure owner";
        }
        return null;
    }

    private static string? CountermeasureLocalReceiptRefresh(CardDatabase db)
    {
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int mode in new[] { 0, 1, 2 })
        {
            var (engine, state) = EmptyBoard(db);
            var gotcha = MakeGotcha(state, "card_event_interception", side, mode == 2 ? 0 : 1500, side.HandOf(), 0);
            engine.Api.GotchaTriggered(gotcha, 0, false, false);
            // Receiver looks up current object/side, not a saved notification-time hand.
            if (mode == 1) gotcha.Owner = side.Opposite();
            // Nonpositive lookup IDs fail; a null context's side=0 maps to HandRight.
            Side receiverSide = mode == 2 ? Side.Right : gotcha.Owner;
            var first = state.CreateWithId("card_unit_panzer_35_t", receiverSide, 1600, receiverSide.HandOf(), 7);
            var second = state.CreateWithId("card_unit_panzer_35_t", receiverSide, 1601, receiverSide.HandOf(), 9);
            var untouched = state.CreateWithId("card_unit_panzer_35_t", receiverSide.Opposite(), 1602, receiverSide.Opposite().HandOf(), 8);
            engine.ApplyPendingLocalRuleReceipts();
            if (first.LocationNumber != 0 || second.LocationNumber != 1 || untouched.LocationNumber != 8)
                return $"Gotcha local receipt must rearrange current owner's hand: {side}/{mode}";
            first.LocationNumber = 12;
            engine.ApplyPendingLocalRuleReceipts();
            if (first.LocationNumber != 12 || gotcha.Location != CardLocation.Discard)
                return "receipt must drain once without rerunning trigger body";
        }
        return null;
    }
}
