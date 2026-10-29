using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static IDisposable InstallAmbushPassiveProbe()
    {
        const string hook = "GetPassiveDefenseBuff";
        var cleanup = InstallSelectionDrawProbe(PlainUnit, hook);
        var locals = (IDictionary<string, IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(PlainUnit)!.Locals!;
        var draw = locals[hook][0];
        locals[hook] = new[] {
            draw,
            new KismetStep(1,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),
                new KismetExpr {Var="incomingDamage",Scope="local"},null,-1,"_ambushIncoming",null) {DestinationScope="instance"},
            new KismetStep(2,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),
                new KismetExpr {Var="_ambushAmount",Scope="instance"},null,-1,"amount",null) {DestinationScope="local"},
            new KismetStep(3,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null),
        };
        return cleanup;
    }

    private static (MatchEngine Engine, GameState State, CardInstance Dealer, CardInstance Receiver) AmbushNumericBoard(CardDatabase db, string dealerName=PlainUnit)
    {
        var (engine,state)=EmptyBoard(db);
        var dealer=state.CreateWithId(dealerName,Side.Left,70,CardLocation.BoardHqLeft,1);
        var receiver=state.CreateWithId(EffectDamageProbeName,Side.Right,71,CardLocation.BoardHqRight,1);
        dealer.Keywords.Clear();receiver.Keywords.Clear();receiver.Keywords.Add(Keyword.Ambush);
        dealer.Attack=7;receiver.Attack=5;receiver.HasBeenAttackedThisTurn=false;
        dealer.BaseHeavyArmor=dealer.TemporaryHeavyArmor=receiver.BaseHeavyArmor=receiver.TemporaryHeavyArmor=0;
        state.CreateWithId("card_unit_51e_regiment",Side.Left,73,CardLocation.DeckLeft,0);
        return (engine,state,dealer,receiver);
    }

    private static int CalculateAmbush(MatchEngine engine,CardInstance dealer,CardInstance receiver)
        => (int)typeof(CardApi).GetMethod("CalculateCombatDamage",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .Invoke(engine.Api,new object[] {dealer,receiver,true})!;

    private static string? AmbushThresholdReference(CardDatabase db)
    {
        using var passive=InstallAmbushPassiveProbe();
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","ambush-defense-reference-20261029.json")));
        int rows=0;
        foreach(var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var (engine,state,dealer,receiver)=AmbushNumericBoard(db);
            dealer.Defense=row.GetProperty("rawDefense").GetInt32();dealer.BaseHeavyArmor=row.GetProperty("armor").GetInt32();
            receiver.Attack=row.GetProperty("reverseDamage").GetInt32();dealer.BlueprintVariables["_ambushAmount"]=row.GetProperty("passive").GetInt32();
            bool called=false;using var reentry=InstallDrawReentryProbe(state,()=>called=true);
            int result=CalculateAmbush(engine,dealer,receiver);
            if(!called || result!=row.GetProperty("expected").GetProperty("damage").GetInt32()
                || Convert.ToInt32(dealer.BlueprintVariables.GetValueOrDefault("_ambushIncoming"))!=receiver.Attack)
                return $"伏击阈值第{rows}行错误：raw={dealer.Defense}, armor={dealer.HeavyArmor}, reverse={receiver.Attack}, damage={result}";
            rows++;
        }
        return rows==216 ? null : $"伏击阈值行数错误：{rows}";
    }

    private static string? AmbushSnapshotBeforePassive(CardDatabase db)
    {
        using var passive=InstallAmbushPassiveProbe();
        foreach(bool grow in new[] {false,true})
        {
            var (engine,state,dealer,receiver)=AmbushNumericBoard(db);
            dealer.Defense=grow?2:20;dealer.BaseHeavyArmor=grow?0:3;dealer.BlueprintVariables["_ambushAmount"]=0;
            bool called=false;
            using var reentry=InstallDrawReentryProbe(state,()=> {called=true;dealer.Defense=grow?20:1;dealer.BaseHeavyArmor=grow?3:0;});
            int result=CalculateAmbush(engine,dealer,receiver);
            if(!called || result!=(grow?0:7))return $"被动防御函数不能改写调用前已读的防御/重甲阈值：grow={grow}, damage={result}";
        }
        return null;
    }

    private static string? Regiment511AmbushIntegration(CardDatabase db)
    {
        var (engine,_,dealer,receiver)=AmbushNumericBoard(db,"card_unit_511th_regiment");
        dealer.Defense=-4; // Controlled stored value; the native getter projects it to0.
        return CalculateAmbush(engine,dealer,receiver)==7 ? null
            : "第511团真实被动防御IR返回6，应在有界基础0之上抵御预测反伤5，保留攻击7";
    }
}
