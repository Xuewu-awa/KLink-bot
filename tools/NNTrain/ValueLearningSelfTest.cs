using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

// Synthetic trainer execution contracts; no game-rule or strength evidence.
internal static class ValueLearningSelfTest
{
    internal static int Run()
    {
        int checks=0;
        var failures=new List<string>();
        void Assert(bool ok,string why){checks++;if(!ok)failures.Add(why);}
        string dir=Path.Combine(Path.GetTempPath(),"klink-value-learning-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string data=Path.Combine(dir,"data.bin"),model=Path.Combine(dir,"model.bin");
        TextWriter output=Console.Out;
        int Quiet(Func<int> action)
        {
            using var capture=new StringWriter();
            try{Console.SetOut(capture);return action();}
            finally{Console.SetOut(output);}
        }
        void Save(string kind="normal",int games=4)
        {
            int dim=StateEncoder.Dim;
            using(var w=new BinaryWriter(File.Create(data)))
            {
                w.Write(0x324C4B41);w.Write(dim);w.Write(2);
                for(int game=0;game<games;game++)for(int row=0;row<2;row++)
                {
                    float label=kind=="one-label"?1:game%2;
                    var x=new float[dim];x[0]=kind=="separate-turns"?(label==1?.1f:.7f):.1f;
                    x[1]=game*10+row;x[2]=7;
                    foreach(float v in x)w.Write(v);
                    w.Write(label);w.Write((float)game);
                    // Fixed sparse fixture: training rows 0 and 2 share a cell;
                    // the first seeded epoch samples other anchors only.
                    float pair=kind=="sparse"?(row==0 && game<2?1:2+game*2+row):kind=="separate-pairs"?label:1f;
                    w.Write(pair);
                }
            }
            var q=new QualityReport{RequestedGames=games,AcceptedGames=games,Rows=games*2,Dim=dim,DeckCount=2,
                DataSha256=TrainingAdmission.Hash(data),SamplingMode="turn-end/v1",
                InputsSha256=new(){["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash()}};
            foreach(string key in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"})q.InputsSha256[key]=new string('a',64);
            for(int game=0;game<games;game++)q.Games.Add(new QualityGame{Game=game,Accepted=true,ObservedSamples=2,WrittenSamples=2,
                SampleKinds=[0,0],SampleActors=[1,1]});
            File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));
        }
        Dictionary<string,string> Options(string target="pair")=>new(){["data"]=data,["out"]=model,["model"]=model,
            ["epochs"]="2",["hidden"]="2",["target"]=target,["split-seed"]="7"};
        try
        {
            foreach(string kind in new[]{"epochs-zero","epochs-negative","hidden-zero","hidden-negative","one-label","separate-pairs","separate-turns","holdout-only-opposite","sparse"})
            {
                Save(kind,kind=="holdout-only-opposite"?2:4);
                var opts=Options();
                if(kind.StartsWith("epochs-"))opts["epochs"]=kind.EndsWith("zero")?"0":"-1";
                if(kind.StartsWith("hidden-"))opts["hidden"]=kind.EndsWith("zero")?"0":"-1";
                if(kind=="sparse")opts["epochs"]="1";
                string message=kind.StartsWith("epochs-")?"--epochs must be positive":kind.StartsWith("hidden-")?"--hidden must be positive":kind=="sparse"?"No training rows or pairs were processed":"No usable training pairs";
                // Both fresh outputs and pre-existing models must remain untouched on rejection.
                foreach(bool existing in new[]{false,true})
                {
                    byte[] sentinel=[1,3,5,7,9];
                    if(existing)File.WriteAllBytes(model,sentinel);else if(File.Exists(model))File.Delete(model);
                    bool rejected=false;
                    try{Quiet(()=>Program.Train(opts));}
                    catch(InvalidDataException e){rejected=e.Message.Contains(message);}
                    catch(Exception){/* Wrong exception is a regression failure below. */}
                    Assert(rejected,kind+": expected explicit rejection");
                    Assert(existing?File.ReadAllBytes(model).SequenceEqual(sentinel):!File.Exists(model),kind+": rejected training wrote model");
                }
            }
            Save("sparse");var sparse=Options();sparse["epochs"]="10";
            Assert(Quiet(()=>Program.Train(sparse))==0,"empty first epoch must allow later sampled pairs");
            using(var meta=JsonDocument.Parse(NnModel.Load(model).MetaJson))
                Assert(meta.RootElement.TryGetProperty("processedTrainingUnits",out var count) && count.GetInt64()>0,"sparse fixture eventually processed pairs");
            foreach(string target in new[]{"win","pair"})
            {
                Save();var opts=Options(target);
                Assert(Quiet(()=>Program.Train(opts))==0,"normal "+target+" train");
                var saved=NnModel.Load(model);
                using var meta=JsonDocument.Parse(saved.MetaJson);
                Assert(meta.RootElement.TryGetProperty("processedTrainingUnits",out var count)
                    && count.GetInt64()==(target=="win"?12:6),"actual row/pair processing count "+target);
                Assert(meta.RootElement.TryGetProperty("trainingUnit",out var unit)
                    && unit.GetString()==(target=="win"?"row":"pair"),"processing count unit "+target);
                Assert(Quiet(()=>Program.Verify(opts))==0,"normal "+target+" saved holdout verify");
            }
        }
        finally{Console.SetOut(output);Directory.Delete(dir,true);}
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        Console.WriteLine($"Value learning: {checks} checks passed (synthetic data; no strength claim)");
        return 0;
    }
}
