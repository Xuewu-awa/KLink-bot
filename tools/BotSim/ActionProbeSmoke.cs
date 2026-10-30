using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;

// Bounded diagnostic on confirmed teacher prefixes, never a gameplay policy or dataset.
internal static class ActionProbeSmoke
{
    internal static int Run(CardDatabase db, Dictionary<string,string> opts)
    {
        int steps=int.Parse(opts.GetValueOrDefault("steps","8"));
        ulong seed=ulong.Parse(opts.GetValueOrDefault("seed","1"));
        if(steps<1 || steps>32)throw new ArgumentOutOfRangeException(nameof(steps));
        string[] Deck(string name)
        {
            var definition=MetaDecks.All.Single(d=>d.Name==name);
            var cards=DeckCodeParser.Expand(DeckCodeParser.Parse(definition.Code),db.DeckCodeIds,out var unknown);
            if(unknown.Count!=0)throw new InvalidDataException("Unknown deck cards");
            return cards.ToArray();
        }
        string leftName=opts.GetValueOrDefault("deck-a","英日"),rightName=opts.GetValueOrDefault("deck-b","日澳快攻");
        var left=Deck(leftName);var right=Deck(rightName);var vectors=new StateEncoder.CardVecs();
        // An empty vector map is deliberate for this execution-only diagnostic;
        // it is not the 200d feature/collision training experiment.
        var teacher=OracleActionEpisodeRunner.RunCore(db,vectors,left,right,seed,maxRows:steps,
            chooseOnePolicy:OracleChooseOnePolicy.Last);
        if(teacher.Labels.Count!=steps || teacher.Errors.Any(e=>e is not ("episode_buffer_limit" or "unfinished")))
            throw new InvalidDataException("Teacher prefix unavailable: "+string.Join(',',teacher.Errors));
        var reference=new MatchEngine(db,left,right,seed);
        using var choices=new ChoicePolicyAdapter(reference,vectors,r=>r.Options.Count==0 && r.CanCancel ? -1 : 0);
        reference.Start();var prefix=new List<ConfirmedRoot>();var rows=new List<object>();
        var watch=Stopwatch.StartNew();
        int engines=0;long roots=0;var totals=new Dictionary<string,int>();var reasons=new Dictionary<string,int>();
        var batchErrors=new Dictionary<string,int>();
        foreach(var label in teacher.Labels)
        {
            var report=IsolatedActionProbe.Run(db,vectors,left,right,seed,prefix,reference);
            var reversed=IsolatedActionProbe.RunCore(db,vectors,left,right,seed,prefix,reference,new(),reverse:true);
            bool same=JsonSerializer.Serialize(report)==JsonSerializer.Serialize(reversed);
            if(!same)throw new InvalidDataException("Probe order changed result");
            engines+=report.EnginesCreated+reversed.EnginesCreated;
            roots+=report.PrefixRootsApplied+reversed.PrefixRootsApplied;
            foreach(var error in report.Errors)batchErrors[error]=batchErrors.GetValueOrDefault(error)+1;
            foreach(var c in report.Candidates)
            {
                totals[c.Status.ToString()]=totals.GetValueOrDefault(c.Status.ToString())+1;
                reasons[c.Reason]=reasons.GetValueOrDefault(c.Reason)+1;
            }
            rows.Add(new{step=prefix.Count,report,reverseExactlyEqual=same});
            var command=label.Request.Candidates[label.ChosenIndex].Command;
            if(!MainActionPolicyAdapter.ApplyRoot(reference,label.Request.Actor,command))
                throw new InvalidDataException("Confirmed teacher root rejected");
            prefix.Add(new(label.Request.Actor,command));
        }
        watch.Stop();
        var options=new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
        options.Converters.Add(new JsonStringEnumConverter());
        var result=new{scope="Experimental simulator executability only; not legality, not training admission, not strength.",
            contract=IsolatedActionProbe.Contract,seed,leftDeck=leftName,rightDeck=rightName,steps,
            vectors="empty map for execution-only diagnostic",enginesCreated=engines,prefixRootsApplied=roots,
            elapsedSeconds=watch.Elapsed.TotalSeconds,counts=totals,reasons,batchErrors,rows};
        string output=opts.GetValueOrDefault("out")??throw new ArgumentException("--out required");
        File.WriteAllText(output,JsonSerializer.Serialize(result,options));
        Console.WriteLine($"Probe prefixes {steps}; engines {engines}; roots {roots}; {JsonSerializer.Serialize(totals)}; {watch.Elapsed.TotalSeconds:F3}s");
        return batchErrors.Count==0 ? 0 : 2;
    }
}
