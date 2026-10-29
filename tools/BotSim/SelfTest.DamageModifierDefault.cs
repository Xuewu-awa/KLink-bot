using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? DamageModifierDefaultBeforeObservers(CardDatabase db)
    {
        const string hook="OnOtherCardDealDamageAddDamageAfterCalc";
        using var cleanup=InstallSelectionDrawProbe(EffectDamageProbeName,hook);
        var locals=(IDictionary<string,IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(EffectDamageProbeName)!.Locals!;
        locals[hook]=new[] {
            new KismetStep(0,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Var="damageAmount",Scope="local"},null,-1,"_543Seen",null) {DestinationScope="instance"},
            new KismetStep(1,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Int=10},null,-1,"damageToAdd",null) {DestinationScope="local"},
            new KismetStep(2,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Bool=false},null,-1,"stopAdding",null) {DestinationScope="local"},
            new KismetStep(3,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null)
        };
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","damage-modifier-default-order-20261030.json")));
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var (engine,state)=EmptyBoard(db);
            var dealer=state.CreateWithId(PlainUnit,side,70,side.HqOf(),1);
            var receiver=state.CreateWithId(PlainUnit,side.Opposite(),71,side.Opposite().HqOf(),1);
            var observer=state.CreateWithId(EffectDamageProbeName,side,72,side.HqOf(),2);
            observer.Definition=new CardDefinition {Name=observer.Name,Type="infantry",UsedTriggers=new[] {hook}};
            dealer.Keywords.Clear();receiver.Keywords.Clear();observer.Keywords.Clear();
            if(KismetLibrary.Default.FindLocalProgram(dealer.Name,"OnDealDamageAddDamageAfterCalc") is not null)return "默认来源不应有本地覆盖";
            if(row.GetProperty("suppressed").GetBoolean())dealer.Keywords.Add(Keyword.Suppressed);
            if(row.GetProperty("immune").GetBoolean())receiver.Keywords.Add(Keyword.Immune);
            int result=engine.Api.ExecuteOnDealDamageAddDamageAfterCalc(dealer,receiver,row.GetProperty("damage").GetInt32(),false,false,false);
            bool called=observer.BlueprintVariables.TryGetValue("_543Seen",out var seen);
            var expectedSeen=row.GetProperty("seen");
            if(result!=row.GetProperty("expected").GetInt32() || called!=(expectedSeen.ValueKind!=JsonValueKind.Null)
                || called && Convert.ToInt32(seen)!=expectedSeen.GetInt32())return $"来源压制{row.GetProperty("suppressed")} 输入{row.GetProperty("damage")}：观察{seen}，输出{result}";
        }
        return null;
    }
}
