using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? EmptyTextReturnsText(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);
        var steps=new[] {
            new KismetStep(0,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Math="GetEmptyText"},null,-1,"result",null) {DestinationScope="local"},
            new KismetStep(1,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null)};
        var value=engine.Api.Vm.RunLocalProgram(new KismetProgram(steps,0),new EffectContext {Engine=engine,State=state,Controller=Side.Left},new Dictionary<string,object?>(),"result");
        return value is string text && text.Length==0 && engine.UnimplementedCalls.Count==0
            ? null : "GetEmptyText必须返回空字符串文本，不能用null或数字0替代";
    }

    private static string? EightNativeEmptyPlayedEvents(CardDatabase db)
    {
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(string name in new[] {"card_unit_panzer_ii_a","card_unit_15th_cavalry_regiment","card_unit_554th_rifles","card_unit_shiden","card_unit_routed_troops","card_unit_tiger_i_e","card_unit_sbd3_dauntless","card_unit_tiger_i"})
        {
            var (engine,state)=EmptyBoard(db);state.Turn=4;state.ActiveSide=side;state.SetKredits(side,20);
            var card=state.CreateWithId(name,side,70,side.HandOf(),0);
            if(KismetLibrary.Default!.FindProgram(name,"OnPlayedFromHand") is null)return "未加载精确空事件契约："+name;
            if(KismetLibrary.Default.FindProgram(name+"_unverified_vet","OnPlayedFromHand") is not null)return "不允许扩展到未核对的变体";
            if(!engine.PlayCard(card) || !card.Location.IsBoard() || engine.UnimplementedCalls.ContainsKey("<card:"+name+">"))return "已核对单位打出仍被视为缺失："+name;
        }
        return null;
    }
}
