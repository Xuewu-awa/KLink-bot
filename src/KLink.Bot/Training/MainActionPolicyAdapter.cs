using KLink.Bot.Engine;
using KLink.Bot.NN;

namespace KLink.Bot.Training;

public enum MainActionKind { EndTurn, Play, Attack, Move }
public sealed record MainActionCommand(MainActionKind Kind, int SourceId = 0, int TargetId = 0, int Slot = -1);
public sealed record MainActionOption(int Index, MainActionCommand Command, IReadOnlyList<float> Features);
public sealed record MainActionRequest(Side Actor, int Turn, IReadOnlyList<MainActionOption> Candidates,
    IReadOnlyList<float> Observation, IReadOnlyList<string> CoverageGaps)
{
    public int ContractVersion => 1;
    public string ObservationSpec => StateEncoder.Spec;
    public string ActionSpec => MainActionFeatures.Spec;
}
public sealed record MainActionResult(MainActionCommand Command, bool Accepted, long VmFaults, int PendingChoices);

public static class MainActionFeatures
{
    public const int Dim = 16;
    public const string Spec = "main-action/v1;dim=16;play,attack,move,end;sourceAtk20,sourceDef20,kredit12,operation12;targetAtk20,targetDef20,targetRelation,targetHQ;slotByCapacity,sourceBoard,sourceHand,sourceOrder";

    internal static IReadOnlyList<float> Encode(GameState state, Side actor, MainActionCommand command)
    {
        var source=state.ById(command.SourceId);var target=state.ById(command.TargetId);var f=new float[Dim];
        f[command.Kind switch {MainActionKind.Play=>0,MainActionKind.Attack=>1,MainActionKind.Move=>2,_=>3}]=1;
        if(source is not null)
        {
            f[4]=source.Attack/20f;f[5]=source.Defense/20f;f[6]=source.KreditCost/12f;f[7]=source.OperationCost/12f;
            f[13]=source.Location.IsBoard()?1:0;f[14]=source.Location==actor.HandOf()?1:0;f[15]=source.Definition.IsOrder?1:0;
        }
        if(target is not null)
        {
            f[8]=target.Attack/20f;f[9]=target.Defense/20f;f[10]=target.Owner==actor?1:-1;f[11]=target.IsHq?1:0;
        }
        if(command.Kind==MainActionKind.Move)f[12]=command.Slot/(float)Math.Max(1,state.FrontlineCapacity-1);
        return Array.AsReadOnly(f);
    }
}

/// <summary>
/// A structural candidate superset, never a legal mask. Enumeration calls no
/// engine predicates. One synchronous pure policy answer executes at most one
/// root engine call. A rejected call is not rolled back: hosts must discard
/// the episode if RejectedActions/Faults increase, rather than retry blindly.
/// </summary>
public sealed class MainActionPolicyAdapter
{
    private readonly MatchEngine _engine;
    private readonly StateEncoder.CardVecs _vectors;
    private bool _choosing;
    public long AppliedActions {get;private set;}
    public long RejectedActions {get;private set;}
    public long Faults {get;private set;}

    public MainActionPolicyAdapter(MatchEngine engine, StateEncoder.CardVecs vectors)
    {
        ArgumentNullException.ThrowIfNull(engine);ArgumentNullException.ThrowIfNull(vectors);
        _engine=engine;_vectors=vectors;
    }

    private void RequireReady(Side actor)
    {
        if(actor is not (Side.Left or Side.Right) || _engine.State.IsFinished
            || _engine.State.ActiveSide!=actor || _engine.PendingSelectionCount!=0)
            throw new InvalidOperationException("Main actions require the active player and no pending choice.");
    }

    public MainActionRequest DescribeCandidates(Side actor)
    {
        RequireReady(actor);var state=_engine.State;
        var commands=new List<MainActionCommand> {new(MainActionKind.EndTurn)};
        var gaps=new List<string> {"legal-mask/unverified","play-placement/default-only","play-targets/board-only"};
        var board=state.AllCards.Where(c=>c.Location.IsBoard()).OrderBy(c=>c.CardId).ToArray();
        foreach(var card in state.Hand(actor).OrderBy(c=>c.CardId))
        {
            if(card.Definition.ChooseOneOptionCount>0){gaps.Add($"choose-one/{card.CardId}");continue;}
            commands.Add(new(MainActionKind.Play,card.CardId));
            foreach(var target in board)commands.Add(new(MainActionKind.Play,card.CardId,target.CardId));
        }
        foreach(var unit in state.Board(actor).OrderBy(c=>c.CardId))
        {
            foreach(var target in board.Where(c=>c.Owner!=actor))commands.Add(new(MainActionKind.Attack,unit.CardId,target.CardId));
            for(int slot=0;slot<state.FrontlineCapacity;slot++)commands.Add(new(MainActionKind.Move,unit.CardId,Slot:slot));
        }
        var options=commands.Select((command,index)=>new MainActionOption(index,command,MainActionFeatures.Encode(state,actor,command))).ToArray();
        return new(actor,state.Turn,Array.AsReadOnly(options),Array.AsReadOnly(StateEncoder.Encode(state,_vectors,actor)),gaps.AsReadOnly());
    }

    public MainActionResult ChooseAndApply(Side actor, Func<MainActionRequest,int> choose)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(choose);
            if(_choosing)throw new InvalidOperationException("Main policy cannot reenter the adapter.");
            _choosing=true;
            try
            {
                var request=DescribeCandidates(actor);var state=_engine.State;
                int actions=state.ActionLog.Count;ulong random=state.Random.State;
                int index=choose(request);RequireReady(actor);
                if(state.Turn!=request.Turn || state.ActionLog.Count!=actions || state.Random.State!=random)
                    throw new InvalidOperationException("Policy changed the engine during a synchronous decision.");
                if(index<0 || index>=request.Candidates.Count)throw new InvalidOperationException("Main policy index outside offer.");
                var command=request.Candidates[index].Command;
                bool accepted=ApplyRoot(_engine,actor,command);
                if(accepted)AppliedActions++;else RejectedActions++;
                return new(command,accepted,_engine.Api.Vm.FaultedPrograms,_engine.PendingSelectionCount);
            }
            finally{_choosing=false;}
        }
        catch{Faults++;throw;}
    }
    // Shared root dispatch for live execution and deterministic prefix rebuilds.
    // No candidate probes, retry or rollback are performed here.
    internal static bool ApplyRoot(MatchEngine engine,Side actor,MainActionCommand command)
    {
        if(engine.State.IsFinished || engine.State.ActiveSide!=actor || engine.PendingSelectionCount!=0)return false;
        if(command.Kind==MainActionKind.EndTurn){engine.EndTurn(actor);return true;}
        var source=engine.State.ById(command.SourceId);var target=engine.State.ById(command.TargetId);
        return source is not null && (command.Kind switch
        {
            MainActionKind.Play=>engine.PlayCard(source,target),
            MainActionKind.Attack=>target is not null && engine.Attack(source,target),
            MainActionKind.Move=>engine.MoveUnit(source,command.Slot),
            _=>false
        });
    }

}
