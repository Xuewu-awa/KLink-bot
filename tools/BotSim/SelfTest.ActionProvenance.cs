using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static void RunProvenanceProbe(MatchEngine engine,KismetProgram program) =>
        engine.Api.Vm.RunLocalProgram(program,new EffectContext {Engine=engine,State=engine.State,Controller=Side.Left},null,"result");

    private static string? ActionProvenancePlanning(CardDatabase db)
    {
        var vectors=new StateEncoder.CardVecs();var deck=OracleDeck();
        var p=new KismetProgram(new[] {ProvenanceStep(0,"math","Format"),ProvenanceStep(1,"return")},0)
            {AssetName="diagnostic_probe",FunctionName="shared",SourceEntry=0};
        var baseline=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:2);
        var captured=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:2,afterProbe:(probe,n)=>RunProvenanceProbe(probe,p));
        if(captured.Labels.Count!=2 || captured.PlanningMissingCallSites.Count!=1
            || captured.PlanningMissingCallSites[0].Count!=2 || captured.PlanningCalls.GetValueOrDefault("math:Format")!=2
            || captured.PlanningMissingCallSites[0].Site.FunctionName!="shared"
            || captured.Teacher.Api.Vm.MissingCallSites.Count!=0 || captured.Replay.Api.Vm.MissingCallSites.Count!=0
            || captured.PlanningMissingCallSitesOmitted!=0 || captured.PlanningMissingCallSiteCaptureFailures!=0)
            return "丢弃规划来源丢失、重复计数或泄漏至保留引擎";
        if(baseline.Teacher.State.SnapshotJson()!=captured.Teacher.State.SnapshotJson()
            || baseline.Replay.State.SnapshotJson()!=captured.Replay.State.SnapshotJson()
            || JsonSerializer.Serialize(baseline.Labels)!=JsonSerializer.Serialize(captured.Labels)) return "来源采集改变标签或执行状态";
        foreach(bool before in new[] {true,false})
        {
            void Fail(MatchEngine probe,int n){RunProvenanceProbe(probe,p);throw new InvalidOperationException("provenance test");}
            var failed=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:2,
                beforeProbe:before?Fail:null,afterProbe:before?null:Fail);
            if(!failed.Errors.Contains("driver_exception:InvalidOperationException") || failed.Labels.Count!=0
                || failed.PlanningCalls.GetValueOrDefault("math:Format")!=1 || failed.PlanningMissingCallSites.Count!=1
                || failed.PlanningMissingCallSites[0].Count!=1) return "异常退出绕过finally来源收集";
        }
        // The VM cannot serialize this unused cycle; the planner must propagate
        // collection failure counts as well as its original missing-call count.
        var args=new List<KismetExpr>();var cycle=new KismetExpr {Args=args};args.Add(cycle);
        var broken=p with {Steps=new[] {ProvenanceStep(0,"math","Format"),ProvenanceStep(1,"return",src:cycle)}};
        var incomplete=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:2,afterProbe:(probe,n)=>RunProvenanceProbe(probe,broken));
        return incomplete.PlanningMissingCallSiteCaptureFailures==2 && incomplete.PlanningMissingCallSites.Count==0
            && incomplete.PlanningCalls.GetValueOrDefault("math:Format")==2 && incomplete.PlanningVmFaults==0
            ? null : "采集失败被隐藏或改变执行健康计数";
    }

    private static string? ActionProvenancePlanningLimit(CardDatabase db)
    {
        var vectors=new StateEncoder.CardVecs();var deck=OracleDeck();
        // Each VM drops two occurrences, and each has a disjoint site identity.
        int calls=KismetVm.MaxMissingCallSites+2;
        var steps=Enumerable.Range(0,calls).Select(i=>ProvenanceStep(i,"call","MissingPlanningCapacity"))
            .Append(ProvenanceStep(calls,"return")).ToArray();
        var result=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:2,afterProbe:(probe,n)=>
            RunProvenanceProbe(probe,new KismetProgram(steps,0) {FunctionName="planner_"+n}));
        long retained=result.PlanningMissingCallSites.Sum(p=>p.Count);
        return result.PlanningMissingCallSites.Count==KismetVm.MaxMissingCallSites
            && result.PlanningMissingCallSitesOmitted==KismetVm.MaxMissingCallSites+4
            && retained+result.PlanningMissingCallSitesOmitted==calls*2L
            && result.PlanningCalls.GetValueOrDefault("MissingPlanningCapacity")==calls*2
            && result.PlanningMissingCallSiteCaptureFailures==0 && result.PlanningVmFaults==0
            ? null : "跨VM容量、单VM遗漏与原始缺口总数不一致";
    }
}
