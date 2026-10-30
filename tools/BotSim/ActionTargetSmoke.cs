using System.Text.Json;
using System.Text.Json.Serialization;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;

// Offline crafted fixtures, not prefixes eligible for training or game-rule certification.
internal static class ActionTargetSmoke
{
    internal sealed record TargetState(int Id, int Defense, int PinnedTurns);
    internal sealed record Result(Side Actor, int RequestedTargetId, bool Accepted, int? CurrentTargetId,
        IReadOnlyList<TargetState> Before, IReadOnlyList<TargetState> After,
        IReadOnlyDictionary<string,int> DiagnosticCalls, int VmFaults);

    internal static Result Execute(CardDatabase db, Side actor, int targetId)
    {
        var engine = new MatchEngine(db,Array.Empty<string>(),Array.Empty<string>(),1);
        var state=engine.State; state.ActiveSide=actor; state.SetKredits(actor,12);
        foreach(var side in new[]{Side.Left,Side.Right})
        {
            var hq=state.CreateWithId("card_location_berlin",side,side==Side.Left?1:41,side.HqOf(),0);
            state.SetLocationCardBySide(side,hq);state.SetHqDefense(side,20);
        }
        var source=state.CreateWithId("card_event_desert_rats",actor,70,actor.HandOf(),0);
        var units=new[]{state.CreateWithId("card_unit_arado_ar_196",actor.Opposite(),80,actor.Opposite().HqOf(),1),
            state.CreateWithId("card_unit_arado_ar_196",actor.Opposite(),81,actor.Opposite().HqOf(),2),
            state.CreateWithId("card_unit_arado_ar_196",actor,82,actor.HqOf(),1)};
        foreach(var unit in units){unit.Defense=unit.MaxDefense=5;unit.PinnedTurns=0;}
        TargetState[] Capture()=>units.Select(c=>new TargetState(c.CardId,c.Defense,c.PinnedTurns)).ToArray();
        var before=Capture();
        bool accepted=MainActionPolicyAdapter.ApplyRoot(engine,actor,new(MainActionKind.Play,70,targetId));
        return new(actor,targetId,accepted,source.CurrentTarget?.CardId,before,Capture(),
            engine.UnimplementedCalls.ToDictionary(p=>p.Key,p=>p.Value),engine.Api.Vm.FaultedPrograms);
    }

    internal static int Run(CardDatabase db, Dictionary<string,string> opts)
    {
        var rows=(from actor in new[]{Side.Left,Side.Right} from target in new[]{80,81,82,0,99999}
            select Execute(db,actor,target)).ToArray();
        var options=new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
        options.Converters.Add(new JsonStringEnumConverter());
        var result=new{scope="Crafted simulator target-forwarding observations only; not authoritative legality or a mask.",
            card="card_event_desert_rats",rows};
        File.WriteAllText(opts.GetValueOrDefault("out")??throw new ArgumentException("--out required"),
            JsonSerializer.Serialize(result,options));
        Console.WriteLine($"Target forwarding fixtures: {rows.Length}");return 0;
    }
}
