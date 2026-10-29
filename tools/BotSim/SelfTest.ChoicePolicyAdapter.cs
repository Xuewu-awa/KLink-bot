using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? PolicyChoiceHand(CardDatabase db)
    {
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (bool cancel in new[] { false, true })
        {
            var (engine,state)=EmptyBoard(db);state.ActiveSide=side.Opposite();
            var source=state.CreateWithId(PlainUnit,side,60,side.HqOf(),1);
            state.CreateWithId(PlainUnit,side,80,side.HandOf(),0);
            var target=state.CreateWithId(PlainUnit,side,81,side.HandOf(),1);
            state.CreateWithId(PlainUnit,side.Opposite(),90,side.Opposite().HandOf(),0);
            PolicyChoiceRequest? seen=null;
            using (var adapter=new ChoicePolicyAdapter(engine,new StateEncoder.CardVecs(),r=>{seen=r;return cancel?-1:1;}))
            {
                engine.Api.InvokeByName("selectTargetFromHand",source,new object?[] {60},
                    new EffectContext {Engine=engine,State=state,Self=source,Controller=side},out _);
                if (seen is null || seen.Actor!=side || seen.Kind!=PolicyChoiceKind.HandTarget
                    || !seen.CanCancel || !seen.Options.Select(o=>o.CardId).SequenceEqual(new[] {80,81})
                    || engine.LastHandTarget?.ChosenCardId!=(cancel?0:81) || engine.PendingSelectionCount!=0
                    || adapter.CompletedChoices!=1 || adapter.Faults!=0) return "手牌索引/取消/行动方边界错误";
                int attack=seen.Options[1].Attack;target.BaseAttack+=7;
                if (seen.Options[1].Attack!=attack || seen.Observation.Count!=StateEncoder.Dim
                    || seen.ContractVersion!=1 || seen.ObservationSpec!=StateEncoder.Spec) return "候选快照或观察契约错误";
                try { ((IList<PolicyCardOption>)seen.Options)[0]=seen.Options[1];return "候选列表可变"; }
                catch (NotSupportedException) { }
            }
            if (engine.ChooseSpawnCard is not null || engine.PickHandTargetId is not null) return "释放适配器未移除自有回调";
        }
        return null;
    }

    private static string? PolicyChoiceOffered(CardDatabase db)
    {
        foreach (Side side in new[] {Side.Left,Side.Right})
        foreach (int route in new[] {0,1,2})
        {
            var (engine,state)=EmptyBoard(db);
            var source=state.CreateWithId(route==2?"card_event_baker_street_irregulars":"card_unit_51e_regiment",side,60,CardLocation.Discard,0);
            state.CreateWithId(PlainUnit,side,80,side.DeckOf(),0);
            var second=state.CreateWithId(PlainUnit,side,81,side.DeckOf(),1);
            PolicyChoiceRequest? seen=null;
            using var adapter=new ChoicePolicyAdapter(engine,new StateEncoder.CardVecs(),r=>{seen=r;return 1;});
            engine.Api.InvokeByName(route==1?"Forecast":"selectCardToDraw",source,
                route==1?new object?[] {null}:new object?[] {60,route==0,false},
                new EffectContext {Engine=engine,State=state,Self=source,Controller=side},out bool handled);
            if (!handled || seen is null || seen.Kind!=PolicyChoiceKind.OfferedCard || seen.CanCancel
                || adapter.CompletedChoices!=1 || adapter.Faults!=0 || engine.PendingSelectionCount!=0)
                return "候选选择回调未经过适配器";
            if (route!=0 ? !state.Hand(side).Any(c=>c.Name==seen.Options[1].Name) : second.Location!=side.HandOf())
                return "索引1未落实到引擎候选（包含重复同名牌）";
        }
        return null;
    }

    private static string? PolicyChoiceInvalidAndOwnership(CardDatabase db)
    {
        foreach (int answer in new[] {-2,-1,3})
        {
            var (engine,state)=EmptyBoard(db);var source=state.CreateWithId(PlainUnit,Side.Left,60,Side.Left.HqOf(),0);
            using var adapter=new ChoicePolicyAdapter(engine,new StateEncoder.CardVecs(),_=>answer);
            bool threw=false;
            try { engine.ChooseSpawnCard!(source,new[] {source}); }
            catch (InvalidOperationException) {threw=true;}
            if (!threw || adapter.CompletedChoices!=0 || adapter.Faults!=1) return "非法候选索引未报错或错误被掩盖";
        }
        {
            var (engine,state)=EmptyBoard(db);var source=state.CreateWithId(PlainUnit,Side.Left,60,Side.Left.HqOf(),0);
            using var adapter=new ChoicePolicyAdapter(engine,new StateEncoder.CardVecs(),_=>-1);
            engine.Api.InvokeByName("selectTargetFromHand",source,new object?[] {60},
                new EffectContext {Engine=engine,State=state,Self=source,Controller=Side.Left},out _);
            if (engine.LastHandTarget?.ChosenCardId!=0 || adapter.CompletedChoices!=1 || engine.PendingSelectionCount!=0)
                return "空手牌显式ID0回复未结算";
        }
        {
            var (engine,state)=EmptyBoard(db);
            Func<CardInstance,IReadOnlyList<CardInstance>,int?> existing=(_,_)=>0;
            engine.PickHandTargetId=existing;bool rejected=false;
            try { using var bad=new ChoicePolicyAdapter(engine,new StateEncoder.CardVecs(),_=>0); }
            catch (InvalidOperationException) {rejected=true;}
            if (!rejected || engine.PickHandTargetId!=existing || engine.ChooseSpawnCard is not null) return "已有回调被覆盖";
            engine.PickHandTargetId=null;
            var adapter=new ChoicePolicyAdapter(engine,new StateEncoder.CardVecs(),_=>0);
            engine.PickHandTargetId=existing;adapter.Dispose();adapter.Dispose();
            if (engine.PickHandTargetId!=existing || engine.ChooseSpawnCard is not null) return "释放覆盖了后续所有者";
        }
        return null;
    }

    private static string? PolicyChoiceHiddenObservation(CardDatabase db)
    {
        foreach (Side side in new[] {Side.Left,Side.Right})
        {
            var (engine,state)=EmptyBoard(db);var source=state.CreateWithId(PlainUnit,side,60,side.HqOf(),0);
            var hidden=state.CreateWithId(PlainUnit,side.Opposite(),80,side.Opposite().HandOf(),0);
            var ownDeck=state.CreateWithId(PlainUnit,side,81,side.DeckOf(),0);
            var otherDeck=state.CreateWithId(PlainUnit,side.Opposite(),82,side.Opposite().DeckOf(),0);
            var ownHand=state.CreateWithId(PlainUnit,side,83,side.HandOf(),0);
            const string alternate="card_unit_queens_own";
            var vectors=new StateEncoder.CardVecs();vectors.ByName[PlainUnit]=Enumerable.Repeat(1f,StateEncoder.CardDim).ToArray();
            vectors.ByName[alternate]=Enumerable.Repeat(9f,StateEncoder.CardDim).ToArray();
            var requests=new List<PolicyChoiceRequest>();
            using var adapter=new ChoicePolicyAdapter(engine,vectors,r=>{requests.Add(r);return 0;});
            void Offer()=>engine.ChooseSpawnCard!(source,new[] {source});
            Offer();
            foreach (var card in new[] {hidden,ownDeck,otherDeck}) card.Reidentify(alternate,db.Find(alternate)!);
            Offer();ownHand.Reidentify(alternate,db.Find(alternate)!);Offer();
            if (!requests[0].Observation.SequenceEqual(requests[1].Observation)
                || requests[1].Observation.SequenceEqual(requests[2].Observation)) return "隐藏身份泄漏或己方手牌正向对照失效";
            if (!requests.Select(r=>r.Sequence).SequenceEqual(new long[] {1,2,3})) return "选择序号不稳定";
        }
        return null;
    }
}
