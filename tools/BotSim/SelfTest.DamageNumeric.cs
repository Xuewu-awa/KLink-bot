using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? DamageNumericClampReference(CardDatabase db)
    {
        using var probes = InstallSelectionDrawProbe(PlainUnit, "OnReceiveDamage");
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "damage-defense-clamp-reference-20261029.json")));
        var apply = typeof(CardApi).GetMethod("ApplyCalculatedDamage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        int checkedRows = 0;
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            int before = row.GetProperty("before").GetInt32(), after = row.GetProperty("after").GetInt32();
            int damage = row.GetProperty("damage").GetInt32();bool excess = row.GetProperty("excess").GetBoolean();
            var expected = row.GetProperty("expected");
            var (engine, state) = EmptyBoard(db);
            var source = state.CreateWithId(PlainUnit, Side.Left, 60, CardLocation.Discard, 0);
            source.Definition = new CardDefinition { Name = PlainUnit, Type = "order" };
            source.Keywords.Clear();if (excess) source.CustomAbility = "excess";
            var target = state.CreateWithId(PlainUnit, Side.Right, 61, CardLocation.BoardHqRight, 1);
            target.Definition = new CardDefinition { Name = PlainUnit, Type = "infantry" };
            target.Keywords.Clear();target.Defense = before;target.MaxDefense = 150;
            state.Hq(Side.Right).Defense = state.Hq(Side.Right).MaxDefense = 99;
            state.CreateWithId("card_unit_51e_regiment", Side.Right, 62, CardLocation.DeckRight, 0);
            bool called = false;
            using var reentry = InstallDrawReentryProbe(state, () => {called = true;target.Defense = after;});
            // Isolate the actual shared apply arithmetic and notification. Death
            // removal is deliberately deferred; real lifecycle tests run separately.
            apply.Invoke(engine.Api, new object?[] {target,damage,source,false,false,false,null,true,null,false});
            var notes = state.ActionLog.SelectMany(a => a.SubActions).Where(a => a.Name == "ZActionDamageCard"
                && a.Values.Any(v => v.Name == "cardID" && v.Value == target.CardId)).ToArray();
            if (notes.Length != 1) return $"数值表第{checkedRows}行通知次数{notes.Length}";
            var fields = notes[0].Values.ToDictionary(v => v.Name);
            if (target.Defense != expected.GetProperty("storedDefense").GetInt32()
                || fields["oldDefense"].Value != expected.GetProperty("notificationOldDefense").GetInt32()
                || fields["damage"].Value != expected.GetProperty("notificationDamage").GetInt32()
                || fields["destroyed"].Value != (expected.GetProperty("destroyed").GetBoolean() ? 1 : 0)
                || called != expected.GetProperty("receiveCalled").GetBoolean())
                return $"数值表第{checkedRows}行错误：before={before} after={after} damage={damage} excess={excess}，raw={target.Defense} old={fields["oldDefense"].Value} dealt={fields["damage"].Value}";
            checkedRows++;
        }
        return checkedRows==300 ? null : $"数值参考表应有300行，实际{checkedRows}";
    }

    private static string? BloodySickleBoundedDefenseIntegration(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);
        var spell=state.CreateWithId("card_event_bloody_sickle",Side.Left,90,CardLocation.Discard,0);
        var target=state.CreateWithId(PlainUnit,Side.Right,91,CardLocation.BoardHqRight,1);
        target.Keywords.Clear();target.Defense=target.MaxDefense=105;
        state.CreateWithId("card_unit_51e_regiment",Side.Left,92,CardLocation.DeckLeft,0);
        engine.Api.RunCardEffect(spell,target);
        var damage=state.ActionLog.SelectMany(a=>a.SubActions).FirstOrDefault(a=>a.Name=="ZActionDamageCard"
            && a.Values.Any(v=>v.Name=="cardID" && v.Value==target.CardId));
        if(target.Defense!=98 || state.Hq(Side.Left).Defense!=19 || state.Hand(Side.Left).Count!=1
            || damage?.Values.Single(v=>v.Name=="oldDefense").Value!=99)
            return $"血腥镰刀有界读取/双向伤害/抽牌错误：目标{target.Defense}，HQ{state.Hq(Side.Left).Defense}，手牌{state.Hand(Side.Left).Count}";
        return null;
    }
}
