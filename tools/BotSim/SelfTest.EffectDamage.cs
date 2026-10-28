using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private const string EffectDamageProbeName = "card_unit_typhoon_mk_ib";

    private static IDisposable InstallEffectEntryProbes()
    {
        var hooks = new[] { "OnCardDealDamage_ModifyDamageDealt", "OnDealDamageAddDamageAfterCalc" };
        var added = new List<string>();
        if (KismetLibrary.Default?.Find(EffectDamageProbeName)?.Locals is not IDictionary<string, IReadOnlyList<KismetStep>> locals)
            throw new InvalidOperationException("无法安装效果伤害入口探针");
        foreach (string hook in hooks)
        {
            if (KismetLibrary.Default.FindProgram(EffectDamageProbeName, hook) is not null)
            {
                foreach (var h in added) locals.Remove(h);
                throw new InvalidOperationException("效果伤害探针与真实程序冲突");
            }
            bool before = hook == hooks[0];
            locals.Add(hook, new[] {
                new KismetStep(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    new KismetExpr { Bool = true }, null, -1, before ? "_entryBefore" : "_entryAfter", null) { DestinationScope = "instance" },
                new KismetStep(1, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    new KismetExpr { Var = "isRedirected", Scope = "local" }, null, -1, "_entryRedirected", null) { DestinationScope = "instance" },
                new KismetStep(2, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    new KismetExpr { Int = before ? 4 : 2 }, null, -1, before ? "newDamage" : "damageToAdd", null) { DestinationScope = "local" },
                new KismetStep(3, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null),
            });
            added.Add(hook);
        }
        return new AuditProbeCleanup(() => { foreach (var h in added) locals.Remove(h); });
    }

    private static string? EffectDamageEntryContract(CardDatabase db)
    {
        using var probes = InstallEffectEntryProbes();
        foreach (CardLocation location in new[] { CardLocation.BoardHqLeft, CardLocation.BoardFrontline, CardLocation.HandLeft, CardLocation.DeckLeft, CardLocation.Discard, CardLocation.NotAvailable })
        foreach (int request in new[] { -2, 0, 1 })
        foreach (bool redirected in new[] { false, true })
        foreach (bool knownSource in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(EffectDamageProbeName, Side.Left, 70, CardLocation.Discard, 0);
            var target = state.CreateWithId(PlainUnit, Side.Right, 71, location, 1);
            source.Keywords.Clear(); target.Keywords.Clear(); target.Defense = target.MaxDefense = 20;
            var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };
            object? raw = engine.Api.InvokeByName("DamageCard", null,
                new object?[] { target, request, knownSource ? 70 : 9999, redirected, false, false, null }, ctx, out bool handled);
            bool board = location is CardLocation.BoardHqLeft or CardLocation.BoardHqRight or CardLocation.BoardFrontline;
            int final = knownSource ? redirected ? Math.Max(request + 2, 0) : 6 : redirected ? request : Math.Clamp(request, 0, 99);
            int applied = board ? Math.Max(final, 0) : 0;
            bool beforeCalled = source.BlueprintVariables.ContainsKey("_entryBefore");
            bool afterCalled = source.BlueprintVariables.ContainsKey("_entryAfter");
            if (!handled || raw is not bool destroyed || destroyed || target.Defense != 20 - applied
                || beforeCalled != (board && knownSource && !redirected) || afterCalled != (board && knownSource))
                return $"入口错误：{location}/{request}/redirect={redirected}/known={knownSource} 防御{target.Defense}/{20-applied} out={raw} before={beforeCalled} after={afterCalled}";
            var notices = state.ActionLog.SelectMany(a => a.SubActions).Where(a => a.Name == "ZActionDamageCard").ToArray();
            if (notices.Length != (board ? 1 : 0)) return "无效区域不应通知；有效区域零伤害应通知";
            if (board)
            {
                var fields = notices[0].Values.ToDictionary(v => v.Name);
                if (fields["damage"].Value != applied || fields["attackerCardID"].Value != (knownSource ? 70 : 0)) return "最终伤害或未知来源被替换为施法者";
                if (knownSource && !Equals(source.BlueprintVariables["_entryRedirected"], redirected)) return "最终修正未保留重定向标志";
            }
        }
        return null;
    }

    private static string? EffectDamageDestroyedOutput(CardDatabase db)
    {
        foreach (int mode in Enumerable.Range(0, 4))
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(PlainUnit, Side.Left, 74, CardLocation.Discard, 0);
            var target = state.CreateWithId(PlainUnit, Side.Right, 75, CardLocation.BoardHqRight, 1);
            target.Keywords.Clear(); target.Defense = 2; target.MaxDefense = 6;
            if (mode == 1) target.Defense = 0;
            if (mode == 2) target.Location = CardLocation.HandRight;
            var ctx = new EffectContext { Engine = engine, State = state, Self = source, Target = target, Controller = Side.Left };
            object? raw = engine.Api.InvokeByName("DamageCard", null,
                new object?[] { mode == 3 ? null : target, mode == 1 ? 0 : 3, 74, false, false, false, null }, ctx, out bool handled);
            bool expected = mode is 0 or 1;
            if (!handled || raw is not bool destroyed || destroyed != expected) return $"targetDestroyed错误：模式{mode} 返回{raw}";
            if (mode == 0 && target.Location != CardLocation.Discard) return "致死伤害未完成死亡结算";
            if (mode == 1 && (target.Location != CardLocation.BoardHqRight || target.Defense != 0)) return "零伤害布尔输出不能伪造一次摧毁";
            if (mode == 3 && (target.Defense != 2 || source.Defense <= 0 || state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionDamageCard"))) return "显式空目标不得回退到上下文目标/自身";
        }
        return null;
    }

    private static string? EffectDamageMovedDuringReceive(CardDatabase db)
    {
        using var receive = InstallSelectionDrawProbe(PlainUnit, "OnReceiveDamage");
        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(PlainUnit, Side.Left, 76, CardLocation.Discard, 0);
        var target = state.CreateWithId(PlainUnit, Side.Right, 77, CardLocation.BoardHqRight, 1);
        target.Keywords.Clear(); target.Defense = target.MaxDefense = 7;
        state.CreateWithId("card_unit_51e_regiment", Side.Right, 78, CardLocation.DeckRight, 0);
        bool called = false;
        using var reentry = InstallDrawReentryProbe(state, () => {
            called = true;
            state.Move(target, CardLocation.Discard, 0, notifyMoved: false);
        });
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };
        var raw = engine.Api.InvokeByName("DamageCard", null,
            new object?[] { target, 2, source.CardId, false, false, false, null }, ctx, out bool handled);
        var damage = state.ActionLog.SelectMany(a => a.SubActions).SingleOrDefault(a => a.Name == "ZActionDamageCard");
        if (!handled || raw is not false || !called || target.Defense != 5 || target.Location != CardLocation.Discard
            || damage?.Values.Single(v => v.Name == "location").Text != "Board_HQRight")
            return "入口通过后不能按回调后的区域重新拒绝；布尔输出也不能按IsAlive推导";
        return null;
    }

    private static string? PincerRedirectImmunityIntegration(CardDatabase db)
    {
        foreach (bool immune in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(PlainUnit, Side.Left, 84, CardLocation.Discard, 0);
            var scottish = state.CreateWithId("card_unit_kings_own_scottish", Side.Right, 85, CardLocation.BoardHqRight, 1);
            var partner = state.CreateWithId(PlainUnit, Side.Right, 86, CardLocation.BoardHqRight, 2);
            scottish.Keywords.Clear(); partner.Keywords.Clear();
            scottish.Defense = scottish.MaxDefense = partner.Defense = partner.MaxDefense = 8;
            if (immune) scottish.Keywords.Add(Keyword.Immune);
            engine.Api.JsonSetInt(scottish, "affected_card", partner.CardId);
            engine.Api.CustomAbilityAdd(partner, "trigger", scottish);
            state.ActionLog.Clear();
            engine.Api.DealDamage(partner, 3, source);
            if (partner.Defense != 8 || scottish.Defense != (immune ? 8 : 5))
                return $"真实Pincer重定向错误：immune={immune} 本体{scottish.Defense} 伙伴{partner.Defense}";
            var damages = state.ActionLog.SelectMany(a => a.SubActions).Where(a => a.Name == "ZActionDamageCard").ToArray();
            if (damages.Length != 2 || damages[0].Values.Single(v => v.Name == "cardID").Value != scottish.CardId
                || damages[0].Values.Single(v => v.Name == "damage").Value != (immune ? 0 : 3)
                || damages[1].Values.Single(v => v.Name == "damage").Value != 0)
                return "真实Pincer递归通知必须先重定向目标、再原目标零伤害";
        }
        return null;
    }
}
