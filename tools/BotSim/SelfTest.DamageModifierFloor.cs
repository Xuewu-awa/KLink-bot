using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? DamageModifierFinalFloor(CardDatabase db)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures","damage-modifier-final-floor-20261030.json")));
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var (engine,state)=EmptyBoard(db);
            var dealer=state.CreateWithId(PlainUnit,side,70,side.HqOf(),1);
            var receiver=state.CreateWithId(EffectDamageProbeName,side.Opposite(),71,side.Opposite().HqOf(),1);
            dealer.Keywords.Clear();receiver.Keywords.Clear();
            dealer.Keywords.Add(Keyword.Suppressed);
            if(row.GetProperty("immune").GetBoolean())receiver.Keywords.Add(Keyword.Immune);
            int input=row.GetProperty("damage").GetInt32();
            int result=engine.Api.ExecuteOnDealDamageAddDamageAfterCalc(dealer,receiver,input,false,false,false);
            if(result!=row.GetProperty("expected").GetInt32())
                return $"{side} 抑制来源输入{input}、免疫{row.GetProperty("immune")}：实际{result}";
        }
        return null;
    }
}
