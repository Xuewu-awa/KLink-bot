using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

internal sealed record ExternalRank(int Correct,int Incorrect,int Ties)
{
    public int Total => Correct+Incorrect+Ties;
    public double? StrictAccuracy => Total==0 ? null : (double)Correct/Total;
    public double? HalfCreditAccuracy => Total==0 ? null : (Correct+Ties*.5)/Total;
}

// New data evaluation is deliberately separate from Verify's saved training holdout.
internal static class ExternalValueEvaluation
{
    internal static void RequireIndependent(ValueTrainingContract contract,QualityReport training,QualityReport external)
    {
        contract.RequireTarget("pair");
        if(training.GateVersion!=2 || external.GateVersion!=2 || training.DataSha256!=contract.DatasetSha256
            || external.DataSha256==training.DataSha256)
            throw new InvalidDataException("External evaluation requires bound training data and a distinct Gate2 dataset.");
        foreach(var q in new[]{training,external})
            contract.RequireDataset(q.SamplingMode,q.ObservationSpec,q.ObservationPerspective,q.ChoicePolicy,
                q.InputsSha256.GetValueOrDefault("KLink.Bot.dll",""),q.InputsSha256);
        var seeds=training.Games.Select(g=>g.Seed).ToHashSet();
        if(external.Games.Any(g=>seeds.Contains(g.Seed)) || external.Games.Select(g=>g.Seed).Distinct().Count()!=external.Games.Count)
            throw new InvalidDataException("External seeds must be unique and disjoint from all original attempts, including holdout/rejected games.");
    }

    internal static ExternalRank Rank(float[] labels,float[] matchups,int[] turns,float[] scores)
    {
        int n=labels.Length;
        if(matchups.Length!=n || turns.Length!=n || scores.Length!=n)throw new InvalidDataException("Ranking lengths differ.");
        var cells=new Dictionary<(float,int),(List<int> W,List<int> L)>();
        for(int i=0;i<n;i++)
        {
            if(labels[i] is not (0f or 1f) || !float.IsFinite(scores[i]) || !float.IsFinite(matchups[i])
                || matchups[i]<0 || matchups[i]!=MathF.Truncate(matchups[i]) || turns[i]<0)
                throw new InvalidDataException("Invalid external ranking row.");
            var key=(matchups[i],turns[i]);
            if(!cells.TryGetValue(key,out var c))cells[key]=c=([],[]);
            (labels[i]==1?c.W:c.L).Add(i);
        }
        int correct=0,incorrect=0,ties=0;
        foreach(var c in cells.Values)for(int i=0;i<Math.Min(c.W.Count,c.L.Count);i++)
        {
            float w=scores[c.W[i]],l=scores[c.L[i]];
            if(w>l)correct++;else if(w<l)incorrect++;else ties++;
        }
        return new(correct,incorrect,ties);
    }

    internal static int Run(Dictionary<string,string> opts)
    {
        string Required(string key)=>opts.TryGetValue(key,out var value)?value:throw new ArgumentException("Missing --"+key);
        string data=Required("data"),training=Required("training-data"),modelPath=Required("model"),output=Required("out");
        foreach(string input in new[]{data,data+".quality.json",training,training+".quality.json",modelPath})
            if(Path.GetFullPath(output).Equals(Path.GetFullPath(input),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("External output must differ from inputs.");
        if(File.Exists(output))throw new InvalidDataException("Refuse overwriting external evaluation output.");
        if(opts.ContainsKey("split-seed") || opts.ContainsKey("split-manifest"))throw new ArgumentException("External evaluation uses all admitted rows, no split options.");
        var model=NnModel.Load(modelPath);var contract=ValueTrainingContract.Read(model);
        contract.RequireRuntime();
        var tq=TrainingAdmission.VerifyDataset(training);var q=TrainingAdmission.VerifyDataset(data);
        RequireIndependent(contract,tq,q);
        var (x,y,gids,pair,n,deckCount)=Program.LoadData(data);
        var accepted=q.Games.Where(g=>g.Accepted).ToDictionary(g=>g.Game);
        var counts=new Dictionary<int,int>();var outcomes=new Dictionary<int,float>();var pairs=new Dictionary<int,float>();
        var score=new float[n];var hq=new float[n];var turns=new int[n];var z=new float[StateEncoder.Dim];
        for(int i=0;i<n;i++)
        {
            var row=x.AsSpan(i*StateEncoder.Dim,StateEncoder.Dim);
            foreach(float value in row)if(!float.IsFinite(value))throw new InvalidDataException("Nonfinite observation.");
            if(!float.IsFinite(gids[i]) || gids[i]!=MathF.Truncate(gids[i]) || gids[i]<0 || gids[i]>=int.MaxValue
                || !accepted.ContainsKey((int)gids[i]) || y[i] is not (0f or 1f)
                || !float.IsFinite(pair[i]) || pair[i]<0 || pair[i]>=deckCount*deckCount || pair[i]!=MathF.Truncate(pair[i]))
                throw new InvalidDataException("External binary contains invalid or unadmitted rows.");
            int gid=(int)gids[i];counts[gid]=counts.GetValueOrDefault(gid)+1;
            if(outcomes.TryGetValue(gid,out float previous) && previous!=y[i]
                || pairs.TryGetValue(gid,out float previousPair) && previousPair!=pair[i])
                throw new InvalidDataException("Game label or matchup changes within external data.");
            outcomes[gid]=y[i];pairs[gid]=pair[i];
            float turn=row[StateEncoder.OffGlobal]*StateEncoder.TurnScale;
            if(!float.IsFinite(turn) || turn<0 || turn>=int.MaxValue)throw new InvalidDataException("Invalid turn.");
            turns[i]=(int)MathF.Round(turn);
            model.NormalizeInto(row,z);score[i]=model.ForwardLogitNormalized(z);
            hq[i]=row[StateEncoder.OffPerspective]-row[StateEncoder.OffOpposite];
        }
        if(accepted.Any(g=>counts.GetValueOrDefault(g.Key)!=g.Value.WrittenSamples))
            throw new InvalidDataException("External per-game row counts differ from admission manifest.");
        object Metrics(int[] idx)
        {
            var labels=idx.Select(i=>y[i]).ToArray();var matchups=idx.Select(i=>pair[i]).ToArray();var ts=idx.Select(i=>turns[i]).ToArray();
            var m=Rank(labels,matchups,ts,idx.Select(i=>score[i]).ToArray());
            return new {rows=idx.Length,games=idx.Select(i=>gids[i]).Distinct().Count(),status=m.Total>0?"available":"no-comparable-pairs",
                model=m,hqDifference=Rank(labels,matchups,ts,idx.Select(i=>hq[i]).ToArray()),constant=Rank(labels,matchups,ts,new float[idx.Length]),
                unpairedRows=idx.Length-2*m.Total};
        }
        var report=new {spec="external-pair-evaluation/v1",modelSha256=TrainingAdmission.Hash(modelPath),trainingDatasetSha256=tq.DataSha256,
            datasetSha256=q.DataSha256,qualitySha256=TrainingAdmission.Hash(data+".quality.json"),
            trainingQualitySha256=TrainingAdmission.Hash(training+".quality.json"),simulatorSha256=contract.SimulatorSha256,
            attempted=q.RequestedGames,accepted=q.AcceptedGames,rejected=q.RejectedGames,
            aggregate=Metrics(Enumerable.Range(0,n).ToArray()),
            matchups=pair.Distinct().Order().Select(p=>new {matchup=p,metrics=Metrics(Enumerable.Range(0,n).Where(i=>pair[i]==p).ToArray())}).ToArray(),
            rows=Enumerable.Range(0,n).Select(i=>new {game=gids[i],matchup=pair[i],turn=turns[i],label=y[i],modelScore=score[i],hqScore=hq[i]}).ToArray(),
            interpretation="All admitted external rows. Same ordered matchup and rounded turn; ith win/ith loss in original row order. Scores are ranks, not win probabilities. Correlated pairs are not independent games. No policy strength or rule certification."};
        File.WriteAllText(output,JsonSerializer.Serialize(report,TrainingAdmission.Json));
        Console.WriteLine($"External evaluation: {q.AcceptedGames}/{q.RequestedGames} games, {n} rows; output {output}");
        return 0;
    }
}
