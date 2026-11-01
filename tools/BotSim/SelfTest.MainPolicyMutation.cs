using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? MainPolicyMutationBoundary(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        for(int mutation=0;mutation<13;mutation++)
        {
            var (engine,state)=EmptyBoard(db);state.ActiveSide=side;
            var own=state.CreateWithId(PlainUnit,side,70,side.HandOf(),0);
            var hidden=state.CreateWithId(PlainUnit,side.Opposite(),80,side.Opposite().HandOf(),0);
            var vectors=new StateEncoder.CardVecs();
            vectors.ByName[PlainUnit]=Enumerable.Repeat(.25f,StateEncoder.CardDim).ToArray();
            var adapter=new MainActionPolicyAdapter(engine,vectors);
            int turn=state.Turn, log=state.ActionLog.Count;
            string? afterMutation=null;bool threw=false;
            try
            {
                adapter.ChooseAndApply(side,_=>
                {
                    switch(mutation)
                    {
                        case 0:own.Attack++;break;
                        case 1:own.Defense--;break;
                        case 2:state.SetKredits(side,state.Kredits(side)+1);break;
                        case 3:hidden.Defense--;break;
                        case 4:own.BlueprintVariables["policy-test"]=17;break;
                        case 5:state.Remove(hidden);break;
                        case 6:own.ChooseOne++;break;
                        case 7:state.StopFurtherActions=!state.StopFurtherActions;break;
                        case 8:state.StopAttack=!state.StopAttack;break;
                        case 9:vectors.ByName[PlainUnit][0]=.75f;break;
                        case 10:state.Random.Next(100);break;
                        case 11:own.Definition=new CardDefinition{Name=own.Name,ChooseOneOptionCount=2};break;
                        case 12:
                            // A policy must not hide a nested adapter failure and
                            // then get a successful root recorded in the same episode.
                            try{adapter.ChooseAndApply(side,_=>0);}catch(InvalidOperationException){}
                            break;
                    }
                    afterMutation=state.SnapshotJson();
                    return 0; // EndTurn must never execute after the mutation.
                });
            }
            catch(InvalidOperationException){threw=true;}
            if(!threw || adapter.Faults!=(mutation==12?2:1) || adapter.AppliedActions!=0
                || adapter.RejectedActions!=0 || state.Turn!=turn || state.ActiveSide!=side
                || state.ActionLog.Count!=log || state.SnapshotJson()!=afterMutation)
                return $"回调突变未在根动作前拒绝，或擅自回滚 {side}/{mutation}";
        }
        // Pure policies must still produce the exact direct EndTurn outcome.
        foreach(var side in new[]{Side.Left,Side.Right})
        {
            var (direct,ds)=EmptyBoard(db);var (engine,state)=EmptyBoard(db);
            ds.ActiveSide=state.ActiveSide=side;
            var adapter=new MainActionPolicyAdapter(engine,new StateEncoder.CardVecs());
            var result=adapter.ChooseAndApply(side,r=>r.Candidates.Single(o=>o.Command.Kind==MainActionKind.EndTurn).Index);
            direct.EndTurn(side);
            if(!result.Accepted || adapter.AppliedActions!=1 || adapter.Faults!=0 || adapter.RejectedActions!=0
                || ds.SnapshotJson()!=state.SnapshotJson() || ds.Random.State!=state.Random.State
                || JsonSerializer.Serialize(ds.ActionLog)!=JsonSerializer.Serialize(state.ActionLog))
                return $"纯策略轨迹改变 {side}";
        }
        return null;
    }
}
