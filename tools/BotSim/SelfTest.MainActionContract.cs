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
            var branches=request.Candidates.Where(o=>o.Command.SourceId==72).ToArray();
            if(request.CoverageGaps.Any(g=>g.StartsWith("choose-one/")) || branches.Length!=2*(1+state.AllCards.Count(c=>c.Location.IsBoard()))
                || branches.Select(o=>o.Features.Last()).Distinct().Single()!=2
                || branches.Select(o=>o.Features[^2]).Distinct().Order().SequenceEqual(new float[]{1,2})==false
                || request.Candidates.Any(o=>o.Command.SourceId==90 || o.Command.TargetId==90))return "分支候选缺失或枚举了隐藏手牌";
            if(request.Candidates.Any(o=>o.Features.Count!=18 || !o.Features.All(float.IsFinite) || o.Features.Take(4).Sum()!=1))
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

    private static string? MainActionHiddenInputs(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        {
            var (engine,state)=EmptyBoard(db);state.ActiveSide=side;
            var own=state.CreateWithId(PlainUnit,side,70,side.HandOf(),0);
            var hidden=state.CreateWithId(PlainUnit,side.Opposite(),80,side.Opposite().HandOf(),0);
            var leftDeck=state.CreateWithId(PlainUnit,side,90,side.DeckOf(),0);
            var rightDeck=state.CreateWithId(PlainUnit,side.Opposite(),91,side.Opposite().DeckOf(),0);
            var vectors=new StateEncoder.CardVecs();
            vectors.ByName[PlainUnit]=Enumerable.Repeat(0.25f,StateEncoder.CardDim).ToArray();
            vectors.ByName["card_unit_light_infantry"]=Enumerable.Repeat(0.75f,StateEncoder.CardDim).ToArray();
            var adapter=new MainActionPolicyAdapter(engine,vectors);
            string before=JsonSerializer.Serialize(adapter.DescribeCandidates(side));
            foreach(var card in new[]{hidden,leftDeck,rightDeck})
            {
                card.Definition=db.All.Single(c=>c.Name=="card_unit_light_infantry");card.Attack=99;card.Defense=98;
                card.ChooseOne=1;card.CardSeen=true;
            }
            if(before!=JsonSerializer.Serialize(adapter.DescribeCandidates(side)))return $"隐藏手牌/双方牌库身份或数值泄漏 {side}";
            own.Definition=db.All.Single(c=>c.Name=="card_unit_light_infantry");
            if(before==JsonSerializer.Serialize(adapter.DescribeCandidates(side)))return "非零卡向量观测测试未观察到自己手牌变化";
        }
        return null;
    }

    private static string? MainChooseOneBranches(CardDatabase db)
    {
        const string research="card_event_advanced_royal_research";
        foreach(Side side in new[]{Side.Left,Side.Right})
        foreach(int branch in new[]{0,1})
        {
            var (direct,ds)=EmptyBoard(db);var (applied,st)=EmptyBoard(db);
            ds.ActiveSide=st.ActiveSide=side;ds.SetKredits(side,12);st.SetKredits(side,12);
            var a=ds.CreateWithId(research,side,70,side.HandOf(),0);
            var b=st.CreateWithId(research,side,70,side.HandOf(),0);
            a.BlueprintVariables["ChooseOne"]=b.BlueprintVariables["ChooseOne"]=77;
            a.ChooseOne=branch;b.ChooseOne=1-branch;
            var command=new MainActionCommand(MainActionKind.Play,70,ChooseOne:branch);
            var adapter=new MainActionPolicyAdapter(applied,new StateEncoder.CardVecs());
            if(!direct.PlayCard(a) || !adapter.ChooseAndApply(side,r=>r.Candidates.ToList().FindIndex(o=>o.Command==command)).Accepted)
                return $"分支执行拒绝 {side}/{branch}";
            string expected=branch==0?"card_event_active_sonar":"card_event_bletchley_park";
            if(!st.Hand(side).Any(c=>c.Name==expected) || b.ChooseOne!=branch || !Equals(b.BlueprintVariables["ChooseOne"],77)
                || ds.SnapshotJson()!=st.SnapshotJson() || JsonSerializer.Serialize(ds.ActionLog)!=JsonSerializer.Serialize(st.ActionLog)
                || ds.Random.State!=st.Random.State)return $"分支结果或独立字段/轨迹不一致 {side}/{branch}";
        }
        foreach(int branch in new[]{-2,-1,2,int.MaxValue})
        {
            var (engine,state)=EmptyBoard(db);state.ActiveSide=Side.Left;
            var source=state.CreateWithId(research,Side.Left,70,Side.Left.HandOf(),0);
            source.ChooseOne=1;string before=state.SnapshotJson();
            if(MainActionPolicyAdapter.ApplyRoot(engine,Side.Left,new(MainActionKind.Play,70,ChooseOne:branch))
                || before!=state.SnapshotJson())return "非法分支未在根调用前拒绝";
        }
        // Prefix rebuilds must retain the explicit native member even where the
        // Blueprint never calls WhichChooseOne (the M1 gun reads it in damage).
        var deck=Enumerable.Repeat("card_unit_120mm_m1_gun",39).ToArray();
        var result=OracleActionEpisodeRunner.RunCore(db,new StateEncoder.CardVecs(),deck,deck,1,maxRows:20);
        if(result.Labels.Count!=20 || result.Errors.Any(e=>e!="episode_buffer_limit" && e!="unfinished")
            || !result.Labels.Any(l=>l.Request.Candidates[l.ChosenIndex].Command is {Kind:MainActionKind.Play,ChooseOne:0}))
            return "选择卡教师/前缀重建失败："+string.Join(',',result.Errors);
        int probes=0;
        var last=OracleActionEpisodeRunner.RunCore(db,new StateEncoder.CardVecs(),deck,deck,1,maxRows:20,
            chooseOnePolicy:OracleChooseOnePolicy.Last,
            afterProbe:(probe,step)=>
            {
                probes++;
                // Unplayed hand choices on the probe must not contaminate execution engines.
                foreach(var card in probe.State.Hand(probe.State.ActiveSide))
                    if(card.Definition.ChooseOneOptionCount>0)card.ChooseOne=12345;
            });
        var again=OracleActionEpisodeRunner.RunCore(db,new StateEncoder.CardVecs(),deck,deck,1,maxRows:20,
            chooseOnePolicy:OracleChooseOnePolicy.Last);
        if(last.Labels.Count!=20 || probes!=20 || last.Errors.Any(e=>e!="episode_buffer_limit" && e!="unfinished")
            || !last.Labels.Any(l=>l.Request.Candidates[l.ChosenIndex].Command is {Kind:MainActionKind.Play,ChooseOne:1})
            || last.Labels.Any(l=>l.Request.Candidates[l.ChosenIndex].Command is {Kind:MainActionKind.Play,ChooseOne:0})
            || JsonSerializer.Serialize(last.Labels)!=JsonSerializer.Serialize(again.Labels)
            || last.Replay.State.SnapshotJson()!=again.Replay.State.SnapshotJson())
            return "固定末项分支/探测隔离/重复性失败："+string.Join(',',last.Errors);
        bool badPolicy=false;
        try{OracleActionEpisodeRunner.Run(db,new StateEncoder.CardVecs(),deck,deck,1,(OracleChooseOnePolicy)99);}
        catch(ArgumentOutOfRangeException){badPolicy=true;}
        if(!badPolicy)return "未知教师选择策略未拒绝";
        return null;
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
