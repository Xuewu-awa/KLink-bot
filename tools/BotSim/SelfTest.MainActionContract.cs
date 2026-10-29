using System.Text.Json;
using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Server;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? MainCandidatesAreStructural(CardDatabase db)
    {
        foreach(Side side in new[]{Side.Left,Side.Right})
        {
            var (engine,state)=EmptyBoard(db);state.ActiveSide=side;
            state.CreateWithId(PlainUnit,side,70,side.HandOf(),0);
            var choice=state.CreateWithId(PlainUnit,side,72,side.HandOf(),1);
            choice.Definition=new CardDefinition {Name=choice.Name,ChooseOneOptionCount=2};
            var hidden=state.CreateWithId(PlainUnit,side.Opposite(),90,side.Opposite().HandOf(),0);
            state.CreateWithId(PlainUnit,side,71,side.HqOf(),1);
            state.CreateWithId(PlainUnit,side.Opposite(),80,side.Opposite().HqOf(),1);
            var adapter=new MainActionPolicyAdapter(engine,new StateEncoder.CardVecs());
            string before=ActionJournal.Fingerprint(engine);ulong rng=state.Random.State;int programs=engine.Api.Vm.ProgramsExecuted;
            var request=adapter.DescribeCandidates(side);
            if(before!=ActionJournal.Fingerprint(engine) || rng!=state.Random.State || engine.Api.Vm.ProgramsExecuted!=programs)
                return "结构候选枚举调用了效果/随机流";
            if(!request.CoverageGaps.Contains("choose-one/72") || request.Candidates.Any(o=>o.Command.SourceId==72
                || o.Command.SourceId==90 || o.Command.TargetId==90))return "未标记缺口或枚举了隐藏手牌";
            if(request.Candidates.Any(o=>o.Features.Count!=16 || !o.Features.All(float.IsFinite) || o.Features.Take(4).Sum()!=1))
                return "动作特征规格错误";
            hidden.Attack=99;hidden.Defense=99;
            var again=adapter.DescribeCandidates(side);
            if(JsonSerializer.Serialize(request)!=JsonSerializer.Serialize(again))return "隐藏手牌数值泄漏至候选";
            try {((IList<float>)request.Candidates[0].Features)[0]=9;return "特征可变";}catch(NotSupportedException){}
        }
        return null;
    }

    private static string? MainActionRejectionIsExplicit(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);state.ActiveSide=Side.Left;state.SetKredits(Side.Left,0);
        var card=state.CreateWithId(PlainUnit,Side.Left,70,Side.Left.HandOf(),0);
        if(card.KreditCost<=0)return "拒绝测试要求正费用";
        var adapter=new MainActionPolicyAdapter(engine,new StateEncoder.CardVecs());
        var result=adapter.ChooseAndApply(Side.Left,r=>r.Candidates.ToList().FindIndex(o=>o.Command==new MainActionCommand(MainActionKind.Play,70)));
        if(result.Accepted || adapter.RejectedActions!=1 || adapter.AppliedActions!=0 || state.ActiveSide!=Side.Left)
            return "拒绝后自动换候选或伪装成功";
        bool threw=false;try{adapter.ChooseAndApply(Side.Left,_=>int.MaxValue);}catch(InvalidOperationException){threw=true;}
        return threw && adapter.Faults==1 && adapter.RejectedActions==1 ? null : "非法索引未独立报错";
    }

    private static string? MainActionsMirrorGreedy(CardDatabase db)
    {
        string[] deck=Enumerable.Range(0,39).Select(i=>i%2==0?PlainUnit:"card_unit_light_infantry").ToArray();
        var oracle=new MatchEngine(db,deck,deck,20261030);var engine=new MatchEngine(db,deck,deck,20261030);
        var vectors=new StateEncoder.CardVecs();
        using var oc=new ChoicePolicyAdapter(oracle,vectors,r=>r.Options.Count==0?-1:0);
        using var ec=new ChoicePolicyAdapter(engine,vectors,r=>r.Options.Count==0?-1:0);
        oracle.Start();engine.Start();var policy=new GreedyBot();var adapter=new MainActionPolicyAdapter(engine,vectors);
        var seen=new HashSet<MainActionKind>();
        for(int step=0;step<5000 && !oracle.State.IsFinished;step++)
        {
            Side actor=oracle.State.ActiveSide;int start=oracle.State.ActionLog.Count;policy.PlayStep(oracle,actor);
            var root=ActionJournal.Derive(oracle.State,start).First();
            MainActionCommand command=root switch {
                PlayCardAction p=>new(MainActionKind.Play,p.CardId,p.TargetId),
                AttackAction a=>new(MainActionKind.Attack,a.AttackerId,a.DefenderId),
                MoveAction m=>new(MainActionKind.Move,m.UnitId,Slot:m.Slot),
                EndTurnAction=>new(MainActionKind.EndTurn),_=>throw new InvalidOperationException("Unexpected root")};
            seen.Add(command.Kind);
            var result=adapter.ChooseAndApply(actor,r=>r.Candidates.ToList().FindIndex(o=>o.Command==command));
            if(!result.Accepted || engine.State.Random.State!=oracle.State.Random.State)return "根动作应用偏离贪心记录";
        }
        return oracle.State.IsFinished && engine.State.IsFinished && seen.Count==4 && adapter.Faults==0 && adapter.RejectedActions==0
            && JsonSerializer.Serialize(engine.State.ActionLog)==JsonSerializer.Serialize(oracle.State.ActionLog)
            ? null : "四类根动作未覆盖或完整轨迹不一致";
    }

    private static ValueTrainingContract TestValueContract(string sampling="policy-and-choice-before/v1",string target="win")=>new()
    {
        Version=1,SamplingMode=sampling,Target=target,ObservationSpec=StateEncoder.Spec,ObservationPerspective="left",
        ChoicePolicy="first-index/v1",DatasetSha256=new string('0',64),SimulatorSha256=ValueTrainingContract.CurrentSimulatorHash(),InputsSha256=new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"}.ToDictionary(k=>k,_=>new string('0',64))
    };

    private static NnModel TestContractModel(ValueTrainingContract? contract,string? spec=null,int activation=0)=>new()
    {
        Dim=StateEncoder.Dim,Hidden=1,CardDim=StateEncoder.CardDim,Zones=StateEncoder.Zones,PerSide=StateEncoder.PerSide,
        Activation=activation,EncoderSpec=spec??StateEncoder.Spec,MetaJson=JsonSerializer.Serialize(new{trainingContract=contract}),
        Mean=new float[StateEncoder.Dim],Std=Enumerable.Repeat(1f,StateEncoder.Dim).ToArray(),W1=new float[StateEncoder.Dim],B1=[0],W2=[0]
    };

    private static string? ValueModelDistributionChecks(CardDatabase db)
    {
        bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        var contract=TestValueContract();var model=TestContractModel(contract);
        var bound=ValueTrainingContract.Read(model);bound.RequireDataset(contract.SamplingMode,StateEncoder.Spec,"left","first-index/v1",contract.SimulatorSha256,contract.InputsSha256);
        if(!Reject(()=>bound.RequireSampling("turn-end/v1")) || !Reject(()=>ValueTrainingContract.Read(TestContractModel(null))))
            return "缺失/混用模型分布未拒绝";
        var different=new Dictionary<string,string>(contract.InputsSha256);different["cards.json"]=new string('1',64);
        if(!Reject(()=>bound.RequireDataset(contract.SamplingMode,StateEncoder.Spec,"left","first-index/v1",contract.SimulatorSha256,different))
            || !Reject(()=>(bound with {SimulatorSha256=new string('1',64)}).RequireRuntime()))return "输入/模拟器版本不匹配未拒绝";
        _=new NnPolicy(model,new StateEncoder.CardVecs(),Side.Left,true,false);
        if(!Reject(()=>new NnPolicy(model,new StateEncoder.CardVecs(),Side.Left,true,true))
            || !Reject(()=>new NnPolicy(TestContractModel(TestValueContract(target:"pair")),new StateEncoder.CardVecs(),Side.Left,true,false)))
            return "策略消费者未拒绝采样/目标错配";
        return null;
    }

    private static string? ValueModelHeaderChecks(CardDatabase db)
    {
        string path=Path.Combine(Path.GetTempPath(),"klink-model-contract-"+Guid.NewGuid().ToString("N")+".bin");
        try
        {
            var good=TestContractModel(TestValueContract());good.Save(path);ValueTrainingContract.Read(NnModel.Load(path)).Validate();
            foreach(var bad in new[]{TestContractModel(TestValueContract(),"wrong-spec"),TestContractModel(TestValueContract(),activation:1)})
            {
                bad.Save(path);bool rejected=false;try{NnModel.Load(path);}catch(FormatException){rejected=true;}
                if(!rejected)return "相同维度的错误规格/激活函数模型被加载";
            }
        }
        finally{File.Delete(path);}
        return null;
    }
}
