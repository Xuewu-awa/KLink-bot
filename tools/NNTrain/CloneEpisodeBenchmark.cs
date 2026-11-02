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
    internal static int Run(CardDatabase db,StateEncoder.CardVecs vectors,
        Dictionary<string,string> opts,string dataDir,string vectorPath)
    {
        int repeats=int.Parse(opts.GetValueOrDefault("repeats","3"),CultureInfo.InvariantCulture);
        ulong seed=ulong.Parse(opts.GetValueOrDefault("seed","1"),CultureInfo.InvariantCulture);
        if(repeats is <1 or >5)throw new ArgumentException("--repeats must be 1..5.");
        if(KismetLibrary.Default is null)throw new InvalidOperationException("Loaded IR required.");
        var policy=ActionDatasetTool.ParseChooseOnePolicy(opts.GetValueOrDefault("choose-one-policy","native"));
        string[] deck=Enumerable.Range(0,39).Select(i=>i%2==0?"card_unit_arado_ar_196":"card_unit_light_infantry").ToArray();
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
            var e=clone?OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,deck,deck,seed,policy)
                :OracleActionEpisodeRunner.Run(db,vectors,deck,deck,seed,policy);
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
            version=1,kind="KLink.ExperimentalCloneEpisodeBenchmark",seed,repeats,
            fixture="39 alternating arado_ar_196/light_infantry per side; synthetic, not constructed-legal",
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
