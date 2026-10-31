using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? NativeSpawnHandNonVisualReceipt(CardDatabase db)
    {
        const string name = "card_unit_51e_regiment", changedName = "card_unit_routed_troops";
        string[] factions = { "NotAvailable", "Germany", "Britain", "Japan", "Soviet", "USA",
            "France", "Italy", "Poland", "Finland", "Anzac", "Allies", "Neutral" };
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (bool gold in new[] { false, true })
        foreach (int faction in Enumerable.Range(0, factions.Length))
        foreach (bool seen in new[] { false, true })
        foreach (int ownMutation in Enumerable.Range(0, 3))
        foreach (bool modifierMutation in new[] { false, true })
        foreach (bool full in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(PlainUnit, side, 30, CardLocation.Discard, 0);
            source.IsGold = gold;
            if (full) for (int i = 0; i < 9; i++) state.CreateWithId(PlainUnit, side, 100 + i, side.HandOf(), i);
            List<SubAction> Actions() => state.ActionLog.SelectMany(a => a.SubActions).ToList();
            List<SubAction> Receipts() => Actions().Where(a => a.Name == "ZActionCreateNonVisualCard").ToList();
            CardInstance? generated = null;
            List<ActionValue2>? expected = null;
            bool correct = true;
            int ownCalls = 0, modifierCalls = 0;
            state.CardCreated += card => {
                if (card.Name != name) return;
                generated = card; ownCalls++;
                correct &= Receipts().Count == 0 && card.IsGold == gold;
                int liveFaction = ownMutation == 1 ? (faction + 1) % factions.Length : faction;
                if (ownMutation != 0)
                {
                    card.Owner = ownMutation == 1 ? side.Opposite() : Side.NotAvailable;
                    card.Location = ownMutation == 1 ? side.DeckOf() : CardLocation.Discard;
                    card.LocationNumber = ownMutation == 1 ? -7 : 999;
                    card.IsGold = !gold;
                    card.Reidentify(changedName, db.Require(changedName));
                    // Payload gates on the enum, not the independent isSalvaged flag.
                    card.IsSalvaged = false;
                    card.SalvageFaction = ownMutation == 1 ? factions[liveFaction]
                        : liveFaction.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                string location = ownMutation == 0 ? (side == Side.Left ? "Hand_Left" : "Hand_Right")
                    : ownMutation == 1 ? (side == Side.Left ? "Deck_Left" : "Deck_Right") : "Discard";
                string owner = ownMutation == 2 ? "NotAvailable" : (ownMutation == 1 ? side.Opposite() : side).ToWire();
                expected = new() {
                    ActionValue2.Int("cardID", card.CardId), ActionValue2.Str("location", location),
                    ActionValue2.Int("locationNumber", card.LocationNumber), ActionValue2.Str("side", owner),
                    ActionValue2.Str("card_type", card.Name), ActionValue2.Bool("gold", card.IsGold)
                };
                if (liveFaction != 0) expected.Add(ActionValue2.Str("salvageFaction", factions[liveFaction]));
            };
            state.CardCreationModifiers += card => {
                if (card != generated) return;
                modifierCalls++;
                var receipts = Receipts();
                correct &= receipts.Count == 1 && expected is not null
                    && receipts[0].Values.SequenceEqual(expected) && card.CardSeen == seen;
                if (!modifierMutation) return;
                card.Owner = side; card.Location = CardLocation.BoardFrontline; card.LocationNumber = 77;
                card.IsGold = gold; card.Reidentify(name, db.Require(name));
                card.SalvageFaction = "Neutral";
            };
            var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = side };
            var raw = engine.Api.InvokeByName("SpawnCardInHandBySide", source,
                new object?[] { (int)side, name, 30, seen, false, false, "", "", faction, null }, ctx, out bool handled);
            var actions = Actions(); var receipts = Receipts();
            int allocation = actions.FindIndex(a => a.Name == "ZActionCreateNonVisualCard");
            int draw = actions.FindIndex(a => a.Name == "ZActionDrawCardFromDeck");
            if (!handled || raw is not CardInstance || !correct || ownCalls != 1 || modifierCalls != 1
                || receipts.Count != 1 || expected is null || !receipts[0].Values.SequenceEqual(expected)
                || allocation < 0 || draw <= allocation)
                return $"非可视生成通知错误：{side}/gold={gold}/faction={faction}/seen={seen}/own={ownMutation}/T35={modifierMutation}/full={full}/correct={correct}/receipts={receipts.Count}";
        }
        // Lower-level allocation alone is not the ordinary CreateCard action path.
        {
            var (engine, state) = EmptyBoard(db);
            state.Create(name, Side.Left, CardLocation.HandLeft, 0);
            if (state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionCreateNonVisualCard"))
                return "低层对象分配不应自动产生普通手牌生成通知";
        }
        return null;
    }
}
