using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    // CreateCard 885/917 -> GetNextCardLocationNumber 483/551 ->
    // BP_GameState_Battle.FetchCardsByLocation 801/1067/1135.
    private static string? NativeSpawnHandInitialSlot(CardDatabase db)
    {
        const string generated = "card_unit_51e_regiment";
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int count in new[] { 0, 3, 9 })
        foreach (bool oppositeOwner in new[] { false, true })
        foreach (int tagMode in Enumerable.Range(0, 4))
        foreach (int mutation in Enumerable.Range(0, 3))
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(PlainUnit, side, 30, CardLocation.Discard, 0);
            int expectedSlot = 0;
            for (int i = 0; i < count; i++)
            {
                // Deliberately sparse/negative slots and mismatched ownership.
                // Tags include inherited, custom, and explicitly removed values.
                var held = state.CreateWithId(tagMode is 2 or 3 ? "card_event_resistance_new" : PlainUnit,
                    oppositeOwner ? side.Opposite() : side, 100 + i, side.HandOf(), i * 7 - 2);
                if (tagMode == 1) engine.Api.AddCustomGameplayTag(held, "autoplay");
                if (tagMode == 3) held.RemovedGameplayTags.Add("autoplay");
                if (tagMode is 0 or 3) expectedSlot++;
            }
            // Cards owned by this side in the other physical hand must not count.
            state.CreateWithId(PlainUnit, side, 200, side.Opposite().HandOf(), 70);
            bool correct = true;
            int ownCalls = 0, modifierCalls = 0;
            state.CardCreated += card => {
                if (card.Name != generated) return;
                ownCalls++;
                correct &= card.LocationNumber == expectedSlot && card.Location == side.HandOf();
                // Initial slot is already saved before callbacks mutate occupancy.
                state.ById(200)!.Location = side.HandOf();
                if (count > 0) state.ById(100)!.Location = CardLocation.Discard;
                if (mutation == 1) card.LocationNumber = 42;
            };
            state.CardCreationModifiers += card => {
                if (card.Name != generated) return;
                modifierCalls++;
                correct &= card.LocationNumber == (mutation == 1 ? 42 : expectedSlot);
                if (mutation == 2) card.LocationNumber = -7;
            };
            var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = side };
            var raw = engine.Api.InvokeByName("SpawnCardInHandBySide", source,
                new object?[] { (int)side, generated, source.CardId, false, false, false, "", "", 0, null },
                ctx, out bool handled);
            int finalSlot = mutation == 1 ? 42 : mutation == 2 ? -7 : expectedSlot;
            if (!handled || raw is not CardInstance result || !correct || ownCalls != 1 || modifierCalls != 1
                || result.LocationNumber != finalSlot
                || result.Location != (expectedSlot >= GameState.HandCapacity ? CardLocation.Discard : side.HandOf())
                || state.Random.ConsumedCount != 0)
                return $"生成初始槽位错误：{side}/count={count}/owner={oppositeOwner}/tag={tagMode}/mutation={mutation}, expected={expectedSlot}";
        }
        return null;
    }
}
