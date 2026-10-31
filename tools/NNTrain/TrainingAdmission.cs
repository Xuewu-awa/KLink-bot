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
        bool irLoaded, int samples, bool finite, long choiceFaults = 0, long driverFaults = 0, int pendingChoices = 0)
    {
        var reasons=new List<string>();
        if(!irLoaded)reasons.Add("ir_not_loaded");
        if(!finished)reasons.Add("unfinished");
        else if(winner is not (Side.Left or Side.Right))reasons.Add("invalid_winner");
        if(faults>0)reasons.Add("vm_fault");
        if(choiceFaults>0)reasons.Add("choice_fault");
        if(driverFaults>0)reasons.Add("driver_fault");
        if(pendingChoices>0)reasons.Add("pending_choice");
        reasons.AddRange(unsupported.Where(p=>p.Value>0).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>"unsupported:"+p.Key));
        reasons.AddRange(calls.Where(p=>p.Value>0 && !IsInformational(p.Key)).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>"unimplemented:"+p.Key));
        if(samples==0)reasons.Add("no_samples");
        if(!finite)reasons.Add("nonfinite_observation");
        return reasons.ToArray();
    }

    internal static string Hash(string path) { using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    internal static QualityReport VerifyDataset(string path)
    {
        string manifest=path+".quality.json";
        if(!File.Exists(manifest))throw new InvalidDataException("训练数据缺少质量清单，请用当前 NNTrain dump 重新生成："+manifest);
        var report=JsonSerializer.Deserialize<QualityReport>(File.ReadAllText(manifest),Json) ?? throw new InvalidDataException("质量清单为空");
        if(report.SchemaVersion!=1 || report.GateVersion is not (1 or 2) || report.Rows<=0 || report.AcceptedGames<=0
            || report.Games.Count!=report.RequestedGames || report.Games.Count(g=>g.Accepted)!=report.AcceptedGames
            || report.Games.Count(g=>!g.Accepted)!=report.RejectedGames
            || report.Games.Sum(g=>(long)g.WrittenSamples)!=report.Rows
            || report.Games.Any(g=>g.Accepted ? g.Reasons.Length!=0 || g.WrittenSamples<=0 : g.WrittenSamples!=0)
            || report.Games.Select(g=>g.Game).Distinct().Count()!=report.Games.Count)
            throw new InvalidDataException("质量清单无可用样本或统计不一致");
        // Accepted is a producer claim. The binary hash does not bind this
        // manifest, so independently reapply the execution-health boundary.
        if(report.Games.Any(g=>g.Accepted && (g.VmFaults!=0 || g.ChoiceFaults!=0 || g.DriverFaults!=0 || g.PendingChoices!=0
                || g.UnsupportedOps.Any(p=>p.Value>0) || g.Calls.Any(p=>p.Value>0 && !IsInformational(p.Key))))
            || report.GateVersion==2 && (report.Dim!=KLink.Bot.NN.StateEncoder.Dim
                || report.SamplingMode is not ("turn-end/v1" or "policy-and-choice-before/v1")
                || report.ChoicePolicy!="first-index/v1" || report.ObservationPerspective!="left"
                || report.ObservationSpec!=KLink.Bot.NN.StateEncoder.Spec
                || report.Games.Any(g=>g.SampleKinds.Length!=g.WrittenSamples || g.SampleActors.Length!=g.WrittenSamples
                    || g.SampleKinds.Any(k=>report.SamplingMode=="turn-end/v1" ? k!=0 : k is not (1 or 2))
                    || g.SampleActors.Any(s=>s is not (1 or 2))
                    || g.MainDecisions<g.SampleKinds.Count(k=>k==1) || g.ChoiceDecisions<g.SampleKinds.Count(k=>k==2)
                    || g.ChoiceFaults<0 || g.DriverFaults<0 || g.PendingChoices<0
                    || g.Accepted && g.DriverError.Length!=0)))
            throw new InvalidDataException("采样契约或选牌/驱动健康清单不一致");
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.BaseStream.Length<12 || reader.ReadInt32()!=0x324C4B41 || reader.ReadInt32()!=report.Dim || reader.ReadInt32()!=report.DeckCount
            || report.Dim<=0 || report.DeckCount<2 || reader.BaseStream.Length!=12+report.Rows*(report.Dim+3L)*sizeof(float)
            || !StringComparer.Ordinal.Equals(Hash(path),report.DataSha256))
            throw new InvalidDataException("训练数据格式、长度或 SHA-256 与质量清单不一致");
        return report;
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
    public long ChoiceFaults {get;set;}
    public long DriverFaults {get;set;}
    public int PendingChoices {get;set;}
    public long MainDecisions {get;set;}
    public long ChoiceDecisions {get;set;}
    public string DriverError {get;set;}="";
    public int[] SampleKinds {get;set;}=[];
    public int[] SampleActors {get;set;}=[];
    public Dictionary<string,int> UnsupportedOps {get;set;}=new();
    public Dictionary<string,int> Calls {get;set;}=new();
}

internal sealed class QualityReport
{
    public int SchemaVersion {get;set;}=1;
    public int GateVersion {get;set;}=2;
    public string SamplingMode {get;set;}="turn-end/v1";
    public string ChoicePolicy {get;set;}="first-index/v1";
    public string ObservationPerspective {get;set;}="left";
    public string ObservationSpec {get;set;}=KLink.Bot.NN.StateEncoder.Spec;
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
