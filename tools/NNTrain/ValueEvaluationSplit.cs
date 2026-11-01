using System.Text.Json;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

// The verify command reproduces the saved holdout, rather than evaluating new games.
internal static class ValueEvaluationSplit
{
    internal static HashSet<float> Create(float[] gids,int seed)
    {
        if(gids.Distinct().Take(2).Count()<2)
            throw new InvalidDataException("At least two admitted games required for game-disjoint evaluation.");
        var rng=seed<0?null:new Random(seed);
        var games=gids.Distinct().OrderBy(_=>rng is null?Random.Shared.Next():rng.Next()).ToArray();
        return games.Take(Math.Max(1,(int)(games.Length*.15))).ToHashSet();
    }

    internal static HashSet<float> Read(NnModel model,string datasetHash,float[] gids,int? requestedSeed)
    {
        if(!string.Equals(ValueTrainingContract.Read(model).DatasetSha256,datasetHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Holdout verification requires the bound training dataset.");
        try
        {
            using var doc=JsonDocument.Parse(model.MetaJson);
            var root=doc.RootElement;
            if(!root.TryGetProperty("splitSeed",out var seedElement) || !seedElement.TryGetInt32(out int savedSeed))
                throw new InvalidDataException("Missing or invalid evaluation split seed; retrain the model.");
            HashSet<float> ReadIds(string name)
            {
                if(!root.TryGetProperty(name,out var values) || values.ValueKind!=JsonValueKind.Array)
                    throw new InvalidDataException("Missing or invalid evaluation split IDs; retrain the model.");
                var ids=new HashSet<float>();
                foreach(var value in values.EnumerateArray())
                {
                    if(value.ValueKind!=JsonValueKind.Number || !value.TryGetSingle(out float id)
                        || !float.IsFinite(id) || !ids.Add(id))
                        throw new InvalidDataException("Invalid or duplicate evaluation split ID.");
                }
                if(ids.Count==0)throw new InvalidDataException("Empty evaluation split partition.");
                return ids;
            }
            var train=ReadIds("trainingGameIds");var validation=ReadIds("validationGameIds");
            if(train.Overlaps(validation) || !train.Concat(validation).ToHashSet().SetEquals(gids))
                throw new InvalidDataException("Saved evaluation split must partition the bound dataset games exactly.");
            if(requestedSeed.HasValue && requestedSeed.Value!=savedSeed)
                throw new InvalidDataException("Requested split-seed conflicts with the saved evaluation split.");
            return validation;
        }
        catch(JsonException error){throw new InvalidDataException("Invalid evaluation split metadata.",error);}
        catch(InvalidOperationException error){throw new InvalidDataException("Invalid evaluation split metadata.",error);}
    }
}
