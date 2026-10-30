using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

internal sealed record ActionDatasetHeader(int Version,string Kind,string ObservationSpec,string ActionSpec,
    string Perspective,string Teacher,string Candidates,ulong Seed,int RequestedGames,Dictionary<string,string> InputsSha256);
internal sealed record ActionDatasetRow(int Game,int Step,Side Actor,int Turn,float[] Observation,
    MainActionOption[] Candidates,int ChosenIndex,string[] CoverageGaps);
internal sealed class ActionGameQuality
{
    public int Game {get;set;}
    public ulong Seed {get;set;}
    public string LeftDeck {get;set;}="";
    public string RightDeck {get;set;}="";
    public bool Accepted {get;set;}
    public bool Finished {get;set;}
    public Side Winner {get;set;}
    public int BufferedRows {get;set;}
    public int WrittenRows {get;set;}
    public int ComparedSteps {get;set;}
    public long PlanningPrefixRoots {get;set;}
    public int PlanningDecisions {get;set;}
    public long MainFaults {get;set;}
    public long MainRejections {get;set;}
    public long AppliedActions {get;set;}
    public long ChoiceFaults {get;set;}
    public int PendingChoices {get;set;}
    public long VmFaults {get;set;}
    public Dictionary<string,int> Unsupported {get;set;}=[];
    public Dictionary<string,int> Calls {get;set;}=[];
    public string[] Reasons {get;set;}=[];
    public string SnapshotDifference {get;set;}
}
internal sealed class ActionDatasetQuality
{
    public required ActionDatasetHeader Header {get;set;}
    public string DataSha256 {get;set;}="";
    public int AcceptedGames {get;set;}
    public int RejectedGames {get;set;}
    public long Rows {get;set;}
    public List<ActionGameQuality> Games {get;set;}=[];
}

internal static class ActionDatasetTool
{
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    internal static string[] AdmissionReasons(OracleActionEpisodeResult result,bool irLoaded)
    {
        var reasons=new List<string>(result.Errors);
        foreach(var engine in new[]{result.Teacher,result.Replay})
            reasons.AddRange(TrainingAdmission.Check(engine.State.IsFinished,engine.State.Winner,engine.Api.Vm.FaultedPrograms,
                engine.Api.Vm.UnsupportedOps,engine.UnimplementedCalls,irLoaded,result.Labels.Count,true,
                result.ChoiceFaults,pendingChoices:engine.PendingSelectionCount));
        reasons.AddRange(TrainingAdmission.Check(result.Replay.State.IsFinished,result.Replay.State.Winner,
            result.PlanningVmFaults,result.PlanningUnsupported,result.PlanningCalls,irLoaded,result.Labels.Count,true,
            result.PlanningChoiceFaults,pendingChoices:result.PlanningPendingChoices));
        if(result.PlanningDecisions!=result.Labels.Count
            || result.PlanningPrefixRoots!=(long)result.Labels.Count*(result.Labels.Count-1)/2)
            reasons.Add("unverified_planning_count");
        if(result.MainFaults!=0)reasons.Add("main_action_fault");
        if(result.MainRejections!=0)reasons.Add("main_action_rejected");
        if(result.AppliedActions!=result.ComparedSteps || result.ComparedSteps!=result.Labels.Count)reasons.Add("unverified_root_count");
        return reasons.Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
    }

    internal static int Dump(CardDatabase db,StateEncoder.CardVecs vectors,Dictionary<string,string> opts,string dataDir,string vectorPath)
    {
        int games=Int(opts,"games",100);int rawSeed=Int(opts,"seed",1);
        if(games<=0 || rawSeed<0)throw new ArgumentException("--games must be positive and --seed nonnegative.");
        var choicePolicy=ParseChooseOnePolicy(opts.GetValueOrDefault("choose-one-policy","native"));
        if(KismetLibrary.Default is null)throw new InvalidOperationException("IR must be loaded for action labels.");
        ulong seed=(ulong)rawSeed;string path=opts.GetValueOrDefault("out","action-labels.jsonl.gz");
        var decks=MetaDecks.All;
        var expanded=decks.Select(d=>
        {
            var result=DeckCodeParser.Expand(DeckCodeParser.Parse(d.Code),db.DeckCodeIds,out var unknown);
            if(unknown.Count!=0)throw new InvalidDataException("Unknown deck codes: "+d.Name);
            return result;
        }).ToArray();
        var inputs=CurrentInputs(dataDir,vectorPath);
        var header=new ActionDatasetHeader(3,"KLink.MainActionLabels",StateEncoder.Spec,MainActionFeatures.Spec,
            "actor",OracleActionEpisodeRunner.ContractFor(choicePolicy),"structural-superset",seed,games,inputs);
        var quality=new ActionDatasetQuality {Header=header};
        using(var file=File.Create(path))
        using(var zip=new GZipStream(file,CompressionLevel.Fastest))
        using(var writer=new StreamWriter(zip,new UTF8Encoding(false)))
        {
            writer.WriteLine(JsonSerializer.Serialize(header,Json));
            for(int g=0;g<games;g++)
            {
                var pick=new DeterministicRandom(seed^(0x9E3779B97F4A7C15UL*((ulong)g+1)));
                int di=pick.Next(decks.Count),dj=pick.Next(decks.Count-1);if(dj>=di)dj++;
                bool swap=pick.Next(2)==1;int l=swap?dj:di,r=swap?di:dj;
                var result=OracleActionEpisodeRunner.Run(db,vectors,expanded[l],expanded[r],seed+(ulong)g,choicePolicy);
                var reasons=AdmissionReasons(result,true);var engines=new[]{result.Teacher,result.Replay};
                Dictionary<string,int> Merge(Func<MatchEngine,IReadOnlyDictionary<string,int>> get)=>engines
                    .SelectMany(e=>get(e)).GroupBy(p=>p.Key).ToDictionary(p=>p.Key,p=>p.Sum(v=>v.Value));
                Dictionary<string,int> WithPlanning(Dictionary<string,int> values,IReadOnlyDictionary<string,int> planning)
                {foreach(var p in planning)values[p.Key]=values.GetValueOrDefault(p.Key)+p.Value;return values;}
                var game=new ActionGameQuality {Game=g,Seed=seed+(ulong)g,LeftDeck=decks[l].Name,RightDeck=decks[r].Name,
                    Accepted=reasons.Length==0,Finished=engines.All(e=>e.State.IsFinished),Winner=result.Replay.State.Winner,
                    BufferedRows=result.Labels.Count,WrittenRows=reasons.Length==0?result.Labels.Count:0,
                    ComparedSteps=result.ComparedSteps,PlanningPrefixRoots=result.PlanningPrefixRoots,PlanningDecisions=result.PlanningDecisions,
                    MainFaults=result.MainFaults,MainRejections=result.MainRejections,
                    AppliedActions=result.AppliedActions,ChoiceFaults=result.ChoiceFaults+result.PlanningChoiceFaults,
                    PendingChoices=engines.Sum(e=>e.PendingSelectionCount)+result.PlanningPendingChoices,
                    VmFaults=engines.Sum(e=>(long)e.Api.Vm.FaultedPrograms)+result.PlanningVmFaults,
                    Unsupported=WithPlanning(Merge(e=>e.Api.Vm.UnsupportedOps),result.PlanningUnsupported),
                    Calls=WithPlanning(Merge(e=>e.UnimplementedCalls),result.PlanningCalls),Reasons=reasons,
                    SnapshotDifference=result.SnapshotDifference};
                quality.Games.Add(game);
                if(!game.Accepted){quality.RejectedGames++;continue;}
                quality.AcceptedGames++;
                quality.Rows+=WriteAdmittedLabels(writer,g,result,true);
            }
        }
        quality.DataSha256=TrainingAdmission.Hash(path);
        File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(quality,TrainingAdmission.Json),new UTF8Encoding(false));
        Console.WriteLine($"Action labels: accepted {quality.AcceptedGames} / rejected {quality.RejectedGames}; rows {quality.Rows}; {path}");
        foreach(var reason in quality.Games.SelectMany(g=>g.Reasons).GroupBy(s=>s).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key,StringComparer.Ordinal))
            Console.WriteLine($"  {reason.Key}: {reason.Count()} games");
        return quality.RejectedGames!=0 || quality.Rows==0?2:0;
    }

    internal static int VerifyCommand(Dictionary<string,string> opts)
    {
        var report=Verify(opts.GetValueOrDefault("data","action-labels.jsonl.gz"));
        Console.WriteLine($"Verified action labels: {report.Rows} rows, {report.AcceptedGames} admitted games.");return 0;
    }

    internal static int WriteAdmittedLabels(TextWriter writer,int game,OracleActionEpisodeResult result,bool irLoaded)
    {
        if(AdmissionReasons(result,irLoaded).Length!=0)return 0;
        foreach(var label in result.Labels)
        {
            var request=label.Request;
            var row=new ActionDatasetRow(game,label.Step,request.Actor,request.Turn,request.Observation.ToArray(),
                request.Candidates.ToArray(),label.ChosenIndex,request.CoverageGaps.ToArray());
            writer.WriteLine(JsonSerializer.Serialize(row,Json));
        }
        return result.Labels.Count;
    }

    internal static ActionDatasetQuality Verify(string path)
    {
        try
        {
            var q=JsonSerializer.Deserialize<ActionDatasetQuality>(File.ReadAllText(path+".quality.json"),Json)
                ?? throw new InvalidDataException("Missing action quality manifest.");
            var h=q.Header;
            if(h is null || h.Version!=3 || h.Kind!="KLink.MainActionLabels" || h.Perspective!="actor"
                || h.ObservationSpec!=StateEncoder.Spec || h.ActionSpec!=MainActionFeatures.Spec
                || h.Teacher is not (OracleActionEpisodeRunner.TeacherContract or OracleActionEpisodeRunner.LastChoiceTeacherContract)
                || h.Candidates!="structural-superset"
                || q.Rows<=0 || q.AcceptedGames<=0 || q.Games.Count!=h.RequestedGames
                || q.Games.Select(g=>g.Game).Distinct().Count()!=q.Games.Count
                || q.Games.Any(g=>g.Game<0 || g.Game>=h.RequestedGames || g.Seed!=h.Seed+(ulong)g.Game)
                || q.Games.Count(g=>g.Accepted)!=q.AcceptedGames || q.Games.Count(g=>!g.Accepted)!=q.RejectedGames
                || q.Games.Sum(g=>(long)g.WrittenRows)!=q.Rows || q.DataSha256!=TrainingAdmission.Hash(path))
                throw new InvalidDataException("Action dataset header/count/hash mismatch.");
            foreach(string key in new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json","KLink.Bot.dll","NNTrain.dll"})
                if(!h.InputsSha256.TryGetValue(key,out var hash) || hash.Length!=64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Missing action dataset input fingerprint.");
            foreach(var g in q.Games)
            {
                if(g.WrittenRows<0 || g.BufferedRows<g.WrittenRows || g.BufferedRows>OracleActionEpisodeRunner.MaxRows
                    || g.PlanningPrefixRoots<0 || g.PlanningPrefixRoots>OracleActionEpisodeRunner.MaxPlanningPrefixRoots
                    || g.PlanningDecisions<0 || g.PlanningDecisions>g.BufferedRows+1
                    || g.Accepted && (!g.Finished || g.Winner is not (Side.Left or Side.Right) || g.Reasons.Length!=0
                        || g.PlanningDecisions!=g.WrittenRows || g.PlanningPrefixRoots!=(long)g.WrittenRows*(g.WrittenRows-1)/2
                        || g.WrittenRows==0 || g.BufferedRows!=g.WrittenRows || g.WrittenRows!=g.ComparedSteps || g.AppliedActions!=g.ComparedSteps
                        || g.MainFaults!=0 || g.MainRejections!=0 || g.ChoiceFaults!=0 || g.PendingChoices!=0 || g.VmFaults!=0
                        || g.Unsupported.Any(p=>p.Value>0) || g.Calls.Any(p=>p.Value>0 && !TrainingAdmission.IsInformational(p.Key)))
                    || !g.Accepted && (g.WrittenRows!=0 || g.Reasons.Length==0))
                    throw new InvalidDataException("Action game health/row statistics mismatch.");
            }
            using var file=File.OpenRead(path);using var zip=new GZipStream(file,CompressionMode.Decompress);
            using var reader=new StreamReader(zip,Encoding.UTF8);
            var stored=JsonSerializer.Deserialize<ActionDatasetHeader>(reader.ReadLine()??"",Json);
            if(JsonSerializer.Serialize(stored,Json)!=JsonSerializer.Serialize(h,Json))throw new InvalidDataException("Action header differs from manifest.");
            long count=0;int last=-1;var steps=q.Games.ToDictionary(g=>g.Game,_=>0);
            var optionCounts=q.Games.ToDictionary(g=>g.Game,_=>0L);
            while(reader.ReadLine() is {} line)
            {
                var row=JsonSerializer.Deserialize<ActionDatasetRow>(line,Json)??throw new InvalidDataException("Null action row.");
                if(!steps.ContainsKey(row.Game) || row.Game<last || !q.Games.Single(g=>g.Game==row.Game).Accepted
                    || row.Step!=steps[row.Game]++ || row.Actor is not (Side.Left or Side.Right) || row.Turn<=0
                    || row.Observation is null || row.Observation.Length!=StateEncoder.Dim || !row.Observation.All(float.IsFinite)
                    || row.Candidates is null || row.Candidates.Length is <1 or >OracleActionEpisodeRunner.MaxCandidatesPerRow
                    || row.ChosenIndex<0 || row.ChosenIndex>=row.Candidates.Length
                    || row.CoverageGaps is null || !row.CoverageGaps.Contains("legal-mask/unverified"))
                    throw new InvalidDataException("Action label/observation shape mismatch.");
                var commands=new HashSet<MainActionCommand>();
                for(int i=0;i<row.Candidates.Length;i++)
                {
                    var option=row.Candidates[i]??throw new InvalidDataException("Null action candidate.");
                    var c=option.Command??throw new InvalidDataException("Null action command.");
                    bool shape=c.Kind switch {MainActionKind.EndTurn=>c.SourceId==0 && c.TargetId==0 && c.Slot==-1,
                        MainActionKind.Play=>c.SourceId>0 && c.TargetId>=0 && c.Slot==-1,
                        MainActionKind.Attack=>c.SourceId>0 && c.TargetId>0 && c.Slot==-1,
                        MainActionKind.Move=>c.SourceId>0 && c.TargetId==0 && c.Slot>=0,_=>false};
                    shape &= c.Kind==MainActionKind.Play ? c.ChooseOne>=-1 : c.ChooseOne==-1;
                    int bit=c.Kind switch {MainActionKind.Play=>0,MainActionKind.Attack=>1,MainActionKind.Move=>2,_=>3};
                    if(!shape || option.Index!=i || !commands.Add(c) || option.Features is null || option.Features.Count!=MainActionFeatures.Dim
                        || !option.Features.All(float.IsFinite) || option.Features.Take(4).Where((f,j)=>f!=(j==bit?1:0)).Any())
                        throw new InvalidDataException("Action candidate/feature mismatch.");
                    float choice=option.Features[16],branches=option.Features[17];
                    if(c.Kind==MainActionKind.Play
                        ? choice!=(float)c.ChooseOne+1 || branches<0 || branches!=MathF.Truncate(branches)
                            || (branches==0 ? c.ChooseOne!=-1 : c.ChooseOne<0 || c.ChooseOne>=branches)
                        : choice!=0 || branches!=0)
                        throw new InvalidDataException("Action choose-one command/feature mismatch.");
                }
                var chosen=row.Candidates[row.ChosenIndex];
                if(h.Teacher==OracleActionEpisodeRunner.LastChoiceTeacherContract && chosen.Command.Kind==MainActionKind.Play
                    && chosen.Features[17]>0 && chosen.Command.ChooseOne!=chosen.Features[17]-1)
                    throw new InvalidDataException("Chosen branch differs from fixed-last teacher contract.");
                optionCounts[row.Game]+=row.Candidates.Length;
                if(optionCounts[row.Game]>OracleActionEpisodeRunner.MaxOptionsPerEpisode)
                    throw new InvalidDataException("Action episode option limit exceeded.");
                last=row.Game;count++;
            }
            if(count!=q.Rows || q.Games.Any(g=>steps[g.Game]!=g.WrittenRows))throw new InvalidDataException("Action row count mismatch.");
            return q;
        }
        catch(JsonException error){throw new InvalidDataException("Invalid action dataset JSON.",error);}
    }

    internal static Dictionary<string,string> CurrentInputs(string dataDir,string vectorPath)
    {
        var inputs=Directory.GetFiles(dataDir,"*.json").OrderBy(p=>p,StringComparer.Ordinal).ToDictionary(Path.GetFileName,TrainingAdmission.Hash);
        inputs["card-vectors.json"]=TrainingAdmission.Hash(vectorPath);
        inputs["KLink.Bot.dll"]=TrainingAdmission.Hash(typeof(MatchEngine).Assembly.Location);
        inputs["NNTrain.dll"]=TrainingAdmission.Hash(typeof(ActionDatasetTool).Assembly.Location);
        return inputs;
    }

    internal static OracleChooseOnePolicy ParseChooseOnePolicy(string value)=>value switch
    {
        "native"=>OracleChooseOnePolicy.Native,
        "last"=>OracleChooseOnePolicy.Last,
        _=>throw new ArgumentException("--choose-one-policy must be native or last.")
    };

    private static int Int(Dictionary<string,string> opts,string key,int fallback)=>opts.TryGetValue(key,out var value)
        ? int.Parse(value,System.Globalization.CultureInfo.InvariantCulture):fallback;
}
