namespace KLink.Bot.NNTrain;

// Counts observations and independent game IDs; never selects or repairs a holdout.
internal sealed record PairCellCoverage(int Matchup,int Turn,int WinRows,int LossRows,int WinGames,int LossGames)
{
    public int Pairs => Math.Min(WinRows,LossRows);
}

internal sealed record PairScopeCoverage(int Rows,int DistinctGames,PairCellCoverage[] Cells)
{
    public int ComparableCells => Cells.Count(c=>c.Pairs>0);
    public int RowsInComparableCells => Cells.Where(c=>c.Pairs>0).Sum(c=>c.WinRows+c.LossRows);
    // Matches PairRank's deterministic min(W,L), not its Cartesian product or SGD draw count.
    public int DeterministicPairs => Cells.Sum(c=>c.Pairs);
    public int PairedRows => 2*DeterministicPairs;
    public int UnpairedRows => Rows-PairedRows;
}

internal sealed record PairCoverageReport(PairScopeCoverage Full,PairScopeCoverage Training,PairScopeCoverage Validation)
{
    public string Spec => "matchup-turn-pair-coverage/v1";
    public int LostPairsBySplit => Full.DeterministicPairs-Training.DeterministicPairs-Validation.DeterministicPairs;
    public int ValidationPairsInTrainComparableCells
    {
        get
        {
            var comparable=Training.Cells.Where(t=>t.Pairs>0).Select(t=>(t.Matchup,t.Turn)).ToHashSet();
            return Validation.Cells.Where(v=>comparable.Contains((v.Matchup,v.Turn))).Sum(v=>v.Pairs);
        }
    }
    public int ValidationPairsOutsideTrainComparableCells => Validation.DeterministicPairs-ValidationPairsInTrainComparableCells;
}

internal static class ValuePairCoverage
{
    private sealed class Cell
    {
        internal int W,L;
        internal readonly HashSet<float> WG=[],LG=[];
    }

    internal static PairCoverageReport Build(float[] labels,float[] games,float[] matchups,int[] turns,HashSet<float> validation)
    {
        int n=labels.Length;
        if(games.Length!=n || matchups.Length!=n || turns.Length!=n)throw new InvalidDataException("Coverage row lengths differ.");
        var maps=Enumerable.Range(0,3).Select(_=>new Dictionary<(int,int),Cell>()).ToArray();
        var ids=Enumerable.Range(0,3).Select(_=>new HashSet<float>()).ToArray();
        var counts=new int[3];
        for(int i=0;i<n;i++)
        {
            if(labels[i] is not (0f or 1f) || !float.IsFinite(games[i]) || games[i]<0 || games[i]!=MathF.Truncate(games[i])
                || !float.IsFinite(matchups[i]) || matchups[i]<0 || matchups[i]>=int.MaxValue
                || matchups[i]!=MathF.Truncate(matchups[i]) || turns[i]<0)
                throw new InvalidDataException("Invalid label, game, matchup or turn in pair coverage.");
            void Add(int scope)
            {
                counts[scope]++;ids[scope].Add(games[i]);var key=((int)matchups[i],turns[i]);
                if(!maps[scope].TryGetValue(key,out var c))maps[scope][key]=c=new Cell();
                if(labels[i]==1){c.W++;c.WG.Add(games[i]);}else{c.L++;c.LG.Add(games[i]);}
            }
            Add(0);Add(validation.Contains(games[i])?2:1);
        }
        if(validation.Count==0 || !validation.IsSubsetOf(ids[0]) || ids[1].Count==0)
            throw new InvalidDataException("Coverage requires nonempty disjoint game partitions.");
        PairScopeCoverage Scope(int i)=>new(counts[i],ids[i].Count,maps[i].OrderBy(x=>x.Key.Item1).ThenBy(x=>x.Key.Item2)
            .Select(x=>new PairCellCoverage(x.Key.Item1,x.Key.Item2,x.Value.W,x.Value.L,x.Value.WG.Count,x.Value.LG.Count)).ToArray());
        return new(Scope(0),Scope(1),Scope(2));
    }

    internal static void Print(PairCoverageReport report)
    {
        Console.WriteLine("排序覆盖（对位 × 回合；样本对数不等于独立对局数）：");
        foreach(var item in new[]{("全体",report.Full),("训练",report.Training),("验证",report.Validation)})
        {
            var c=item.Item2;
            Console.WriteLine($"  {item.Item1}: {c.DistinctGames} 局，{c.Rows} 行，{c.ComparableCells}/{c.Cells.Length} 个可配对单元，"
                +$"{c.DeterministicPairs} 对，{c.UnpairedRows} 行未用于确定性评估配对，{c.RowsInComparableCells} 行位于可配对单元");
        }
        Console.WriteLine($"  切分后减少 {report.LostPairsBySplit} 对；验证中 {report.ValidationPairsOutsideTrainComparableCells} 对所在单元在训练中不能配对。");
        if(report.Full.DeterministicPairs==0)Console.WriteLine("  全体数据缺少同对位、同回合的胜负样本；继续按预定采样方案收集独立对局。");
        else if(report.Validation.DeterministicPairs==0)Console.WriteLine("  全体数据可配对但留出集不可配对；请扩大预先约定的独立对局采样，勿反复挑选切分种子或筛掉不利结果。");
    }
}
