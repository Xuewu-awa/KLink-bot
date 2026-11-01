using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

internal static class ExternalValueEvaluationSelfTest
{
    internal static int Run()
    {
        int checks=0;var failures=new List<string>();
        void Check(bool ok,string why){checks++;if(!ok)failures.Add(why);}
        void Reject(Action action,string why){try{action();Check(false,why);}catch(InvalidDataException){Check(true,why);}catch(ArgumentException){Check(true,why);}}
        var rank=ExternalValueEvaluation.Rank([1,0,1,0,1,0],[1,1,1,1,1,1],[2,2,2,2,2,2],[3,1,0,2,4,4]);
        Check(rank==new ExternalRank(1,1,1),"strict/tie/loss counts");
        Check(rank.StrictAccuracy==1.0/3 && rank.HalfCreditAccuracy==.5,"tie definitions");
        Check(ExternalValueEvaluation.Rank([1,0],[1,2],[1,1],[4,1]).StrictAccuracy is null,"different matchup unavailable");
        Check(ExternalValueEvaluation.Rank([1,0],[1,1],[1,2],[4,1]).Total==0,"different turn unavailable");
        Check(ExternalValueEvaluation.Rank([1,1],[1,1],[1,1],[4,1]).Total==0,"single label unavailable");
        Reject(()=>ExternalValueEvaluation.Rank([1],[1],[1],[float.NaN]),"nonfinite score");
        Reject(()=>ExternalValueEvaluation.Rank([.5f],[1],[1],[0]),"nonbinary label");
        Reject(()=>ExternalValueEvaluation.Rank([1],[],[1],[0]),"length mismatch");
        string dir=Path.Combine(Path.GetTempPath(),"klink-external-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var inputs=new Dictionary<string,string>{["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash()};
            foreach(string k in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"})inputs[k]=new string('a',64);
            QualityReport Write(string path,ulong seed)
            {
                using(var w=new BinaryWriter(File.Create(path)))
                {
                    w.Write(0x324C4B41);w.Write(StateEncoder.Dim);w.Write(2);
                    for(int i=0;i<4;i++)
                    {
                        var x=new float[StateEncoder.Dim];x[0]=.1f;x[1]=i%2==0?1:0;
                        x[StateEncoder.OffPerspective]=i%2==0?1:.5f;
                        foreach(float f in x)w.Write(f);w.Write(i%2==0?1f:0f);w.Write((float)i);w.Write(1f);
                    }
                }
                var q=new QualityReport{RequestedGames=4,AcceptedGames=4,Rows=4,Dim=StateEncoder.Dim,DeckCount=2,DataSha256=TrainingAdmission.Hash(path),
                    SamplingMode="policy-and-choice-before/v1",InputsSha256=new(inputs)};
                for(int i=0;i<4;i++)q.Games.Add(new(){Game=i,Seed=seed+(ulong)i,Accepted=true,WrittenSamples=1,ObservedSamples=1,MainDecisions=1,SampleKinds=[1],SampleActors=[1]});
                File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));return q;
            }
            string train=Path.Combine(dir,"train.bin"),data=Path.Combine(dir,"data.bin"),mp=Path.Combine(dir,"model.bin"),op=Path.Combine(dir,"eval.json");
            var tq=Write(train,10);var q=Write(data,20);
            // Distinct dataset bytes, unchanged intended rank signal.
            var bytes=File.ReadAllBytes(data);BitConverter.GetBytes(.25f).CopyTo(bytes,12+2*4);File.WriteAllBytes(data,bytes);
            q.DataSha256=TrainingAdmission.Hash(data);
            void SaveQ()=>File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));SaveQ();
            var contract=new ValueTrainingContract{Version=1,SamplingMode=q.SamplingMode,ObservationSpec=StateEncoder.Spec,ObservationPerspective="left",ChoicePolicy=q.ChoicePolicy,
                Target="pair",DatasetSha256=tq.DataSha256,SimulatorSha256=inputs["KLink.Bot.dll"],InputsSha256=ValueTrainingContract.SelectInputs(inputs)};
            ExternalValueEvaluation.RequireIndependent(contract,tq,q);Check(true,"independent contracts pass");
            q.Games[0].Seed=10;Reject(()=>ExternalValueEvaluation.RequireIndependent(contract,tq,q),"overlap training seed");q.Games[0].Seed=20;
            q.Games[0].Seed=21;Reject(()=>ExternalValueEvaluation.RequireIndependent(contract,tq,q),"duplicate external seed");q.Games[0].Seed=20;
            q.DataSha256=tq.DataSha256;Reject(()=>ExternalValueEvaluation.RequireIndependent(contract,tq,q),"same dataset");q.DataSha256=TrainingAdmission.Hash(data);
            Reject(()=>ExternalValueEvaluation.RequireIndependent(contract with{Target="win"},tq,q),"wrong target");
            Reject(()=>ExternalValueEvaluation.RequireIndependent(contract with{DatasetSha256=new string('b',64)},tq,q),"wrong training binding");
            q.SamplingMode="turn-end/v1";Reject(()=>ExternalValueEvaluation.RequireIndependent(contract,tq,q),"sampling drift");q.SamplingMode=contract.SamplingMode;
            q.InputsSha256["cards.json"]=new string('b',64);Reject(()=>ExternalValueEvaluation.RequireIndependent(contract,tq,q),"input drift");q.InputsSha256["cards.json"]=inputs["cards.json"];
            q.InputsSha256["KLink.Bot.dll"]=new string('b',64);Reject(()=>ExternalValueEvaluation.RequireIndependent(contract,tq,q),"simulator drift");q.InputsSha256["KLink.Bot.dll"]=inputs["KLink.Bot.dll"];
            SaveQ();
            var w1=new float[StateEncoder.Dim];w1[1]=1;
            new NnModel{Dim=StateEncoder.Dim,Hidden=1,CardDim=StateEncoder.CardDim,Zones=StateEncoder.Zones,PerSide=StateEncoder.PerSide,EncoderSpec=StateEncoder.Spec,
                MetaJson=JsonSerializer.Serialize(new{trainingContract=contract}),Mean=new float[StateEncoder.Dim],Std=Enumerable.Repeat(1f,StateEncoder.Dim).ToArray(),W1=w1,B1=[0],W2=[1]}.Save(mp);
            var opts=new Dictionary<string,string>{{"data",data},{"training-data",train},{"model",mp},{"out",op}};
            var hashes=new[]{data,train,mp}.Select(TrainingAdmission.Hash).ToArray();
            Check(ExternalValueEvaluation.Run(opts)==0,"actual command");
            using(var doc=JsonDocument.Parse(File.ReadAllText(op)))
            {
                var a=doc.RootElement.GetProperty("aggregate");
                Check(a.GetProperty("rows").GetInt32()==4 && a.GetProperty("games").GetInt32()==4,"all admitted games, no split");
                Check(a.GetProperty("model").GetProperty("correct").GetInt32()==2,"known model rank");
                Check(a.GetProperty("hqDifference").GetProperty("correct").GetInt32()==2,"HQ baseline");
                Check(a.GetProperty("constant").GetProperty("ties").GetInt32()==2,"constant ties explicit");
            }
            Check(hashes.SequenceEqual(new[]{data,train,mp}.Select(TrainingAdmission.Hash)),"inputs untouched");
            Reject(()=>ExternalValueEvaluation.Run(opts),"existing output protected");
            foreach(string input in new[]{data,data+".quality.json",train,train+".quality.json",mp})
            {opts["out"]=input;Reject(()=>ExternalValueEvaluation.Run(opts),"input collision");}
            opts["out"]=Path.Combine(dir,"other.json");opts["split-seed"]="1";
            Reject(()=>ExternalValueEvaluation.Run(opts),"split options forbidden");opts.Remove("split-seed");
            q.Games[0].WrittenSamples=2;q.Games[1].WrittenSamples=0;q.Games[1].Accepted=false;q.Games[1].Reasons=["test"];
            q.AcceptedGames=3;q.RejectedGames=1;SaveQ();
            Reject(()=>ExternalValueEvaluation.Run(opts),"unadmitted binary row");
        }
        finally{Directory.Delete(dir,true);}
        foreach(string f in failures)Console.Error.WriteLine(f);
        Console.WriteLine($"External evaluation: {checks} checks passed, {failures.Count} failures");return failures.Count==0?0:1;
    }
}
