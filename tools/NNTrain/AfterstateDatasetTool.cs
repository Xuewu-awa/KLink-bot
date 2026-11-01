using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KLink.Bot.Engine;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

internal sealed record AfterstateDatasetHeader(int Version,string Kind,string ObservationSpec,
    string Perspective,string Target,string SourceActionDataSha256,int SourceActionSchema,
    Dictionary<string,string> InputsSha256);
internal sealed record AfterstateDatasetRow(int Game,int Step,float[] Observation,float Target);
internal sealed class AfterstateDatasetQuality
{
    public required AfterstateDatasetHeader Header {get;set;}
    public string DataSha256 {get;set;}="";
    public int AcceptedGames {get;set;}
    public long Rows {get;set;}
    public List<AfterstateGameQuality> Games {get;set;}=[];
}
internal sealed class AfterstateGameQuality
{
    public int Game {get;set;}
    public ulong Seed {get;set;}
    public bool Accepted {get;set;}
    public Side Winner {get;set;}
    public int Rows {get;set;}
}

internal static class AfterstateDatasetTool
{
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};

    internal static int Build(Dictionary<string,string> opts)
    {
        string source=opts.GetValueOrDefault("actions","action-labels.jsonl.gz");
        string output=opts.GetValueOrDefault("out","afterstate-win.jsonl.gz");
        if(File.Exists(output))throw new IOException("Refusing to overwrite afterstate dataset.");
        var action=ActionDatasetTool.Verify(source);
        var header=new AfterstateDatasetHeader(1,"KLink.AfterstateWin",StateEncoder.Spec,"left","win",
            TrainingAdmission.Hash(source),action.Header.Version,new(action.Header.InputsSha256));
        var games=action.Games.Where(g=>g.Accepted).ToDictionary(g=>g.Game);
        var quality=new AfterstateDatasetQuality{Header=header,AcceptedGames=games.Count,
            Games=action.Games.Select(g=>new AfterstateGameQuality{Game=g.Game,Seed=g.Seed,Accepted=g.Accepted,Winner=g.Winner}).ToList()};
        {
            using var file=File.Create(output);using var zip=new GZipStream(file,CompressionLevel.Fastest);
            using var writer=new StreamWriter(zip,new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(header,Json));
            using(var input=File.OpenRead(source))using(var inZip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(inZip))
            {
                _=reader.ReadLine();
                while(reader.ReadLine() is {} line)
                {
                    var row=JsonSerializer.Deserialize<ActionDatasetRow>(line,ActionDatasetTool.Json)??throw new InvalidDataException("Null action row.");
                    if(!games.TryGetValue(row.Game,out var game))continue;
                    if(row.AfterObservation is null)throw new InvalidDataException("Action row has no afterstate.");
                    var outputRow=new AfterstateDatasetRow(row.Game,row.Step,row.AfterObservation,game.Winner==Side.Left?1:0);
                    writer.WriteLine(JsonSerializer.Serialize(outputRow,Json));
                    quality.Rows++;var q=quality.Games.Single(g=>g.Game==row.Game);q.Rows++;
                }
            }
        }
        quality.DataSha256=TrainingAdmission.Hash(output);
        File.WriteAllText(output+".quality.json",JsonSerializer.Serialize(quality,TrainingAdmission.Json),new UTF8Encoding(false));
        Verify(output);
        Console.WriteLine($"Afterstate win data: {quality.AcceptedGames} games, {quality.Rows} rows; {output}");
        return 0;
    }

    internal static AfterstateDatasetQuality Verify(string path)
    {
        var q=JsonSerializer.Deserialize<AfterstateDatasetQuality>(File.ReadAllText(path+".quality.json"),TrainingAdmission.Json)
            ?? throw new InvalidDataException("Missing afterstate quality manifest.");
        var h=q.Header;
        if(h is null || h.Version!=1 || h.Kind!="KLink.AfterstateWin" || h.ObservationSpec!=StateEncoder.Spec
            || h.Perspective!="left" || h.Target!="win" || h.SourceActionSchema!=5
            || h.SourceActionDataSha256.Length!=64 || q.AcceptedGames<=0 || q.Rows<=0
            || q.Games.Count==0 || q.Games.Count(g=>g.Accepted)!=q.AcceptedGames
            || q.Games.Sum(g=>g.Rows)!=q.Rows || q.DataSha256!=TrainingAdmission.Hash(path))
            throw new InvalidDataException("Afterstate header/count/hash mismatch.");
        if(h.InputsSha256 is null || h.InputsSha256.Any(p=>p.Value.Length!=64 || !p.Value.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Afterstate input fingerprint missing.");
        using var file=File.OpenRead(path);using var zip=new GZipStream(file,CompressionMode.Decompress);using var reader=new StreamReader(zip);
        var stored=JsonSerializer.Deserialize<AfterstateDatasetHeader>(reader.ReadLine()??"",Json);
        if(JsonSerializer.Serialize(stored,Json)!=JsonSerializer.Serialize(h,Json))throw new InvalidDataException("Afterstate header differs from manifest.");
        var counts=q.Games.ToDictionary(g=>g.Game,_=>0);var lastGame=-1;var nextStep=q.Games.ToDictionary(g=>g.Game,_=>0);
        long rows=0;
        while(reader.ReadLine() is {} line)
        {
            var row=JsonSerializer.Deserialize<AfterstateDatasetRow>(line,Json)??throw new InvalidDataException("Null afterstate row.");
            var game=q.Games.SingleOrDefault(g=>g.Game==row.Game);
            if(game is null || !game.Accepted || row.Game<lastGame || row.Step!=nextStep[row.Game]++
                || row.Observation is null || row.Observation.Length!=StateEncoder.Dim || !row.Observation.All(float.IsFinite)
                || !float.IsFinite(row.Target) || row.Target is not (0 or 1))
                throw new InvalidDataException("Afterstate row shape/target mismatch.");
            counts[row.Game]++;rows++;lastGame=row.Game;
        }
        if(rows!=q.Rows || q.Games.Any(g=>counts[g.Game]!=g.Rows))throw new InvalidDataException("Afterstate row count mismatch.");
        return q;
    }
}
