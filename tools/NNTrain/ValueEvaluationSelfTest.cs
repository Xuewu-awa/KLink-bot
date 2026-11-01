using System.Text.Json;
using System.Text.Json.Nodes;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

// Exercises the actual trainer/verifier with synthetic data, not game-rule evidence.
internal static class ValueEvaluationSelfTest
{
    internal static int Run()
    {
        int checks=0;
        void Assert(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
        string dir=Path.Combine(Path.GetTempPath(),"klink-value-eval-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string data=Path.Combine(dir,"data.bin"),modelPath=Path.Combine(dir,"model.bin");
        var output=Console.Out;
        string Quiet(Func<int> operation)
        {
            using var capture=new StringWriter();
            try{Console.SetOut(capture);Assert(operation()==0,"trainer/verifier exit");return capture.ToString();}
            finally{Console.SetOut(output);}
        }
        void Reject(Func<int> operation,string message,string why)
        {
            bool rejected=false;
            try{Quiet(operation);}catch(InvalidDataException error){rejected=error.Message.Contains(message);}
            Assert(rejected,why);
        }
        void Save(bool decision,bool changed=false)
        {
            using(var writer=new BinaryWriter(File.Create(data)))
            {
                writer.Write(0x324C4B41);writer.Write(StateEncoder.Dim);writer.Write(2);
                for(int game=0;game<4;game++)for(int row=0;row<2;row++)
                {
                    var x=new float[StateEncoder.Dim];x[0]=.1f;x[1]=game*10+row+(changed?100:0);
                    foreach(float value in x)writer.Write(value);
                    writer.Write((float)(game%2));writer.Write((float)game);writer.Write(1f);
                }
            }
            var q=new QualityReport{RequestedGames=4,AcceptedGames=4,Rows=8,Dim=StateEncoder.Dim,DeckCount=2,
                DataSha256=TrainingAdmission.Hash(data),SamplingMode=decision?"policy-and-choice-before/v1":"turn-end/v1",
                InputsSha256=new(){["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash()}};
            foreach(string key in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"})q.InputsSha256[key]=new string('a',64);
            for(int game=0;game<4;game++)q.Games.Add(new QualityGame{Game=game,Accepted=true,ObservedSamples=2,WrittenSamples=2,
                SampleKinds=decision?[1,1]:[0,0],SampleActors=[1,1],MainDecisions=decision?2:0});
            File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));
        }
        void SaveMetadata(NnModel original,JsonObject meta)
        {
            new NnModel{Dim=original.Dim,Hidden=original.Hidden,CardDim=original.CardDim,Zones=original.Zones,
                PerSide=original.PerSide,Activation=original.Activation,EncoderSpec=original.EncoderSpec,
                MetaJson=meta.ToJsonString(),Mean=original.Mean,Std=original.Std,W1=original.W1,B1=original.B1,W2=original.W2,B2=original.B2}.Save(modelPath);
        }
        try
        {
            foreach(int seed in new[]{7,-1})foreach(bool decision in new[]{false,true})foreach(string target in new[]{"win","pair"})
            {
                Save(decision);
                var opts=new Dictionary<string,string>{{"data",data},{"out",modelPath},{"model",modelPath},
                    {"epochs","2"},{"hidden","2"},{"split-seed",seed.ToString()},{"target",target}};
                Quiet(()=>Program.Train(opts));var model=NnModel.Load(modelPath);
                var original=JsonNode.Parse(model.MetaJson)!.AsObject();
                var wrong=new Dictionary<string,string>(opts){["split-seed"]="999"};
                Reject(()=>Program.Verify(wrong),"split-seed conflicts","conflicting verification seed was accepted");
                var invalid=new Dictionary<string,string>(opts){["split-seed"]="invalid"};
                Reject(()=>Program.Verify(invalid),"evaluation split-seed","malformed seed option was accepted");
                string exact=Quiet(()=>Program.Verify(opts));
                var omitted=new Dictionary<string,string>(opts);omitted.Remove("split-seed");
                for(int repeat=0;repeat<3;repeat++)Assert(Quiet(()=>Program.Verify(omitted))==exact,"omitted seed changed saved evaluation split");
                string ids=string.Join(",",original["validationGameIds"]!.AsArray().Select(x=>x!.GetValue<float>()));
                Assert(exact.Contains("验证对局 ID: "+ids),"verifier did not report saved validation games");
                double? accuracy=original["valAccuracy"]?.GetValue<double>();
                Assert(exact.Contains(target=="win"?$"验证集准确率 {accuracy:P2}":"验证集 不可评估") && (target=="win" || accuracy is null),"saved metric differs from exact held-out verification");
                foreach(string mutation in new[]{"missing","null","empty","overlap","duplicate","unknown","omitted-game","type","nonfinite","seed-missing","seed-type","fractional"})
                {
                    var meta=(JsonObject)original.DeepClone();
                    var train=meta["trainingGameIds"]!.AsArray();var val=meta["validationGameIds"]!.AsArray();
                    switch(mutation)
                    {
                        case "missing":meta.Remove("validationGameIds");break;
                        case "null":meta["trainingGameIds"]=null;break;
                        case "empty":val.Clear();break;
                        case "overlap":val.Add(train[0]!.DeepClone());break;
                        case "duplicate":train.Add(train[0]!.DeepClone());break;
                        case "unknown":val[0]=99f;break;
                        case "omitted-game":train.RemoveAt(0);break;
                        case "type":meta["validationGameIds"]="invalid";break;
                        case "nonfinite":val[0]=JsonNode.Parse("1e100");break;
                        case "seed-missing":meta.Remove("splitSeed");break;
                        case "seed-type":meta["splitSeed"]="7";break;
                        case "fractional":val[0]=.5f;break;
                    }
                    SaveMetadata(model,meta);
                    Reject(()=>Program.Verify(omitted),"evaluation split",mutation+" metadata was accepted");
                }
                model.Save(modelPath);
                Save(decision,changed:true);
                Reject(()=>Program.Verify(omitted),"bound training dataset","different admitted dataset was accepted as original holdout");
            }
        }
        finally
        {
            Console.SetOut(output);
            foreach(string f in new[]{data,data+".quality.json",modelPath})if(File.Exists(f))File.Delete(f);
            Directory.Delete(dir);
        }
        Console.WriteLine($"Value evaluation: {checks} checks passed (synthetic data; no strength claim)");return 0;
    }
}
