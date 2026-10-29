using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? SixNativeEmptyPlayedEvents(CardDatabase db)
    {
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(string name in new[] {"card_unit_light_infantry","card_unit_flak_37_spg","card_unit_sissi","card_unit_panzer_iii_j","card_unit_104th_infantry_regiment","card_unit_swordfish"})
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
