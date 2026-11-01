using System.Text.Json;

namespace KLink.Bot.NNTrain;

// A file binding is evidence of the selected partition, not proof it was chosen before outcomes.
internal sealed class FrozenValueSplit
{
    public int SchemaVersion {get;set;}
    public string DatasetSha256 {get;set;}="";
    public string SourcePlanSha256 {get;set;}="";
    public float[] TrainingGameIds {get;set;}=[];
    public float[] ValidationGameIds {get;set;}=[];

    internal static HashSet<float> Load(string path,string hash,float[] games)
    {
        FrozenValueSplit split;
        try{split=JsonSerializer.Deserialize<FrozenValueSplit>(File.ReadAllText(path),TrainingAdmission.Json);}
        catch(JsonException e){throw new InvalidDataException("Invalid frozen split JSON.",e);}
        if(split is null || split.SchemaVersion!=1 || split.DatasetSha256!=hash
            || split.SourcePlanSha256 is null || split.SourcePlanSha256.Length!=64 || split.SourcePlanSha256.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("Frozen split version, dataset or plan binding is invalid.");
        bool Valid(float[] ids)=>ids is not null && ids.Length>0 && ids.All(g=>float.IsFinite(g) && g>=0 && g==MathF.Truncate(g))
            && ids.Distinct().Count()==ids.Length;
        if(!Valid(split.TrainingGameIds) || !Valid(split.ValidationGameIds))
            throw new InvalidDataException("Frozen split IDs must be nonempty, unique nonnegative integers.");
        var train=split.TrainingGameIds.ToHashSet();var validation=split.ValidationGameIds.ToHashSet();
        if(train.Overlaps(validation) || !train.Concat(validation).ToHashSet().SetEquals(games))
            throw new InvalidDataException("Frozen split must partition admitted dataset games exactly.");
        return validation;
    }

    internal static void RequireSaved(string path,string datasetHash,float[] games,string metadata)
    {
        var ids=Load(path,datasetHash,games);
        using var doc=JsonDocument.Parse(metadata);var root=doc.RootElement;
        if(!root.TryGetProperty("splitSource",out var source) || source.GetString()!="frozen-manifest/v1"
            || !root.TryGetProperty("splitManifestSha256",out var binding) || binding.ValueKind!=JsonValueKind.String
            || binding.GetString()!=TrainingAdmission.Hash(path)
            || !ids.SetEquals(root.GetProperty("validationGameIds").EnumerateArray().Select(x=>x.GetSingle())))
            throw new InvalidDataException("Requested frozen split conflicts with saved model binding.");
    }
}
