using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

internal static class ActionModelTool
{
    internal sealed record Metrics(int Rows,double CrossEntropy,double Top1,double UniformCrossEntropy,
        double EndTurnTop1,int ChosenFeatureTieRows);
    internal const int MaxLoadedRows=20000;
    internal const int MaxLoadedCandidates=2000000;

    private static (ActionDatasetQuality Quality,ActionDatasetRow[] Rows) Read(string path)
    {
        var q=ActionDatasetTool.Verify(path);
        if(q.Rows>MaxLoadedRows || q.Games.Sum(g=>(long)g.WrittenRows)>MaxLoadedRows)
            throw new InvalidDataException("Offline smoke trainer row limit exceeded; no silent truncation.");
        var rows=new List<ActionDatasetRow>();long candidates=0;
        using var file=File.OpenRead(path);using var zip=new GZipStream(file,CompressionMode.Decompress);
        using var reader=new StreamReader(zip);_=reader.ReadLine();
        while(reader.ReadLine() is {} line)
        {
            var row=JsonSerializer.Deserialize<ActionDatasetRow>(line,ActionDatasetTool.Json)!;
            candidates+=row.Candidates.Length;
            if(candidates>MaxLoadedCandidates)throw new InvalidDataException("Offline smoke trainer candidate limit exceeded.");
            rows.Add(row);
        }
        return (q,rows.ToArray());
    }

    internal static (int[] Train,int[] Validation) Split(IEnumerable<int> ids,int seed)
    {
        var games=ids.Distinct().Order().ToArray();
        if(games.Length<2)throw new InvalidDataException("At least two admitted games required for game-disjoint evaluation.");
        var random=new Random(seed);
        for(int i=games.Length-1;i>0;i--){int j=random.Next(i+1);(games[i],games[j])=(games[j],games[i]);}
        int n=Math.Max(1,games.Length/5);
        return (games.Skip(n).Order().ToArray(),games.Take(n).Order().ToArray());
    }

    private static IReadOnlyList<IReadOnlyList<float>> Features(ActionDatasetRow row)=>row.Candidates.Select(o=>o.Features).ToArray();

    internal static Metrics Evaluate(MainActionModel model,IReadOnlyList<ActionDatasetRow> rows)
    {
        if(rows.Count==0)throw new InvalidDataException("Empty action evaluation split.");
        double loss=0,uniform=0;int hits=0,end=0,ties=0;
        foreach(var row in rows)
        {
            var actions=Features(row);var p=model.Probabilities(row.Observation,actions);
            loss+=model.LossAndUpdate(row.Observation,actions,row.ChosenIndex,0);
            uniform+=Math.Log(actions.Count);
            int best=0;for(int i=1;i<p.Length;i++)if(p[i]>p[best])best=i;
            if(best==row.ChosenIndex)hits++;
            if(row.Candidates[row.ChosenIndex].Command.Kind==Training.MainActionKind.EndTurn)end++;
            if(actions.Count(a=>a.SequenceEqual(actions[row.ChosenIndex]))>1)ties++;
        }
        return new(rows.Count,loss/rows.Count,hits/(double)rows.Count,uniform/rows.Count,end/(double)rows.Count,ties);
    }

    internal static int Train(Dictionary<string,string> opts,string dataDir,string vectorPath)
    {
        string path=opts.GetValueOrDefault("data","action-labels.jsonl.gz");
        int epochs=Int(opts,"epochs",3),hidden=Int(opts,"hidden",16),seed=Int(opts,"split-seed",1);
        double lr=double.Parse(opts.GetValueOrDefault("lr","0.01"),CultureInfo.InvariantCulture);
        if(epochs is <1 or >100 || hidden is <1 or >64 || !double.IsFinite(lr) || lr<=0 || lr>1)
            throw new ArgumentException("Expected epochs 1..100, hidden 1..64 and finite lr in (0,1].");
        var (q,rows)=Read(path);var split=Split(q.Games.Where(g=>g.Accepted).Select(g=>g.Game),seed);
        var contract=new MainActionTrainingContract {Teacher=q.Header.Teacher,DatasetSha256=q.DataSha256,
            InputsSha256=q.Header.InputsSha256,SplitSeed=seed,TrainingGames=split.Train,ValidationGames=split.Validation};
        contract.RequireInputs(q.Header.Teacher,ActionDatasetTool.CurrentInputs(dataDir,vectorPath));
        var model=MainActionModel.Create(contract,hidden,seed);
        var train=rows.Where(r=>split.Train.Contains(r.Game)).ToArray();
        var validation=rows.Where(r=>split.Validation.Contains(r.Game)).ToArray();
        var initial=Evaluate(model,train);var random=new Random(seed);
        var order=Enumerable.Range(0,train.Length).ToArray();
        for(int epoch=0;epoch<epochs;epoch++)
        {
            for(int i=order.Length-1;i>0;i--){int j=random.Next(i+1);(order[i],order[j])=(order[j],order[i]);}
            double loss=0;
            foreach(int i in order)loss+=model.LossAndUpdate(train[i].Observation,Features(train[i]),train[i].ChosenIndex,lr);
            model.Validate();Console.WriteLine($"Action epoch {epoch+1}: online train NLL {loss/train.Length:F6}");
        }
        string output=opts.GetValueOrDefault("out","action-model.json");model.Save(output);
        var loaded=MainActionModel.Load(output);loaded.Contract.RequireInputs(q.Header.Teacher,ActionDatasetTool.CurrentInputs(dataDir,vectorPath));
        foreach(var row in rows)
            if(!model.Probabilities(row.Observation,Features(row)).SequenceEqual(loaded.Probabilities(row.Observation,Features(row))))
                throw new InvalidDataException("Action scorer changed across save/load.");
        var report=new {Scope="Offline teacher imitation only; structural candidates, no legality mask or strength claim.",
            Epochs=epochs,LearningRate=lr,Contract=contract,InitialTrain=initial,Train=Evaluate(loaded,train),
            Validation=Evaluate(loaded,validation),SavedLoadedRowsCompared=rows.Length,ModelSha256=TrainingAdmission.Hash(output)};
        File.WriteAllText(output+".report.json",JsonSerializer.Serialize(report,TrainingAdmission.Json));
        Console.WriteLine(JsonSerializer.Serialize(report,TrainingAdmission.Json));return 0;
    }

    internal static int Verify(Dictionary<string,string> opts,string dataDir,string vectorPath)
    {
        var model=MainActionModel.Load(opts.GetValueOrDefault("model","action-model.json"));
        var (q,rows)=Read(opts.GetValueOrDefault("data","action-labels.jsonl.gz"));
        model.Contract.RequireInputs(q.Header.Teacher,q.Header.InputsSha256);
        model.Contract.RequireInputs(q.Header.Teacher,ActionDatasetTool.CurrentInputs(dataDir,vectorPath));
        var split=Split(q.Games.Where(g=>g.Accepted).Select(g=>g.Game),model.Contract.SplitSeed);
        if(q.DataSha256!=model.Contract.DatasetSha256 || !split.Train.SequenceEqual(model.Contract.TrainingGames)
            || !split.Validation.SequenceEqual(model.Contract.ValidationGames))
            throw new InvalidDataException("Smoke verifier expects the bound dataset and exact game split.");
        Console.WriteLine(JsonSerializer.Serialize(Evaluate(model,rows.Where(r=>split.Validation.Contains(r.Game)).ToArray()),TrainingAdmission.Json));
        return 0;
    }

    private static int Int(Dictionary<string,string> opts,string key,int fallback)=>opts.TryGetValue(key,out var value)
        ? int.Parse(value,CultureInfo.InvariantCulture):fallback;
}
