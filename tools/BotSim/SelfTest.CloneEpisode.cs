using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? CloneEpisodeEquivalence(CardDatabase db)
    {
        var vectors=new StateEncoder.CardVecs();
        var deck=OracleDeck();
        foreach(ulong seed in new ulong[]{1,2})
        {
            var replay=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,seed);
            var clone=OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,deck,deck,seed);
            if(replay.Errors.Count!=0 || clone.Errors.Count!=0 || !clone.Replay.State.IsFinished
                || JsonSerializer.Serialize(replay.Labels)!=JsonSerializer.Serialize(clone.Labels)
                || replay.Replay.State.SnapshotJson()!=clone.Replay.State.SnapshotJson()
                || JsonSerializer.Serialize(replay.Replay.State.ActionLog)!=JsonSerializer.Serialize(clone.Replay.State.ActionLog)
                || replay.Replay.State.Random.State!=clone.Replay.State.Random.State
                || replay.Replay.State.Random.ConsumedCount!=clone.Replay.State.Random.ConsumedCount)
                return $"seed {seed}: {string.Join(',',clone.Errors)} / labels {clone.Labels.Count}";
            if(clone.PlanningPrefixRoots!=0 || clone.PlanningClones!=clone.Labels.Count
                || clone.PlanningBaselineRoots!=clone.Labels.Count || clone.PlanningDecisions!=clone.Labels.Count
                || clone.PlanningVmFaults+clone.PlanningChoiceFaults+clone.MainFaults+clone.MainRejections!=0)
                return "克隆工作/健康计数错误";
        }
        string[] Expand(string name)
        {
            var definition=MetaDecks.All.Single(d=>d.Name==name);
            var expanded=DeckCodeParser.Expand(DeckCodeParser.Parse(definition.Code),db.DeckCodeIds,out var unknown);
            if(unknown.Count!=0)throw new InvalidOperationException("Unknown regression deck");
            return expanded.ToArray();
        }
        var left=Expand("英日");var right=Expand("日澳快攻");
        var prefix=OracleActionEpisodeRunner.RunCore(db,vectors,left,right,1,maxRows:16);
        var clonedPrefix=OracleActionEpisodeRunner.RunCore(db,vectors,left,right,1,maxRows:16,clonePlanning:true);
        if(prefix.Labels.Count!=16 || JsonSerializer.Serialize(prefix.Labels)!=JsonSerializer.Serialize(clonedPrefix.Labels)
            || JsonSerializer.Serialize(prefix.Errors)!=JsonSerializer.Serialize(clonedPrefix.Errors)
            || prefix.Replay.State.SnapshotJson()!=clonedPrefix.Replay.State.SnapshotJson())
            return "复杂查询副作用夹具的克隆前缀不一致: "+string.Join(',',clonedPrefix.Errors);
        return null;
    }

    private static string? CloneEpisodeIsolation(CardDatabase db)
    {
        var vectors=new StateEncoder.CardVecs();var deck=OracleDeck();
        var replay=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:12);
        var clone=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:12,clonePlanning:true,
            afterProbe:(probe,n)=>{
                probe.State.Hq(Side.Right).BaseAttack+=100;
                probe.State.Hq(Side.Left).BlueprintVariables["targetOverride"]=999;
                probe.State.Random.Next(10);probe.State.ActionLog.Clear();probe.State.AddAutoPlayCards(9999);
            });
        if(clone.Labels.Count!=12 || JsonSerializer.Serialize(replay.Labels)!=JsonSerializer.Serialize(clone.Labels)
            || replay.Replay.State.SnapshotJson()!=clone.Replay.State.SnapshotJson())return "克隆探测污染泄漏";
        var mismatch=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:12,clonePlanning:true,
            beforeProbe:(p,n)=>{if(n==1)p.State.Hq(Side.Right).BaseAttack++;});
        if(!mismatch.Errors.Contains("planning_prefix_mismatch") || mismatch.Labels.Count!=1)return "克隆分叉未拒绝";
        var unsupported=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:12,clonePlanning:true,
            beforeClone:(p,n)=>{if(n==1)p.ChooseSpawnCard=(c,cards)=>cards[0];});
        if(!unsupported.Errors.Contains("planning_clone_unsupported") || unsupported.Labels.Count!=1
            || unsupported.PlanningClones!=1 || unsupported.PlanningPrefixRoots!=0)return "不支持克隆未显式停止/意外回退";
        var fault=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:12,clonePlanning:true,
            afterProbe:(p,n)=>{if(n==1)p.Api.Vm.UnsupportedOps["<forced-clone-fault>"]=1;});
        return fault.Errors.Contains("planning_execution_fault") && fault.Labels.Count==1
            && fault.PlanningUnsupported.GetValueOrDefault("<forced-clone-fault>")==1?null:"克隆故障被丢弃";
    }

    private static string? CloneEpisodeChoice(CardDatabase db)
    {
        var vectors=new StateEncoder.CardVecs();var deck=Enumerable.Repeat("card_unit_120mm_m1_gun",39).ToArray();
        foreach(var policy in new[]{OracleChooseOnePolicy.Native,OracleChooseOnePolicy.Last})
        {
            var replay=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:20,chooseOnePolicy:policy);
            var clone=OracleActionEpisodeRunner.RunCore(db,vectors,deck,deck,1,maxRows:20,chooseOnePolicy:policy,clonePlanning:true);
            if(clone.Labels.Count!=20 || JsonSerializer.Serialize(replay.Labels)!=JsonSerializer.Serialize(clone.Labels)
                || replay.Replay.State.SnapshotJson()!=clone.Replay.State.SnapshotJson()
                || !clone.Labels.Any(l=>l.Request.Candidates[l.ChosenIndex].Command is {Kind:MainActionKind.Play,ChooseOne:var n}
                    && n==(policy==OracleChooseOnePolicy.Last?1:0)))return "克隆选择分支不一致";
        }
        return null;
    }
}
