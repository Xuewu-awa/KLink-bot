using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? EntryDispatchReturnClosure(CardDatabase db)
    {
        foreach(var item in new[] {
            (Card:"card_unit_3_7_inch_mountain_howitzer",Event:"OnBeforeAttack",Target:983),
            (Card:"card_event_the_double_sunrise",Event:"OnPlayedFromHand",Target:2094)})
        {
            var (engine,state)=EmptyBoard(db);
            var card=state.CreateWithId(item.Card,Side.Left,70,CardLocation.BoardHqLeft,1);
            var program=KismetLibrary.Default!.FindProgram(item.Card,item.Event);
            if(program is null)return "缺少真实事件程序："+item;
            var trace=new List<string>();engine.Api.Vm.StepTrace=trace;
            engine.Api.Vm.Run(program,new EffectContext {Engine=engine,State=state,Self=card,Controller=Side.Left});
            if(engine.Api.Vm.UnresolvedJumps!=0 || engine.Api.Vm.UnsupportedOps.Count!=0
                || !trace.Any(s=>s.Contains($"i={item.Target} return",StringComparison.Ordinal)))
                return $"{item.Card}.{item.Event}未抵达真实Return {item.Target}，未解析跳转={engine.Api.Vm.UnresolvedJumps}";
        }
        // A genuinely missing target must still reject; do not fix admission by
        // clearing the unsupported counter or treating every missing pop as return.
        {
            var (engine,state)=EmptyBoard(db);var steps=new[] {
                new KismetStep(0,"pushFlow",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,99,null,null),
                new KismetStep(1,"popFlow",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null)};
            engine.Api.Vm.Run(new KismetProgram(steps,0),new EffectContext {Engine=engine,State=state,Controller=Side.Left});
            if(engine.Api.Vm.UnsupportedOps.GetValueOrDefault("<pop-unresolved>")!=1)return "缺失目标的诊断不得被屏蔽";
        }
        return null;
    }
}
