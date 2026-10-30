using System.Security.Cryptography;
using System.Text.Json;
using KLink.Bot.Training;

namespace KLink.Bot.NN;

/// <summary>Offline behavior-cloning contract. Scores are not legality or win probabilities.</summary>
public sealed record MainActionTrainingContract
{
    public int Version {get;init;}=1;
    public string ObservationSpec {get;init;}=StateEncoder.Spec;
    public string ActionSpec {get;init;}=MainActionFeatures.Spec;
    public string Perspective {get;init;}="actor";
    public string Candidates {get;init;}="structural-superset";
    public string Objective {get;init;}="teacher-index-softmax/v1";
    public string Teacher {get;init;}="";
    public string DatasetSha256 {get;init;}="";
    public Dictionary<string,string> InputsSha256 {get;init;}=new();
    public int SplitSeed {get;init;}
    public int[] TrainingGames {get;init;}=[];
    public int[] ValidationGames {get;init;}=[];

    public void Validate()
    {
        static bool Hash(string? s)=>s is {Length:64} && s.All(Uri.IsHexDigit);
        if(Version!=1 || ObservationSpec!=StateEncoder.Spec || ActionSpec!=MainActionFeatures.Spec
            || Perspective!="actor" || Candidates!="structural-superset" || Objective!="teacher-index-softmax/v1"
            || Teacher is not (OracleActionEpisodeRunner.TeacherContract or OracleActionEpisodeRunner.LastChoiceTeacherContract)
            || !Hash(DatasetSha256) || InputsSha256 is null || InputsSha256.Any(p=>!Hash(p.Value))
            || new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json","KLink.Bot.dll","NNTrain.dll"}.Any(k=>!InputsSha256.ContainsKey(k))
            || TrainingGames is not {Length:>0} || ValidationGames is not {Length:>0}
            || TrainingGames.Concat(ValidationGames).Any(g=>g<0)
            || TrainingGames.Concat(ValidationGames).Distinct().Count()!=TrainingGames.Length+ValidationGames.Length)
            throw new InvalidDataException("Incomplete or unsupported action-model contract.");
    }

    public void RequireInputs(string teacher,IReadOnlyDictionary<string,string> inputs)
    {
        Validate();
        if(Teacher!=teacher || InputsSha256.Count!=inputs.Count
            || InputsSha256.Any(p=>!inputs.TryGetValue(p.Key,out var value) || value!=p.Value))
            throw new InvalidDataException("Action model teacher/input distribution differs from dataset or runtime.");
        using var file=File.OpenRead(typeof(Engine.MatchEngine).Assembly.Location);
        string hash=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if(InputsSha256["KLink.Bot.dll"]!=hash)
            throw new InvalidDataException("Action model simulator differs; regenerate data and retrain.");
    }
}

/// <summary>
/// Shared tanh candidate scorer: context and action interact before the output.
/// Only observation/features enter the network; IDs, teacher labels, game IDs,
/// outcomes and candidate index are excluded. No engine probes or execution.
/// </summary>
public sealed class MainActionModel
{
    public const string Format="KLink.MainActionScorer/tanh-v1";
    public string ModelFormat {get;init;}=Format;
    public int Hidden {get;init;}
    public MainActionTrainingContract Contract {get;init;}=new();
    public double[] ContextWeights {get;init;}=[];
    public double[] ActionWeights {get;init;}=[];
    public double[] Bias {get;init;}=[];
    public double[] OutputWeights {get;init;}=[];

    public static MainActionModel Create(MainActionTrainingContract contract,int hidden,int seed)
    {
        if(hidden is <1 or >64)throw new ArgumentOutOfRangeException(nameof(hidden));
        var random=new Random(seed);
        double[] Weights(int n,int fanIn)=>Enumerable.Range(0,n).Select(_=>(random.NextDouble()*2-1)/Math.Sqrt(fanIn)).ToArray();
        var model=new MainActionModel {Hidden=hidden,Contract=contract,
            ContextWeights=Weights(hidden*StateEncoder.Dim,StateEncoder.Dim),
            ActionWeights=Weights(hidden*MainActionFeatures.Dim,MainActionFeatures.Dim),
            Bias=new double[hidden],OutputWeights=Weights(hidden,hidden)};
        model.Validate();return model;
    }

    public void Validate()
    {
        Contract?.Validate();
        if(ModelFormat!=Format || Contract is null || Hidden is <1 or >64
            || ContextWeights?.Length!=Hidden*StateEncoder.Dim || ActionWeights?.Length!=Hidden*MainActionFeatures.Dim
            || Bias?.Length!=Hidden || OutputWeights?.Length!=Hidden
            || ContextWeights.Concat(ActionWeights).Concat(Bias).Concat(OutputWeights).Any(v=>!double.IsFinite(v)))
            throw new InvalidDataException("Invalid action model format/dimensions/weights.");
    }

    private double[][] Activations(IReadOnlyList<float> observation,IReadOnlyList<IReadOnlyList<float>> actions)
    {
        if(observation.Count!=StateEncoder.Dim || observation.Any(v=>!float.IsFinite(v))
            || actions.Count is <1 or >OracleActionEpisodeRunner.MaxCandidatesPerRow
            || actions.Any(a=>a.Count!=MainActionFeatures.Dim || a.Any(v=>!float.IsFinite(v))))
            throw new InvalidDataException("Invalid action model input.");
        var context=new double[Hidden];
        for(int j=0;j<Hidden;j++)
        {
            context[j]=Bias[j];
            for(int d=0;d<StateEncoder.Dim;d++)context[j]+=ContextWeights[j*StateEncoder.Dim+d]*observation[d];
        }
        var h=new double[actions.Count][];
        for(int i=0;i<actions.Count;i++)
        {
            h[i]=new double[Hidden];
            for(int j=0;j<Hidden;j++)
            {
                double z=context[j];
                for(int d=0;d<MainActionFeatures.Dim;d++)z+=ActionWeights[j*MainActionFeatures.Dim+d]*actions[i][d];
                h[i][j]=Math.Tanh(z);
            }
        }
        return h;
    }

    private double[] Logits(double[][] h)=>h.Select(a=>a.Select((v,j)=>v*OutputWeights[j]).Sum()).ToArray();
    private static double[] Softmax(double[] logits)
    {
        if(logits.Any(v=>!double.IsFinite(v)))throw new InvalidDataException("Nonfinite action logits.");
        double max=logits.Max();var p=logits.Select(v=>Math.Exp(v-max)).ToArray();double sum=p.Sum();
        for(int i=0;i<p.Length;i++)p[i]/=sum;
        return p;
    }

    public double[] Probabilities(IReadOnlyList<float> observation,IReadOnlyList<IReadOnlyList<float>> actions)
        =>Softmax(Logits(Activations(observation,actions)));

    public double[] Probabilities(MainActionRequest request)
    {
        if(request.Actor is not (Engine.Side.Left or Engine.Side.Right) || request.ObservationSpec!=Contract.ObservationSpec
            || request.ActionSpec!=Contract.ActionSpec || !request.CoverageGaps.Contains("legal-mask/unverified"))
            throw new InvalidDataException("Action request differs from scorer contract.");
        return Probabilities(request.Observation,request.Candidates.Select(o=>o.Features).ToArray());
    }

    /// <summary>One row of exact softmax cross entropy SGD; lr=0 evaluates only.</summary>
    public double LossAndUpdate(IReadOnlyList<float> observation,IReadOnlyList<IReadOnlyList<float>> actions,int chosen,double learningRate)
    {
        if((uint)chosen>=(uint)actions.Count || !double.IsFinite(learningRate) || learningRate<0 || learningRate>1)
            throw new ArgumentOutOfRangeException(nameof(chosen));
        var h=Activations(observation,actions);var logits=Logits(h);var p=Softmax(logits);
        double max=logits.Max();double loss=max+Math.Log(logits.Sum(v=>Math.Exp(v-max)))-logits[chosen];
        if(learningRate==0)return loss;
        var contextGradient=new double[Hidden];var outputGradient=new double[Hidden];
        var actionGradient=new double[ActionWeights.Length];
        for(int i=0;i<actions.Count;i++)
        {
            double error=p[i]-(i==chosen?1:0);
            for(int j=0;j<Hidden;j++)
            {
                outputGradient[j]+=error*h[i][j];
                double delta=error*OutputWeights[j]*(1-h[i][j]*h[i][j]);
                contextGradient[j]+=delta;
                for(int d=0;d<MainActionFeatures.Dim;d++)actionGradient[j*MainActionFeatures.Dim+d]+=delta*actions[i][d];
            }
        }
        for(int j=0;j<Hidden;j++)
        {
            OutputWeights[j]-=learningRate*outputGradient[j];Bias[j]-=learningRate*contextGradient[j];
            for(int d=0;d<StateEncoder.Dim;d++)ContextWeights[j*StateEncoder.Dim+d]-=learningRate*contextGradient[j]*observation[d];
        }
        for(int i=0;i<ActionWeights.Length;i++)ActionWeights[i]-=learningRate*actionGradient[i];
        return loss;
    }

    public void Save(string path)
    {
        Validate();File.WriteAllText(path,JsonSerializer.Serialize(this));
    }
    public static MainActionModel Load(string path)
    {
        try
        {
            var model=JsonSerializer.Deserialize<MainActionModel>(File.ReadAllText(path))
                ??throw new InvalidDataException("Missing action model.");
            model.Validate();return model;
        }
        catch(JsonException e){throw new InvalidDataException("Invalid action model JSON.",e);}
    }
}
