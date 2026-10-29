using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static (MatchEngine Engine,GameState State,CardInstance Dealer,CardInstance Receiver) PassiveMitigationBoard(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);
        var dealer=state.CreateWithId(EffectDamageProbeName,Side.Left,70,CardLocation.BoardHqLeft,1);
        var receiver=state.CreateWithId(PlainUnit,Side.Right,71,CardLocation.BoardHqRight,2);
        dealer.Definition=new CardDefinition {Name=dealer.Name,Type="infantry"};
        receiver.Definition=new CardDefinition {Name=receiver.Name,Type="infantry"};
        dealer.Keywords.Clear();receiver.Keywords.Clear();dealer.Attack=6;receiver.Attack=0;
        dealer.BaseHeavyArmor=dealer.TemporaryHeavyArmor=receiver.BaseHeavyArmor=receiver.TemporaryHeavyArmor=0;
        state.CreateWithId("card_unit_51e_regiment",Side.Right,73,CardLocation.DeckRight,0);
        return (engine,state,dealer,receiver);
    }

    private static int CalculatePassiveMitigation(MatchEngine engine,CardInstance dealer,CardInstance receiver,bool isAttacker)
        => (int)typeof(CardApi).GetMethod("CalculateCombatDamage",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .Invoke(engine.Api,new object[] {dealer,receiver,isAttacker})!;

    private static string? PassiveMitigationReference(CardDatabase db)
    {
        using var passive=InstallAmbushPassiveProbe();
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","passive-mitigation-reference-20261029.json")));
        int rows=0;
        foreach(var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var (engine,state,dealer,receiver)=PassiveMitigationBoard(db);
            dealer.Attack=row.GetProperty("damage").GetInt32();receiver.BaseHeavyArmor=row.GetProperty("armor").GetInt32();
            if(row.GetProperty("suppressed").GetBoolean())receiver.Keywords.Add(Keyword.Suppressed);
            receiver.BlueprintVariables["_ambushAmount"]=row.GetProperty("passive").GetInt32();
            int calls=0;
            using var reentry=InstallDrawReentryProbe(state,()=> {calls++;receiver.BaseHeavyArmor=row.GetProperty("afterArmor").GetInt32();});
            int result=CalculatePassiveMitigation(engine,dealer,receiver,row.GetProperty("isAttacker").GetBoolean());
            var expected=row.GetProperty("expected");bool called=expected.GetProperty("called").GetBoolean();
            if(calls!=(called?1:0) || result!=expected.GetProperty("damage").GetInt32()
                || receiver.HeavyArmor!=expected.GetProperty("finalArmor").GetInt32()
                || called && Convert.ToInt32(receiver.BlueprintVariables.GetValueOrDefault("_ambushIncoming"))!=dealer.Attack)
                return $"受伤方被动减伤表第{rows}行错误：calls={calls}, damage={result}, armor={receiver.HeavyArmor}";
            rows++;
        }
        return rows==288 ? null : $"受伤方被动减伤表行数错误：{rows}";
    }

    private static string? PassiveMitigationGates(CardDatabase db)
    {
        using var passive=InstallAmbushPassiveProbe();
        foreach(int mode in Enumerable.Range(0,7))
        {
            var (engine,state,dealer,receiver)=PassiveMitigationBoard(db);
            receiver.BlueprintVariables["_ambushAmount"]=100;
            if(mode==0)receiver.Keywords.Add(Keyword.Immune);
            if(mode==1)dealer.Attack=0;
            if(mode==2)receiver.Definition=new CardDefinition {Name=receiver.Name,Type="artillery"};
            if(mode==3)dealer.Definition=new CardDefinition {Name=dealer.Name,Type="bomber"};
            if(mode==4)receiver.Definition=new CardDefinition {Name=receiver.Name,Type="bomber"};
            if(mode==5)receiver.Keywords.Add(Keyword.Shock);
            if(mode==6) {dealer.Attack=1;receiver.BaseHeavyArmor=3;}
            int calls=0;using var reentry=InstallDrawReentryProbe(state,()=>calls++);
            int result=CalculatePassiveMitigation(engine,dealer,receiver,mode is 0 or 1 or 6);
            if(result!=0 || calls!=(mode==6?1:0))
                return $"免疫/零伤害/反击限制应先于被动调用，重甲减到零则仍调用：mode={mode}, calls={calls}, damage={result}";
        }
        return null;
    }

    private static string? Armored48PassiveMitigationIntegration(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);
        var dealer=state.CreateWithId(EffectDamageProbeName,Side.Left,70,CardLocation.BoardHqLeft,1);
        var receiver=state.CreateWithId("card_unit_48th_armored_infantry",Side.Right,71,CardLocation.BoardHqRight,2);
        dealer.Keywords.Clear();receiver.Keywords.Clear();dealer.Attack=6;receiver.Attack=0;
        receiver.BaseHeavyArmor=1;receiver.TemporaryHeavyArmor=0;
        state.CreateWithId(PlainUnit,Side.Right,72,CardLocation.BoardHqRight,1);
        state.CreateWithId(PlainUnit,Side.Right,74,CardLocation.BoardHqRight,3);
        engine.Api.TriggerTrace=new List<string>();
        int result=CalculatePassiveMitigation(engine,dealer,receiver,true);
        var ctx=new EffectContext {Engine=engine,State=state,Self=receiver,Controller=Side.Right};
        var adjacent=engine.Api.InvokeByName("GetDefenseBuffFromAdjacentUnits",null,new object?[] {null},ctx,out bool handled);
        return result==5 && handled && Equals(adjacent,4)
            && engine.Api.TriggerTrace.Count(x=>x.StartsWith("GetPassiveDefenseBuff(out amount)") && x.Contains(receiver.Name+"#"+receiver.CardId))==1
            ? null : $"第48装甲步兵真实被动IR必须被调用，但普通预计算不再扣其返回4：damage={result}, adjacent={adjacent}";
    }
}
