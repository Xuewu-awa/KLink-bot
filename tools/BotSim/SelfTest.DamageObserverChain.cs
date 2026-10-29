using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static IDisposable InstallDamageObserverChainProbes()
    {
        const string hook="OnOtherCardDealDamageAddDamageAfterCalc";
        var library=KismetLibrary.Default!;
        var cache=(Dictionary<string,Dictionary<string,KismetProgram>>)typeof(KismetLibrary).GetField("_localCache",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(library)!;
        var restore=new List<Action>();
        foreach(string name in new[] {EffectDamageProbeName,"card_event_national_fire_service"})
        {
            var locals=(IDictionary<string,IReadOnlyList<KismetStep>>)library.Find(name)!.Locals!;
            locals.TryGetValue(hook,out var old);KismetProgram? prior=null;
            if(cache.TryGetValue(name,out var programs))programs.Remove(hook,out prior);
            locals[hook]=new[] {
                new KismetStep(0,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Var="damageAmount",Scope="local"},null,-1,"_545Seen",null) {DestinationScope="instance"},
                new KismetStep(1,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Var="_545Add",Scope="instance"},null,-1,"damageToAdd",null) {DestinationScope="local"},
                new KismetStep(2,"set",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),new KismetExpr {Var="_545Stop",Scope="instance"},null,-1,"stopAdding",null) {DestinationScope="local"},
                new KismetStep(3,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null)
            };
            restore.Add(()=> {if(old is null)locals.Remove(hook);else locals[hook]=old;if(cache.TryGetValue(name,out var current)){current.Remove(hook);if(prior is not null)current[hook]=prior;}});
        }
        return new AuditProbeCleanup(()=> {foreach(var action in restore)action();});
    }

    private static string? DamageObserverChain(CardDatabase db)
    {
        const string hook="OnOtherCardDealDamageAddDamageAfterCalc";
        using var cleanup=InstallDamageObserverChainProbes();
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","damage-observer-chain-20261030.json")));
        foreach(Side side in new[] {Side.Left,Side.Right})
        foreach(var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var (engine,state)=EmptyBoard(db);var observers=new Dictionary<int,CardInstance>();
            var types=row.GetProperty("types").EnumerateArray().Select(x=>x.GetString()).ToArray();
            // Remap logical labels to ascending IDs to supply each requested query order;
            // this does not assert that native subscription iteration sorts by ID.
            int nextId=20;
            foreach(int id in row.GetProperty("order").EnumerateArray().Select(x=>x.GetInt32()))
            {
                string name=types[id-20]=="deferred"?"card_event_national_fire_service":EffectDamageProbeName;
                var card=state.CreateWithId(name,side,nextId++,side.HqOf(),id-20);
                card.Keywords.Clear();card.Definition=new CardDefinition {Name=name,Type="infantry",UsedTriggers=new[] {hook}};
                card.BlueprintVariables["_545Add"]=new[] {-10,3,8}[id-20];
                card.BlueprintVariables["_545Stop"]=id==row.GetProperty("stopId").GetInt32();observers.Add(id,card);
            }
            // Detached alias tests native ID comparison without inserting a duplicate ID into GameState.
            string kind=row.GetProperty("dealerKind").GetString()!;
            CardInstance dealer=kind=="self"?observers[20]:kind=="alias"?
                new CardInstance {CardId=observers[20].CardId,Name=PlainUnit,Owner=side,Definition=db.Require(PlainUnit)}:
                state.CreateWithId(PlainUnit,side,70,side.HqOf(),4);
            dealer.Keywords.Clear();var receiver=state.CreateWithId(PlainUnit,side.Opposite(),71,side.Opposite().HqOf(),1);receiver.Keywords.Clear();
            var trace=new List<string>();engine.Api.TriggerTrace=trace;
            int actual=engine.Api.ExecuteOnDealDamageAddDamageAfterCalc(dealer,receiver,row.GetProperty("damage").GetInt32(),false,false,false);
            var visits=row.GetProperty("visits").EnumerateArray().ToArray();
            var labels=observers.ToDictionary(p=>p.Value.CardId,p=>p.Key);
            var called=trace.Where(t=>t.StartsWith(hook+"(out",StringComparison.Ordinal)).Select(t=>labels[int.Parse(System.Text.RegularExpressions.Regex.Match(t,@"#(-?\d+)（").Groups[1].Value)]).ToArray();
            if(actual!=row.GetProperty("expected").GetInt32() || !called.SequenceEqual(visits.Select(v=>v.GetProperty("id").GetInt32())))return $"观察者顺序/结果不符：{row}；actual={actual},calls={string.Join(',',called)}";
            foreach(var visit in visits)
                if(!observers[visit.GetProperty("id").GetInt32()].BlueprintVariables.TryGetValue("_545Seen",out var seen) || Convert.ToInt32(seen)!=visit.GetProperty("seen").GetInt32())return $"观察者未读到前一修正后的伤害：{row}";
        }
        return null;
    }
}
