using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static (MatchEngine Engine,string[] Deck,List<ConfirmedRoot> Prefix) ProbeFixture(CardDatabase db,int turns=0)
    {
        var deck=Enumerable.Repeat(PlainUnit,39).ToArray();
        var engine=new MatchEngine(db,deck,deck,1);engine.Start();
        var history=new List<ConfirmedRoot>();
        for(int i=0;i<turns;i++)
        {
            var root=new ConfirmedRoot(engine.State.ActiveSide,new(MainActionKind.EndTurn));
            if(!MainActionPolicyAdapter.ApplyRoot(engine,root.Actor,root.Command))throw new InvalidOperationException("Fixture root rejected");
            history.Add(root);
        }
        return(engine,deck,history);
    }

    private static string? ProbeClassifiesAndPreservesReference(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db);var vectors=new StateEncoder.CardVecs();
        var played=new ConfirmedRoot(engine.State.ActiveSide,new(MainActionKind.Play,engine.State.Hand(engine.State.ActiveSide).First().CardId));
        if(!MainActionPolicyAdapter.ApplyRoot(engine,played.Actor,played.Command))return "测试部署拒绝";
        prefix.Add(played);
        string snapshot=engine.State.SnapshotJson(),trace=JsonSerializer.Serialize(engine.State.ActionLog);
        ulong rng=engine.State.Random.State;int programs=engine.Api.Vm.ProgramsExecuted;
        var report=IsolatedActionProbe.Run(db,vectors,deck,deck,1,prefix,engine);
        int count=new MainActionPolicyAdapter(engine,vectors).DescribeCandidates(engine.State.ActiveSide).Candidates.Count;
        if(report.Errors.Count!=0 || report.Candidates.Count!=count || report.EnginesCreated!=count
            || report.PrefixRootsApplied!=count || !report.Candidates.Any(c=>c.Status==SimulatorExecutability.Accepted)
            || !report.Candidates.Any(c=>c.Status==SimulatorExecutability.Rejected))
            return "三态候选/独立引擎计数错误："+JsonSerializer.Serialize(report);
        return snapshot==engine.State.SnapshotJson() && trace==JsonSerializer.Serialize(engine.State.ActionLog)
            && rng==engine.State.Random.State && programs==engine.Api.Vm.ProgramsExecuted
            ? null : "探测改变了正式引擎";
    }

    private static string? ProbeOrderAndDiscardIsolation(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db,2);var vectors=new StateEncoder.CardVecs();
        var a=IsolatedActionProbe.Run(db,vectors,deck,deck,1,prefix,engine);
        var b=IsolatedActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),reverse:true,
            afterApply:(p,i)=>{p.State.Hq(Side.Left).BlueprintVariables["targetOverride"]=999;
                p.State.Hq(Side.Right).BaseAttack+=100;p.State.Random.Next(10);p.State.ActionLog.Clear();p.State.AddAutoPlayCards(9999);});
        return a.Errors.Count==0 && b.Errors.Count==0 && JsonSerializer.Serialize(a.Candidates)==JsonSerializer.Serialize(b.Candidates)
            && a.PrefixRootsApplied==(long)prefix.Count*a.Candidates.Count && b.PrefixRootsApplied==a.PrefixRootsApplied
            ? null : "候选顺序/被丢弃状态影响后续探测";
    }

    private static string? ProbeRejectsRebuildMismatch(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db,2);var vectors=new StateEncoder.CardVecs();
        var mismatch=IsolatedActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),
            afterRebuild:(p,i)=>p.State.Hq(Side.Left).BaseAttack++);
        var badPrefix=new[]{new ConfirmedRoot(Side.Right,new MainActionCommand(MainActionKind.Play,99999))};
        var rejected=IsolatedActionProbe.Run(db,vectors,deck,deck,1,badPrefix,engine);
        return !mismatch.AllDeterminate && mismatch.Candidates.Count>0
            && mismatch.Candidates.All(c=>c.Status==SimulatorExecutability.Unknown && c.Reason=="prefix_mismatch")
            && rejected.Candidates.All(c=>c.Status==SimulatorExecutability.Unknown && c.Reason=="prefix_rejected")
            ? null : "前缀失败被当作明确拒绝";
    }

    private static string? ProbeFaultAndChoiceAreUnknown(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db);var vectors=new StateEncoder.CardVecs();
        var injections=new (string Reason,Action<MatchEngine,int> Apply)[]{
            ("unsupported_op",(p,i)=>p.Api.Vm.UnsupportedOps["<forced-probe-fault>"]=1),
            ("unimplemented_call",(p,i)=>p.State.UnimplementedCalls["<forced-unimplemented>"]=1),
            ("choice_policy_dependent",(p,i)=>{var h=p.State.Hq(Side.Left);p.ChooseSpawnCard!(h,new[]{h});}),
            ("choice_fault",(p,i)=>{try{p.ChooseSpawnCard!(p.State.Hq(Side.Left),Array.Empty<CardInstance>());}catch(InvalidOperationException){}}),
            ("pending_choice",(p,i)=>p.ResolveHandSelection(p.State.Hq(Side.Left),()=>false,false)),
            ("probe_exception:InvalidOperationException",(p,i)=>throw new InvalidOperationException("test"))};
        foreach(var test in injections)
        {
            var result=IsolatedActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterApply:test.Apply);
            if(result.AllDeterminate || result.Candidates.Count==0
                || result.Candidates.Any(c=>c.Status!=SimulatorExecutability.Unknown || c.Reason!=test.Reason))
                return "未知被伪装成合法/非法："+test.Reason+" / "+JsonSerializer.Serialize(result);
        }
        return null;
    }

    private static string? ProbeLimitsAndReferenceContamination(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db,2);var vectors=new StateEncoder.CardVecs();
        foreach(var limit in new[]{new ActionProbeLimits(MaxCandidates:1),new ActionProbeLimits(MaxPrefixRoots:1),
            new ActionProbeLimits(MaxTotalPrefixRoots:0)})
        {
            var r=IsolatedActionProbe.Run(db,vectors,deck,deck,1,prefix,engine,limit);
            if(r.AllDeterminate || !r.Errors.Contains("probe_work_limit") || r.EnginesCreated!=0
                || r.PrefixRootsApplied!=0 || r.Candidates.Count!=0)return "超限仍然部分执行/截断";
        }
        var polluted=IsolatedActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),
            afterApply:(p,i)=>{if(i==0)engine.State.Hq(Side.Right).BaseAttack++;});
        return polluted.Errors.Contains("reference_changed") && !polluted.AllDeterminate
            && polluted.Candidates.All(c=>c.Status==SimulatorExecutability.Unknown && c.Reason=="reference_changed")
            ? null : "正式状态变化未使整个批次失效";
    }
    private static string? ProbeEvidenceIsRootDelta(CardDatabase db)
    {
        var (engine,deck,prefix)=ProbeFixture(db,2);var vectors=new StateEncoder.CardVecs();
        var request=new MainActionPolicyAdapter(engine,vectors).DescribeCandidates(engine.State.ActiveSide);
        var report=IsolatedActionProbe.RunCore(db,vectors,deck,deck,1,prefix,engine,new(),afterApply:(p,i)=>{
            p.State.UnimplementedCalls["<local-ran:probe-evidence>"]=4;
            p.State.UnimplementedCalls["<probe-evidence-missing>"]=2;
            p.Api.Vm.UnsupportedOps["<probe-evidence-op>"]=3;
        });
        if(report.Errors.Count!=0 || report.Candidates.Count!=request.Candidates.Count)return "证据报告为空";
        foreach(var c in report.Candidates)
        {
            var e=c.Evidence;
            if(e is null || e.Command!=request.Candidates[c.Index].Command || e.Phase!="root"
                || e.MissingCalls.Count!=1 || e.MissingCalls.GetValueOrDefault("<probe-evidence-missing>")!=2
                || e.UnsupportedOps.GetValueOrDefault("<probe-evidence-op>")!=3 || e.VmFaults!=0
                || e.ChoiceFaults!=0 || e.CompletedChoices!=0 || c.Status!=SimulatorExecutability.Unknown)
                return "根增量/命令/信息性排除不正确";
        }
        return null;
    }

    private static string? ProbeTargetForwarding(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        foreach(var target in new[]{80,81})
        {
            var r=ActionTargetSmoke.Execute(db,side,target);
            if(!r.Accepted || r.CurrentTargetId!=target || r.VmFaults!=0 || r.DiagnosticCalls.Any(p=>p.Key!="<local-ran:GetPlayFromHandDamage>" && p.Value>0))
                return "指定敌方目标的执行路径有故障";
            foreach(var unit in r.After)
            {
                var before=r.Before.Single(c=>c.Id==unit.Id);
                if(unit.Id==target ? unit.Defense!=before.Defense-1 || unit.PinnedTurns<=0
                    : unit!=before)return "伤害/压制没有严格作用于指定目标";
            }
        }
        return null;
    }

}
