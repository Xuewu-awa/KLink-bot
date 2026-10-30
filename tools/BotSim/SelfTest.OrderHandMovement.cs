using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? OrderHandMovement(CardDatabase db)
    {
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int residentSlot in new[] { 0, 7 })
        foreach (int route in new[] { 0, 1, 2 })
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = side; state.SetKredits(side, 12);
            var source = state.CreateWithId("card_event_war_bonds", side, 70, side.HandOf(), 1);
            var before = state.CreateWithId("card_unit_panzer_35_t", side, 101, side.HandOf(), 0);
            var after = state.CreateWithId("card_unit_panzer_35_t", side, 102, side.HandOf(), 2);
            var resident = state.CreateWithId("card_event_war_bonds", side.Opposite(), 80, CardLocation.Discard, residentSlot);
            bool receipts = false, refreshed = false;
            var original = state.CardMoved;
            state.CardMoved = (card, oldLocation, newLocation, owner, reason) => {
                if (card == source)
                {
                    var notices = state.ActionLog.SelectMany(a => a.SubActions).ToArray();
                    var play = notices.SingleOrDefault(a => a.Name == "ZActionPlayCardFromHand");
                    var move = notices.SingleOrDefault(a => a.Name == "ZActionMoveCardToNewLocation");
                    int V(SubAction? a, string key) => a?.Values.LastOrDefault(v => v.Name == key)?.Value ?? -999;
                    string T(SubAction? a, string key) => a?.Values.LastOrDefault(v => v.Name == key)?.Text ?? "";
                    KLink.Bot.Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", 8, out var discardName);
                    KLink.Bot.Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", (int)side.HandOf(), out var handName);
                    KLink.Bot.Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Game/Structs/CardMoveReason.CardMoveReason", 4, out var reasonName);
                    receipts = play is not null && move is not null
                        && Array.IndexOf(notices, play) < Array.IndexOf(notices, move)
                        && !notices.Any(a => a.Name == "ZActionPlayOrderCardFromHand")
                        && V(play, "cardID") == 70 && V(play, "instigatorID") == 70
                        && V(play, "targetCardID") == 0 && V(play, "enterPlayOnTurn") == state.Turn
                        && V(play, "locationNumber") == 0 && V(play, "nonVisualLocationNumber") == 0
                        && T(play, "location") == discardName
                        && !play!.Values.Any(v => v.Name is "playedDirectly" or "chooseOneIndex")
                        && V(move, "cardID") == 70 && V(move, "instigatorID") == 70
                        && V(move, "newLocationNumber") == 0 && V(move, "nonVisualLocationNumber") == 0
                        && T(move, "oldLocation") == handName && T(move, "newLocation") == discardName
                        && T(move, "moveReason") == reasonName && reason == CardMoveReason.PlayCardPlayerMove;
                    refreshed = before.LocationNumber == 0 && after.LocationNumber == 1
                        && source.LocationNumber == 1 && resident.LocationNumber == residentSlot + 1;
                }
                original?.Invoke(card, oldLocation, newLocation, owner, reason);
            };
            if (route == 0)
            {
                if (!MainActionPolicyAdapter.ApplyRoot(engine, side, new(MainActionKind.Play, 70))) return "order root rejected";
            }
            else
            {
                state.AddAutoPlayCards(70);
                if (route == 1) engine.ExecuteAutoPlayCards(); else engine.ExecuteQueuedAutoPlayActions();
            }
            if (!receipts || !refreshed || source.Location != CardLocation.Discard || source.LocationNumber != 1)
                return $"order play/move receipts and discard insertion must precede callback: {side}/{residentSlot}/{route}, receipts={receipts}, refresh={refreshed}";
            if (engine.Api.Vm.FaultedPrograms != 0) return "order movement VM fault";
        }
        foreach (Side side in new[] { Side.Left, Side.Right })
        {
            var (engine, source, units) = ActionTargetSmoke.Fixture(db, side);
            source.LocationNumber = 3;
            if (!MainActionPolicyAdapter.ApplyRoot(engine, side, new(MainActionKind.Play, 70, 81))) return "targeted order rejected";
            var play = engine.State.ActionLog.SelectMany(a => a.SubActions).Single(a => a.Name == "ZActionPlayCardFromHand");
            if (play.Values.Single(v => v.Name == "targetCardID").Value != 81
                || units[1].Defense != 4 || units[1].PinnedTurns <= 0 || units[0].Defense != 5
                || source.LocationNumber != 3 || engine.Api.Vm.FaultedPrograms != 0)
                return "targeted order receipt/effect must preserve target and discarded source slot";
        }
        return null;
    }
}
