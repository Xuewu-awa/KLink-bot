using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? HandDeploymentLocationRefresh(CardDatabase db)
    {
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (bool sparse in new[] { false, true })
        foreach (int sourceId in new[] { 40, 90 })
        foreach (int route in new[] { 0, 1, 2 })
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = side; state.SetKredits(side, 12);
            var hq = state.Hq(side);
            hq.LocationNumber = 0;
            var resident = state.CreateWithId("card_unit_panzer_35_t", side, 60, side.HqOf(), sparse ? 3 : 1);
            var source = state.CreateWithId("card_unit_panzer_35_t", side, sourceId, side.HandOf(), 1);
            var before = state.CreateWithId("card_unit_panzer_35_t", side, 101, side.HandOf(), 0);
            var after = state.CreateWithId("card_unit_panzer_35_t", side, 102, side.HandOf(), 2);
            source.HasMovedThisTurn = sourceId == 90;
            source.SetRemainingAttacks(sourceId == 90 ? 2 : 1);
            bool callbackSawRefresh = false;
            bool callbackSawReceipts = false;
            int expectedSource = route == 0 ? (sparse ? 1 : 2) : 0;
            int expectedResident = route == 0 ? (sparse ? 2 : 1) : 2;
            int expectedHq = route == 0 ? 0 : 1;
            var original = state.CardMoved;
            state.CardMoved = (card, oldLocation, newLocation, owner, reason) => {
                if (card == source)
                {
                    var notices = state.ActionLog.SelectMany(a => a.SubActions).ToArray();
                    var play = notices.SingleOrDefault(a => a.Name == "ZActionPlayCardFromHand");
                    var move = notices.SingleOrDefault(a => a.Name == "ZActionMoveCardToNewLocation");
                    string T(SubAction? a, string key) => a?.Values.LastOrDefault(v => v.Name == key)?.Text ?? "";
                    KLink.Bot.Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", (int)side.HandOf(), out var handName);
                    KLink.Bot.Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", (int)side.HqOf(), out var boardName);
                    KLink.Bot.Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Game/Structs/CardMoveReason.CardMoveReason", 4, out var reasonName);
                    int V(SubAction? a, string key) => a?.Values.LastOrDefault(v => v.Name == key)?.Value ?? -999;
                    callbackSawReceipts = play is not null && move is not null
                        && Array.IndexOf(notices, play) < Array.IndexOf(notices, move)
                        && V(play, "cardID") == sourceId && V(play, "instigatorID") == sourceId
                        && V(play, "locationNumber") == (route == 0 ? 2 : 0)
                        && V(play, "nonVisualLocationNumber") == (route == 0 ? 2 : 0)
                        && V(play, "targetCardID") == 0 && V(play, "enterPlayOnTurn") == state.Turn
                        && V(move, "cardID") == sourceId && V(move, "instigatorID") == sourceId
                        && V(move, "newLocationNumber") == (route == 0 ? 2 : 0)
                        && V(move, "nonVisualLocationNumber") == (route == 0 ? 2 : 0)
                        && V(move, "attackLeft") == (sourceId == 90 ? 2 : 1)
                        && V(move, "movementLeft") == (sourceId == 90 ? 0 : 1)
                        && T(play, "location") == boardName
                        && T(move, "oldLocation") == handName && T(move, "newLocation") == boardName
                        && T(move, "moveReason") == reasonName
                        && !play!.Values.Any(v => v.Name == "playedDirectly")
                        && !play.Values.Any(v => v.Name == "chooseOneIndex")
                        && reason == CardMoveReason.PlayCardPlayerMove;
                    callbackSawRefresh = before.LocationNumber == 0 && after.LocationNumber == 1
                        && source.LocationNumber == expectedSource
                        && resident.LocationNumber == expectedResident && hq.LocationNumber == expectedHq;
                }
                original?.Invoke(card, oldLocation, newLocation, owner, reason);
            };
            if (route == 0)
            {
                if (!MainActionPolicyAdapter.ApplyRoot(engine, side, new(MainActionKind.Play, sourceId)))
                    return "default hand deployment rejected";
            }
            else
            {
                state.AddAutoPlayCards(sourceId);
                if (route == 1) engine.ExecuteAutoPlayCards();
                else engine.ExecuteQueuedAutoPlayActions();
            }
            if (!callbackSawReceipts) return $"ordinary deployment receipts must precede movement callbacks with saved slots: {side}/{sparse}/{sourceId}/{route}";
            if (!callbackSawRefresh || after.LocationNumber != 1
                || source.LocationNumber != expectedSource || hq.LocationNumber != expectedHq
                || resident.LocationNumber != expectedResident || source.Location != side.HqOf()
                || state.GetAutoPlayCards().Length != 0)
                return $"hand deployment must gap-insert then refresh both locations before callback: {side}/{sparse}/{sourceId}/{route}";
            if (engine.Api.Vm.FaultedPrograms != 0) return "hand placement VM fault";
        }
        return null;
    }
}
