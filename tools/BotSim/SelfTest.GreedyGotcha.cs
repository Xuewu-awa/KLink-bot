using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? GreedyGotchaTurnProgress(CardDatabase db)
    {
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(string name in new[] {"card_event_hms_spectre","card_event_air_defense","card_event_ultra"})
        foreach(bool active in new[] {false,true})
        {
            var (engine,state)=EmptyBoard(db);state.ActiveSide=side;state.Turn=4;state.SetKredits(side,12);
            var card=state.CreateWithId(name,side,70,side.HandOf(),0);
            card.GotchaActivated=active?1:0;card.CustomJson.Set("kreditsPayed","2",CardJsonType.Number);
            if(!engine.Api.IsGotcha(card))return "反制测试布景错误："+name;
            new GreedyBot(maxActionsPerTurn:3).PlayTurn(engine,side);
            int plays=state.ActionLog.Count(a=>a.ActionType=="XActionPlayCardFromHand");
            if(state.ActiveSide==side && !state.IsFinished || plays!=(active?0:1))
                return $"{name} active={active} 未结束或重复切换：plays={plays},side={state.ActiveSide}";
        }
        return null;
    }
}
