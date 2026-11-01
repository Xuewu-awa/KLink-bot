using System.Security.Cryptography;
using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

// Synthetic objective/gradient tests, not game-rule or playing-strength evidence.
internal static class PairDirectionSelfTest
{
    internal static int Run()
    {
        int checks=0;
        var failures=new List<string>();
        void Assert(bool ok,string why){checks++;if(!ok)failures.Add(why);}
        string dir=Path.Combine(Path.GetTempPath(),"klink-pair-direction-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string data=Path.Combine(dir,"data.bin"),model=Path.Combine(dir,"model.bin");
        var output=Console.Out;
        int Quiet(Func<int> action)
        {
            using var capture=new StringWriter();
            try{Console.SetOut(capture);return action();}
            finally{Console.SetOut(output);}
        }
        float[] Features(bool winner)
        {
            var x=new float[StateEncoder.Dim];x[0]=.1f;x[1]=winner?1:-1;return x;
        }
        void Save(bool reverse,bool decision)
        {
            using(var w=new BinaryWriter(File.Create(data)))
            {
                w.Write(0x324C4B41);w.Write(StateEncoder.Dim);w.Write(2);
                // First occurrence order of game IDs stays fixed, so the split is identical.
                // Reorder only second observations; labels remain constant per game.
                for(int row=0;row<2;row++)foreach(int game in row==1 && reverse?new[]{0,2,1,3}:new[]{0,1,2,3})
                {
                    bool winner=game%2==1;
                    foreach(float v in Features(winner))w.Write(v);
                    w.Write(winner?1f:0f);w.Write((float)game);w.Write(1f);
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
        NnModel Train(bool reverse,bool decision,int seed,int epochs,string target="pair")
        {
            Save(reverse,decision);
            var opts=new Dictionary<string,string>{["data"]=data,["out"]=model,["model"]=model,["epochs"]=epochs.ToString(),
                ["hidden"]="4",["target"]=target,["split-seed"]=seed.ToString()};
            Assert(Quiet(()=>Program.Train(opts))==0,"train exit");
            var saved=NnModel.Load(model);
            Assert(Quiet(()=>Program.Verify(opts))==0,"saved holdout verify");
            return saved;
        }
        void Equal(NnModel a,NnModel b,string why)
        {
            bool Near(float[] x,float[] y)=>x.Zip(y).All(p=>Math.Abs(p.First-p.Second)<2e-6f);
            Assert(Near(a.Mean,b.Mean) && Near(a.Std,b.Std),why+" scaler");
            Assert(Near(a.W1,b.W1) && Near(a.B1,b.B1)
                && Near(a.W2,b.W2) && a.B2==b.B2,why+" parameters");
        }
        void CheckNumericalGradient(NnModel trained)
        {
            // Reconstruct only the public fixed initialization. Derivatives below
            // are central differences of the ranking loss, not copied backprop.
            var rng=new Random(42);
            double Initial(int fan)=>(float)(Math.Sqrt(2.0/fan)*(rng.NextDouble()*2-1));
            var w1=Enumerable.Range(0,trained.W1.Length).Select(_=>Initial(trained.Dim)).ToArray();
            var w2=Enumerable.Range(0,trained.Hidden).Select(_=>Initial(trained.Hidden)).ToArray();
            var b1=new double[trained.Hidden];
            var win=new float[trained.Dim];var lose=new float[trained.Dim];
            trained.NormalizeInto(Features(true),win);trained.NormalizeInto(Features(false),lose);
            double Score(float[] x)
            {
                double score=0;
                for(int j=0;j<trained.Hidden;j++)
                {
                    double h=b1[j];
                    for(int d=0;d<trained.Dim;d++)h+=w1[j*trained.Dim+d]*x[d];
                    score+=w2[j]*Math.Max(0,h);
                }
                return score;
            }
            double Loss()=>Math.Log(1+Math.Exp(Score(lose)-Score(win)));
            double initialLoss=Loss();
            void Check(double[] parameters,float[] actual,IEnumerable<int> indices,string name)
            {
                foreach(int i in indices)
                {
                    double before=parameters[i];const double eps=1e-5;
                    parameters[i]=before+eps;double plus=Loss();
                    parameters[i]=before-eps;double minus=Loss();parameters[i]=before;
                    double expected=before-.05*(plus-minus)/(2*eps);
                    Assert(Math.Abs(expected-actual[i])<2e-6,"finite-difference one-step "+name+"["+i+"]");
                }
            }
            Check(w1,trained.W1,Enumerable.Range(0,trained.Hidden).Select(j=>j*trained.Dim+1),"W1 active feature");
            Check(b1,trained.B1,Enumerable.Range(0,trained.Hidden),"B1");
            Check(w2,trained.W2,Enumerable.Range(0,trained.Hidden),"W2");
            double trainedLoss=Math.Log(1+Math.Exp(trained.PredictLogit(Features(false))-trained.PredictLogit(Features(true))));
            Assert(trainedLoss<initialLoss,"one-step winner-minus-loser loss must decrease");
        }
        try
        {
            foreach(bool decision in new[]{false,true})foreach(int seed in new[]{7,19})
            {
                var first=Train(false,decision,seed,1);CheckNumericalGradient(first);
                var reversed=Train(true,decision,seed,1);CheckNumericalGradient(reversed);
                Equal(first,reversed,"one-step row-order invariance");
                var later=Train(false,decision,seed,12);var laterReversed=Train(true,decision,seed,12);
                Equal(later,laterReversed,"multi-step row-order invariance");
                using var meta=JsonDocument.Parse(later.MetaJson);
                Assert(meta.RootElement.GetProperty("processedTrainingUnits").GetInt64()==36,"all ranking pairs processed");
            }
            // A before/after parameter fingerprint also binds unchanged win/BCE execution.
            var winModel=Train(false,false,7,1,"win");
            using var bytes=new MemoryStream();
            using(var w=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
                foreach(var values in new[]{winModel.Mean,winModel.Std,winModel.W1,winModel.B1,winModel.W2})
                    foreach(float value in values)w.Write(value);
            Console.WriteLine("Win parameter SHA256: "+Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        }
        finally{Console.SetOut(output);Directory.Delete(dir,true);}
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        Console.WriteLine($"Pair direction: {checks} checks passed (synthetic data; no strength claim)");return 0;
    }
}
