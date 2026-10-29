using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? HqSlotLifecycle(CardDatabase db)
    {
        var setter = typeof(GameState).GetMethod("SetLocationCardBySide");
        var clear = typeof(GameState).GetMethod("ClearLocationCardBindings");
        if (setter is null || clear is null) return "缺少显式总部保存槽/清空入口";
        foreach (Side slot in new[] { Side.Left, Side.Right })
        foreach (Side owner in new[] { Side.Left, Side.Right })
        foreach (bool locationType in new[] { false, true })
        foreach (CardLocation zone in new[] { CardLocation.HandLeft, CardLocation.Discard, CardLocation.BoardHqRight })
        {
            var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 1);
            var state = engine.State;
            var card = state.CreateWithId(locationType ? "card_location_berlin" : PlainUnit, owner, 71, zone, 0);
            var decoy = state.CreateWithId("card_location_london", slot, 72, slot.HqOf(), 0);
            if (engine.Api.GetLocationCardBySide(slot) is not null) return "创建位置卡不能隐式绑定总部";
            setter.Invoke(state, new object?[] { slot, card });
            bool IsBound(CardInstance? expected)
            {
                var ctx = new EffectContext { Engine = engine, State = state, Self = card, Controller = slot };
                var raw = engine.Api.InvokeByName("GetLocationCardBySide", null, new object?[] { null, null, slot }, ctx, out bool handled);
                return handled && raw is object?[] values && ReferenceEquals(values[0], expected)
                    && Convert.ToInt32(values[1]) == (expected?.CardId ?? 0)
                    && ReferenceEquals(engine.Api.GetLocationCardBySide(slot), expected);
            }
            if (!IsBound(card)) return "总部绑定不能按类型、归属或位置过滤";
            state.ChangeOwner(card, owner.Opposite(), normalize: false);
            card.Definition = new CardDefinition { Name = card.Name, Type = "infantry" };
            if (!IsBound(card) || !ReferenceEquals(state.Hq(slot), card)) return "换主/类型变化不能替换保存的总部引用";
            foreach (Side invalid in new[] { Side.NotAvailable, (Side)3, (Side)255 })
            {
                setter.Invoke(state, new object?[] { invalid, decoy });
                if (engine.Api.GetLocationCardBySide(invalid) is not null || !IsBound(card)) return "无效侧别不能改写总部槽";
            }
            setter.Invoke(state, new object?[] { slot, decoy });
            if (!IsBound(decoy)) return "显式重绑定必须覆盖旧引用和ID";
            setter.Invoke(state, new object?[] { slot, null });
            if (!IsBound(null)) return "显式空引用应清零ID，不能回退到侧表";
            setter.Invoke(state, new object?[] { Side.Left, card });
            setter.Invoke(state, new object?[] { Side.Right, decoy });
            clear.Invoke(state, null);
            if (engine.Api.GetLocationCardBySide(Side.Left) is not null || engine.Api.GetLocationCardBySide(Side.Right) is not null
                || !ReferenceEquals(state.ById(71), card)) return "总部槽清理应清双侧引用/ID；局部入口不伪装整局重置";
        }
        return null;
    }

    private static string? HqSlotDamageIntegration(CardDatabase db)
    {
        var setter = typeof(GameState).GetMethod("SetLocationCardBySide");
        if (setter is null) return "缺少显式总部保存槽";
        using var callback = InstallSelectionDrawProbe(EffectDamageProbeName, "OnCardDealDamage");
        var apply = typeof(CardApi).GetMethod("ApplyCalculatedDamage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (Side slot in new[] { Side.Left, Side.Right })
        foreach (int mutation in Enumerable.Range(0, 4))
        {
            var (engine, state) = EmptyBoard(db);var target = state.Hq(slot);target.Defense = 1;
            var source = state.CreateWithId(EffectDamageProbeName, slot.Opposite(), 70, CardLocation.Discard, 0);
            source.Keywords.Clear();
            var replacement = state.CreateWithId(PlainUnit, slot, 74, CardLocation.Discard, 0);
            state.CreateWithId("card_unit_51e_regiment", slot.Opposite(), 73, slot.Opposite().DeckOf(), 0);
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => {
                called = true;
                if (mutation is 0 or 2) target.Definition = new CardDefinition { Name = target.Name, Type = "infantry" };
                if (mutation is 1 or 2) state.ChangeOwner(target, slot.Opposite(), normalize: false);
                if (mutation == 3) setter.Invoke(state, new object?[] { slot, replacement });
            });
            apply.Invoke(engine.Api, new object?[] { target, 1, source, false, false, false, null, false, null, false });
            if (!called || state.IsFinished != (mutation != 3)
                || state.Winner != (mutation == 3 ? Side.NotAvailable : slot.Opposite())
                || mutation == 3 && target.Location != CardLocation.Discard)
                return $"致死回调后的总部查询须读取保存槽及显式重绑定：{slot}/{mutation}";
        }
        var (realEngine, realState) = EmptyBoard(db);
        var hq = realState.Hq(Side.Right);hq.Defense = 1;
        hq.Definition = new CardDefinition { Name = hq.Name, Type = "infantry" };
        realState.ChangeOwner(hq, Side.Left, normalize: false);
        var spell = realState.CreateWithId("card_event_bloody_sickle", Side.Left, 90, CardLocation.Discard, 0);
        realEngine.Api.RunCardEffect(spell, hq);
        return realState.IsFinished && realState.Winner == Side.Left && ReferenceEquals(realState.Hq(Side.Right), hq)
            ? null : "血腥镰刀真实IR伤害应使用保存的右侧总部身份判胜";
    }
}
