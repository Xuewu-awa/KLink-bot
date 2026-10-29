using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? DamageDeathRetainedSnapshot(CardDatabase db)
    {
        using var own = InstallRetainedDestroyProbes();
        using var callback = InstallSelectionDrawProbe(EffectDamageProbeName, "OnCardDealDamage");
        using var payload = InstallCombatPayloadProbes(onlyDestroyed: true);
        var apply = typeof(CardApi).GetMethod("ApplyCalculatedDamage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int mode in Enumerable.Range(0, 5))
        foreach (bool suppressed in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(EffectDamageProbeName, side, 70, CardLocation.Discard, 0);
            source.Definition = new CardDefinition { Name = source.Name, Type = "order" }; source.Keywords.Clear();
            var victim = state.CreateWithId(PlainUnit, side.Opposite(), 71, side.Opposite().HqOf(), 27);
            victim.Definition = new CardDefinition { Name = victim.Name, Type = "infantry",
                UsedTriggers = new[] { "OnBeforeDestroyed", "OnDestroyed", "OnAfterDestroyed" } };
            victim.Keywords.Clear(); victim.Keywords.Add(Keyword.Destruction);
            if (suppressed) victim.Keywords.Add(Keyword.Suppressed);
            victim.Defense = 3; victim.MaxDefense = 12;
            var observer = state.CreateWithId(EffectDamageProbeName, side, 72, side.HqOf(), 1);
            observer.Definition = new CardDefinition { Name = observer.Name, Type = "fighter", UsedTriggers = new[] { "OnOtherCardDestroyed" } };
            observer.Keywords.Clear();
            state.CreateWithId("card_unit_51e_regiment", side, 73, side.DeckOf(), 0);
            bool called = false; bool premature = false;
            using var reentry = InstallDrawReentryProbe(state, () => {
                if (called) return;
                called = true;
                premature = victim.BlueprintVariables.ContainsKey("_retained_OnBeforeDestroyed");
                victim.Defense = 9; // The already-saved lethal decision must survive healing.
                if (mode == 4) engine.Api.DestroyCard(victim, source);
                else if (mode != 0) state.Move(victim, mode switch {
                    1 => CardLocation.Discard, 2 => CardLocation.NotAvailable, _ => side.Opposite().HandOf()
                }, 27, notifyMoved: false, normalizeLocation: false);
            });
            apply.Invoke(engine.Api, new object?[] { victim, 5, source, false, false, false, null, false, null, false });
            engine.ApplyPendingLocalRuleReceipts();
            int repeats = mode == 4 ? 2 : 1;
            int Count(string hook) => Convert.ToInt32(victim.BlueprintVariables.GetValueOrDefault("_retained_" + hook));
            var old = mode switch { 0 => side.Opposite().HqOf(), 2 => CardLocation.NotAvailable,
                3 => side.Opposite().HandOf(), _ => CardLocation.Discard };
            if (!called || premature || victim.Location != CardLocation.Discard || victim.Defense != 9
                || !ReferenceEquals(state.ById(71), victim)
                || Count("OnBeforeDestroyed") != (suppressed ? 0 : repeats)
                || Count("OnDestroyed") != (suppressed ? 0 : repeats) || Count("OnAfterDestroyed") != repeats
                || !state.DestroyedCardIdsAll.SequenceEqual(Enumerable.Repeat(71, repeats))
                || Convert.ToInt32(observer.BlueprintVariables.GetValueOrDefault("_auditDeathPayloadSerial")) != repeats
                || Convert.ToInt32(observer.BlueprintVariables.GetValueOrDefault("_auditPayloadOld")) != (int)old
                || state.ActionLog.SelectMany(a => a.SubActions).Count(a => a.Name == "ZActionDestroyUnit") != (mode == 4 ? 1 : 0))
                return $"致死快照后回调不得被区域/恢复防御/重复移除截断：{side}/{mode}/{suppressed}, before={Count("OnBeforeDestroyed")}, after={Count("OnAfterDestroyed")}, called={called}, premature={premature}, loc={victim.Location}, def={victim.Defense}, own={Count("OnDestroyed")}, serial={observer.BlueprintVariables.GetValueOrDefault("_auditDeathPayloadSerial")}, old={observer.BlueprintVariables.GetValueOrDefault("_auditPayloadOld")}/{(int)old}, history={string.Join(',',state.DestroyedCardIdsAll)}";
        }
        return null;
    }

    private static string? DamageDeathNonlethalSnapshot(CardDatabase db)
    {
        using var own = InstallRetainedDestroyProbes();
        using var callback = InstallSelectionDrawProbe(EffectDamageProbeName, "OnCardDealDamage");
        var apply = typeof(CardApi).GetMethod("ApplyCalculatedDamage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (bool lethalAfterCallback in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(EffectDamageProbeName, Side.Left, 70, CardLocation.Discard, 0); source.Keywords.Clear();
            var victim = state.CreateWithId(PlainUnit, Side.Right, 71, CardLocation.BoardHqRight, 1);
            victim.Keywords.Clear(); victim.Defense = victim.MaxDefense = 3;
            state.CreateWithId("card_unit_51e_regiment", Side.Left, 73, CardLocation.DeckLeft, 0);
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => { called = true; victim.Defense = lethalAfterCallback ? 0 : 9; });
            apply.Invoke(engine.Api, new object?[] { victim, 1, source, false, false, false, null, false, null, false });
            var notice = state.ActionLog.SelectMany(a => a.SubActions).Single(a => a.Name == "ZActionDamageCard");
            if (!called || victim.Location != CardLocation.BoardHqRight || state.DestroyedCardIdsAll.Count != 0
                || victim.BlueprintVariables.ContainsKey("_retained_OnBeforeDestroyed")
                || notice.Values.Single(v => v.Name == "destroyed").Value != 0)
                return "本伤害入口不得用伤害后回调的新防御重新计算非致死快照";
        }
        return null;
    }
}
