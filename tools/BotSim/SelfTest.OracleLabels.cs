using KLink.Bot.Cards;
using KLink.Bot.Bots;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string[] OracleDeck()=>Enumerable.Range(0,39)
        .Select(i=>i%2==0?PlainUnit:"card_unit_light_infantry").ToArray();

    private static string? OracleLabelsComplete(CardDatabase db)
    {
        var deck=OracleDeck();var vectors=new StateEncoder.CardVecs();
        foreach(ulong seed in new ulong[]{1,2,20261030})
        {
            var result=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,seed);
            if(result.Errors.Count!=0 || !result.Replay.State.IsFinished || result.Labels.Count==0
                || result.AppliedActions!=result.Labels.Count || result.ComparedSteps!=result.Labels.Count
                || result.ChoiceFaults+result.MainFaults+result.MainRejections!=0)
                return $"seed {seed}: {string.Join(',',result.Errors)} / labels {result.Labels.Count}";
            if(result.Labels.Any(l=>l.Request.Candidates.Count(o=>o.Command==l.Request.Candidates[l.ChosenIndex].Command)!=1))
                return "标签不唯一";
            if(result.Teacher.State.SnapshotJson()!=result.Replay.State.SnapshotJson())return "最终快照分叉";
            if(result.Labels.Select(l=>l.Request.Candidates[l.ChosenIndex].Command.Kind).Distinct().Count()!=4)
                return "未覆盖四类动作";
        }
        return null;
    }

    private static string? OracleLabelsRejectDivergence(CardDatabase db)
    {
        var deck=OracleDeck();var vectors=new StateEncoder.CardVecs();
        foreach(bool duplicate in new[]{false,true})
        {
            var result=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,
                offer:r=>r with {Candidates=duplicate?r.Candidates.Concat(r.Candidates).ToArray():Array.Empty<MainActionOption>()});
            string error=duplicate?"ambiguous_oracle_action":"uncovered_oracle_action";
            if(!result.Errors.Contains(error) || result.Labels.Count!=0)return "未拒绝 "+error;
        }
        foreach(int mutation in new[]{0,1,2})
        {
            var result=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,afterRoot:(t,r,n)=>
            {
                if(n!=1)return; // retain a verified prefix before forcing a late failure
                if(mutation==0)r.State.Random.Next(10);
                else if(mutation==1)r.State.Hq(Side.Right).BaseAttack++;
                else r.State.ActionLog.RemoveAt(r.State.ActionLog.Count-1);
            });
            string error=mutation==1?"root_snapshot_mismatch":"root_trace_mismatch";
            if(!result.Errors.Contains(error) || result.Labels.Count!=1)return $"分叉 {mutation}: {string.Join(',',result.Errors)}";
        }
        var rejected=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,select:(r,index)=>
        {
            int attack=r.Candidates.ToList().FindIndex(o=>o.Command.Kind==MainActionKind.Attack);
            return attack<0?index:attack;
        });
        return rejected.Errors.Contains("main_action_rejected") && rejected.MainRejections==1
            ? null : "教师重放拒绝没有终止整局: "+string.Join(',',rejected.Errors);
    }

    private static string? OracleLabelsBufferLimits(CardDatabase db)
    {
        var deck=OracleDeck();var vectors=new StateEncoder.CardVecs();
        var rows=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:1);
        var candidates=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxCandidates:1);
        var options=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxOptions:1);
        return new[]{rows,candidates,options}.All(r=>r.Errors.Contains("episode_buffer_limit"))
            && rows.Labels.Count==1 && candidates.Labels.Count==0 && options.Labels.Count==0
            ? null : "缓冲上限失效";
    }

    private static string? OracleLabelsRejectProbeEffects(CardDatabase db)
    {
        string[] Expand(string name)
        {
            var deck=MetaDecks.All.Single(d=>d.Name==name);
            var cards=DeckCodeParser.Expand(DeckCodeParser.Parse(deck.Code),db.DeckCodeIds,out var unknown);
            if(unknown.Count!=0)throw new InvalidOperationException("Unknown regression deck cards");
            return cards.ToArray();
        }
        var vectors=new StateEncoder.CardVecs();var left=Expand("英日");var right=Expand("日澳快攻");
        // Keep the original failure demonstrated independently of the new planner:
        // raw Greedy queries change targetOverride even when root logs/RNG agree.
        var raw=new MatchEngine(db,left,right,1);var replay=new MatchEngine(db,left,right,1);
        using var rc=new ChoicePolicyAdapter(raw,vectors,_=>0);
        using var pc=new ChoicePolicyAdapter(replay,vectors,_=>0);
        raw.Start();replay.Start();var bot=new GreedyBot();bool sawDifference=false;
        for(int step=0;step<12 && !raw.State.IsFinished;step++)
        {
            var actor=raw.State.ActiveSide;bot.PlayStep(raw,actor);
            if(bot.LastStepAction is not {} command || !MainActionPolicyAdapter.ApplyRoot(replay,actor,command))return "原始根动作拒绝";
            if(raw.State.SnapshotJson()==replay.State.SnapshotJson())continue;
            sawDifference=raw.State.SnapshotJson().Contains("targetOverride")
                && System.Text.Json.JsonSerializer.Serialize(raw.State.ActionLog)==System.Text.Json.JsonSerializer.Serialize(replay.State.ActionLog)
                && raw.State.Random.State==replay.State.Random.State;
            break;
        }
        if(!sawDifference)return "未复现原始查询副作用";
        var result=OracleActionEpisodeRunner.RunCore(db,vectors,left,right,1,maxRows:12);
        return result.Labels.Count>=7 && !result.Errors.Contains("root_snapshot_mismatch")
            && !result.Errors.Contains("planning_prefix_mismatch") && result.SnapshotDifference is null
            && result.Teacher.State.SnapshotJson()==result.Replay.State.SnapshotJson()
            ? null : "隔离后未越过原始分叉: "+string.Join(',',result.Errors)+" / "+result.Labels.Count;
    }

    private static string? OracleLabelsIsolatePlanning(CardDatabase db)
    {
        var vectors=new StateEncoder.CardVecs();var deck=OracleDeck();
        var baseline=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:12);
        var polluted=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:12,afterProbe:(probe,n)=>
        {
            // Pollute several state families after selection; none may leak into
            // the next planner, observation, label, or either execution engine.
            probe.State.Hq(Side.Left).BlueprintVariables["targetOverride"]=999;
            probe.State.Hq(Side.Right).BaseAttack+=100;
            probe.State.Random.Next(10);probe.State.ActionLog.Clear();
            probe.State.AddAutoPlayCards(9999);
        });
        if(baseline.Labels.Count!=12 || polluted.Labels.Count!=12
            || baseline.Teacher.State.SnapshotJson()!=polluted.Teacher.State.SnapshotJson()
            || baseline.Replay.State.SnapshotJson()!=polluted.Replay.State.SnapshotJson()
            || System.Text.Json.JsonSerializer.Serialize(baseline.Labels)!=System.Text.Json.JsonSerializer.Serialize(polluted.Labels))
            return "探测状态泄漏到后续重建/标签/执行";
        if(polluted.PlanningDecisions!=12 || polluted.PlanningPrefixRoots!=66)return "重建工作计数错误";
        var mismatch=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,beforeProbe:(p,n)=>
        {if(n==1)p.State.Hq(Side.Right).BaseAttack++;});
        if(!mismatch.Errors.Contains("planning_prefix_mismatch") || mismatch.Labels.Count!=1)return "未拒绝重建状态分叉";
        var fault=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,afterProbe:(p,n)=>
        {if(n==1)p.Api.Vm.UnsupportedOps["<forced-probe-fault>"]=1;});
        if(!fault.Errors.Contains("planning_execution_fault") || fault.Labels.Count!=1
            || fault.PlanningUnsupported.GetValueOrDefault("<forced-probe-fault>")!=1)return "被丢弃探测故障丢失";
        var limited=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxPlanningPrefixRoots:0);
        return limited.Errors.Contains("planning_work_limit") && limited.Labels.Count==1 && limited.PlanningPrefixRoots==0
            ? null : "探测工作上限无效";
    }
}
