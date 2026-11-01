using System.Text.Json;
using System.Text.Json.Nodes;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

internal static class PairCoverageSelfTest
{
    internal static int Run()
    {
        int checks=0;
        void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
        void Reject(Action a,string why){bool rejected=false;try{a();}catch(InvalidDataException){rejected=true;}Check(rejected,why);}
        string Json(object value)=>JsonSerializer.Serialize(value,TrainingAdmission.Json);
        float[] g=[0,0,0,1,1,2,3,3,4,4,5,5,5];
        float[] y=[1,1,1,0,0,1,0,0,1,1,0,0,0];
        float[] m=[1,1,1,1,1,2,2,2,1,1,1,1,1];
        int[] t=[3,3,3,3,4,3,3,3,3,4,3,3,5];
        HashSet<float> val=[4,5];
        string original=Json(new{g,y,m,t,val});
        var c=ValuePairCoverage.Build(y,g,m,t,val);
        void Scope(PairScopeCoverage scope,int rows,int games,int cells,int comparable,int eligible,int pairs)
        {
            Check(scope.Rows==rows && scope.DistinctGames==games,"row/game counts");
            Check(scope.Cells.Length==cells && scope.ComparableCells==comparable,"cell counts");
            Check(scope.RowsInComparableCells==eligible && scope.DeterministicPairs==pairs,"eligible rows/pairs");
            Check(scope.PairedRows==pairs*2 && scope.UnpairedRows==rows-pairs*2,"paired/unpaired rows");
        }
        Scope(c.Full,13,6,4,3,12,5);Scope(c.Training,8,4,3,2,7,2);Scope(c.Validation,5,2,3,1,3,1);
        Check(c.Full.Cells.SequenceEqual(new PairCellCoverage[]{new(1,3,4,3,2,2),new(1,4,1,1,1,1),new(1,5,0,1,0,1),new(2,3,1,2,1,1)}),"repeated observations remain distinct rows, not distinct games");
        Check(c.LostPairsBySplit==2 && c.ValidationPairsInTrainComparableCells==1 && c.ValidationPairsOutsideTrainComparableCells==0,"split loss/overlap");
        Check(original==Json(new{g,y,m,t,val}),"inputs not mutated");
        Check(Json(c)==Json(ValuePairCoverage.Build(y.Reverse().ToArray(),g.Reverse().ToArray(),m.Reverse().ToArray(),t.Reverse().ToArray(),val)),"row order invariant counts");
        var unseen=ValuePairCoverage.Build([1,0,1,0],[0,1,2,3],[1,1,2,2],[1,1,1,1],[2,3]);
        Check(unseen.ValidationPairsOutsideTrainComparableCells==1 && unseen.ValidationPairsInTrainComparableCells==0,"validation-only cell");
        var crossed=ValuePairCoverage.Build([1,0],[0,1],[1,1],[1,1],[1]);
        Check(crossed.Full.DeterministicPairs==1 && crossed.LostPairsBySplit==1 && crossed.Training.DeterministicPairs==0 && crossed.Validation.DeterministicPairs==0,"all pairs cross game split");
        foreach(bool separateTurn in new[]{false,true})
        {
            var disjoint=ValuePairCoverage.Build([1,0],[0,1],separateTurn?[1,1]:[1,2],separateTurn?[1,2]:[1,1],[1]);
            Check(disjoint.Full.DeterministicPairs==0,"different matchup or turn cannot pair");
        }
        Check(ValuePairCoverage.Build([1,1],[0,1],[1,1],[1,1],[1]).Full.DeterministicPairs==0,"single-label cells");
        Reject(()=>ValuePairCoverage.Build(y,g,m,[1],val),"length mismatch");
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-1f,.5f})
        {
            Reject(()=>ValuePairCoverage.Build([bad,0],[0,1],[1,1],[1,1],[1]),"invalid labels");
            Reject(()=>ValuePairCoverage.Build([1,0],[bad,1],[1,1],[1,1],[1]),"invalid game IDs");
            Reject(()=>ValuePairCoverage.Build([1,0],[0,1],[bad,1],[1,1],[1]),"invalid matchup IDs");
        }
        Reject(()=>ValuePairCoverage.Build([1,0],[0,1],[float.MaxValue,1],[1,1],[1]),"out of range matchup");
        Reject(()=>ValuePairCoverage.Build([1,0],[0,1],[1,1],[-1,1],[1]),"negative turn");
        foreach(HashSet<float> invalid in new HashSet<float>[]{[],[99],[0,1],[1,99]})
            Reject(()=>ValuePairCoverage.Build([1,0],[0,1],[1,1],[1,1],invalid),"invalid partitions");
        foreach(int seed in new[]{0,7,19})
        {
            var rng=new Random(seed);var expected=g.Distinct().OrderBy(_=>rng.Next()).Take(1).ToHashSet();
            Check(ValueEvaluationSplit.Create(g,seed).SetEquals(expected),"shared split preserves original algorithm");
            Check(ValueEvaluationSplit.Create(g,seed).SetEquals(ValueEvaluationSplit.Create(g,seed)),"seed repeatable");
        }
        Reject(()=>ValueEvaluationSplit.Create([0,0],7),"one game cannot be split");
        string dir=Path.Combine(Path.GetTempPath(),"klink-pair-coverage-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);var console=Console.Out;
        string Capture(Func<int> action)
        {
            using var w=new StringWriter();try{Console.SetOut(w);Check(action()==0,"command exit");return w.ToString();}
            finally{Console.SetOut(console);}
        }
        try
        {
            string data=Path.Combine(dir,"data.bin"),model=Path.Combine(dir,"model.bin"),report=Path.Combine(dir,"coverage.json");
            foreach(bool decision in new[]{false,true})foreach(string target in new[]{"pair","win"})
            {
                var selected=ValueEvaluationSplit.Create(Enumerable.Range(0,14).Select(i=>(float)i).ToArray(),7).Order().ToArray();
                bool Winner(int id)=>id==selected[0] || (id!=selected[1] && id%2==1);
                using(var w=new BinaryWriter(File.Create(data)))
                {
                    w.Write(0x324C4B41);w.Write(StateEncoder.Dim);w.Write(2);
                    for(int id=0;id<14;id++)for(int row=0;row<2;row++)
                    {
                        var x=new float[StateEncoder.Dim];x[0]=.1f;x[1]=Winner(id)?1:-1;
                        foreach(float v in x)w.Write(v);
                        w.Write(Winner(id)?1f:0f);w.Write((float)id);w.Write(1f);
                    }
                }
                var q=new QualityReport{RequestedGames=14,AcceptedGames=14,Rows=28,Dim=StateEncoder.Dim,DeckCount=2,
                    DataSha256=TrainingAdmission.Hash(data),SamplingMode=decision?"policy-and-choice-before/v1":"turn-end/v1",
                    InputsSha256=new(){["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash()}};
                foreach(string key in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json"})q.InputsSha256[key]=new string('a',64);
                for(int id=0;id<14;id++)q.Games.Add(new QualityGame{Game=id,Accepted=true,ObservedSamples=2,WrittenSamples=2,
                    SampleKinds=decision?[1,1]:[0,0],SampleActors=[1,1],MainDecisions=decision?2:0});
                File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));
                var opts=new Dictionary<string,string>{{"data",data},{"out",model},{"model",model},{"epochs","2"},{"hidden","2"},{"target",target},{"split-seed","7"},{"eval-every","1"}};
                string train=Capture(()=>Program.Train(opts));
                var meta=JsonNode.Parse(NnModel.Load(model).MetaJson)!;
                var saved=meta["pairCoverage"]!;
                Check(saved is not null,"Train must save pair coverage");
                Check(saved!["Validation"]!["DeterministicPairs"]!.GetValue<int>()==2,"saved validation pairs");
                Check(saved["Training"]!["RowsInComparableCells"]!.GetValue<int>()==24,"training eligible rows");
                Check(meta["validationMetricTotal"]!.GetValue<int>()==(target=="pair"?2:4),"actual evaluator denominator agrees with coverage");
                Check(meta["processedTrainingUnits"]!.GetValue<long>()==(target=="pair"?24:48),"SGD processed units differ from deterministic pairs");
                Check(train.Contains("排序覆盖") && Capture(()=>Program.Verify(opts)).Contains("排序覆盖"),"Train and Verify print diagnosis");
                var co=new Dictionary<string,string>{{"data",data},{"out",report},{"split-seed","7"}};
                Capture(()=>Program.Coverage(co));string first=File.ReadAllText(report);
                Capture(()=>Program.Coverage(co));Check(first==File.ReadAllText(report),"diagnosis deterministic");
                var result=JsonNode.Parse(first)!;
                Check(result["simulatorMatchesCurrent"]!.GetValue<bool>(),"current simulator fingerprint");
                Check(result["coverage"]!["validation"]!["deterministicPairs"]!.GetValue<int>()==2,"seeded coverage pairs");
                Check(result["validationGameIds"]!.ToJsonString()==meta["validationGameIds"]!.ToJsonString(),"same seeded holdout");
                co.Remove("split-seed");Reject(()=>Program.Coverage(co),"diagnosis requires reproducible split");
                foreach(string bad in new[]{"x","-1","1.5"}){co["split-seed"]=bad;Reject(()=>Program.Coverage(co),"bad coverage seed");}
                co.Remove("split-seed");co["model"]=model;Capture(()=>Program.Coverage(co));
                result=JsonNode.Parse(File.ReadAllText(report))!;
                Check(result["splitSource"]!.GetValue<string>()=="saved-model" && result["validationGameIds"]!.ToJsonString()==meta["validationGameIds"]!.ToJsonString(),"model exact holdout");
                Check(JsonNode.DeepEquals(result["coverage"],JsonNode.Parse(Json(saved.Deserialize<PairCoverageReport>()))),"model coverage matches saved metadata");
                co["split-seed"]="19";Reject(()=>Program.Coverage(co),"conflicting saved seed");co.Remove("split-seed");
                foreach(string input in new[]{data,data+".quality.json",model})
                {
                    string hash=TrainingAdmission.Hash(input);co["out"]=input;
                    Reject(()=>Program.Coverage(co),"diagnostic output collision");Check(TrainingAdmission.Hash(input)==hash,"input bytes preserved");
                }
                co["out"]=report;
                q.InputsSha256["KLink.Bot.dll"]=new string('b',64);
                File.WriteAllText(data+".quality.json",JsonSerializer.Serialize(q,TrainingAdmission.Json));
                Reject(()=>Program.Coverage(co),"model dataset simulator contract required");
                co.Remove("model");co["split-seed"]="7";Capture(()=>Program.Coverage(co));
                Check(!JsonNode.Parse(File.ReadAllText(report))!["simulatorMatchesCurrent"]!.GetValue<bool>(),"historical diagnosis explicitly identifies runtime mismatch");
            }
        }
        finally{Console.SetOut(console);Directory.Delete(dir,true);}
        Console.WriteLine($"Pair coverage: {checks} checks passed (synthetic data; no strength claim)");return 0;
    }
}
