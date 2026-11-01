using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? PolicyChoiceMutationBoundary(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        foreach(bool hand in new[]{false,true})
        for(int mutation=0;mutation<20;mutation++)
        {
            var (engine,state)=EmptyBoard(db);state.ActiveSide=side.Opposite();
            var source=state.CreateWithId(PlainUnit,side,60,side.HqOf(),0);
            var own=state.CreateWithId(PlainUnit,side,70,side.HandOf(),0);
            var hidden=state.CreateWithId(PlainUnit,side.Opposite(),80,side.Opposite().HandOf(),0);
            // Real offered templates need not be registered battle objects.
            var offered=new CardInstance{CardId=900,Name=PlainUnit,Definition=db.Find(PlainUnit)!,Owner=side};
            var offers=new List<CardInstance>{own,offered};
            var vectors=new StateEncoder.CardVecs();vectors.ByName[PlainUnit]=Enumerable.Repeat(.25f,StateEncoder.CardDim).ToArray();
            ChoicePolicyAdapter? adapter=null;string? after=null;int invocations=0;
            using(adapter=new ChoicePolicyAdapter(engine,vectors,_=>
            {
                invocations++;
                switch(mutation)
                {
                    case 0:own.Attack++;break;
                    case 1:state.SetKredits(side,state.Kredits(side)+1);break;
                    case 2:hidden.Defense--;break;
                    case 3:own.BlueprintVariables["choice-test"]=17;break;
                    case 4:state.Remove(own);break;
                    case 5:own.ChooseOne++;break;
                    case 6:state.StopFurtherActions=!state.StopFurtherActions;break;
                    case 7:state.Random.Next(100);break;
                    case 8:offers.Reverse();break;
                    case 9:offers.Clear();break;
                    case 10:offered.Defense--;break;
                    case 11:offered.ChooseOne++;break;
                    case 12:source.CurrentTarget=hidden;break;
                    case 13:vectors.ByName[PlainUnit][0]=.75f;break;
                    case 14:state.Finish(side,"policy-mutated");break;
                    case 15:engine.ChooseSpawnCard=(_,_)=>source;break;
                    case 16:engine.PickHandTargetId=(_,_)=>0;break;
                    case 17:
                        try{engine.ChooseSpawnCard!(source,offers);}catch(InvalidOperationException){}
                        break;
                    case 18:adapter!.Dispose();break;
                    case 19:offers[1]=new CardInstance{CardId=offered.CardId,Name=offered.Name,Definition=offered.Definition,Owner=side};break;
                }
                after=state.SnapshotJson();return 1;
            }))
            {
                bool threw=false;
                try
                {
                    if(hand)_=engine.PickHandTargetId!(source,offers);
                    else _=engine.ChooseSpawnCard!(source,offers);
                }
                catch(ObjectDisposedException){threw=mutation==18;}
                catch(InvalidOperationException){threw=true;}
                if(!threw || adapter.CompletedChoices!=0 || adapter.Faults!=(mutation==17?2:1)
                    || invocations!=1 || state.SnapshotJson()!=after)
                    return $"选择突变未拒绝或被回滚 {side}/{hand}/{mutation}";
                // Disposal preserves a replacement handler installed by policy.
                adapter.Dispose();
                if(mutation==15 && engine.ChooseSpawnCard is null || mutation==16 && engine.PickHandTargetId is null)
                    return "故障后的释放覆盖了后续回调所有者";
            }
        }
        foreach(var side in new[]{Side.Left,Side.Right})
        foreach(bool hand in new[]{false,true})
        foreach(bool cancel in new[]{false,true})
        {
            if(cancel && !hand)continue;
            var (engine,state)=EmptyBoard(db);state.ActiveSide=side.Opposite();
            var source=state.CreateWithId(PlainUnit,side,60,side.HqOf(),0);
            var a=new CardInstance{CardId=900,Name=source.Name,Definition=source.Definition,Owner=side};
            var b=new CardInstance{CardId=901,Name=source.Name,Definition=source.Definition,Owner=side};
            var offers=new[]{a,b};string before=state.SnapshotJson();ulong rng=state.Random.State;
            using var adapter=new ChoicePolicyAdapter(engine,new StateEncoder.CardVecs(),_=>cancel?-1:1);
            for(int i=0;i<2;i++)
            {
                if(hand ? engine.PickHandTargetId!(source,offers)!=(cancel?0:901)
                    : !ReferenceEquals(engine.ChooseSpawnCard!(source,offers),b))return "纯选择返回对象/ID变化";
            }
            if(adapter.CompletedChoices!=2 || adapter.Faults!=0 || state.SnapshotJson()!=before || state.Random.State!=rng)
                return "纯选择计数或状态变化";
        }
        return null;
    }
}
