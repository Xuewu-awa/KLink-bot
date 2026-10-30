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
            bool callbackSawRefresh = false;
            int expectedSource = route == 0 ? (sparse ? 1 : 2) : 0;
            int expectedResident = route == 0 ? (sparse ? 2 : 1) : 2;
            int expectedHq = route == 0 ? 0 : 1;
            var original = state.CardMoved;
            state.CardMoved = (card, oldLocation, newLocation, owner, reason) => {
                if (card == source)
                    callbackSawRefresh = before.LocationNumber == 0 && after.LocationNumber == 1
                        && source.LocationNumber == expectedSource
                        && resident.LocationNumber == expectedResident && hq.LocationNumber == expectedHq;
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
