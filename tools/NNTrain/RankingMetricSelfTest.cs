using System.Text.Json;
using System.Text.Json.Nodes;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

// Actual Train/Save/Load/Verify, using synthetic games only.
internal static class RankingMetricSelfTest
{
    internal static int Run()
    {
        int checks=0;
        var failures=new List<string>();
        void Assert(bool ok,string why){checks++;if(!ok)failures.Add(why);}
        string dir=Path.Combine(Path.GetTempPath(),"klink-ranking-metric-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string data=Path.Combine(dir,"data.bin"),path=Path.Combine(dir,"model.bin");
        var output=Console.Out;
        string Capture(Func<int> action)
        {
            using var writer=new StringWriter();
            try{Console.SetOut(writer);Assert(action()==0,"command exit");return writer.ToString();}
            finally{Console.SetOut(output);}
        }
        NnModel Copy(NnModel m,string meta,float[]? w1=null,float[]? b1=null,float[]? w2=null,
            float[]? mean=null,float[]? std=null)=>new(){Dim=m.Dim,Hidden=m.Hidden,CardDim=m.CardDim,
                Zones=m.Zones,PerSide=m.PerSide,Activation=m.Activation,EncoderSpec=m.EncoderSpec,
                MetaJson=meta,Mean=mean??m.Mean,Std=std??m.Std,W1=w1??m.W1,B1=b1??m.B1,W2=w2??m.W2,B2=m.B2};
        try
        {
            foreach(bool decision in new[]{false,true})foreach(string mode in new[]{"single-label","matchup","turn","paired"})
            {
                var rng=new Random(7);
                var val=Enumerable.Range(0,14).OrderBy(_=>rng.Next()).Take(2).ToArray();
                bool Winner(int g)=>g==val[0] || (g!=val[1] && g%2==1) || (mode=="single-label" && g==val[1]);
                using(var w=new BinaryWriter(File.Create(data)))
                {
                    w.Write(0x324C4B41);w.Write(StateEncoder.Dim);w.Write(2);
                    for(int g=0;g<14;g++)for(int row=0;row<2;row++)
                    {
                        var x=new float[StateEncoder.Dim];
                        x[0]=mode=="turn" && g==val[1] ? .2f : .1f;
                        x[1]=Winner(g)?(row==0?2:-2):0;
                        foreach(float f in x)w.Write(f);
                        w.Write(Winner(g)?1f:0f);w.Write((float)g);
                        w.Write(mode=="matchup" && g==val[1]?2f:1f);
                    }
                }
                var q=new QualityReport{RequestedGames=14,AcceptedGames=14,Rows=28,Dim=StateEncoder.Dim,DeckCount=2,
                    DataSha256=TrainingAdmission.Hash(data),SamplingMode=decision?"policy-and-choice-before/v1":"turn-end/v1",
                    InputsSha256=new(){["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash()}};
                foreach(string key in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"})q.InputsSha256[key]=new string('a',64);
                for(int g=0;g<14;g++)q.Games.Add(new QualityGame{Game=g,Accepted=true,ObservedSamples=2,WrittenSamples=2,
                    SampleKinds=decision?[1,1]:[0,0],SampleActors=[1,1],MainDecisions=decision?2:0});
                File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));
                var opts=new Dictionary<string,string>{{"data",data},{"out",path},{"model",path},{"epochs","2"},
                    {"hidden","2"},{"target","pair"},{"split-seed","7"},{"eval-every","1"}};
                string train=Capture(()=>Program.Train(opts));var model=NnModel.Load(path);
                var meta=JsonNode.Parse(model.MetaJson)!.AsObject();bool available=mode=="paired";
                Assert(meta["validationGameIds"]!.AsArray().Select(x=>(int)x!.GetValue<float>()).Order().SequenceEqual(val.Order()),"fixture holdout");
                Assert(meta["validationMetric"]?.GetValue<string>()=="pair-ranking/v1","metric kind");
                Assert(meta["validationMetricStatus"]?.GetValue<string>()==(available?"available":"no-comparable-pairs"),"metric status");
                Assert(meta["validationMetricTotal"]?.GetValue<int>()==(available?2:0),"metric denominator");
                if(!available)
                {
                    Assert(meta["valAccuracy"] is null && meta["bestValAccuracy"] is null,"zero pairs must save null accuracies");
                    Assert(meta["validationMetricCorrect"]?.GetValue<int>()==0,"zero pairs correct count");
                    Assert(train.Contains("不可评估") && !train.Contains("编码可能没信号") && !train.Contains("编码里有信号"),"unavailable summary cannot infer signal");
                    Assert(model.Describe().Contains("不可评估") && model.Describe().Contains("epochs 2"),"null metric describe preserves remaining metadata");
                }
                else
                {
                    Assert(meta["valAccuracy"] is JsonValue && meta["bestValAccuracy"] is JsonValue,"available numeric metrics");
                    Assert(Math.Abs(meta["valAccuracy"]!.GetValue<double>()-(meta["validationMetricCorrect"]?.GetValue<int>()??-1)/2.0)<1e-7,"saved numerator/denominator");
                }
                string actual=Capture(()=>Program.Verify(opts));
                Assert(actual.Contains(available?$"验证集 {meta["valAccuracy"]!.GetValue<double>():P2}":"验证集 不可评估"),"saved trained metric matches Verify");
                Assert(model.Describe().Contains("验证排序准确率"),"ranking description labels the metric");
                // Controlled scores, independent of the optimizer. Recompute even with bogus saved metric fields.
                foreach(double expected in available?new[]{0.0,.5,1.0}:new[]{0.0})
                {
                    var altered=(JsonObject)meta.DeepClone();
                    altered["validationMetricStatus"]="available";altered["validationMetricTotal"]=99;
                    altered["validationMetricCorrect"]=99;altered["valAccuracy"]=1.0;
                    var w1=new float[model.W1.Length];var w2=new float[2];var b1=new float[2];
                    // Two ReLU units represent score x[1], or a constant for all-tie (0%).
                    if(expected==.5){w1[1]=1;w1[model.Dim+1]=-1;w2[0]=1;w2[1]=-1;}
                    if(expected==1){w1[1]=1;w1[model.Dim+1]=-1;w2[0]=w2[1]=1;}
                    Copy(model,altered.ToJsonString(),w1,b1,w2,new float[model.Dim],Enumerable.Repeat(1f,model.Dim).ToArray()).Save(path);
                    string verify=Capture(()=>Program.Verify(opts));
                    Assert(verify.Contains(available?$"验证集 {expected:P2}":"验证集 不可评估"),"Verify recomputes actual denominator and accuracy");
                    Assert(verify.Contains(available?$"({(int)(expected*2)}/2 对":"(0/0 对"),"Verify pair counts");
                    if(!available)Assert(verify.Split('\n').Any(l=>l.Contains("合计") && l.Contains("不可评估")),"empty bucket total unavailable");
                }
                var legacy=(JsonObject)meta.DeepClone();
                foreach(string key in new[]{"validationMetric","validationMetricStatus","validationMetricTotal","validationMetricCorrect"})legacy.Remove(key);
                legacy["valAccuracy"]=.5;
                Assert(Copy(model,legacy.ToJsonString()).Describe().Contains($"{.5:P1}"),"legacy numeric description");
                Copy(model,legacy.ToJsonString()).Save(path);
                Assert(Capture(()=>Program.Verify(opts)).Contains(available?"/2 对":"验证集 不可评估"),"legacy Verify recomputes metrics");
                opts["target"]="win";Capture(()=>Program.Train(opts));var win=NnModel.Load(path);
                var wm=JsonNode.Parse(win.MetaJson)!;
                Assert(wm["validationMetric"]?.GetValue<string>()=="win-accuracy/v1" && wm["validationMetricTotal"]?.GetValue<int>()==4,"win metric row count");
                Assert(wm["validationMetricStatus"]?.GetValue<string>()=="available" && wm["valAccuracy"] is JsonValue,"win remains numeric");
                Console.WriteLine($"ranking metric: {mode}, decision={decision}, controlled 0/50/100 or zero-pair cases checked");
            }
        }
        finally{Console.SetOut(output);Directory.Delete(dir,true);}
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        Console.WriteLine($"Ranking metric: {checks} checks passed (synthetic data; no strength claim)");return 0;
    }
}
