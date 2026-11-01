using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

internal static class AfterstateWinTraining
{
    private sealed class Split
    {
        public string DatasetSha256 {get;set;}=""; public int SplitSeed {get;set;}
        public int[] TrainingGames {get;set;}=[]; public int[] ValidationGames {get;set;}=[];
    }
    private sealed record Row(int Game,int Step,float[] Observation,float Target);
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};

    private static (List<Row> Rows, Dictionary<int,int> Games) Read(string data,string splitPath)
    {
        var split=JsonSerializer.Deserialize<Split>(File.ReadAllText(splitPath),Json)??throw new InvalidDataException("Invalid afterstate split.");
        if(split.DatasetSha256!=TrainingAdmission.Hash(data))throw new InvalidDataException("Afterstate data/split hash mismatch.");
        var rows=new List<Row>();
        using var file=File.OpenRead(data);using var zip=new GZipStream(file,CompressionMode.Decompress);using var reader=new StreamReader(zip);
        _=reader.ReadLine(); while(reader.ReadLine() is {} line) rows.Add(JsonSerializer.Deserialize<Row>(line,Json)??throw new InvalidDataException("Null afterstate row."));
        var training=split.TrainingGames.ToHashSet();var validation=split.ValidationGames.ToHashSet();
        if(training.Overlaps(validation)||training.Count==0||validation.Count==0||rows.Any(r=>!training.Contains(r.Game)&&!validation.Contains(r.Game)))throw new InvalidDataException("Invalid game-disjoint split.");
        var games=rows.GroupBy(r=>r.Game).ToDictionary(g=>g.Key,g=>training.Contains(g.Key)?0:1);
        if(games.Values.All(v=>v==0)||games.Values.All(v=>v==1))throw new InvalidDataException("Both afterstate splits require rows.");
        return (rows,games);
    }

    internal static int Train(Dictionary<string,string> opts)
    {
        string data=opts.GetValueOrDefault("data","afterstate-win.jsonl.gz"), splitPath=opts.GetValueOrDefault("split",data+".split.json"), output=opts.GetValueOrDefault("out","afterstate-win-model.bin");
        if(File.Exists(output))throw new IOException("Refusing to overwrite afterstate model.");
        var (rows,groups)=Read(data,splitPath);int hidden=Int(opts,"hidden",16),epochs=Int(opts,"epochs",10),init=Int(opts,"init-seed",42);float lr=Float(opts,"lr",0.03f);
        if(hidden<1||hidden>128||epochs<1||epochs>200||!float.IsFinite(lr)||lr<=0||lr>1)throw new ArgumentException("Invalid afterstate training parameters.");
        var train=rows.Where(r=>groups[r.Game]==0).ToArray();var val=rows.Where(r=>groups[r.Game]==1).ToArray();
        int dim=StateEncoder.Dim;var mean=new float[dim];var std=new float[dim];
        foreach(var row in train)for(int d=0;d<dim;d++)mean[d]+=row.Observation[d];
        for(int d=0;d<dim;d++)mean[d]/=train.Length;
        foreach(var row in train)for(int d=0;d<dim;d++){float x=row.Observation[d]-mean[d];std[d]+=x*x;}
        for(int d=0;d<dim;d++)std[d]=MathF.Sqrt(std[d]/train.Length)+1e-6f;
        float[] X(Row r){var x=new float[dim];for(int d=0;d<dim;d++)x[d]=(r.Observation[d]-mean[d])/std[d];return x;}
        var tx=train.Select(r=>(X(r),r.Target)).ToArray();var vx=val.Select(r=>(X(r),r.Target)).ToArray();
        var rng=new Random(init);float Scale(int fan)=>(float)(Math.Sqrt(2.0/fan)*(rng.NextDouble()*2-1));var w1=new float[hidden*dim];var b1=new float[hidden];var w2=new float[hidden];for(int i=0;i<w1.Length;i++)w1[i]=Scale(dim);for(int i=0;i<w2.Length;i++)w2[i]=Scale(hidden);
        var order=Enumerable.Range(0,tx.Length).ToArray();var h=new float[hidden];var g1=new float[hidden*dim];var gb=new float[hidden];var g2=new float[hidden];
        float Logit(float[] x,float[] hh){for(int j=0;j<hidden;j++){float s=b1[j];for(int d=0;d<dim;d++)s+=w1[j*dim+d]*x[d];hh[j]=s>0?s:0;}float o=0;for(int j=0;j<hidden;j++)o+=w2[j]*hh[j];return o;}
        (double Loss,double Acc) Eval((float[] X,float Y)[] set){double loss=0;int ok=0;foreach(var r in set){float o=Logit(r.X,h);float p=1/(1+MathF.Exp(-o));loss+=-r.Y*Math.Log(p+1e-7)-(1-r.Y)*Math.Log(1-p+1e-7);if((o>0)==(r.Y>.5f))ok++;}return(loss/set.Length,ok/(double)set.Length);}
        var curve=new List<object>();for(int ep=1;ep<=epochs;ep++){Array.Clear(g1);Array.Clear(gb);Array.Clear(g2);for(int i=order.Length-1;i>0;i--){int j=rng.Next(i+1);(order[i],order[j])=(order[j],order[i]);}double loss=0;foreach(int k in order){var (x,y)=tx[k];float o=Logit(x,h),p=1/(1+MathF.Exp(-o)),e=p-y;loss+=-y*Math.Log(p+1e-7)-(1-y)*Math.Log(1-p+1e-7);for(int j=0;j<hidden;j++){if(h[j]==0)continue;g2[j]+=e*h[j];float c=e*w2[j];gb[j]+=c;for(int d=0;d<dim;d++)g1[j*dim+d]+=c*x[d];}}float step=lr/tx.Length;for(int i=0;i<w1.Length;i++)w1[i]-=step*g1[i];for(int j=0;j<hidden;j++){b1[j]-=step*gb[j];w2[j]-=step*g2[j];}var ev=Eval(vx);curve.Add(new{epoch=ep,trainLoss=loss/tx.Length,validationLoss=ev.Loss,validationAccuracy=ev.Acc});Console.WriteLine($"afterstate epoch {ep}: train loss {loss/tx.Length:F6}, validation {ev.Acc:P2}");}
        var split=JsonSerializer.Deserialize<Split>(File.ReadAllText(splitPath),Json)!;
        var meta=new Dictionary<string,object?>
        {
            ["schema"]="afterstate-win-model/v1", ["encoderSpec"]=StateEncoder.Spec,
            ["trainingContract"]=new Dictionary<string,object?>
            {
                ["target"]="win", ["sampling"]="afterstate-left/v1", ["observationSpec"]=StateEncoder.Spec,
                ["observationPerspective"]="left", ["dataSha256"]=TrainingAdmission.Hash(data),
                ["splitSha256"]=TrainingAdmission.Hash(splitPath), ["splitSeed"]=split.SplitSeed,
                ["trainingGames"]=groups.Where(p=>p.Value==0).Select(p=>p.Key).Order().ToArray(),
                ["validationGames"]=groups.Where(p=>p.Value==1).Select(p=>p.Key).Order().ToArray(),
                ["simulatorSha256"]=TrainingAdmission.Hash(typeof(KLink.Bot.Engine.MatchEngine).Assembly.Location)
            },
            ["data"]=Path.GetFileName(data), ["rows"]=rows.Count, ["trainRows"]=train.Length,
            ["validationRows"]=val.Length, ["epochs"]=epochs, ["hidden"]=hidden, ["learningRate"]=lr,
            ["initSeed"]=init, ["curve"]=curve, ["trainedAt"]=DateTime.UtcNow.ToString("O")
        };
        var model=new NnModel{Dim=dim,Hidden=hidden,CardDim=StateEncoder.CardDim,Zones=StateEncoder.Zones,PerSide=StateEncoder.PerSide,Activation=0,EncoderSpec=StateEncoder.Spec,MetaJson=JsonSerializer.Serialize(meta),Mean=mean,Std=std,W1=w1,B1=b1,W2=w2};model.Save(output);File.WriteAllText(output+".report.json",JsonSerializer.Serialize(meta,TrainingAdmission.Json));Console.WriteLine($"Afterstate model saved: {output}");return 0;
    }

    internal static int Verify(Dictionary<string,string> opts)
    {
        string data=opts.GetValueOrDefault("data","afterstate-win.jsonl.gz"),split=opts.GetValueOrDefault("split",data+".split.json"),modelPath=opts.GetValueOrDefault("model","afterstate-win-model.bin");var (rows,groups)=Read(data,split);var model=NnModel.Load(modelPath);var val=rows.Where(r=>groups[r.Game]==1).ToArray();int ok=0;double loss=0;foreach(var r in val){float p=model.Predict(r.Observation),logit=model.PredictLogit(r.Observation);loss+=-r.Target*Math.Log(p+1e-7)-(1-r.Target)*Math.Log(1-p+1e-7);if((logit>0)==(r.Target>.5f))ok++;}Console.WriteLine($"Afterstate validation: {ok}/{val.Length} = {ok/(double)val.Length:P2}, loss {loss/val.Length:F6}");return 0;
    }
    private static int Int(Dictionary<string,string> o,string k,int d)=>o.TryGetValue(k,out var v)?int.Parse(v,CultureInfo.InvariantCulture):d;
    private static float Float(Dictionary<string,string> o,string k,float d)=>o.TryGetValue(k,out var v)?float.Parse(v,CultureInfo.InvariantCulture):d;
}
