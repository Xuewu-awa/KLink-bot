using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    // Hand generation and deck draws deliberately share the same action name.
    private static bool IsDeckDrawReceipt(SubAction action) => action.Name == "ZActionDrawCardFromDeck"
        && !action.Values.Any(v => v.Name == "spawnInHand" && v.Value != 0);

    private static string? NativeSpawnHandReceipt(CardDatabase db)
    {
        const string name = "card_unit_51e_regiment", marker = "card_unit_routed_troops";
        const string own = "OnCardSpawnedInHand";
        if (KismetLibrary.Default?.Find(name)?.Locals is not IDictionary<string, IReadOnlyList<KismetStep>> locals
            || locals.ContainsKey(own)) return "无法安装生成通知探针";
        locals[own] = new[] {
            new KismetStep(0, "call", "SpawnCardInDeckBySide", new[] {
                new KismetExpr { Int = (int)Side.Right }, new KismetExpr { Str = marker },
                new KismetExpr { Int = 30 }, new KismetExpr { Int = 1 }
            }, Array.Empty<int>(), null, null, -1, null, null),
            new KismetStep(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null)
        };
        try
        {
            foreach (Side side in new[] { Side.Left, Side.Right })
            foreach (int count in new[] { 0, 8, 9 })
            foreach (bool autoplay in new[] { false, true })
            foreach (int mutation in Enumerable.Range(0, 4))
            foreach (string text in new[] { "", " ", "generated text" })
            foreach (bool skip in new[] { false, true })
            foreach (bool opposite in new[] { false, true })
            {
                var (engine, state) = EmptyBoard(db);
                var source = state.CreateWithId(PlainUnit, side, 30, CardLocation.Discard, 0);
                for (int i = 0; i < count; i++)
                    state.CreateWithId(PlainUnit, side.Opposite(), 100 + i, side.HandOf(), i * 3);
                var extra = state.CreateWithId(PlainUnit, side, 200, CardLocation.Discard, 0);
                bool correct = true; int markerCalls = 0;
                CardInstance? created = null;
                List<SubAction> Receipts() => state.ActionLog.SelectMany(a => a.SubActions)
                    .Where(a => a.Name == "ZActionDrawCardFromDeck").ToList();
                state.CardCreationModifiers += card => {
                    if (card.Name != name) return;
                    created = card;
                    correct &= Receipts().Count == 0;
                    if (autoplay) engine.Api.AddCustomGameplayTag(card, "autoplay");
                    if (mutation == 1) extra.Location = side.HandOf();
                    if (mutation == 2 && count > 0) state.ById(100)!.Location = CardLocation.Discard;
                    if (mutation == 3) card.Location = side.DeckOf();
                };
                state.CardCreated += card => {
                    if (card.Name != marker) return;
                    markerCalls++;
                    correct &= Receipts().Count == 1 && created is not null
                        && created.Location == (count >= 9 && !autoplay ? CardLocation.Discard
                            : mutation == 3 ? side.DeckOf() : side.HandOf());
                };
                var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = side };
                var raw = engine.Api.InvokeByName("SpawnCardInHandBySide", source,
                    new object?[] { (int)side, name, 30, false, skip, opposite, text, "", 0, null }, ctx, out bool handled);
                int qty = count + (mutation == 1 ? 1 : 0) - (mutation == 2 && count > 0 ? 1 : 0)
                    + (!autoplay && mutation != 3 ? 1 : 0);
                var expected = new List<ActionValue2> {
                    ActionValue2.Int("instigatorID", 30), ActionValue2.Str("side", side.ToWire()),
                    ActionValue2.Int("cardID", created?.CardId ?? -1), ActionValue2.Str("cardType", name),
                    ActionValue2.Bool("spawnInHand", true), ActionValue2.Bool("skipDrawAnimation", skip),
                    ActionValue2.Int("PreQtyInHand", qty - 1), ActionValue2.Bool("handIsFull", qty > 9),
                    ActionValue2.Str("drawDelay", "0.4")
                };
                if (text.Length > 0) expected.Add(ActionValue2.Str("newCardText", text));
                if (opposite) expected.Add(ActionValue2.Bool("oppSide", true));
                var receipts = Receipts();
                if (!handled || raw is not CardInstance || !correct || markerCalls != 1 || receipts.Count != 1
                    || !receipts[0].Values.SequenceEqual(expected)
                    || state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionSpawnCard"))
                    return $"生成通知错误：{side}/{count}/autoplay={autoplay}/mutation={mutation}/text={text.Length}/skip={skip}/opposite={opposite}/correct={correct}/markers={markerCalls}/receipts={receipts.Count}/actual={string.Join(",", receipts.SelectMany(x => x.Values))}/expected={string.Join(",", expected)}";
            }
        }
        finally { locals.Remove(own); }
        return null;
    }
}
