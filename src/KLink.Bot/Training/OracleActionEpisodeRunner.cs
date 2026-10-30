using System.Text.Json;
using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;

namespace KLink.Bot.Training;

public sealed record OracleActionLabel(int Step, MainActionRequest Request, int ChosenIndex);
public sealed record OracleActionEpisodeResult(MatchEngine Teacher, MatchEngine Replay,
    IReadOnlyList<OracleActionLabel> Labels, IReadOnlyList<string> Errors, long ChoiceFaults,
    long MainFaults, long MainRejections, long AppliedActions, int ComparedSteps)
{
    public string? SnapshotDifference {get;init;}
}

/// <summary>
/// Collects greedy main-action labels using two independently initialized engines.
/// Observable trace/RNG/candidate checks are bounded software checks, not a full
/// private-state equivalence proof. The host must admit/reject the entire episode.
/// </summary>
public static class OracleActionEpisodeRunner
{
    public const int MaxRows = 5000;
    public const int MaxCandidatesPerRow = 4096;
    public const int MaxOptionsPerEpisode = 200000;

    public static OracleActionEpisodeResult Run(CardDatabase database, StateEncoder.CardVecs vectors,
        IReadOnlyList<string> left, IReadOnlyList<string> right, ulong seed)
        => RunCore(database,vectors,left,right,seed);

    // Test seams stay internal: production callers cannot mutate a collecting episode.
    internal static OracleActionEpisodeResult RunCore(CardDatabase database, StateEncoder.CardVecs vectors,
        IReadOnlyList<string> left, IReadOnlyList<string> right, ulong seed,
        int maxRows=MaxRows,int maxCandidates=MaxCandidatesPerRow,int maxOptions=MaxOptionsPerEpisode,
        Func<MainActionRequest,MainActionRequest>? offer=null,
        Action<MatchEngine,MatchEngine,int>? afterRoot=null,
        Func<MainActionRequest,int,int>? select=null)
    {
        var teacher=new MatchEngine(database,left,right,seed);var replay=new MatchEngine(database,left,right,seed);
        var labels=new List<OracleActionLabel>();var errors=new List<string>();
        var main=new MainActionPolicyAdapter(replay,vectors);var teacherView=new MainActionPolicyAdapter(teacher,vectors);
        ChoicePolicyAdapter? teacherChoice=null,replayChoice=null;
        int compared=0,options=0;string? snapshotDifference=null;
        bool SameRequest(MainActionRequest a,MainActionRequest b)=>a.Actor==b.Actor && a.Turn==b.Turn
            && a.Observation.SequenceEqual(b.Observation) && a.CoverageGaps.SequenceEqual(b.CoverageGaps)
            && a.Candidates.Count==b.Candidates.Count && a.Candidates.Zip(b.Candidates).All(p=>p.First.Command==p.Second.Command
                && p.First.Features.SequenceEqual(p.Second.Features));
        bool SameClock()=>teacher.State.Turn==replay.State.Turn && teacher.State.ActiveSide==replay.State.ActiveSide
            && teacher.State.IsFinished==replay.State.IsFinished && teacher.State.Winner==replay.State.Winner
            && teacher.State.Random.State==replay.State.Random.State;
        bool SameTrace(int t,int r)=>JsonSerializer.SerializeToUtf8Bytes(teacher.State.ActionLog.Skip(t).ToArray())
            .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(replay.State.ActionLog.Skip(r).ToArray()));
        // This covers serialized match/card fields (including hidden cards), not
        // all engine/VM private state, delegates or future queued execution.
        bool SameSnapshot()
        {
            string a=teacher.State.SnapshotJson(),b=replay.State.SnapshotJson();
            if(a==b)return true;
            int i=0;while(i<Math.Min(a.Length,b.Length) && a[i]==b[i])i++;
            int start=Math.Max(0,i-80);
            snapshotDifference??=$"offset={i};teacher={a.Substring(start,Math.Min(200,a.Length-start))};replay={b.Substring(start,Math.Min(200,b.Length-start))}";
            return false;
        }
        try
        {
            static int First(PolicyChoiceRequest request)=>request.Options.Count==0 && request.CanCancel?-1:0;
            teacherChoice=new(teacher,vectors,First);replayChoice=new(replay,vectors,First);
            teacher.Start();replay.Start();
            if(!SameClock() || !SameTrace(0,0) || !SameSnapshot())errors.Add("initial_trace_mismatch");
            var bot=new GreedyBot();
            for(int turnCall=0;turnCall<500 && !teacher.State.IsFinished && errors.Count==0;turnCall++)
            {
                Side actor=teacher.State.ActiveSide;
                for(int step=0;step<200 && !teacher.State.IsFinished && errors.Count==0;step++)
                {
                    if(teacher.State.ActiveSide!=actor)break;
                    if(teacher.PendingSelectionCount!=0 || replay.PendingSelectionCount!=0){errors.Add("pending_choice");break;}
                    var live=main.DescribeCandidates(actor);
                    if(!SameClock() || !SameSnapshot() || !SameRequest(live,teacherView.DescribeCandidates(actor)))
                    {errors.Add("predecision_mismatch");break;}
                    var request=offer is null?live:offer(live);
                    if(labels.Count>=maxRows || request.Candidates.Count>maxCandidates
                        || options+request.Candidates.Count>maxOptions){errors.Add("episode_buffer_limit");break;}
                    if(!request.Observation.All(float.IsFinite) || request.Candidates.Any(c=>!c.Features.All(float.IsFinite)))
                    {errors.Add("nonfinite_action_observation");break;}
                    int t=teacher.State.ActionLog.Count,r=replay.State.ActionLog.Count;
                    var result=bot.PlayStep(teacher,actor);var command=bot.LastStepAction;
                    if(command is null){errors.Add("oracle_no_root");break;}
                    int index=-1,matches=0;
                    for(int i=0;i<request.Candidates.Count;i++)if(request.Candidates[i].Command==command){index=i;matches++;}
                    if(matches!=1)
                    {
                        errors.Add(matches==0?"uncovered_oracle_action":"ambiguous_oracle_action");
                        if(command.Kind==MainActionKind.Play && request.CoverageGaps.Contains($"choose-one/{command.SourceId}"))
                            errors.Add("uncovered_choose_one");
                        break;
                    }
                    var applied=main.ChooseAndApply(actor,current=>
                    {
                        if(!SameRequest(request,current))throw new InvalidOperationException("Live request changed during teacher step.");
                        return select is null?index:select(current,index);
                    });
                    if(!applied.Accepted){errors.Add("main_action_rejected");break;}
                    afterRoot?.Invoke(teacher,replay,compared);
                    if(!SameClock() || !SameTrace(t,r)){errors.Add("root_trace_mismatch");break;}
                    if(!SameSnapshot()){errors.Add("root_snapshot_mismatch");break;}
                    compared++;options+=request.Candidates.Count;
                    labels.Add(new(labels.Count,request,index));
                    if(teacherChoice.Faults+replayChoice.Faults+main.Faults!=0 || teacher.Api.Vm.FaultedPrograms+replay.Api.Vm.FaultedPrograms!=0)
                    {errors.Add("execution_fault");break;}
                    if(result!=GreedyStepResult.ActionApplied)break;
                }
            }
            if(!teacher.State.IsFinished || !replay.State.IsFinished)errors.Add("unfinished");
            if(teacher.PendingSelectionCount+replay.PendingSelectionCount!=0)errors.Add("pending_choice");
            if(labels.Count==0)errors.Add("no_action_rows");
        }
        catch(Exception error)when(error is not OutOfMemoryException){errors.Add("driver_exception:"+error.GetType().Name);}
        finally{teacherChoice?.Dispose();replayChoice?.Dispose();}
        return new(teacher,replay,labels.AsReadOnly(),errors.Distinct(StringComparer.Ordinal).ToArray(),
            (teacherChoice?.Faults??0)+(replayChoice?.Faults??0),main.Faults,main.RejectedActions,main.AppliedActions,compared)
            {SnapshotDifference=snapshotDifference};
    }
}
