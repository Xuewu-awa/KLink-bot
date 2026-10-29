using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static IDisposable InstallAfterShockOutputProbe()
    {
        const string hook="OnAfterAttack";
        var cleanup=InstallSelectionDrawProbe(EffectDamageProbeName,hook);
        var locals=(IDictionary<string,IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(EffectDamageProbeName)!.Locals!;
        locals[hook]=new[] {
            new KismetStep(0,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),
                new KismetExpr {Var="afterShockAttack",Scope="local"},null,-1,"_539AfterShock",null) {DestinationScope="instance"},
            new KismetStep(1,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),
                new KismetExpr {Var="K2Node_Event_WasShockAttack",Scope="local"},null,-1,"_539CanonicalShock",null) {DestinationScope="instance"},
            new KismetStep(2,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),
                new KismetExpr {Var="K2Node_Event_defenderCard",Scope="local"},null,-1,"_539CanonicalDefender",null) {DestinationScope="instance"},
            new KismetStep(3,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null),
        };
        return cleanup;
    }

    private static (MatchEngine Engine,GameState State,CardInstance Attacker,CardInstance Defender) ShockOutputBoard(CardDatabase db,Side side,string name=EffectDamageProbeName)
    {
        var (engine,state)=EmptyBoard(db);state.ActiveSide=side;state.Turn=4;state.SetKredits(side,20);
        var attacker=state.CreateWithId(name,side,70,CardLocation.BoardFrontline,1);
        var defender=state.CreateWithId(PlainUnit,side.Opposite(),71,side.Opposite().HqOf(),1);
        defender.Definition=new CardDefinition {Name=defender.Name,Type="infantry"};
        attacker.Keywords.Clear();defender.Keywords.Clear();attacker.Attack=2;defender.Attack=1;
        attacker.Defense=attacker.MaxDefense=defender.Defense=defender.MaxDefense=20;
        attacker.EnteredPlayOnTurn=-1;attacker.OperationCost=5;
        return (engine,state,attacker,defender);
    }

    private static string? PassiveDefaultZeroContract(CardDatabase db)
    {
        if(KismetLibrary.Default!.FindProgram(PlainUnit,"GetPassiveDefenseBuff") is not null
            || KismetLibrary.Default.FindProgram(EffectDamageProbeName,"GetPassiveDefenseBuff") is not null)
            return "默认回退布景不应存在蓝图被动覆盖";
        foreach(bool ambush in new[] {false,true})
        {
            var (engine,_,dealer,receiver)=AmbushNumericBoard(db);
            dealer.Defense=20;receiver.Attack=3;receiver.BaseHeavyArmor=1;
            if(!ambush)receiver.Keywords.Remove(Keyword.Ambush);
            if(CalculateAmbush(engine,dealer,receiver)!=6 || dealer.Defense!=20 || receiver.HeavyArmor!=1)
                return "无本地覆盖时的零被动回退应与已取证的基类零输出一致";
        }
        return null;
    }

    private static string? AfterShockOutputPayload(CardDatabase db)
    {
        using var probe=InstallAfterShockOutputProbe();
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(bool immune in new[] {false,true})
        foreach(bool shock in new[] {false,true})
        foreach(bool keepShock in new[] {false,true})
        {
            var (engine,_,attacker,defender)=ShockOutputBoard(db,side);
            if(immune)attacker.Keywords.Add(Keyword.Immune);
            if(shock)attacker.Keywords.Add(Keyword.Shock);
            if(keepShock)attacker.ReceivedAbilitiesFromCards["CantLoseShock"]=new List<int>{9004};
            if(!engine.Attack(attacker,defender,out string reason))return "冲击输出布景不能攻击："+reason;
            if(!Equals(attacker.BlueprintVariables.GetValueOrDefault("_539AfterShock"),shock&&!immune)
                || !Equals(attacker.BlueprintVariables.GetValueOrDefault("_539CanonicalShock"),shock&&!immune)
                || !ReferenceEquals(attacker.BlueprintVariables.GetValueOrDefault("_539CanonicalDefender"),defender))
                return $"攻击后冲击载荷应取反击预计算输出：{side}/immune={immune}/shock={shock}/keep={keepShock}";
            var notice=engine.State.ActionLog.SelectMany(a=>a.SubActions).Single(a=>a.Name=="ZActionAttackCard");
            if(notice.Values.Single(v=>v.Name=="wasShockAttack").Value!=(shock&&!keepShock?1:0))
                return "攻击通知的关键词移除标志不得与攻击后预计算输出合并";
        }
        return null;
    }

    private static string? AfterShockOutputTiming(CardDatabase db)
    {
        using var after=InstallAfterShockOutputProbe();
        const string modifier="OnCardDealDamage_ModifyDamageDealt";
        using var modify=InstallSelectionDrawProbe(EffectDamageProbeName,modifier);
        var locals=(IDictionary<string,IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(EffectDamageProbeName)!.Locals!;
        var draw=locals[modifier][0];
        locals[modifier]=new[] {draw,new KismetStep(1,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),
            new KismetExpr {Var="damage",Scope="local"},null,-1,"newDamage",null) {DestinationScope="local"},
            new KismetStep(2,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null)};
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(bool before in new[] {false,true})
        {
            var (engine,state,attacker,defender)=ShockOutputBoard(db,side);
            if(before)attacker.Keywords.Add(Keyword.Shock);
            state.CreateWithId("card_unit_51e_regiment",side,73,side.DeckOf(),0);
            bool called=false;using var reentry=InstallDrawReentryProbe(state,()=> {
                if(called)return;called=true;
                if(before)attacker.Keywords.Remove(Keyword.Shock);else attacker.Keywords.Add(Keyword.Shock);
            });
            if(!engine.Attack(attacker,defender,out string reason))return "冲击时序布景不能攻击："+reason;
            if(!called || !Equals(attacker.BlueprintVariables.GetValueOrDefault("_539AfterShock"),!before))
                return "攻击后冲击不能使用伤害修正前的关键词快照";
        }
        return null;
    }

    private static string? Airborne511AfterShockIntegration(CardDatabase db)
    {
        foreach(bool immune in new[] {false,true})
        {
            var (engine,_,attacker,defender)=ShockOutputBoard(db,Side.Left,"card_unit_511th_airborne");
            attacker.Keywords.Add(Keyword.Shock);if(immune)attacker.Keywords.Add(Keyword.Immune);
            if(!engine.Attack(attacker,defender,out string reason))return "第511空降团布景不能攻击："+reason;
            if(attacker.OperationCost!=(immune?5:2))
                return $"第511空降团真实IR仅在预计算输出冲击为真时降行动费3：immune={immune}, cost={attacker.OperationCost}";
        }
        return null;
    }
}
