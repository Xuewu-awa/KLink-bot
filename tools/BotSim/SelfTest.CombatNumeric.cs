using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? CombatBoundedDefenseReference(CardDatabase db)
    {
        using var receive = InstallSelectionDrawProbe(PlainUnit, "OnReceiveDamage");
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "combat-defense-reference-20261029.json")));
        var pair = typeof(CardApi).GetMethod("DealCombatDamagePair",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        int rows = 0;
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            int damage = row.GetProperty("damage").GetInt32();var expected = row.GetProperty("expected");
            var (engine, state) = EmptyBoard(db);
            var attacker = state.CreateWithId(EffectDamageProbeName, Side.Left, 70, CardLocation.BoardHqLeft, 1);
            attacker.Definition = new CardDefinition { Name = attacker.Name, Type = "infantry" };
            attacker.Keywords.Clear();attacker.Attack = damage;attacker.Defense = row.GetProperty("attackerRaw").GetInt32();
            if (row.GetProperty("excess").GetBoolean()) attacker.CustomAbility = "excess";
            var defender = state.CreateWithId(PlainUnit, Side.Right, 71, CardLocation.BoardHqRight, 1);
            defender.Definition = new CardDefinition { Name = defender.Name, Type = "infantry" };
            defender.Keywords.Clear();defender.Attack = 0;defender.Defense = row.GetProperty("defenderBefore").GetInt32();
            state.Hq(Side.Right).Defense = state.Hq(Side.Right).MaxDefense = 99;
            state.CreateWithId("card_unit_51e_regiment", Side.Right, 73, CardLocation.DeckRight, 0);
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => {called = true;defender.Defense = row.GetProperty("defenderAfter").GetInt32();});
            pair.Invoke(engine.Api, new object[] {attacker,defender});
            var fields = state.ActionLog.SelectMany(a => a.SubActions).Single(a => a.Name == "ZActionAttackCard").Values.ToDictionary(v => v.Name);
            int E(string name) => expected.GetProperty(name).GetInt32();
            bool EB(string name) => expected.GetProperty(name).GetBoolean();
            if (attacker.Defense != E("attackerStored") || defender.Defense != E("defenderStored")
                || fields["attackerDefense"].Value != E("attackerOldDefense") || fields["defenderDefense"].Value != E("defenderOldDefense")
                || fields["damageDefender"].Value != E("toDefender") || fields["damageAttacker"].Value != 0
                || fields["attackerDestroyed"].Value != (EB("attackerDestroyed") ? 1 : 0)
                || fields["defenderDestroyed"].Value != (EB("defenderDestroyed") ? 1 : 0)
                || called != EB("receiveCalled") || state.Hq(Side.Right).Defense != 99 - E("spill"))
                return $"战斗数值表第{rows}行错误：raw={attacker.Defense}/{defender.Defense}, old={fields["attackerDefense"].Value}/{fields["defenderDefense"].Value}, damage={fields["damageDefender"].Value}";
            rows++;
        }
        return rows == 216 ? null : $"战斗数值表行数错误：{rows}";
    }

    private static string? CombatPrecomputedSignedWrites(CardDatabase db)
    {
        var apply = typeof(MatchEngine).GetMethod("ApplyDamage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (int raw in new[] { -4, 0, 3, 105 })
        foreach (int damage in new[] { -2, 0, 1, 7 })
        {
            var (engine, state) = EmptyBoard(db);
            var target = state.CreateWithId(PlainUnit, Side.Left, 70, CardLocation.Discard, 0);target.Defense = raw;
            object? applied = apply.Invoke(engine,new object?[] {target,damage,null,true,false,true});
            if (target.Defense != Math.Clamp(raw,0,99)-damage || !Equals(applied,damage)
                || target.Location != CardLocation.Discard || state.ActionLog.Count != 0)
                return $"预计算战斗写入不应跳过零/负值减法：raw={raw}, damage={damage}, stored={target.Defense}";
        }
        return null;
    }

    private static string? HellcatBoundedCombatIntegration(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);
        var attacker=state.CreateWithId("card_unit_m18_hellcat",Side.Left,70,CardLocation.BoardHqLeft,1);
        var defender=state.CreateWithId("card_unit_stug_iii_g",Side.Right,71,CardLocation.BoardHqRight,1);
        attacker.Keywords.Clear();defender.Keywords.Clear();attacker.Attack=7;defender.Attack=0;
        attacker.Defense=defender.Defense=105;
        typeof(CardApi).GetMethod("DealCombatDamagePair",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .Invoke(engine.Api,new object[] {attacker,defender});
        var notice=state.ActionLog.SelectMany(a=>a.SubActions).Single(a=>a.Name=="ZActionAttackCard");
        return attacker.Defense==99 && defender.Defense==85 && notice.Values.Single(v=>v.Name=="damageDefender").Value==14
            ? null : $"M18真实伤害修正IR应先对坦克翻倍，再从有界防御扣除：{attacker.Defense}/{defender.Defense}";
    }
}
