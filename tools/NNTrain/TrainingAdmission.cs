using System.Security.Cryptography;
using System.Text.Json;
using KLink.Bot.Engine;

namespace KLink.Bot.NNTrain;

// Execution health is necessary for training admission; it is not rule certification.
internal static class TrainingAdmission
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    internal static bool IsInformational(string name) =>
        name.StartsWith("<local-ran:", StringComparison.Ordinal) && name.EndsWith('>')
        || name is "<frontline-blocked-full>" or "<frontline-blocked-by-opponent>";

    internal static string[] Check(bool finished, Side winner, long faults,
        IReadOnlyDictionary<string,int> unsupported, IReadOnlyDictionary<string,int> calls,
        bool irLoaded, int samples, bool finite)
    {
        var reasons=new List<string>();
        if(!irLoaded)reasons.Add("ir_not_loaded");
        if(!finished)reasons.Add("unfinished");
        else if(winner is not (Side.Left or Side.Right))reasons.Add("invalid_winner");
        if(faults>0)reasons.Add("vm_fault");
        reasons.AddRange(unsupported.Where(p=>p.Value>0).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>"unsupported:"+p.Key));
        reasons.AddRange(calls.Where(p=>p.Value>0 && !IsInformational(p.Key)).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>"unimplemented:"+p.Key));
        if(samples==0)reasons.Add("no_samples");
        if(!finite)reasons.Add("nonfinite_observation");
        return reasons.ToArray();
    }

    internal static string Hash(string path) { using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    internal static void VerifyDataset(string path)
    {
        string manifest=path+".quality.json";
        if(!File.Exists(manifest))throw new InvalidDataException("训练数据缺少质量清单，请用当前 NNTrain dump 重新生成："+manifest);
        var report=JsonSerializer.Deserialize<QualityReport>(File.ReadAllText(manifest),Json) ?? throw new InvalidDataException("质量清单为空");
        if(report.SchemaVersion!=1 || report.GateVersion!=1 || report.Rows<=0 || report.AcceptedGames<=0
            || report.Games.Count!=report.RequestedGames || report.Games.Count(g=>g.Accepted)!=report.AcceptedGames
            || report.Games.Count(g=>!g.Accepted)!=report.RejectedGames
            || report.Games.Sum(g=>(long)g.WrittenSamples)!=report.Rows
            || report.Games.Any(g=>g.Accepted ? g.Reasons.Length!=0 || g.WrittenSamples<=0 : g.WrittenSamples!=0)
            || report.Games.Select(g=>g.Game).Distinct().Count()!=report.Games.Count)
            throw new InvalidDataException("质量清单无可用样本或统计不一致");
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.BaseStream.Length<12 || reader.ReadInt32()!=0x324C4B41 || reader.ReadInt32()!=report.Dim || reader.ReadInt32()!=report.DeckCount
            || report.Dim<=0 || report.DeckCount<2 || reader.BaseStream.Length!=12+report.Rows*(report.Dim+3L)*sizeof(float)
            || !StringComparer.Ordinal.Equals(Hash(path),report.DataSha256))
            throw new InvalidDataException("训练数据格式、长度或 SHA-256 与质量清单不一致");
    }
}

internal sealed class QualityGame
{
    public int Game {get;set;}
    public ulong Seed {get;set;}
    public string LeftDeck {get;set;}="";
    public string RightDeck {get;set;}="";
    public bool Accepted {get;set;}
    public string[] Reasons {get;set;}=[];
    public int Turns {get;set;}
    public int ObservedSamples {get;set;}
    public int WrittenSamples {get;set;}
    public long VmFaults {get;set;}
    public Dictionary<string,int> UnsupportedOps {get;set;}=new();
    public Dictionary<string,int> Calls {get;set;}=new();
}

internal sealed class QualityReport
{
    public int SchemaVersion {get;set;}=1;
    public int GateVersion {get;set;}=1;
    public string Scope {get;set;}="Execution diagnostics only; no whole-card or rule-fidelity certification.";
    public ulong Seed {get;set;}
    public int RequestedGames {get;set;}
    public int AcceptedGames {get;set;}
    public int RejectedGames {get;set;}
    public long Rows {get;set;}
    public int Dim {get;set;}
    public int DeckCount {get;set;}
    public string DataSha256 {get;set;}="";
    public Dictionary<string,string> InputsSha256 {get;set;}=new();
    public List<QualityGame> Games {get;set;}=new();
}
