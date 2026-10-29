using System.Text.Json;
using System.Security.Cryptography;

namespace KLink.Bot.NN;

/// <summary>Value-model training distribution/provenance; not an action-policy specification.</summary>
public sealed record ValueTrainingContract
{
    public int Version {get;init;}
    public required string SamplingMode {get;init;}
    public required string ObservationSpec {get;init;}
    public required string ObservationPerspective {get;init;}
    public required string ChoicePolicy {get;init;}
    public required string Target {get;init;}
    public required string DatasetSha256 {get;init;}
    public required string SimulatorSha256 {get;init;}
    public required Dictionary<string,string> InputsSha256 {get;init;}

    private static readonly string[] BoundInputs=["cards.json","card-ir.json","card-effects.json","card-vectors.json","event-contracts.json","card-ir-native-defaults.json"];

    public static Dictionary<string,string> SelectInputs(IReadOnlyDictionary<string,string> inputs)
        => BoundInputs.Where(inputs.ContainsKey).ToDictionary(k=>k,k=>inputs[k],StringComparer.Ordinal);

    public static string CurrentSimulatorHash()
    {
        using var stream=File.OpenRead(typeof(Engine.MatchEngine).Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Validate()
    {
        static bool Hash(string? s)=>s is {Length:64} && s.All(Uri.IsHexDigit);
        if(Version!=1 || SamplingMode is not ("turn-end/v1" or "policy-and-choice-before/v1")
            || ObservationSpec!=StateEncoder.Spec || ObservationPerspective!="left"
            || ChoicePolicy!="first-index/v1" || Target is not ("win" or "pair")
            || !Hash(DatasetSha256) || !Hash(SimulatorSha256) || InputsSha256 is null
            || BoundInputs.Take(4).Any(k=>!InputsSha256.ContainsKey(k))
            || InputsSha256.Any(p=>!BoundInputs.Contains(p.Key,StringComparer.Ordinal) || !Hash(p.Value)))
            throw new InvalidDataException("Unsupported or incomplete value-model training contract.");
    }

    public static ValueTrainingContract Read(NnModel model)
    {
        try
        {
            using var doc=JsonDocument.Parse(model.MetaJson);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object || !doc.RootElement.TryGetProperty("trainingContract",out var element))
                throw new InvalidDataException("Model has no bound training distribution; retrain with a Gate2 dataset.");
            var contract=element.Deserialize<ValueTrainingContract>() ?? throw new InvalidDataException("Empty training contract.");
            contract.Validate();
            if(model.EncoderSpec!=contract.ObservationSpec)throw new InvalidDataException("Model and training observation specifications differ.");
            return contract;
        }
        catch(JsonException error){throw new InvalidDataException("Invalid value-model training metadata.",error);}
    }

    public void RequireSampling(string sampling)
    {
        Validate();
        if(SamplingMode!=sampling)throw new InvalidDataException($"Model sampling {SamplingMode} differs from requested {sampling}.");
    }

    public void RequireTarget(string target)
    {
        Validate();
        if(Target!=target)throw new InvalidDataException("Model training objective differs from the consumer contract.");
    }

    public void RequireRuntime()
    {
        Validate();
        if(SimulatorSha256!=CurrentSimulatorHash())throw new InvalidDataException("Model simulator binary differs from the running engine; regenerate data and retrain.");
    }

    public void RequireDataset(string sampling, string observationSpec, string perspective,
        string choicePolicy, string simulatorHash, IReadOnlyDictionary<string,string> inputs)
    {
        RequireSampling(sampling);
        var selected=SelectInputs(inputs);
        if(ObservationSpec!=observationSpec || ObservationPerspective!=perspective || ChoicePolicy!=choicePolicy || SimulatorSha256!=simulatorHash
            || InputsSha256.Count!=selected.Count || InputsSha256.Any(p=>!selected.TryGetValue(p.Key,out var value) || value!=p.Value))
            throw new InvalidDataException("Dataset input/observation contract differs from the model training distribution.");
        // A different dataset hash is allowed for genuinely new evaluation games.
    }
}
