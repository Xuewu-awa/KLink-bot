using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

// Pilot contract only. No row writer/dataset schema accepts this contract yet.
internal static class ClonePlanningAdmission
{
    internal const string Contract="experimental-clone-paired-reference/v1";
    internal const string Mode="experimental-owned-idle-clone/v1";
    internal static string[] Check(OracleActionEpisodeResult clone,OracleActionEpisodeResult reference,bool irLoaded)
    {
        var reasons=new List<string>();
        reasons.AddRange(ActionDatasetTool.ExecutionReasons(clone,irLoaded).Select(r=>"clone:"+r));
        reasons.AddRange(ActionDatasetTool.AdmissionReasons(reference,irLoaded).Select(r=>"reference:"+r));
        if(clone.PlanningMode!=Mode)reasons.Add("clone_mode_mismatch");
        if(clone.Identity is null || reference.Identity is null || clone.Identity!=reference.Identity
            || clone.Identity.OrderedDecksSha256.Length!=64 || !clone.Identity.OrderedDecksSha256.All(Uri.IsHexDigit)
            || clone.Identity.Teacher is not (OracleActionEpisodeRunner.TeacherContract or OracleActionEpisodeRunner.LastChoiceTeacherContract))
            reasons.Add("episode_identity_mismatch");
        var engines=new[]{clone.Teacher,clone.Replay,reference.Teacher,reference.Replay};
        if(engines.Distinct(ReferenceEqualityComparer.Instance).Count()!=4
            || engines.Any(e=>!ReferenceEquals(e.State.Database,reference.Teacher.State.Database)))
            reasons.Add("execution_engine_ownership_mismatch");
        if(clone.SnapshotDifference is not null || reference.SnapshotDifference is not null)
            reasons.Add("snapshot_difference_recorded");
        int n=clone.Labels.Count;
        if(n<=0 || n>OracleActionEpisodeRunner.MaxRows || (long)n*(n-1)/2>OracleActionEpisodeRunner.MaxPlanningPrefixRoots
            || clone.PlanningClones!=n || clone.PlanningBaselineRoots!=n || clone.PlanningBaselineVerifiedRoots!=n
            || clone.PlanningDecisions!=n || clone.PlanningPrefixRoots!=0)
            reasons.Add("clone_work_count_mismatch");
        if(!clone.PlanningBaselineInitialVerified || !clone.PlanningBaselineFinalVerified)
            reasons.Add("clone_baseline_not_verified");
        if(clone.PlanningCloneFailure is not null || clone.PlanningCloneFailureStep is not null)
            reasons.Add("clone_boundary_failed");
        foreach(var e in new[]{clone,reference})
        {
            if(new[]{e.Teacher,e.Replay}.Any(engine=>engine.Api.Vm.FaultedPrograms<0
                || engine.PendingSelectionCount<0 || engine.Api.Vm.UnsupportedOps.Any(p=>p.Value<0)
                || engine.UnimplementedCalls.Any(p=>p.Value<0)))reasons.Add("negative_diagnostic_count");
            if(e.PlanningVmFaults<0 || e.PlanningChoiceFaults<0 || e.ChoiceFaults<0 || e.PlanningPendingChoices<0
                || e.PlanningUnsupported.Any(p=>p.Value<0) || e.PlanningCalls.Any(p=>p.Value<0))
                reasons.Add("negative_diagnostic_count");
            var provenance=ActionDatasetTool.CaptureCallProvenance(e);
            if(provenance.Any(p=>p.SitesOmitted!=0 || p.CaptureFailures!=0))reasons.Add("incomplete_call_provenance");
            if(provenance.Any(p=>p.Sites.Count!=0))reasons.Add("missing_call_recorded");
            if(!ValidLabels(e.Labels))reasons.Add("invalid_action_labels");
        }
        if(!Equivalent(clone.Teacher,clone.Replay) || !Equivalent(reference.Teacher,reference.Replay)
            || !Equivalent(clone.Replay,reference.Replay)
            || !ValidLabels(clone.Labels) || !ValidLabels(reference.Labels)
            || JsonSerializer.Serialize(clone.Labels,ActionDatasetTool.Json)!=JsonSerializer.Serialize(reference.Labels,ActionDatasetTool.Json))
            reasons.Add("reference_episode_mismatch");
        return reasons.Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
    }
    private static bool Equivalent(MatchEngine a,MatchEngine b)=>a.State.SnapshotJson()==b.State.SnapshotJson()
        && JsonSerializer.Serialize(a.State.ActionLog)==JsonSerializer.Serialize(b.State.ActionLog)
        && a.State.IsFinished==b.State.IsFinished && a.State.Winner==b.State.Winner && a.State.Turn==b.State.Turn
        && a.State.ActiveSide==b.State.ActiveSide && a.State.Random.State==b.State.Random.State
        && a.State.Random.ConsumedCount==b.State.Random.ConsumedCount;
    private static bool ValidLabels(IReadOnlyList<OracleActionLabel> labels)
    {
        long options=0;
        for(int i=0;i<labels.Count;i++)
        {
            var l=labels[i];var r=l.Request;
            if(l.AfterObservation is null || l.AfterObservation.Length!=StateEncoder.Dim || !l.AfterObservation.All(float.IsFinite)
                || l.Step!=i || r.Actor is not (Side.Left or Side.Right) || r.Turn<=0
                || r.Observation.Count!=StateEncoder.Dim || !r.Observation.All(float.IsFinite)
                || r.Candidates.Count is <1 or >OracleActionEpisodeRunner.MaxCandidatesPerRow
                || l.ChosenIndex<0 || l.ChosenIndex>=r.Candidates.Count
                || !r.CoverageGaps.Contains("legal-mask/unverified")
                || r.Candidates.Select(c=>c.Command).Distinct().Count()!=r.Candidates.Count
                || r.Candidates.Where((c,j)=>c.Index!=j || c.Features.Count!=MainActionFeatures.Dim || !c.Features.All(float.IsFinite)).Any())return false;
            options+=r.Candidates.Count;
        }
        return options<=OracleActionEpisodeRunner.MaxOptionsPerEpisode;
    }
    internal static int Inspect(CardDatabase db,StateEncoder.CardVecs vectors,Dictionary<string,string> opts,string dataDir,string vectorPath)
    {
        if(KLink.Bot.Effects.Blueprint.KismetLibrary.Default is null)throw new InvalidOperationException("Loaded IR required.");
        var fixture=CloneEpisodeBenchmark.ResolveFixture(db,opts);
        ulong seed=ulong.Parse(opts.GetValueOrDefault("seed","1"),System.Globalization.CultureInfo.InvariantCulture);
        var policy=ActionDatasetTool.ParseChooseOnePolicy(opts.GetValueOrDefault("choose-one-policy","native"));
        var reference=OracleActionEpisodeRunner.Run(db,vectors,fixture.Left,fixture.Right,seed,policy);
        var clone=OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,fixture.Left,fixture.Right,seed,policy);
        var reasons=Check(clone,reference,true);
        string path=Path.GetFullPath(opts.GetValueOrDefault("out","clone-admission.json"));Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object Fingerprint(OracleActionEpisodeResult e)=>new {
            labelsSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(e.Labels,
                new JsonSerializerOptions(ActionDatasetTool.Json){NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}))),
            snapshotSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(e.Replay.State.SnapshotJson()))),
            traceSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(e.Replay.State.ActionLog))),
            e.Replay.State.Random.State,e.Replay.State.Random.ConsumedCount,e.Replay.State.Winner};
        File.WriteAllText(path,JsonSerializer.Serialize(new {version=1,contract=Contract,accepted=reasons.Length==0,reasons,
            writesTrainingRows=false,fixture,clone.Identity,
            cloneFingerprint=Fingerprint(clone),referenceFingerprint=Fingerprint(reference),inputsSha256=ActionDatasetTool.CurrentInputs(dataDir,vectorPath),
            defaultAdmissionReasons=ActionDatasetTool.AdmissionReasons(clone,true),
            cloneDiagnostics=CloneEpisodeBenchmark.Diagnostics(clone),referenceDiagnostics=CloneEpisodeBenchmark.Diagnostics(reference),
            rows=clone.Labels.Count,clone.PlanningClones,clone.PlanningBaselineRoots,clone.PlanningBaselineVerifiedRoots,
            scope="Paired software consistency/health check, not rule certification. Shared catalogs and IR frozen; single thread; fixed first-index choice adapters. No dataset output or training admission change."},TrainingAdmission.Json));
        Console.WriteLine($"Clone pilot admission: {reasons.Length==0}; rows {clone.Labels.Count}; report {path}");
        return reasons.Length==0?0:2;
    }
}
