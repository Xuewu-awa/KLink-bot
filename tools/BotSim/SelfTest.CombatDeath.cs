using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? CombatHqSavedIdentity(CardDatabase db)
    {
        using var callback = InstallSelectionDrawProbe(EffectDamageProbeName, "OnCardDealDamage");
        var pair = typeof(CardApi).GetMethod("DealCombatDamagePair",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (Side slot in new[] { Side.Left, Side.Right })
        foreach (int mode in Enumerable.Range(0, 4))
        {
            var (engine, state) = EmptyBoard(db);var defender = state.Hq(slot);
            defender.Keywords.Clear();defender.Attack = 0;defender.Defense = 3;
            var attacker = state.CreateWithId(EffectDamageProbeName, slot.Opposite(), 70, slot.Opposite().HqOf(), 1);
            attacker.Definition = new CardDefinition { Name = attacker.Name, Type = "infantry" };
            attacker.Keywords.Clear();attacker.Attack = 4;attacker.Defense = 20;
            var replacement = state.CreateWithId(PlainUnit, slot, 74, CardLocation.Discard, 0);
            state.CreateWithId("card_unit_51e_regiment", slot.Opposite(), 73, slot.Opposite().DeckOf(), 0);
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => {
                if (called) return;called = true;
                if (mode == 1) state.ChangeOwner(defender, slot.Opposite(), normalize: false);
                if (mode == 2) defender.Definition = new CardDefinition { Name = defender.Name, Type = "infantry" };
                if (mode == 3) state.SetLocationCardBySide(slot, replacement);
            });
            pair.Invoke(engine.Api, new object[] { attacker, defender });
            if (!called || state.IsFinished != (mode != 3)
                || state.Winner != (mode == 3 ? Side.NotAvailable : slot.Opposite())
                || mode == 3 && defender.Location != CardLocation.Discard)
                return $"战斗死亡须在回调后按保存的侧别总部ID判断：{slot}/{mode}, winner={state.Winner}, location={defender.Location}";
        }
        return null;
    }

    private static string? CombatRetainedDeathCallbacks(CardDatabase db)
    {
        using var own = InstallRetainedDestroyProbes();
        using var callback = InstallSelectionDrawProbe(EffectDamageProbeName, "OnCardDealDamage");
        var pair = typeof(CardApi).GetMethod("DealCombatDamagePair",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (bool bothDie in new[] { false, true })
        foreach (int mode in Enumerable.Range(0, 3))
        {
            var (engine, state) = EmptyBoard(db);
            var attacker = state.CreateWithId(EffectDamageProbeName, side, 70, side.HqOf(), 1);
            attacker.Definition = new CardDefinition { Name = attacker.Name, Type = "infantry" };
            attacker.Keywords.Clear();attacker.Attack = 4;attacker.Defense = bothDie ? 2 : 20;
            var defender = state.CreateWithId(PlainUnit, side.Opposite(), 71, side.Opposite().HqOf(), 1);
            defender.Definition = new CardDefinition { Name = defender.Name, Type = "infantry",
                UsedTriggers = new[] { "OnBeforeDestroyed", "OnDestroyed", "OnAfterDestroyed" } };
            defender.Keywords.Clear();defender.Keywords.Add(Keyword.Destruction);defender.Attack = 3;defender.Defense = 3;
            state.CreateWithId("card_unit_51e_regiment", side, 73, side.DeckOf(), 0);
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => {
                if (called) return;called = true;defender.Defense = 9;
                if (mode == 2) defender.Definition = new CardDefinition { Name = defender.Name, Type = "location",
                    UsedTriggers = new[] { "OnBeforeDestroyed", "OnDestroyed", "OnAfterDestroyed" } };
                else state.Move(defender, mode == 0 ? CardLocation.Discard : CardLocation.NotAvailable, 27,
                    notifyMoved: false, normalizeLocation: false);
            });
            pair.Invoke(engine.Api, new object[] { attacker, defender });
            int Count(string hook) => Convert.ToInt32(defender.BlueprintVariables.GetValueOrDefault("_retained_" + hook));
            if (!called || state.IsFinished || defender.Location != CardLocation.Discard || defender.Defense != 9
                || Count("OnBeforeDestroyed") != 1 || Count("OnDestroyed") != 1 || Count("OnAfterDestroyed") != 1
                || bothDie && attacker.Location != CardLocation.Discard
                || state.ActionLog.SelectMany(a => a.SubActions).Count(a => a.Name == "ZActionLeaveBoardOrOwner"
                    && a.Values.Any(v => v.Name == "instigatorID" && v.Value == defender.CardId)) != (mode == 2 ? 1 : 0)
                || state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionDestroyUnit"))
                return $"战斗致死快照后换区/类型变化不能截断死亡链：{side}/{bothDie}/{mode}, after={Count("OnAfterDestroyed")}";
        }
        return null;
    }
}
