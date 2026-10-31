using System.Security.Cryptography;
using System.Text.Json;

namespace KLink.Bot.Effects.Blueprint;

/// <summary>Observed VM call location, not a display exemption or rule certificate.
/// Program hash uses serialized runtime IR (including synthetic dispatch), not raw pak bytes.
/// Null asset/export identity means unattributed. A joined path is explicitly ambiguous.</summary>
public sealed record KismetMissingCallSite(string Key, string? SelfCard, string? AssetName,
    string? FunctionName, int? SourceEntry, int ProgramEntry, int Instruction, int ByteOffset,
    string ExpressionPath, string RuntimeProgramSha256, string HashSchema = "kismet-program-json/v1");

public sealed record KismetMissingCallCount(KismetMissingCallSite Site, int Count);

public sealed partial class KismetVm
{
    public const int MaxMissingCallSites = 4096;
    /// <summary>Diagnostic only; turning capture off never changes aggregate health counters.</summary>
    public bool CaptureMissingCallSites { get; set; } = true;
    private readonly Dictionary<KismetMissingCallSite, int> _missingCallSites = new();
    public IReadOnlyDictionary<KismetMissingCallSite, int> MissingCallSites => _missingCallSites;
    public long MissingCallSitesOmitted { get; private set; }
    public long MissingCallSiteCaptureFailures { get; private set; }
    private static readonly JsonSerializerOptions DiagnosticProgramJson = new() { MaxDepth = 256 };

    private void CaptureMissingCall(string key, Frame frame, EffectContext ctx, KismetExpr? origin = null)
    {
        if (!CaptureMissingCallSites) return;
        // Failure to collect optional provenance must not turn a missing call into
        // a VM exception, consume randomness or suppress its original health count.
        try
        {
            if (frame.CurrentStep is not { } step) { MissingCallSiteCaptureFailures++; return; }
            frame.DiagnosticProgramSha256 ??= Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(frame.Program, DiagnosticProgramJson))).ToLowerInvariant();
            var site = new KismetMissingCallSite(key, ctx.Self?.Definition.Name,
                frame.Program.AssetName, frame.Program.FunctionName, frame.Program.SourceEntry,
                frame.Program.Entry, step.Index, step.ByteOffset, ExpressionPaths(step, origin),
                frame.DiagnosticProgramSha256);
            if (_missingCallSites.TryGetValue(site, out int count)) _missingCallSites[site] = count + 1;
            else if (_missingCallSites.Count < MaxMissingCallSites) _missingCallSites.Add(site, 1);
            else MissingCallSitesOmitted++;
        }
        catch (Exception)
        {
            MissingCallSiteCaptureFailures++;
        }
    }

    private static string ExpressionPaths(KismetStep step, KismetExpr? origin)
    {
        if (origin is null) return "$";
        var paths = new List<string>();
        void Find(KismetExpr? expr, string path, int depth)
        {
            if (expr is null) return;
            if (ReferenceEquals(expr, origin)) paths.Add(path);
            if (depth >= 128) return;
            Find(expr.Context, path + ".ctx", depth + 1);
            for (int i = 0; i < expr.Args.Count; i++) Find(expr.Args[i], path + $".args[{i}]", depth + 1);
            for (int i = 0; i < expr.Array.Count; i++) Find(expr.Array[i], path + $".array[{i}]", depth + 1);
        }
        Find(step.Source, "src", 0); Find(step.Condition, "cond", 0); Find(step.Receiver, "recv", 0);
        for (int i = 0; i < step.Args.Count; i++) Find(step.Args[i], $"args[{i}]", 0);
        return paths.Count == 0 ? "<unresolved>" : string.Join("|", paths);
    }
}
