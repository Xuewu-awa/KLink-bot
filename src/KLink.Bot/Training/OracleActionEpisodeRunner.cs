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
    public long PlanningPrefixRoots {get;init;}
    public int PlanningDecisions {get;init;}
    public long PlanningVmFaults {get;init;}
    public long PlanningChoiceFaults {get;init;}
    public int PlanningPendingChoices {get;init;}
    public IReadOnlyDictionary<string,int> PlanningUnsupported {get;init;}=new Dictionary<string,int>();
    public IReadOnlyDictionary<string,int> PlanningCalls {get;init;}=new Dictionary<string,int>();
}

public enum OracleChooseOnePolicy { Native, Last }

/// <summary>
/// Collects labels on two independently initialized execution engines. Greedy
/// probes run only on a disposable third engine rebuilt from seed and root history.
/// Observable trace/RNG/candidate checks are bounded software checks, not a full
/// private-state equivalence proof. The host must admit/reject the entire episode.
/// </summary>
public static class OracleActionEpisodeRunner
{
    public const string TeacherContract = "greedy-isolated-prefix/v3+choose-one-explicit/v1+first-index/v1";
    public const string LastChoiceTeacherContract = "greedy-isolated-prefix/v3+choose-one-last/v1+first-index/v1";
    public static string ContractFor(OracleChooseOnePolicy policy) => policy switch
    {
        OracleChooseOnePolicy.Native => TeacherContract,
        OracleChooseOnePolicy.Last => LastChoiceTeacherContract,
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };
    public const int MaxPlanningPrefixRoots = 250000;
    public const int MaxRows = 5000;
    public const int MaxCandidatesPerRow = 4096;
    public const int MaxOptionsPerEpisode = 200000;

    public static OracleActionEpisodeResult Run(CardDatabase database, StateEncoder.CardVecs vectors,
        IReadOnlyList<string> left, IReadOnlyList<string> right, ulong seed,
        OracleChooseOnePolicy chooseOnePolicy=OracleChooseOnePolicy.Native)
        => RunCore(database,vectors,left,right,seed,chooseOnePolicy:chooseOnePolicy);

    // Test seams stay internal: production callers cannot mutate a collecting episode.
    internal static OracleActionEpisodeResult RunCore(CardDatabase database, StateEncoder.CardVecs vectors,
        IReadOnlyList<string> left, IReadOnlyList<string> right, ulong seed,
        int maxRows=MaxRows,int maxCandidates=MaxCandidatesPerRow,int maxOptions=MaxOptionsPerEpisode,
        Func<MainActionRequest,MainActionRequest>? offer=null,
        Action<MatchEngine,MatchEngine,int>? afterRoot=null,
        Func<MainActionRequest,int,int>? select=null,
        int maxPlanningPrefixRoots=MaxPlanningPrefixRoots,
        Action<MatchEngine,int>? beforeProbe=null,
        Action<MatchEngine,int>? afterProbe=null,
        OracleChooseOnePolicy chooseOnePolicy=OracleChooseOnePolicy.Native)
    {
        _=ContractFor(chooseOnePolicy);
        var teacher=new MatchEngine(database,left,right,seed);var replay=new MatchEngine(database,left,right,seed);
        var labels=new List<OracleActionLabel>();var errors=new List<string>();
        var main=new MainActionPolicyAdapter(replay,vectors);var teacherView=new MainActionPolicyAdapter(teacher,vectors);
        ChoicePolicyAdapter? teacherChoice=null,replayChoice=null;
        int compared=0,options=0,planningDecisions=0,planningPending=0;
        long prefixRoots=0,planningVmFaults=0,planningChoiceFaults=0;
        var planningUnsupported=new Dictionary<string,int>();var planningCalls=new Dictionary<string,int>();
        string? snapshotDifference=null;
        bool SameRequest(MainActionRequest a,MainActionRequest b)=>a.Actor==b.Actor && a.Turn==b.Turn
            && a.Observation.SequenceEqual(b.Observation) && a.CoverageGaps.SequenceEqual(b.CoverageGaps)
            && a.Candidates.Count==b.Candidates.Count && a.Candidates.Zip(b.Candidates).All(p=>p.First.Command==p.Second.Command
                && p.First.Features.SequenceEqual(p.Second.Features));
        bool SameClock(MatchEngine a,MatchEngine b)=>a.State.Turn==b.State.Turn && a.State.ActiveSide==b.State.ActiveSide
            && a.State.IsFinished==b.State.IsFinished && a.State.Winner==b.State.Winner
            && a.State.Random.State==b.State.Random.State && a.State.Random.ConsumedCount==b.State.Random.ConsumedCount;
        bool SameFullTrace(MatchEngine a,MatchEngine b)=>JsonSerializer.Serialize(a.State.ActionLog)==JsonSerializer.Serialize(b.State.ActionLog);
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
            if(!SameClock(teacher,replay) || !SameTrace(0,0) || !SameSnapshot())errors.Add("initial_trace_mismatch");

            for(int turnCall=0;turnCall<500 && !teacher.State.IsFinished && errors.Count==0;turnCall++)
            {
                Side actor=teacher.State.ActiveSide;
                for(int step=0;step<200 && !teacher.State.IsFinished && errors.Count==0;step++)
                {
                    if(teacher.State.ActiveSide!=actor)break;
                    if(teacher.PendingSelectionCount!=0 || replay.PendingSelectionCount!=0){errors.Add("pending_choice");break;}
                    var live=main.DescribeCandidates(actor);
                    if(!SameClock(teacher,replay) || !SameSnapshot() || !SameRequest(live,teacherView.DescribeCandidates(actor)))
                    {errors.Add("predecision_mismatch");break;}
                    var request=offer is null?live:offer(live);
                    if(labels.Count>=maxRows || request.Candidates.Count>maxCandidates
                        || options+request.Candidates.Count>maxOptions){errors.Add("episode_buffer_limit");break;}
                    if(!request.Observation.All(float.IsFinite) || request.Candidates.Any(c=>!c.Features.All(float.IsFinite)))
                    {errors.Add("nonfinite_action_observation");break;}
                    int t=teacher.State.ActionLog.Count,r=replay.State.ActionLog.Count;
                    if(prefixRoots+labels.Count>maxPlanningPrefixRoots){errors.Add("planning_work_limit");break;}
                    MainActionCommand? command=null;
                    // Never copy a partial snapshot back into an execution engine. Rebuild all
                    // engine/VM private state by executing the confirmed root prefix instead.
                    var probe=new MatchEngine(database,left,right,seed);
                    using(var probeChoice=new ChoicePolicyAdapter(probe,vectors,First))
                    {
                        try
                        {
                            probe.Start();
                            foreach(var label in labels)
                            {
                                prefixRoots++;
                                if(!MainActionPolicyAdapter.ApplyRoot(probe,label.Request.Actor,label.Request.Candidates[label.ChosenIndex].Command))
                                {errors.Add("planning_prefix_rejected");break;}
                            }
                            beforeProbe?.Invoke(probe,compared);
                            if(errors.Count==0 && (!SameClock(teacher,probe) || !SameFullTrace(teacher,probe)
                                || teacher.State.SnapshotJson()!=probe.State.SnapshotJson()
                                || !SameRequest(live,new MainActionPolicyAdapter(probe,vectors).DescribeCandidates(actor))))
                                errors.Add("planning_prefix_mismatch");
                            if(errors.Count==0)
                            {
                                // Select before CanPlay/target queries, only on the disposable probe.
                                if(chooseOnePolicy==OracleChooseOnePolicy.Last)
                                    foreach(var card in probe.State.Hand(actor))
                                        if(card.Definition.ChooseOneOptionCount>0)
                                            card.ChooseOne=card.Definition.ChooseOneOptionCount-1;
                                var bot=new GreedyBot();planningDecisions++;
                                bot.PlayStep(probe,actor);command=bot.LastStepAction;
                                afterProbe?.Invoke(probe,compared);
                            }
                        }
                        finally
                        {
                            // Probe health matters even though its state is discarded. Prefix
                            // replays repeat diagnostics, so these are work counters, not live events.
                            planningVmFaults+=probe.Api.Vm.FaultedPrograms;planningChoiceFaults+=probeChoice.Faults;
                            planningPending=Math.Max(planningPending,probe.PendingSelectionCount);
                            void Merge(Dictionary<string,int> into,IReadOnlyDictionary<string,int> from)
                            {foreach(var pair in from)into[pair.Key]=into.GetValueOrDefault(pair.Key)+pair.Value;}
                            Merge(planningUnsupported,probe.Api.Vm.UnsupportedOps);Merge(planningCalls,probe.UnimplementedCalls);
                        }
                    }
                    if(planningVmFaults+planningChoiceFaults!=0 || planningPending!=0 || planningUnsupported.Any(p=>p.Value>0))
                        errors.Add("planning_execution_fault");
                    if(errors.Count!=0)break;
                    if(command is null){errors.Add("oracle_no_root");break;}
                    if(chooseOnePolicy==OracleChooseOnePolicy.Last && command.Kind==MainActionKind.Play)
                    {
                        int branches=teacher.State.ById(command.SourceId)?.Definition.ChooseOneOptionCount??0;
                        if(branches>0 && command.ChooseOne!=branches-1){errors.Add("teacher_choice_policy_mismatch");break;}
                    }
                    int index=-1,matches=0;
                    for(int i=0;i<request.Candidates.Count;i++)if(request.Candidates[i].Command==command){index=i;matches++;}
                    if(matches!=1)
                    {
                        errors.Add(matches==0?"uncovered_oracle_action":"ambiguous_oracle_action");
                        if(command.Kind==MainActionKind.Play && request.CoverageGaps.Contains($"choose-one/{command.SourceId}"))
                            errors.Add("uncovered_choose_one");
                        break;
                    }
                    var teacherApplied=teacherView.ChooseAndApply(actor,current=>
                    {
                        if(!SameRequest(live,current))throw new InvalidOperationException("Teacher request changed during planning.");
                        return current.Candidates.ToList().FindIndex(o=>o.Command==command);
                    });
                    if(!teacherApplied.Accepted){errors.Add("teacher_action_rejected");break;}
                    var applied=main.ChooseAndApply(actor,current=>
                    {
                        if(!SameRequest(request,current))throw new InvalidOperationException("Live request changed during teacher step.");
                        return select is null?index:select(current,index);
                    });
                    if(!applied.Accepted){errors.Add("main_action_rejected");break;}
                    afterRoot?.Invoke(teacher,replay,compared);
                    if(!SameClock(teacher,replay) || !SameTrace(t,r)){errors.Add("root_trace_mismatch");break;}
                    if(!SameSnapshot()){errors.Add("root_snapshot_mismatch");break;}
                    compared++;options+=request.Candidates.Count;
                    labels.Add(new(labels.Count,request,index));
                    if(teacherChoice.Faults+replayChoice.Faults+main.Faults+teacherView.Faults!=0 || teacher.Api.Vm.FaultedPrograms+replay.Api.Vm.FaultedPrograms!=0)
                    {errors.Add("execution_fault");break;}
                    if(command.Kind==MainActionKind.EndTurn)break;
                }
            }
            if(!teacher.State.IsFinished || !replay.State.IsFinished)errors.Add("unfinished");
            if(teacher.PendingSelectionCount+replay.PendingSelectionCount!=0)errors.Add("pending_choice");
            if(labels.Count==0)errors.Add("no_action_rows");
        }
        catch(Exception error)when(error is not OutOfMemoryException){errors.Add("driver_exception:"+error.GetType().Name);}
        finally{teacherChoice?.Dispose();replayChoice?.Dispose();}
        return new(teacher,replay,labels.AsReadOnly(),errors.Distinct(StringComparer.Ordinal).ToArray(),
            (teacherChoice?.Faults??0)+(replayChoice?.Faults??0),main.Faults+teacherView.Faults,
            main.RejectedActions+teacherView.RejectedActions,main.AppliedActions,compared)
            {SnapshotDifference=snapshotDifference,PlanningPrefixRoots=prefixRoots,PlanningDecisions=planningDecisions,
             PlanningVmFaults=planningVmFaults,PlanningChoiceFaults=planningChoiceFaults,PlanningPendingChoices=planningPending,
             PlanningUnsupported=planningUnsupported,PlanningCalls=planningCalls};
    }
}
