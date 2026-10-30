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
        var result=OracleActionEpisodeRunner.Run(db,new StateEncoder.CardVecs(),Expand("英日"),Expand("日澳快攻"),1);
        return result.Errors.Contains("root_snapshot_mismatch") && result.Labels.Count>0
            && result.SnapshotDifference?.Contains("targetOverride")==true
            && System.Text.Json.JsonSerializer.Serialize(result.Teacher.State.ActionLog)
                ==System.Text.Json.JsonSerializer.Serialize(result.Replay.State.ActionLog)
            && result.Teacher.State.Random.State==result.Replay.State.Random.State
            ? null : "真实查询副作用未被拒绝，或未复现日志/RNG相等的状态分叉";
    }
}
