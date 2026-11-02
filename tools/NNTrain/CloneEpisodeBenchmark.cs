using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

// Full-episode comparison only. Never emits training rows or normalizes clone work counts.
internal static class CloneEpisodeBenchmark
{
    internal sealed record Fixture(string Name,string[] Left,string[] Right);
    internal static Fixture ResolveFixture(CardDatabase db,Dictionary<string,string> opts)
    {
        bool left=opts.TryGetValue("left-deck",out var l),right=opts.TryGetValue("right-deck",out var r);
        if(left!=right || (left && opts.ContainsKey("fixture")))
            throw new ArgumentException("Use both --left-deck and --right-deck, or --fixture exclusively.");
        string[] Expand(string name)
        {
            var matches=MetaDecks.All.Where(d=>d.Name==name).ToArray();
            if(matches.Length!=1)throw new ArgumentException("Unknown meta deck: "+name);
            var cards=DeckCodeParser.Expand(DeckCodeParser.Parse(matches[0].Code),db.DeckCodeIds,out var unknown);
            if(unknown.Count!=0)throw new InvalidDataException("Unknown codes in meta deck: "+name);
            return cards.ToArray();
        }
        if(left)return new("meta: "+l+" vs "+r,Expand(l),Expand(r));
        return opts.GetValueOrDefault("fixture","alternating") switch {
            "alternating"=>new("synthetic alternating",Enumerable.Range(0,39).Select(i=>i%2==0
                ?"card_unit_arado_ar_196":"card_unit_light_infantry").ToArray(),
                Enumerable.Range(0,39).Select(i=>i%2==0?"card_unit_arado_ar_196":"card_unit_light_infantry").ToArray()),
            "m1"=>new("synthetic M1",Enumerable.Repeat("card_unit_120mm_m1_gun",39).ToArray(),
                Enumerable.Repeat("card_unit_120mm_m1_gun",39).ToArray()),
            _=>throw new ArgumentException("Unknown --fixture. Use alternating or m1.")
        };
    }
    internal static object Diagnostics(OracleActionEpisodeResult e)
    {
        object Engine(KLink.Bot.Engine.MatchEngine engine)=>new {
            engine.Api.Vm.FaultedPrograms,engine.Api.Vm.UnsupportedOps,engine.UnimplementedCalls,engine.PendingSelectionCount};
        return new {e.Identity,e.PlanningBaselineInitialVerified,e.PlanningBaselineFinalVerified,e.PlanningBaselineVerifiedRoots,
            e.Errors,e.SnapshotDifference,e.PlanningCloneFailureStep,e.PlanningCloneFailure,
            teacher=Engine(e.Teacher),replay=Engine(e.Replay),
            planning=new {e.PlanningVmFaults,e.PlanningUnsupported,e.PlanningCalls,e.PlanningPendingChoices,
                counterScope="Cumulative diagnostics including inherited clone prefixes; not newly executed work."},
            missingCalls=ActionDatasetTool.CaptureCallProvenance(e)};
    }
    internal static int Run(CardDatabase db,StateEncoder.CardVecs vectors,
        Dictionary<string,string> opts,string dataDir,string vectorPath)
    {
        int repeats=int.Parse(opts.GetValueOrDefault("repeats","3"),CultureInfo.InvariantCulture);
        ulong seed=ulong.Parse(opts.GetValueOrDefault("seed","1"),CultureInfo.InvariantCulture);
        if(repeats is <1 or >5)throw new ArgumentException("--repeats must be 1..5.");
        if(KismetLibrary.Default is null)throw new InvalidOperationException("Loaded IR required.");
        var policy=ActionDatasetTool.ParseChooseOnePolicy(opts.GetValueOrDefault("choose-one-policy","native"));
        var fixture=ResolveFixture(db,opts);
        string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,ActionDatasetTool.Json))));
        object Fingerprint(OracleActionEpisodeResult e)=>new {
            labelsSha256=Hash(e.Labels),e.Errors,e.ComparedSteps,e.AppliedActions,e.MainFaults,e.MainRejections,e.ChoiceFaults,
            teacherSnapshotSha256=Hash(e.Teacher.State.SnapshotJson()),replaySnapshotSha256=Hash(e.Replay.State.SnapshotJson()),
            teacherTraceSha256=Hash(e.Teacher.State.ActionLog),replayTraceSha256=Hash(e.Replay.State.ActionLog),
            e.Replay.State.IsFinished,e.Replay.State.Winner,e.Replay.State.Random.State,e.Replay.State.Random.ConsumedCount
        };
        bool HealthyClone(OracleActionEpisodeResult e)
        {
            bool EngineHealthy(KLink.Bot.Engine.MatchEngine engine)=>TrainingAdmission.Check(engine.State.IsFinished,engine.State.Winner,
                engine.Api.Vm.FaultedPrograms,engine.Api.Vm.UnsupportedOps,engine.UnimplementedCalls,true,e.Labels.Count,true,
                e.ChoiceFaults,pendingChoices:engine.PendingSelectionCount).Length==0;
            return e.Errors.Count==0 && e.PlanningMode=="experimental-owned-idle-clone/v1"
                && e.PlanningClones==e.Labels.Count && e.PlanningBaselineRoots==e.Labels.Count && e.PlanningPrefixRoots==0
                && e.PlanningDecisions==e.Labels.Count && EngineHealthy(e.Teacher) && EngineHealthy(e.Replay)
                && e.MainFaults+e.MainRejections+e.ChoiceFaults==0 && e.ComparedSteps==e.Labels.Count && e.AppliedActions==e.Labels.Count
                && TrainingAdmission.Check(e.Replay.State.IsFinished,e.Replay.State.Winner,e.PlanningVmFaults,e.PlanningUnsupported,
                    e.PlanningCalls,true,e.Labels.Count,true,e.PlanningChoiceFaults,pendingChoices:e.PlanningPendingChoices).Length==0
                && e.Labels.All(l=>l.AfterObservation is {Length:var n} && n==StateEncoder.Dim && l.AfterObservation.All(float.IsFinite));
        }
        (OracleActionEpisodeResult Episode,object Timing) Measure(bool clone)
        {
            long start=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();
            var e=clone?OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,fixture.Left,fixture.Right,seed,policy)
                :OracleActionEpisodeRunner.Run(db,vectors,fixture.Left,fixture.Right,seed,policy);
            double ms=clock.Elapsed.TotalMilliseconds;long bytes=GC.GetAllocatedBytesForCurrentThread()-start;
            return(e,new {mode=e.PlanningMode,elapsedMs=ms,allocatedBytes=bytes,e.PlanningPrefixRoots,e.PlanningBaselineRoots,e.PlanningClones});
        }
        Console.WriteLine("Warm full replay and experimental clone episodes...");
        var replay=Measure(false);var clone=Measure(true);
        var expected=Fingerprint(replay.Episode);string signature=JsonSerializer.Serialize(expected,ActionDatasetTool.Json);
        var baselineReasons=ActionDatasetTool.AdmissionReasons(replay.Episode,true);
        var cloneAdmission=ActionDatasetTool.AdmissionReasons(clone.Episode,true);
        bool equal=signature==JsonSerializer.Serialize(Fingerprint(clone.Episode),ActionDatasetTool.Json);
        bool healthy=HealthyClone(clone.Episode);
        var samples=new List<object>();
        if(baselineReasons.Length==0 && equal && healthy)
            for(int i=0;i<repeats;i++)
                foreach(bool useClone in i%2==0?new[]{false,true}:new[]{true,false})
                {
                    var sample=Measure(useClone);
                    if(signature!=JsonSerializer.Serialize(Fingerprint(sample.Episode),ActionDatasetTool.Json)
                        || (useClone?!HealthyClone(sample.Episode):ActionDatasetTool.AdmissionReasons(sample.Episode,true).Length!=0))
                        throw new InvalidOperationException("Episode comparison/health changed during measurement.");
                    samples.Add(sample.Timing);Console.WriteLine(JsonSerializer.Serialize(sample.Timing,ActionDatasetTool.Json));
                }
        string output=Path.GetFullPath(opts.GetValueOrDefault("out","clone-episode-benchmark.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,JsonSerializer.Serialize(new {
            version=2,kind="KLink.ExperimentalCloneEpisodeBenchmark",seed,repeats,
            fixture=new {fixture.Name,fixture.Left,fixture.Right,sha256=Hash(new {fixture.Left,fixture.Right}),
                scope="Exact ordered inputs; no current constructed legality certification. Synthetic fixtures are deliberately nonlegal."},
            chooseOnePolicy=policy.ToString(),
            baselineDiagnostics=Diagnostics(replay.Episode),cloneDiagnostics=Diagnostics(clone.Episode),
            teacher=OracleActionEpisodeRunner.ContractFor(policy),experimentalMode=clone.Episode.PlanningMode,
            inputsSha256=ActionDatasetTool.CurrentInputs(dataDir,vectorPath),
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            scope="Full public episode only; excludes startup, data loading, hashes, warm-up, admission, serialization, IO and training epochs. Current-thread managed allocation, not peak memory. No trainable dataset output.",
            baselineReasons,cloneAdmissionReasons=cloneAdmission,cloneHealthy=healthy,fingerprintsEqual=equal,
            baselineFingerprint=expected,cloneFingerprint=Fingerprint(clone.Episode),
            rows=replay.Episode.Labels.Count,candidates=replay.Episode.Labels.Sum(l=>(long)l.Request.Candidates.Count),
            warmReplayWork=replay.Timing,warmCloneWork=clone.Timing,samples
        },new JsonSerializerOptions(ActionDatasetTool.Json){WriteIndented=true}));
        Console.WriteLine($"Report: {output}; equal={equal}, cloneHealthy={healthy}");
        return baselineReasons.Length==0 && equal && healthy && cloneAdmission.Contains("experimental_planning_not_admitted")?0:2;
    }
}
