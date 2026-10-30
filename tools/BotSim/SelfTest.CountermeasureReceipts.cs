using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? CountermeasureCancellationBatch(CardDatabase db)
    {
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int route in new[] { 0, 1, 2, 3 })
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
                else engine.ResolveHandSelection(order, () => { engine.ExecuteAutoPlayCards(); return true; }, false);
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
