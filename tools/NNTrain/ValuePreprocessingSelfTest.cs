using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

// Synthetic execution tests, not game/card correctness evidence.
internal static class ValuePreprocessingSelfTest
{
    internal static int Run()
    {
        int checks=0;
        void Assert(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
        string dir=Path.Combine(Path.GetTempPath(),"klink-value-split-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string data=Path.Combine(dir,"data.bin"),modelPath=Path.Combine(dir,"model.bin");
        TextWriter output=Console.Out;
        string Quiet(Func<int> operation)
        {
            using var capture=new StringWriter();
            try{Console.SetOut(capture);Assert(operation()==0,"trainer/verify exit");return capture.ToString();}
            finally{Console.SetOut(output);}
        }
        void Save(int heldout=-1,int games=4,bool decision=false)
        {
            int dim=StateEncoder.Dim;
            using(var writer=new BinaryWriter(File.Create(data)))
            {
                writer.Write(0x324C4B41);writer.Write(dim);writer.Write(2);
                for(int game=0;game<games;game++)for(int row=0;row<2;row++)
                {
                    var x=new float[dim];x[0]=.1f;x[1]=game==heldout ? -1001-row*2 : game*100+1+row*2;x[2]=7;
                    foreach(float value in x)writer.Write(value);
                    writer.Write(game==heldout ? 1f-game%2 : (float)(game%2));writer.Write((float)game);writer.Write(1f);
                }
            }
            var q=new QualityReport{RequestedGames=games,AcceptedGames=games,Rows=games*2,Dim=dim,DeckCount=2,
                DataSha256=TrainingAdmission.Hash(data),SamplingMode=decision?"policy-and-choice-before/v1":"turn-end/v1",
                InputsSha256=new(){["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash()}};
            foreach(string key in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"})q.InputsSha256[key]=new string('a',64);
            for(int game=0;game<games;game++)q.Games.Add(new QualityGame{Game=game,Accepted=true,ObservedSamples=2,WrittenSamples=2,
                SampleKinds=decision?[1,1]:[0,0],SampleActors=[1,1],MainDecisions=decision?2:0});
            File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));
        }
        try
        {
            foreach(int seed in new[]{7,19})foreach(string target in new[]{"win","pair"})
            {
                bool decision=seed==19;
                var opts=new Dictionary<string,string>{{"data",data},{"out",modelPath},{"model",modelPath},
                    {"epochs","2"},{"hidden","2"},{"split-seed",seed.ToString()},{"target",target}};
                Save(decision:decision);Quiet(()=>Program.Train(opts));var first=NnModel.Load(modelPath);
                int heldout;
                using(var meta=JsonDocument.Parse(first.MetaJson))
                {
                    var root=meta.RootElement;
                    var train=root.GetProperty("trainingGameIds").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
                    var val=root.GetProperty("validationGameIds").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
                    Assert(train.Length==3 && val.Length==1 && !train.Intersect(val).Any()
                        && train.Concat(val).Order().SequenceEqual(new float[]{0,1,2,3}),"disjoint game metadata");
                    heldout=(int)val.Single();
                    var values=train.SelectMany(g=>new[]{g*100+1,g*100+3}).ToArray();
                    float mean=values.Sum()/values.Length;
                    float std=MathF.Sqrt(values.Sum(x=>(x-mean)*(x-mean))/values.Length)+1e-6f;
                    Assert(first.Mean[1]==mean,"held-out observations leaked into saved normalization mean");
                    Assert(Math.Abs(first.Std[1]-std)<1e-5f,"normalization variance must use training rows only");
                    Assert(first.Mean[2]==7 && first.Std[2]==1e-6f,"constant training feature scaling");
                    Assert(root.GetProperty("normalizationSpec").GetString()=="training-split-only/v1","normalization metadata");
                    Assert(root.GetProperty("processedTrainingUnits").GetInt64()==(target=="win"?12:6),"normal fixture must execute rows/pairs");
                    string verification=Quiet(()=>Program.Verify(opts));
                    double accuracy=root.GetProperty("valAccuracy").GetDouble();
                    Assert(verification.Contains(target=="win"?$"验证集准确率 {accuracy:P2}":$"验证集 {accuracy:P2}"),"saved-model verification must reproduce validation metric");
                }
                // Only validation observations/labels change. Training parameters must be identical.
                Save(heldout,decision:decision);Quiet(()=>Program.Train(opts));var second=NnModel.Load(modelPath);
                Assert(first.Mean.SequenceEqual(second.Mean) && first.Std.SequenceEqual(second.Std),"validation perturbation changed fitted scaler");
                Assert(first.W1.SequenceEqual(second.W1) && first.W2.SequenceEqual(second.W2)
                    && first.B1.SequenceEqual(second.B1) && first.B2==second.B2,"validation perturbation changed trained weights");
            }
            Save(games:1,decision:true);
            var single=new Dictionary<string,string>{{"data",data},{"out",modelPath},{"model",modelPath},{"epochs","1"},{"hidden","2"},{"split-seed","7"}};
            foreach(bool verify in new[]{false,true})
            {
                bool rejected=false;
                try{Quiet(()=>verify?Program.Verify(single):Program.Train(single));}
                catch(InvalidDataException error){rejected=error.Message.Contains("two admitted games");}
                Assert(rejected,"single-game train/verify must fail before empty training split");
            }
        }
        finally
        {
            Console.SetOut(output);
            foreach(string file in new[]{data,data+".quality.json",modelPath})if(File.Exists(file))File.Delete(file);
            Directory.Delete(dir);
        }
        Console.WriteLine($"Value preprocessing: {checks} checks passed (synthetic data; no strength claim)");return 0;
    }
}
