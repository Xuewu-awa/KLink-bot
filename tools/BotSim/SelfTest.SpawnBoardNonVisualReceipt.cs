using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? NativeSpawnBoardNonVisualReceipt(CardDatabase db)
    {
        const string name = "card_unit_rule475_fixture";
        var fixture = VeteranRawFixture(db, 0);
        string[] factions = { "NotAvailable", "Germany", "Britain", "Japan", "Soviet", "USA",
            "France", "Italy", "Poland", "Finland", "Anzac", "Allies", "Neutral" };
        int cases = 0;
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (bool frontline in new[] { false, true })
        foreach (int slot in new[] { -1, 2 })
        foreach (bool gold in new[] { false, true })
        foreach (int faction in Enumerable.Range(0, factions.Length))
        foreach (bool veteran in new[] { false, true })
        foreach (int ownMutation in Enumerable.Range(0, 3))
        foreach (bool modifierMutation in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(fixture);
            CardLocation where = frontline ? CardLocation.BoardFrontline : side.HqOf();
            state.CreateWithId(PlainUnit, side, 30, where, 0);
            List<SubAction> Actions() => state.ActionLog.SelectMany(a => a.SubActions).ToList();
            List<SubAction> Receipts() => Actions().Where(a => a.Name == "ZActionCreateNonVisualCard").ToList();
            List<ActionValue2>? expected = null;
            CardInstance? generated = null;
            bool correct = true;
            int ownCalls = 0, modifierCalls = 0;
            state.CardCreated += card => {
                if (card.Name != name) return;
                generated = card; ownCalls++;
                correct &= Receipts().Count == 0 && !card.CustomJson.ContainsKey("veteran")
                    && card.LocationNumber == (slot == -1 ? (frontline ? 1 : 2) : slot) && card.IsGold == gold;
                int liveFaction = ownMutation == 1 ? (faction + 1) % factions.Length : faction;
                if (ownMutation != 0)
                {
                    card.Owner = ownMutation == 1 ? side.Opposite() : Side.NotAvailable;
                    card.LocationNumber = ownMutation == 1 ? -7 : 999;
                    card.IsGold = !gold;
                    card.IsSalvaged = false;
                    card.SalvageFaction = ownMutation == 1 ? factions[liveFaction] : liveFaction.ToString();
                    if (ownMutation == 2)
                    {
                        card.Location = CardLocation.Discard;
                        card.Reidentify("card_unit_routed_troops", fixture.Require("card_unit_routed_troops"));
                    }
                }
                string location = ownMutation == 2 ? "Discard" : frontline ? "Board_Frontline"
                    : side == Side.Left ? "Board_HQLeft" : "Board_HQRight";
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
                correct &= receipts.Count == 1 && expected is not null && receipts[0].Values.SequenceEqual(expected)
                    && !card.CardSeen && card.CustomJson.ContainsKey("veteran") == (veteran && ownMutation != 2);
                if (!modifierMutation) return;
                card.Owner = side; card.Location = side.HandOf(); card.LocationNumber = 77;
                card.IsGold = gold; card.SalvageFaction = "Neutral";
            };
            engine.Api.SpawnOnBattlefield(side, name, frontline, slot, forceGoldCard: gold,
                salvageFaction: faction, makeVeteran: veteran);
            var actions = Actions(); var receipts = Receipts();
            int allocation = actions.FindIndex(a => a.Name == "ZActionCreateNonVisualCard");
            int upgrade = actions.FindIndex(a => a.Name == "ZActionMakeVeteran");
            int spawn = actions.FindIndex(a => a.Name == "ZActionSpawnCard");
            if (!correct || ownCalls != 1 || modifierCalls != 1 || receipts.Count != 1
                || expected is null || !receipts[0].Values.SequenceEqual(expected) || allocation < 0 || spawn <= allocation
                || (veteran && ownMutation != 2 && upgrade <= allocation)
                || (!(veteran && ownMutation != 2) && upgrade >= 0))
                return $"board allocation receipt {side}/{frontline}/slot={slot}/gold={gold}/faction={faction}/veteran={veteran}/own={ownMutation}/T35={modifierMutation}: correct={correct}, receipts={receipts.Count}";
            cases++;
        }
        // Lower-level object allocation must not inherit the board action receipt.
        {
            var (engine, state) = EmptyBoard(db);
            state.Create(PlainUnit, Side.Left, CardLocation.BoardHqLeft, 0);
            if (state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionCreateNonVisualCard"))
                return "low-level allocation must not inherit ordinary board receipt";
        }
        return cases == 2496 ? null : $"board allocation receipt cases={cases}";
    }
}
