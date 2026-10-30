using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

internal static class ActionModelSelfTest
{
    internal static int Run()
    {
        int checks=0;
        void Assert(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks++;}
        bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        var inputs=new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json","KLink.Bot.dll","NNTrain.dll"}
            .ToDictionary(k=>k,_=>new string('0',64));
        inputs["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash();
        var contract=new MainActionTrainingContract {Teacher=OracleActionEpisodeRunner.TeacherContract,
            DatasetSha256=new string('0',64),InputsSha256=inputs,TrainingGames=[0,2],ValidationGames=[1],SplitSeed=7};
        var model=MainActionModel.Create(contract,3,1);
        var observation=new float[StateEncoder.Dim];observation[0]=0.7f;observation[3]=0.5f;
        IReadOnlyList<IReadOnlyList<float>> actions=[new float[18],Enumerable.Repeat(0.3f,18).ToArray(),Enumerable.Repeat(-0.2f,18).ToArray()];
        var p=model.Probabilities(observation,actions);
        Assert(p.All(double.IsFinite) && Math.Abs(p.Sum()-1)<1e-12,"finite normalized ragged softmax");
        Assert(p.SequenceEqual(MainActionModel.Create(contract,3,1).Probabilities(observation,actions)),"seeded initialization repeats");
        var request=new MainActionRequest(Engine.Side.Left,1,
            actions.Select((a,i)=>new MainActionOption(i,new(MainActionKind.Play,i+1),a)).ToArray(),
            observation,new[]{"legal-mask/unverified"});
        Assert(p.SequenceEqual(model.Probabilities(request)),"request and offline scorer agree");
        var renamed=request with {Actor=Engine.Side.Right,Turn=999,Candidates=request.Candidates
            .Select(o=>o with {Index=o.Index+17,Command=new(MainActionKind.Attack,900+o.Index,800)}).ToArray()};
        Assert(p.SequenceEqual(model.Probabilities(renamed)),"IDs turn side and index excluded from network input");
        Assert(Reject(()=>model.Probabilities(request with {CoverageGaps=Array.Empty<string>()})),"unmarked candidate semantics rejected");
        // Check independent central differences for context, action, bias and output gradients.
        foreach(var (name,weights,index) in new[]{("context",model.ContextWeights,0),("action",model.ActionWeights,0),
            ("bias",model.Bias,0),("output",model.OutputWeights,0)})
        {
            double original=weights[index],epsilon=1e-5;
            weights[index]=original+epsilon;double plus=model.LossAndUpdate(observation,actions,1,0);
            weights[index]=original-epsilon;double minus=model.LossAndUpdate(observation,actions,1,0);weights[index]=original;
            var copy=MainActionModel.Create(contract,3,1);copy.LossAndUpdate(observation,actions,1,0.001);
            double updated=name switch {"context"=>copy.ContextWeights[index],"action"=>copy.ActionWeights[index],"bias"=>copy.Bias[index],_=>copy.OutputWeights[index]};
            Assert(Math.Abs((original-updated)/0.001-(plus-minus)/(2*epsilon))<1e-7,"finite difference "+name);
        }
        double before=model.LossAndUpdate(observation,actions,1,0);
        for(int i=0;i<100;i++)model.LossAndUpdate(observation,actions,1,0.03);
        Assert(model.LossAndUpdate(observation,actions,1,0)<before,"SGD lowers controlled cross entropy");
        var permuted=model.Probabilities(observation,new[]{actions[2],actions[0],actions[1]});
        p=model.Probabilities(observation,actions);
        Assert(Math.Abs(p[0]-permuted[1])<1e-12 && Math.Abs(p[1]-permuted[2])<1e-12,"candidate permutation equivariance");
        var tied=model.Probabilities(observation,new[]{actions[0],actions[0]});
        Assert(tied[0]==0.5 && tied[1]==0.5,"identical features remain indistinguishable");
        var altered=(float[])observation.Clone();altered[0]=10;
        Assert(!p.SequenceEqual(model.Probabilities(altered,actions)),"context affects relative probabilities");
        var split=ActionModelTool.Split(new[]{0,1,2,3,4,5,6,7,8,9},7);
        Assert(split.Train.Intersect(split.Validation).Count()==0 && split.Train.Length==8 && split.Validation.Length==2
            && split.Train.SequenceEqual(ActionModelTool.Split(Enumerable.Range(0,10).Reverse(),7).Train),"stable game-disjoint split");
        Assert(Reject(()=>ActionModelTool.Split(new[]{0},1)),"single-game split rejected");
        contract.RequireInputs(contract.Teacher,inputs);Assert(true,"bound runtime inputs accepted");
        Assert(Reject(()=>(contract with {ValidationGames=[2]}).Validate()),"overlapping game splits rejected");
        Assert(Reject(()=>(contract with {Teacher=OracleActionEpisodeRunner.LastChoiceTeacherContract}).RequireInputs(contract.Teacher,inputs)),"teacher mixing rejected");
        var changed=new Dictionary<string,string>(inputs);changed["card-vectors.json"]=new string('1',64);
        Assert(Reject(()=>contract.RequireInputs(contract.Teacher,changed)),"vector source mismatch rejected");
        changed=new Dictionary<string,string>(inputs);changed["KLink.Bot.dll"]=new string('1',64);
        Assert(Reject(()=>(contract with {InputsSha256=changed}).RequireInputs(contract.Teacher,changed)),"stale simulator rejected");
        Assert(Reject(()=>(contract with {ActionSpec="other"}).Validate()),"same-width wrong spec rejected");
        var bad=(float[])observation.Clone();bad[1]=float.NaN;
        Assert(Reject(()=>model.Probabilities(bad,actions)),"nonfinite observation rejected");
        Assert(Reject(()=>model.Probabilities(observation,new[]{(IReadOnlyList<float>)new float[17]})),"wrong action width rejected");
        Assert(Reject(()=>model.Probabilities(observation,Array.Empty<IReadOnlyList<float>>())),"empty candidates rejected");
        string file=Path.Combine(Path.GetTempPath(),"klink-action-model-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            model.Save(file);var loaded=MainActionModel.Load(file);
            Assert(p.SequenceEqual(loaded.Probabilities(observation,actions)),"save/load scores match exactly");
            loaded.ContextWeights[0]=double.NaN;Assert(Reject(()=>loaded.Save(file)),"nonfinite weights rejected");
            string valid=File.ReadAllText(file);File.WriteAllText(file,valid.Replace(MainActionModel.Format,"wrong-format"));
            Assert(Reject(()=>MainActionModel.Load(file)),"wrong model format rejected");
            File.WriteAllText(file,"{}");Assert(Reject(()=>MainActionModel.Load(file)),"missing model contract rejected");
        }
        finally{File.Delete(file);}
        Console.WriteLine($"Action model: {checks} checks passed");return 0;
    }
}
