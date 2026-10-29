using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? DamageModifierOwnOverrides(CardDatabase db)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","damage-modifier-own-overrides-20261030.json")));
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var (engine,state)=EmptyBoard(db);string name=row.GetProperty("card").GetString()!;
            bool same=row.GetProperty("same").GetBoolean(),onboard=row.GetProperty("onboard").GetBoolean();
            var dealer=state.CreateWithId(name,side,70,onboard?side.HqOf():side.HandOf(),1);
            var receiver=same?dealer:state.CreateWithId(PlainUnit,side.Opposite(),71,side.Opposite().HqOf(),1);
            dealer.Keywords.Clear();receiver.Keywords.Clear();
            if(!row.GetProperty("unit").GetBoolean())receiver.Definition=new CardDefinition {Name=receiver.Name,Type="location"};
            receiver.Defense=row.GetProperty("defense").GetInt32();receiver.BaseHeavyArmor=row.GetProperty("armor").GetInt32();receiver.TemporaryHeavyArmor=0;
            if(row.GetProperty("veteran").GetBoolean())dealer.CustomJson.Set("veteran","1",CardJsonType.Number);
            else dealer.CustomJson.Remove("veteran");
            dealer.CustomJson.Set("excess_damage","77",CardJsonType.Number);
            state.CreateWithId(row.GetProperty("navy").GetBoolean()?"card_event_hms_belfast":"card_unit_2nd_parachute",side,72,side.DeckOf(),0);
            int result=engine.Api.ExecuteOnDealDamageAddDamageAfterCalc(dealer,receiver,row.GetProperty("damage").GetInt32(),false,false,row.GetProperty("redirected").GetBoolean());
            if(result!=row.GetProperty("expectedFinal").GetInt32() || engine.Api.JsonGetInt(dealer,"excess_damage")!=row.GetProperty("expectedExcess").GetInt32())
                return $"{name} {side} 参数{row}：输出{result}、excess={engine.Api.JsonGetInt(dealer,"excess_damage")}";
        }
        return null;
    }
}
