using System.Security.Cryptography;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? LiteralTextIdentity(CardDatabase db)
    {
        foreach(string text in new[] {"", "null", "Hello", "中文 %{value}", "a\nb", "نص"})
        {
            var (engine,state)=EmptyBoard(db);
            var steps=new[] {
                new KismetStep(0,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Math="MakeLiteralText",Args=new[] {new KismetExpr {Var="input",Scope="local"}}},null,-1,"result",null) {DestinationScope="local"},
                new KismetStep(1,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null)};
            var value=engine.Api.Vm.RunLocalProgram(new KismetProgram(steps,0),new EffectContext {Engine=engine,State=state,Controller=Side.Left},new Dictionary<string,object?> { ["input"]=text },"result");
            if(value is not string actual || actual!=text || engine.UnimplementedCalls.Count!=0)return "文本字面量未原样返回："+text;
        }
        return null;
    }

    private static string? NativeEmptyPlayedEvents(CardDatabase db)
    {
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(string name in new[] {"card_unit_red_devils","card_unit_ki_61_hien"})
        {
            var (engine,state)=EmptyBoard(db);state.Turn=4;state.ActiveSide=side;state.SetKredits(side,12);
            var card=state.CreateWithId(name,side,70,side.HandOf(),0);
            if(KismetLibrary.Default!.FindProgram(name,"OnPlayedFromHand") is null)return "缺少已证明的空事件程序："+name;
            if(KismetLibrary.Default.FindProgram(name+"_unverified_vet","OnPlayedFromHand") is not null)return "空事件契约不得借变体回退扩展";
            if(!engine.PlayCard(card) || !card.Location.IsBoard() || engine.UnimplementedCalls.ContainsKey("<card:"+name+">"))return "原生空手牌事件被误报为缺失："+name;
        }
        return null;
    }

    private static string? NativeEmptyContractIntegrity(CardDatabase db)
    {
        string directory=Path.Combine(Path.GetTempPath(),"klink-empty-contract-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string ir=Path.Combine(directory,"card-ir.json"),cards=Path.Combine(directory,"cards.json"),contract=Path.Combine(directory,"card-ir-native-defaults.json");
        try
        {
            File.WriteAllText(ir,"{}");File.WriteAllText(cards,"{}");
            string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
            File.WriteAllText(contract,JsonSerializer.Serialize(new {schema_version=1,base_ir_sha256=Hash(ir),cards_sha256=Hash(cards),source_client_sha256=new string('0',64),empty_events=new[] {new {card="test_card",@event="OnPlayedFromHand",kind="non_native_empty_script",script_bytes=0,function_flags=0u}}}));
            if(KismetLibrary.Load(ir).FindProgram("test_card","OnPlayedFromHand") is null)return "契约未加载";
            File.WriteAllText(ir,"{ }");bool rejected=false;
            try{KismetLibrary.Load(ir);}catch(InvalidDataException){rejected=true;}
            if(!rejected)return "不匹配的IR哈希必须拒绝";
            File.WriteAllText(ir,"{}");File.WriteAllText(cards,"{ }");rejected=false;
            try{KismetLibrary.Load(ir);}catch(InvalidDataException){rejected=true;}
            if(!rejected)return "不匹配的卡表哈希必须拒绝";
        }
        finally {File.Delete(ir);File.Delete(cards);File.Delete(contract);Directory.Delete(directory);}
        return null;
    }
}
