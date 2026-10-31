using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? NativeSpawnHandSeenReceipt(CardDatabase db)
    {
        const string name = "card_unit_51e_regiment", marker = "card_unit_routed_troops";
        const string own = "OnCardSpawnedInHand", other = "OnOtherCardSpawnedInHand";
        if (KismetLibrary.Default?.Find(name)?.Locals is not IDictionary<string, IReadOnlyList<KismetStep>> locals
            || locals.ContainsKey(own) || locals.ContainsKey(other)) return "无法安装生成公开通知探针";
        var program = new[] {
            new KismetStep(0, "call", "SpawnCardInDeckBySide", new[] {
                new KismetExpr { Int = (int)Side.Right }, new KismetExpr { Str = marker },
                new KismetExpr { Int = 30 }, new KismetExpr { Int = 1 }
            }, Array.Empty<int>(), null, null, -1, null, null),
            new KismetStep(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null)
        };
        locals[own] = program; locals[other] = program;
        try
        {
            foreach (Side side in new[] { Side.Left, Side.Right })
            foreach (bool full in new[] { false, true })
            foreach (bool autoplay in new[] { false, true })
            foreach (bool? seen in new bool?[] { null, false, true })
            foreach (int ownMutation in Enumerable.Range(0, 3))
            foreach (int modifierMutation in Enumerable.Range(0, 3))
            {
                var (engine, state) = EmptyBoard(db);
                var source = state.CreateWithId(PlainUnit, side, 30, CardLocation.Discard, 0);
                var watcher = state.CreateWithId(name, side.Opposite(), 20, CardLocation.Discard, 0);
                watcher.Definition = new CardDefinition { Name = name, Type = "infantry", UsedTriggers = new[] { other } };
                if (full) for (int i = 0; i < 9; i++) state.CreateWithId(PlainUnit, side, 100 + i, side.HandOf(), i);
                List<SubAction> Actions() => state.ActionLog.SelectMany(a => a.SubActions).ToList();
                List<SubAction> Seen() => Actions().Where(a => a.Name == "ZActionCardsSeen").ToList();
                CardInstance? generated = null;
                bool correct = true, expectedField = false;
                int events = 0;
                state.CardCreated += card => {
                    if (card.Name == name)
                    {
                        generated = card;
                        correct &= Seen().Count == 0;
                        if (ownMutation != 0) card.CardSeen = ownMutation == 2;
                        expectedField = card.CardSeen;
                        return;
                    }
                    if (card.Name != marker) return;
                    events++;
                    correct &= Seen().Count == (seen == true ? 1 : 0)
                        && generated is not null && generated.CardSeen == expectedField
                        && generated.Location == (full && !autoplay ? CardLocation.Discard : side.HandOf());
                };
                state.CardCreationModifiers += card => {
                    if (card != generated) return;
                    correct &= Seen().Count == 0 && card.CardSeen == (seen ?? expectedField);
                    if (modifierMutation != 0) card.CardSeen = modifierMutation == 2;
                    expectedField = card.CardSeen;
                    if (autoplay) engine.Api.AddCustomGameplayTag(card, "autoplay");
                };
                CardInstance result;
                if (!seen.HasValue) result = engine.Api.SpawnCardInHand(side, name);
                else
                {
                    var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = side };
                    var raw = engine.Api.InvokeByName("SpawnCardInHandBySide", source,
                        new object?[] { (int)side, name, 30, seen.Value, false, false, "", "", 0, null }, ctx, out bool handled);
                    if (!handled || raw is not CardInstance card) return "生成公开探针调用失败";
                    result = card;
                }
                var actions = Actions(); var notifications = Seen();
                var expected = new[] {
                    ActionValue2.Str("cardIDs", result.CardId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ActionValue2.Str("seenValues", "1"), ActionValue2.Bool("showAnimation", false),
                    ActionValue2.Bool("enemyTurn", false)
                };
                int drawIndex = actions.FindIndex(a => a.Name == "ZActionDrawCardFromDeck");
                int seenIndex = actions.FindIndex(a => a.Name == "ZActionCardsSeen");
                if (!correct || events != 2 || result.CardSeen != expectedField
                    || notifications.Count != (seen == true ? 1 : 0)
                    || seen == true && (!notifications[0].Values.SequenceEqual(expected) || seenIndex <= drawIndex))
                    return $"生成公开通知错误：{side}/full={full}/autoplay={autoplay}/seen={seen}/own={ownMutation}/T35={modifierMutation}/events={events}/receipts={notifications.Count}";
            }
        }
        finally { locals.Remove(own); locals.Remove(other); }
        return null;
    }
}
