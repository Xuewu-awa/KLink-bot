using System.Diagnostics;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;

// Engineering comparison, not authoritative rule validation or training admission.
internal static class CloneProbeBenchmark
{
    internal static string Successor(MatchEngine e) => e.State.SnapshotJson()
        + JsonSerializer.Serialize(e.State.ActionLog) + JsonSerializer.Serialize(e.Log)
        + JsonSerializer.Serialize(new { e.State.Random.State, e.State.Random.ConsumedCount,
            e.PendingSelectionCount, e.Api.Vm.ProgramsExecuted, e.Api.Vm.StepsExecuted,
            e.Api.Vm.FaultedPrograms, e.Api.Vm.UnsupportedOps, e.UnimplementedCalls });

    internal static string? Compare(CardDatabase db, StateEncoder.CardVecs vectors,
        string[] left, string[] right, ulong seed, List<ConfirmedRoot> prefix, MatchEngine reference)
    {
        var replayStates = new Dictionary<int,string>(); var cloneStates = new Dictionary<int,string>();
        var reverseStates = new Dictionary<int,string>();
        var replay = IsolatedActionProbe.RunCore(db,vectors,left,right,seed,prefix,reference,new(),
            afterApply:(e,i)=>replayStates.Add(i,Successor(e)));
        var clone = CloneActionProbe.RunCore(db,vectors,left,right,seed,prefix,reference,new(),
            afterApply:(e,i)=>cloneStates.Add(i,Successor(e)));
        var reverse = CloneActionProbe.RunCore(db,vectors,left,right,seed,prefix,reference,new(),reverse:true,
            afterApply:(e,i)=>reverseStates.Add(i,Successor(e)));
        if (replay.Errors.Count!=0 || clone.Errors.Count!=0 || reverse.Errors.Count!=0)
            return "batch_errors:"+JsonSerializer.Serialize(new {replay.Errors,CloneErrors=clone.Errors,ReverseErrors=reverse.Errors});
        if (JsonSerializer.Serialize(replay.Candidates)!=JsonSerializer.Serialize(clone.Candidates)
            || JsonSerializer.Serialize(clone.Candidates)!=JsonSerializer.Serialize(reverse.Candidates)) return "candidate_mismatch";
        if (replayStates.Count!=replay.Candidates.Count || cloneStates.Count!=clone.Candidates.Count || reverseStates.Count!=reverse.Candidates.Count)
            return "root_successors_missing";
        foreach (var pair in replayStates)
            if (!cloneStates.TryGetValue(pair.Key,out var value) || value!=pair.Value
                || !reverseStates.TryGetValue(pair.Key,out var reversed) || reversed!=pair.Value) return "successor_mismatch:"+pair.Key;
        return null;
    }

    internal static int Run(CardDatabase db, Dictionary<string,string> opts)
    {
        int steps=int.Parse(opts.GetValueOrDefault("steps","8"));
        int repeats=int.Parse(opts.GetValueOrDefault("repeats","5"));
        ulong seed=ulong.Parse(opts.GetValueOrDefault("seed","1"));
        if (steps<1 || steps>32 || repeats<1 || repeats>20) throw new ArgumentOutOfRangeException();
        string output=opts.GetValueOrDefault("out")??throw new ArgumentException("--out required");
        string[] Deck(string name)
        {
            var definition=MetaDecks.All.Single(d=>d.Name==name);
            var cards=DeckCodeParser.Expand(DeckCodeParser.Parse(definition.Code),db.DeckCodeIds,out var unknown);
            if(unknown.Count!=0)throw new InvalidDataException("Unknown deck cards");
            return cards.ToArray();
        }
        bool plain=opts.ContainsKey("plain");
        string leftName=plain?"plain-arado":opts.GetValueOrDefault("deck-a","英日");
        string rightName=plain?"plain-arado":opts.GetValueOrDefault("deck-b","日澳快攻");
        var left=plain?Enumerable.Repeat("card_unit_arado_ar_196",39).ToArray():Deck(leftName);
        var right=plain?left.ToArray():Deck(rightName);
        var vectors=new StateEncoder.CardVecs();
        var teacher=OracleActionEpisodeRunner.RunCore(db,vectors,left,right,seed,maxRows:steps,
            chooseOnePolicy:OracleChooseOnePolicy.Last);
        if(teacher.Labels.Count!=steps || teacher.Errors.Any(e=>e is not ("episode_buffer_limit" or "unfinished")))
            throw new InvalidDataException("Teacher prefix unavailable: "+string.Join(',',teacher.Errors));
        var profile=new CloneProbeProfile();
        var reference=new MatchEngine(db,left,right,seed);
        using var choices=new ChoicePolicyAdapter(reference,vectors,r=>r.Options.Count==0 && r.CanCancel ? -1 : 0);
        reference.Start(); var prefix=new List<ConfirmedRoot>(); var rows=new List<object>();
        int mismatches=0, determinate=0, unknown=0; double replayMs=0,cloneMs=0; long replayBytes=0,cloneBytes=0;
        (double Ms,long Bytes) Measure(Action action)
        {
            long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
            action();
            return (Stopwatch.GetElapsedTime(start).TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-bytes);
        }
        foreach(var label in teacher.Labels)
        {
            // Separate validation from timed runs so successor serialization is not charged to either path.
            string? mismatch=Compare(db,vectors,left,right,seed,prefix,reference);
            if(mismatch is not null)mismatches++;
            var replay=IsolatedActionProbe.Run(db,vectors,left,right,seed,prefix,reference); // warm both
            var clone=CloneActionProbe.Run(db,vectors,left,right,seed,prefix,reference);
            determinate+=clone.Candidates.Count(c=>c.Status!=SimulatorExecutability.Unknown);
            unknown+=clone.Candidates.Count(c=>c.Status==SimulatorExecutability.Unknown);
            // Profiling is a separate run: instrumentation is excluded from timed comparisons.
            var profiled=CloneActionProbe.RunCore(db,vectors,left,right,seed,prefix,reference,new(),profile:profile);
            if(JsonSerializer.Serialize(profiled)!=JsonSerializer.Serialize(clone))throw new InvalidDataException("Profiling changed report");
            double rm=0,cm=0; long rb=0,cb=0;
            for(int i=0;i<repeats;i++)
            {
                void Replay(){var m=Measure(()=>IsolatedActionProbe.Run(db,vectors,left,right,seed,prefix,reference));rm+=m.Ms;rb+=m.Bytes;}
                void Clone(){var m=Measure(()=>CloneActionProbe.Run(db,vectors,left,right,seed,prefix,reference));cm+=m.Ms;cb+=m.Bytes;}
                if(i%2==0){Replay();Clone();}else{Clone();Replay();}
            }
            replayMs+=rm;cloneMs+=cm;replayBytes+=rb;cloneBytes+=cb;
            rows.Add(new {step=prefix.Count,mismatch,candidates=clone.Candidates.Count,
                reasons=clone.Candidates.GroupBy(c=>c.Reason).ToDictionary(g=>g.Key,g=>g.Count()),
                counts=clone.Candidates.GroupBy(c=>c.Status.ToString()).ToDictionary(g=>g.Key,g=>g.Count()),
                replayEngines=replay.EnginesCreated,cloneEngines=clone.EnginesCreated,clone.ClonesCreated,
                replayRoots=replay.PrefixRootsApplied,cloneRoots=clone.PrefixRootsApplied,
                replayErrors=replay.Errors,cloneErrors=clone.Errors,replayMs=rm,cloneMs=cm,replayBytes=rb,cloneBytes=cb});
            var command=label.Request.Candidates[label.ChosenIndex].Command;
            if(!MainActionPolicyAdapter.ApplyRoot(reference,label.Request.Actor,command))throw new InvalidDataException("Teacher root rejected");
            prefix.Add(new(label.Request.Actor,command));
        }
        var report=new {scope="Bounded engineering fixtures; equal Unknown is not semantic coverage; no default change or PvP certification.",
            replayContract=IsolatedActionProbe.Contract,cloneContract=CloneActionProbe.Contract,
            seed,leftDeck=leftName,rightDeck=rightName,steps,repeats,vectors="empty execution-only map",
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            profileScope="one separate clone run per prefix; total includes stages; comparison includes comparison.state/request; unlisted work remains in total; elapsed/allocated, not peak memory",
            cloneProfile=profile.Samples,
            mismatches,determinate,unknown,replayMs,cloneMs,replayBytes,cloneBytes,rows};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Clone comparison: mismatches={mismatches}, determinate={determinate}, unknown={unknown}; replay={replayMs:F1}ms/{replayBytes}B clone={cloneMs:F1}ms/{cloneBytes}B");
        return mismatches==0 && determinate>0 ? 0 : 1;
    }
}
