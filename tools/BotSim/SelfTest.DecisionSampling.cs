using System.Text.Json;
using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? GreedySingleStepBoundary(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);state.Turn=4;state.ActiveSide=Side.Left;state.SetKredits(Side.Left,20);
        var card=state.CreateWithId(PlainUnit,Side.Left,70,Side.Left.HandOf(),0);var bot=new GreedyBot();
        int count=state.ActionLog.Count;ulong random=state.Random.State;
        if(bot.PlayStep(engine,Side.Right)!=GreedyStepResult.NotActive || state.ActionLog.Count!=count || state.Random.State!=random)
            return "非行动方步骤产生了副作用";
        if(bot.PlayStep(engine,Side.Left)!=GreedyStepResult.ActionApplied || !card.Location.IsBoard()
            || state.ActionLog.Count(a=>a.ActionType=="XActionPlayCardFromHand")!=1
            || state.ActionLog.Any(a=>a.ActionType=="XActionEndOfTurn"))return "单步边界执行了多次主动作";
        engine.PickHandTargetId=(_,_)=>null;
        engine.Api.InvokeByName("selectTargetFromHand",card,new object?[] {70},
            new EffectContext {Engine=engine,State=state,Self=card,Controller=Side.Left},out _);
        count=state.ActionLog.Count;random=state.Random.State;
        if(engine.PendingSelectionCount!=1 || bot.PlayStep(engine,Side.Left)!=GreedyStepResult.PendingSelection
            || state.ActionLog.Count!=count || state.Random.State!=random)return "待选时仍执行了主动作";
        state.Finish(Side.Left,"test");
        return bot.PlayStep(engine,Side.Left)==GreedyStepResult.Finished ? null : "终局仍可驱动";
    }

    private static string? DecisionSamplingPreservesEpisode(CardDatabase db)
    {
        var vectors=new StateEncoder.CardVecs();string[] deck=Enumerable.Repeat(PlainUnit,39).ToArray();
        var turn=new MatchEngine(db,deck,deck,20261030);var decision=new MatchEngine(db,deck,deck,20261030);
        var sparse=new MatchEngine(db,deck,deck,20261030);
        var a=GreedyEpisodeRunner.Run(turn,vectors,EpisodeSampling.Turn);
        var b=GreedyEpisodeRunner.Run(decision,vectors,EpisodeSampling.Decision);
        var c=GreedyEpisodeRunner.Run(sparse,vectors,EpisodeSampling.Decision,2);
        if(!turn.State.IsFinished || !decision.State.IsFinished || a.DriverFaults+b.DriverFaults+c.DriverFaults!=0
            || JsonSerializer.Serialize(turn.State.ActionLog)!=JsonSerializer.Serialize(decision.State.ActionLog)
            || JsonSerializer.Serialize(turn.State.ActionLog)!=JsonSerializer.Serialize(sparse.State.ActionLog)
            || turn.State.Random.State!=decision.State.Random.State || turn.State.Winner!=decision.State.Winner)
            return "采样模式改变了对局";
        if(b.Samples.Count<=a.Samples.Count || b.Samples.Any(s=>s.Kind!=EpisodeSampleKind.MainDecision)
            || b.MainDecisions!=b.Samples.Count || c.Samples.Count!=(b.Samples.Count+1)/2
            || !c.Samples.Select(s=>JsonSerializer.Serialize(s)).SequenceEqual(b.Samples.Where((_,i)=>i%2==0).Select(s=>JsonSerializer.Serialize(s))))
            return "逐决策时机或every抽样不一致";
        return null;
    }

    private static string? DecisionRunnerChoiceFault(CardDatabase db)
    {
        var left=Enumerable.Repeat("card_event_baker_street_irregulars",39).ToArray();
        var right=Enumerable.Repeat(PlainUnit,39).ToArray();
        var engine=new MatchEngine(db,left,right,20261030);
        var result=GreedyEpisodeRunner.Run(engine,new StateEncoder.CardVecs(),EpisodeSampling.Decision,
            choose:_=>int.MaxValue);
        if(result.ChoiceFaults==0 || result.ChoiceDecisions==0 || !result.Samples.Any(s=>s.Kind==EpisodeSampleKind.Choice))
            return "非法选牌未保留独立错误计数和选择边界";
        return engine.ChooseSpawnCard is null && engine.PickHandTargetId is null ? null : "驱动退出未释放回调";
    }
}
