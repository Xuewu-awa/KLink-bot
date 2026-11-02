using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? CloneProbeProfileIsObservational(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db,2); var vectors=new StateEncoder.CardVecs();
        foreach(bool fault in new[]{false,true})
        {
            Action<MatchEngine,int>? hook=fault ? (e,i)=>throw new InvalidOperationException("profile fixture") : null;
            var plain=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterApply:hook);
            var profile=new CloneProbeProfile();
            var measured=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterApply:hook,profile:profile);
            if(JsonSerializer.Serialize(plain)!=JsonSerializer.Serialize(measured))return "profiling changed outcomes";
            var samples=profile.Samples;
            if(samples["total"].Count!=1 || samples["baseline_replay"].Count!=1
                || samples["engine_clone"].Count!=measured.ClonesCreated
                || samples["comparison"].Count!=measured.Candidates.Count
                || samples["root_execution"].Count!=measured.Candidates.Count
                || samples["evidence"].Count!=measured.Candidates.Count
                || samples.Values.Any(v=>v.Milliseconds<0 || v.AllocatedBytes<0))return "profiling count/unwind";
        }
        var limited=new CloneProbeProfile();
        var rejected=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(MaxCandidates:1),profile:limited);
        return rejected.Errors.Contains("probe_work_limit") && limited.Samples["total"].Count==1
            && !limited.Samples.ContainsKey("baseline_replay") ? null : "early exit profiling";
    }

    private static string? CloneProbeReplayEquivalence(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db); var vectors=new StateEncoder.CardVecs();
        using var choices=new ChoicePolicyAdapter(engine,vectors,r=>r.Options.Count==0 && r.CanCancel ? -1 : 0);
        var offered=engine.ChooseSpawnCard; var hand=engine.PickHandTargetId;
        // Build nonempty play/turn/move/attack prefixes through normal roots only.
        void Apply(MainActionCommand command)
        {
            var root=new ConfirmedRoot(engine.State.ActiveSide,command);
            if(!MainActionPolicyAdapter.ApplyRoot(engine,root.Actor,command))throw new InvalidOperationException("fixture rejected "+command);
            prefix.Add(root);
        }
        string? Check()
        {
            string before=CloneProbeBenchmark.Successor(engine);
            var error=CloneProbeBenchmark.Compare(db,vectors,deck,deck,1,prefix,engine);
            if(error is not null)return "prefix "+prefix.Count+": "+error;
            if(before!=CloneProbeBenchmark.Successor(engine) || offered!=engine.ChooseSpawnCard || hand!=engine.PickHandTargetId)
                return "caller state/adapter changed";
            var r=CloneActionProbe.Run(db,vectors,deck,deck,1,prefix,engine);
            return r.EnginesCreated==1 && r.ClonesCreated==r.Candidates.Count && r.PrefixRootsApplied==prefix.Count
                && r.AllDeterminate ? null : "clone work accounting/health: "+JsonSerializer.Serialize(r);
        }
        var failure=Check(); if(failure is not null)return failure;
        int id=engine.State.Hand(engine.State.ActiveSide).First().CardId;
        Apply(new(MainActionKind.Play,id));
        failure=Check();if(failure is not null)return failure;
        Apply(new(MainActionKind.EndTurn));Apply(new(MainActionKind.EndTurn));
        Apply(new(MainActionKind.Move,id,Slot:0));
        failure=Check();if(failure is not null)return failure;
        Apply(new(MainActionKind.EndTurn));Apply(new(MainActionKind.EndTurn));
        Apply(new(MainActionKind.Attack,id,engine.State.Hq(engine.State.ActiveSide.Opposite()).CardId));
        return Check();
    }

    private static string? CloneProbeIsolationAndFailures(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db,2);var vectors=new StateEncoder.CardVecs();
        var clean=CloneActionProbe.Run(db,vectors,deck,deck,1,prefix,engine);
        var polluted=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),reverse:true,
            afterApply:(e,i)=>{e.State.Hq(Side.Left).BaseAttack+=100;e.State.Random.Next(10);e.State.ActionLog.Clear();});
        if(JsonSerializer.Serialize(clean.Candidates)!=JsonSerializer.Serialize(polluted.Candidates))return "branch leaked";
        var injections=new (string Reason,Action<MatchEngine,int> Apply)[]{
            ("unsupported_op",(e,i)=>e.Api.Vm.UnsupportedOps["forced-clone-op"]=1),
            ("unimplemented_call",(e,i)=>e.State.UnimplementedCalls["forced-clone-call"]=1),
            ("choice_policy_dependent",(e,i)=>{var h=e.State.Hq(Side.Left);e.ChooseSpawnCard!(h,new[]{h});}),
            ("pending_choice",(e,i)=>e.ResolveHandSelection(e.State.Hq(Side.Left),()=>false,false)),
            ("probe_exception:InvalidOperationException",(e,i)=>throw new InvalidOperationException("fixture"))};
        foreach(var test in injections)
        {
            var r=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterApply:test.Apply);
            if(r.Candidates.Count==0 || r.Candidates.Any(c=>c.Status!=SimulatorExecutability.Unknown || c.Reason!=test.Reason))
                return "failure classification: "+JsonSerializer.Serialize(r);
            if(test.Reason=="choice_policy_dependent" && r.Candidates.Any(c=>c.Evidence?.CompletedChoices!=1))return "choice delta";
        }
        var mismatch=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterRebuild:(e,i)=>e.State.Hq(Side.Left).BaseAttack++);
        if(mismatch.Candidates.Any(c=>c.Reason!="prefix_mismatch") || mismatch.AllDeterminate)return "prefix mismatch";
        var boundary=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterBaseline:e=>e.ChooseSpawnCard=(s,c)=>s);
        if(boundary.ClonesCreated!=0 || boundary.Candidates.Count==0 || boundary.Candidates.Any(c=>c.Status!=SimulatorExecutability.Unknown
            || c.Reason!="probe_exception:NotSupportedException"))return "boundary rejection hidden: "+JsonSerializer.Serialize(boundary);
        return null;
    }

    private static string? CloneProbeLimitsAndInvalidation(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db,2);var vectors=new StateEncoder.CardVecs();
        foreach(var limit in new[]{new ActionProbeLimits(MaxCandidates:1),new ActionProbeLimits(MaxPrefixRoots:1),new ActionProbeLimits(MaxTotalPrefixRoots:0)})
        {
            var r=CloneActionProbe.Run(db,vectors,deck,deck,1,prefix,engine,limit);
            if(!r.Errors.Contains("probe_work_limit") || r.EnginesCreated!=0 || r.ClonesCreated!=0 || r.Candidates.Count!=0)return "limit";
        }
        var bad=CloneActionProbe.Run(db,vectors,deck,deck,1,new[]{new ConfirmedRoot(Side.Right,new MainActionCommand(MainActionKind.Play,99999))},engine);
        if(!bad.Errors.Contains("baseline_prefix_rejected") || bad.ClonesCreated!=0 || bad.AllDeterminate)return "bad prefix";
        var dirty=CloneActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterApply:(e,i)=>{if(i==0)engine.State.Hq(Side.Right).BaseAttack++;});
        return dirty.Errors.Contains("reference_changed") && dirty.Candidates.Count>0
            && dirty.Candidates.All(c=>c.Status==SimulatorExecutability.Unknown && c.Reason=="reference_changed") ? null : "reference invalidation";
    }
}
