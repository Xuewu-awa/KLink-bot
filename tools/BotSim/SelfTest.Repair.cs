using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static readonly string[] RepairProbeNames = { "card_unit_typhoon_mk_ib", "card_event_honorifics", "card_unit_51e_regiment" };
    private static readonly string[] RepairProbeHooks = { "OnBeforeFullyRepaired", "OnOtherCardFullyRepaired", "OnFullyRepaired" };

    private static IDisposable InstallRepairProbes()
    {
        var added = new List<(IDictionary<string, IReadOnlyList<KismetStep>> Locals, string Hook)>();
        foreach (string name in RepairProbeNames)
        foreach (string hook in RepairProbeHooks)
        {
            if (KismetLibrary.Default?.Find(name)?.Locals is not IDictionary<string, IReadOnlyList<KismetStep>> locals
                || KismetLibrary.Default.FindProgram(name, hook) is not null)
            {
                foreach (var item in added) item.Locals.Remove(item.Hook);
                throw new InvalidOperationException("无法安全安装完整修复探针：" + name + "/" + hook);
            }
            var steps = new List<KismetStep>();
            void Save(string key, KismetExpr value) => steps.Add(new KismetStep(steps.Count, "set", null,
                Array.Empty<KismetExpr>(), Array.Empty<int>(), value, null, -1, key, null) { DestinationScope = "instance" });
            Save("_repairHook", new KismetExpr { Str = hook });
            Save("_repairAmount", new KismetExpr { Var = "amount", Scope = "local" });
            Save("_repairTarget", new KismetExpr { Var = "cardRepaired", Scope = "local" });
            if (hook == "OnBeforeFullyRepaired")
                steps.Add(new KismetStep(steps.Count, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    new KismetExpr { Var = "_repairStop", Scope = "instance" }, null, -1, "stopAction", null) { DestinationScope = "local" });
            steps.Add(new KismetStep(steps.Count, "call", "SpawnCardInDeckBySide", new[] {
                new KismetExpr { Int = (int)Side.Right }, new KismetExpr { Str = "card_unit_routed_troops" },
                new KismetExpr { Int = 30 }, new KismetExpr { Int = 1 }
            }, Array.Empty<int>(), null, null, -1, null, null));
            steps.Add(new KismetStep(steps.Count, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null));
            locals.Add(hook, steps); added.Add((locals, hook));
        }
        return new AuditProbeCleanup(() => { foreach (var item in added) item.Locals.Remove(item.Hook); });
    }

    private static CardInstance RepairProbe(GameState state, int index, int id, params string[] hooks)
    {
        var c = state.CreateWithId(RepairProbeNames[index], Side.Left, id, CardLocation.Discard, 0);
        c.Definition = new CardDefinition { Name = c.Name, Type = "infantry", UsedTriggers = hooks };
        c.Keywords.Clear(); c.Defense = 3; c.MaxDefense = 8;
        return c;
    }

    private static string? FullyRepairAuthorityContract(CardDatabase db)
    {
        using var probes = InstallRepairProbes();
        foreach (int mode in Enumerable.Range(0, 6))
        {
            var (engine, state) = EmptyBoard(db);
            var target = RepairProbe(state, 0, 70);
            var before1 = RepairProbe(state, 1, 71, "OnBeforeFullyRepaired");
            var before2 = RepairProbe(state, 2, 72, "OnBeforeFullyRepaired");
            target.Location = mode == 0 ? CardLocation.Discard : CardLocation.BoardHqLeft;
            if (mode == 1) before1.BlueprintVariables["_repairStop"] = true;
            if (mode == 4) { target.Defense = 101; target.MaxDefense = 105; }
            if (mode == 5) { target.Defense = 0; target.MaxDefense = 8; }
            var calls = new List<int>();
            state.CardCreated = made => {
                if (made.Name != "card_unit_routed_troops") return;
                var who = new[] { before1, before2 }.Single(c => c.BlueprintVariables.Remove("_repairHook"));
                calls.Add(who.CardId);
                if (mode == 2 && who == before1) { target.Defense = 7; target.MaxDefense = 12; }
                if (mode == 3 && who == before1) { target.Defense = 7; target.MaxDefense = -2; }
            };
            var ctx = new EffectContext { Engine = engine, State = state, Self = before1, Controller = Side.Left };
            var raw = engine.Api.InvokeByName("FullyHealCard", null, new object?[] { target, 9007, null }, ctx, out bool handled);
            int expectedAmount = mode is 1 or 5 ? 0 : mode == 4 ? 6 : 5;
            int expectedDefense = mode == 1 ? 3 : mode == 2 ? 12 : mode == 3 ? -2 : mode == 4 ? 105 : mode == 5 ? 0 : 8;
            if (!handled || raw is not int healed || healed != expectedAmount
                || target.Defense != expectedDefense || !calls.SequenceEqual(mode == 5 ? Array.Empty<int>() : mode == 1 ? new[] { 71 } : new[] { 71, 72 }))
                return $"完整修复分支{mode}错误：防御{target.Defense}/{expectedDefense}、返回{raw}、前置[{string.Join(',', calls)}]";
            var notices = state.ActionLog.SelectMany(a => a.SubActions).Where(a => a.Name == "ZActionHealCard").ToArray();
            if (notices.Length != (expectedAmount > 0 ? 1 : 0)) return $"完整修复分支{mode}通知次数错误";
            if (notices.Length == 1)
            {
                var fields = notices[0].Values.ToDictionary(v => v.Name);
                if (fields.GetValueOrDefault("cardID")?.Value != 70 || fields.GetValueOrDefault("amount")?.Value != expectedAmount
                    || fields.GetValueOrDefault("instigatorID")?.Value != 9007) return $"完整修复分支{mode}通知未保留原始差值和治疗来源";
            }
        }
        return null;
    }

    private static string? FullyRepairEventContract(CardDatabase db)
    {
        using var probes = InstallRepairProbes();
        foreach (bool suppressOwn in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var target = RepairProbe(state, 0, 80, "OnOtherCardFullyRepaired");
            target.Location = CardLocation.BoardHqLeft;
            var first = RepairProbe(state, 1, 81, "OnOtherCardFullyRepaired");
            var later = RepairProbe(state, 2, 82, "OnOtherCardFullyRepaired");
            if (suppressOwn) target.Keywords.Add(Keyword.Suppressed);
            var calls = new List<string>(); string? error = null;
            state.CardCreated = made => {
                if (made.Name != "card_unit_routed_troops") return;
                var who = new[] { target, first, later }.Single(c => c.BlueprintVariables.ContainsKey("_repairHook"));
                string hook = (string)who.BlueprintVariables["_repairHook"]!;who.BlueprintVariables.Remove("_repairHook");
                calls.Add($"{who.CardId}:{hook}");
                if (Convert.ToInt32(who.BlueprintVariables["_repairAmount"]) != 5) error = "修复回调未收到保存的原始量";
                if (who != target && who.BlueprintVariables.GetValueOrDefault("_repairTarget") != target) error = "观察者回调目标错误";
                if (!state.ActionLog.SelectMany(a => a.SubActions).Any(a => a.Name == "ZActionHealCard")) error = "通知必须先于修复回调";
                if (who == first)
                {
                    later.Keywords.Add(Keyword.Suppressed); later.Definition = new CardDefinition { Name = later.Name, Type = "infantry" };
                }
            };
            int amount = engine.Api.FullyHealCard(target);
            var expected = new List<string> { "81:OnOtherCardFullyRepaired", "82:OnOtherCardFullyRepaired" };
            if (!suppressOwn) expected.Add("80:OnFullyRepaired");
            if (error is not null || amount != 5 || !calls.SequenceEqual(expected))
                return error ?? $"修复顺序/快照/抑制错误：[{string.Join(',', calls)}]";
        }
        return null;
    }

    private static string? FieldHospitalRepairIntegration(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_event_field_hospital", Side.Left, 88, CardLocation.Discard, 0);
        var injured = state.CreateWithId(PlainUnit, Side.Left, 89, CardLocation.BoardHqLeft, 1);
        var healthy = state.CreateWithId(PlainUnit, Side.Left, 90, CardLocation.BoardHqLeft, 2);
        var enemy = state.CreateWithId(PlainUnit, Side.Right, 91, CardLocation.BoardHqRight, 1);
        injured.MaxDefense = healthy.MaxDefense = enemy.MaxDefense = 6;
        injured.Defense = 4; healthy.Defense = 6; enemy.Defense = 4;
        for (int i = 0; i < 3; i++) state.CreateWithId("card_unit_routed_troops", Side.Left, 100 + i, CardLocation.DeckLeft, i);
        engine.Api.RunCardEffect(source, null);
        var notices = state.ActionLog.SelectMany(a => a.SubActions).Where(a => a.Name == "ZActionHealCard").ToArray();
        if (injured.Defense != 6 || healthy.Defense != 6 || enemy.Defense != 4 || state.Hand(Side.Left).Count != 1)
            return $"野战医院修复/抽牌错误：{injured.Defense}/{healthy.Defense}/{enemy.Defense}、手牌{state.Hand(Side.Left).Count}";
        if (notices.Length != 1 || notices[0].Values.LastOrDefault(v => v.Name == "instigatorID")?.Value != source.CardId)
            return "野战医院真实IR的治疗来源未传至通知";
        return null;
    }
}
