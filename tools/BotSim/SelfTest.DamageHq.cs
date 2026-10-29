using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? DamageHqSideIdentity(CardDatabase db)
    {
        using var callback = InstallSelectionDrawProbe(EffectDamageProbeName, "OnCardDealDamage");
        var apply = typeof(CardApi).GetMethod("ApplyCalculatedDamage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int ownerMode in Enumerable.Range(0, 3))
        foreach (bool lethal in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var hq = state.Hq(side); hq.Defense = hq.MaxDefense = 3;
            var source = state.CreateWithId(EffectDamageProbeName, side.Opposite(), 70, CardLocation.Discard, 0);
            source.Definition = new CardDefinition { Name = source.Name, Type = "order" }; source.Keywords.Clear();
            state.CreateWithId("card_unit_51e_regiment", side.Opposite(), 73, side.Opposite().DeckOf(), 0);
            if (ownerMode == 1) hq.Owner = side.Opposite();
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => {
                called = true;
                if (ownerMode == 2) hq.Owner = side.Opposite();
            });
            apply.Invoke(engine.Api, new object?[] { hq, lethal ? 3 : 1, source, false, false, false, null, false, null, false });
            if (!called || state.IsFinished != lethal || state.Winner != (lethal ? side.Opposite() : Side.NotAvailable)
                || hq.Location != side.HqOf() || state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionDestroyUnit"))
                return $"总部致死胜方须按侧别总部ID确定，不按可变Owner：{side}/{ownerMode}/{lethal}, winner={state.Winner}";
        }
        return null;
    }

    private static string? DamageHqLocationImpostor(CardDatabase db)
    {
        using var own = InstallRetainedDestroyProbes();
        using var callback = InstallSelectionDrawProbe(EffectDamageProbeName, "OnCardDealDamage");
        var apply = typeof(CardApi).GetMethod("ApplyCalculatedDamage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (bool convertAfter in new[] { false, true })
        foreach (bool suppressed in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(EffectDamageProbeName, side.Opposite(), 70, CardLocation.Discard, 0);
            source.Definition = new CardDefinition { Name = source.Name, Type = "order" }; source.Keywords.Clear();
            var victim = state.CreateWithId(PlainUnit, side, 71, side.HqOf(), 27);
            CardDefinition Definition(string type) => new() { Name = victim.Name, Type = type,
                UsedTriggers = new[] { "OnBeforeDestroyed", "OnDestroyed", "OnAfterDestroyed" } };
            victim.Definition = Definition(convertAfter ? "infantry" : "location");
            victim.Keywords.Clear(); victim.Keywords.Add(Keyword.Destruction);
            if (suppressed) victim.Keywords.Add(Keyword.Suppressed);
            victim.Defense = victim.MaxDefense = 3;
            state.CreateWithId("card_unit_51e_regiment", side.Opposite(), 73, side.Opposite().DeckOf(), 0);
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => { called = true; if (convertAfter) victim.Definition = Definition("location"); });
            apply.Invoke(engine.Api, new object?[] { victim, 3, source, false, false, false, null, false, null, false });
            int Count(string hook) => Convert.ToInt32(victim.BlueprintVariables.GetValueOrDefault("_retained_" + hook));
            if (!called || state.IsFinished || victim.Location != CardLocation.Discard
                || Count("OnBeforeDestroyed") != (suppressed ? 0 : 1) || Count("OnAfterDestroyed") != 1
                || state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionDestroyUnit"))
                return $"非总部ID的位置牌不能仅凭类型触发胜负：{side}/{convertAfter}/{suppressed}, finished={state.IsFinished}";
        }
        return null;
    }
}
