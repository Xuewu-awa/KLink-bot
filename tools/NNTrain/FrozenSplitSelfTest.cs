using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

internal static class FrozenSplitSelfTest
{
    internal static int Run()
    {
        int checks=0;
        void Check(bool ok,string name){checks++;if(!ok)throw new InvalidOperationException(name);}
        void Reject(Action action,string name){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,name);}
        string dir=Path.Combine(Path.GetTempPath(),"klink-frozen-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string data=Path.Combine(dir,"data.bin"),split=Path.Combine(dir,"split.json"),modelPath=Path.Combine(dir,"model.bin");
        var output=Console.Out;
        int Quiet(Func<int> action){try{Console.SetOut(new StringWriter());return action();}finally{Console.SetOut(output);}}
        try
        {
            using(var w=new BinaryWriter(File.Create(data)))
            {
                w.Write(0x324c4b41);w.Write(StateEncoder.Dim);w.Write(2);
                for(int g=0;g<8;g++)
                {
                    var x=new float[StateEncoder.Dim];x[0]=.1f;x[1]=g%2==0?-1:1;
                    foreach(float v in x)w.Write(v);w.Write((float)(g%2));w.Write((float)g);w.Write(1f);
                }
            }
            string hash=TrainingAdmission.Hash(data);
            var q=new QualityReport{RequestedGames=8,AcceptedGames=8,Rows=8,Dim=StateEncoder.Dim,DeckCount=2,DataSha256=hash,
                InputsSha256=new(){["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash()}};
            foreach(string key in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"})q.InputsSha256[key]=new string('a',64);
            for(int g=0;g<8;g++)q.Games.Add(new QualityGame{Game=g,Accepted=true,ObservedSamples=1,WrittenSamples=1,SampleKinds=[0],SampleActors=[1]});
            File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));
            var frozen=new FrozenValueSplit{SchemaVersion=1,DatasetSha256=hash,SourcePlanSha256=new string('b',64),TrainingGameIds=[0,1,4,5],ValidationGameIds=[2,3,6,7]};
            void Save()=>File.WriteAllText(split,JsonSerializer.Serialize(frozen,TrainingAdmission.Json));
            var gids=Enumerable.Range(0,8).Select(x=>(float)x).ToArray();Save();
            Check(FrozenValueSplit.Load(split,hash,gids).SetEquals(new float[]{2,3,6,7}),"exact explicit holdout");
            frozen.ValidationGameIds=[2,2,6,7];Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"duplicate");
            frozen.ValidationGameIds=[0,2,3,6,7];Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"overlap");
            frozen.ValidationGameIds=[2,3,6];Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"missing game");
            frozen.ValidationGameIds=[2,3,6,8];Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"extra game");
            frozen.ValidationGameIds=[];Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"empty");
            frozen.ValidationGameIds=[2,3,6,7.5f];Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"fractional");
            frozen.ValidationGameIds=[2,3,6,7];Save();Reject(()=>FrozenValueSplit.Load(split,new string('c',64),gids),"wrong dataset");
            frozen.SourcePlanSha256="bad";Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"invalid plan binding");
            frozen.SourcePlanSha256=null;Save();Reject(()=>FrozenValueSplit.Load(split,hash,gids),"null plan binding");
            frozen.SourcePlanSha256=new string('b',64);Save();
            Reject(()=>FrozenValueSplit.RequireSaved(split,hash,gids,"{\"splitSource\":\"frozen-manifest/v1\"}"),"missing saved hash");
            foreach(string target in new[]{"pair","win"})
            {
                var opts=new Dictionary<string,string>{["data"]=data,["out"]=modelPath,["model"]=modelPath,["split-manifest"]=split,["epochs"]="1",["hidden"]="4",["target"]=target};
                var conflict=new Dictionary<string,string>(opts){["split-seed"]="7"};
                Reject(()=>Quiet(()=>Program.Train(conflict)),"conflicting train seed");
                Check(Quiet(()=>Program.Train(opts))==0,"actual train");
                var model=NnModel.Load(modelPath);using var doc=JsonDocument.Parse(model.MetaJson);var meta=doc.RootElement;
                Check(meta.GetProperty("splitManifestSha256").GetString()==TrainingAdmission.Hash(split),"saved hash");
                Check(ValueEvaluationSplit.Read(model,hash,gids,null).SetEquals(frozen.ValidationGameIds),"saved IDs");
                Check(Quiet(()=>Program.Verify(opts))==0,"actual verify with manifest");
                var savedOnly=new Dictionary<string,string>(opts);savedOnly.Remove("split-manifest");
                Check(Quiet(()=>Program.Verify(savedOnly))==0,"verify uses saved IDs without file");
                Reject(()=>Quiet(()=>Program.Verify(conflict)),"seed cannot override frozen model");
                var cov=new Dictionary<string,string>(opts){["out"]=Path.Combine(dir,"coverage.json")};
                Check(Quiet(()=>Program.Coverage(cov))==0,"coverage saved model");
                cov.Remove("model");Check(Quiet(()=>Program.Coverage(cov))==0,"coverage explicit manifest");
                using(var c=JsonDocument.Parse(File.ReadAllText(cov["out"])))
                    Check(c.RootElement.GetProperty("validationGameIds").EnumerateArray().Select(x=>x.GetSingle()).ToHashSet().SetEquals(frozen.ValidationGameIds),"coverage exact IDs");
                cov["split-seed"]="7";Reject(()=>Quiet(()=>Program.Coverage(cov)),"coverage conflict");cov.Remove("split-seed");
                cov["out"]=split;Reject(()=>Quiet(()=>Program.Coverage(cov)),"preserve input split file");
                frozen.SourcePlanSha256=new string('d',64);Save();Reject(()=>Quiet(()=>Program.Verify(opts)),"different manifest same IDs");
                frozen.SourcePlanSha256=new string('b',64);Save();
                Check(Quiet(()=>Program.Verify(opts))==0,"restored exact binding");
            }
        }
        finally{Console.SetOut(output);Directory.Delete(dir,true);}
        Console.WriteLine($"Frozen split: {checks} checks passed (synthetic data; no strength claim)");return 0;
    }
}
