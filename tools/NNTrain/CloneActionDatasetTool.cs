using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

internal sealed record CloneActionFingerprint(string RowsSha256,string SnapshotSha256,string TraceSha256,
    ulong RandomState,long RandomConsumed);
internal sealed record CloneActionEvidence(OracleActionEpisodeIdentity Identity,OracleActionEpisodeIdentity ReferenceIdentity,
    string[] Left,string[] Right,int Clones,long BaselineRoots,long VerifiedRoots,bool InitialVerified,bool FinalVerified,
    string BoundaryFailure,int? BoundaryFailureStep,CloneActionFingerprint CloneFingerprint,
    CloneActionFingerprint ReferenceFingerprint,ActionGameQuality Reference);

// Explicit opt-in paired producer. Manifest claims are software provenance, not signed attestation.
internal static class CloneActionDatasetTool
{
    internal const string Kind="KLink.PairedCloneActionLabels";
    internal static bool OptIn(Dictionary<string,string> opts)=>opts.GetValueOrDefault("planning-mode","replay-prefix") switch
    {
        "replay-prefix"=>false,"paired-clone"=>true,
        _=>throw new ArgumentException("--planning-mode must be replay-prefix or paired-clone.")
    };
    internal static string DeckHash(string[] left,string[] right)=>Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new {left,right})));
    internal static CloneActionFingerprint Fingerprint(int game,OracleActionEpisodeResult e)
    {
        using var rows=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var label in e.Labels)
            rows.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ActionDatasetTool.Row(game,label),ActionDatasetTool.Json)+"\n"));
        return new(Convert.ToHexString(rows.GetHashAndReset()),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.Replay.State.SnapshotJson()))),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(e.Replay.State.ActionLog))),
            e.Replay.State.Random.State,e.Replay.State.Random.ConsumedCount);
    }
    internal static ActionGameQuality WritePair(TextWriter writer,int game,string leftName,string rightName,
        string[] left,string[] right,OracleActionEpisodeResult clone,OracleActionEpisodeResult reference,bool irLoaded)
    {
        var reasons=ClonePlanningAdmission.Check(clone,reference,irLoaded).ToList();
        if(clone.Identity is null || clone.Identity.OrderedDecksSha256!=DeckHash(left,right))reasons.Add("producer_deck_identity_mismatch");
        try {foreach(var label in clone.Labels)ActionDatasetTool.VerifyRow(ActionDatasetTool.Row(game,label),clone.Identity?.Teacher);}
        catch(InvalidDataException){reasons.Add("invalid_serialized_action_row");}
        var q=ActionDatasetTool.Summarize(game,clone.Identity?.Seed??0,leftName,rightName,clone,reasons.ToArray());
        // Do not serialize malformed/NaN buffered labels or claim successful fingerprints on rejected runs.
        q.CloneEvidence=new(clone.Identity,reference.Identity,left.ToArray(),right.ToArray(),clone.PlanningClones,
            clone.PlanningBaselineRoots,clone.PlanningBaselineVerifiedRoots,clone.PlanningBaselineInitialVerified,
            clone.PlanningBaselineFinalVerified,clone.PlanningCloneFailure,clone.PlanningCloneFailureStep,
            q.Accepted?Fingerprint(game,clone):null,q.Accepted?Fingerprint(game,reference):null,
            ActionDatasetTool.Summarize(game,reference.Identity?.Seed??0,leftName,rightName,reference,
                ActionDatasetTool.AdmissionReasons(reference,irLoaded)));
        if(!q.Accepted)return q;
        // Buffer the whole episode before the first write. Never emit a rejected prefix.
        string rows=string.Concat(clone.Labels.Select(l=>JsonSerializer.Serialize(ActionDatasetTool.Row(game,l),ActionDatasetTool.Json)+"\n"));
        writer.Write(rows);
        return q;
    }
    internal static void VerifyEvidence(ActionGameQuality g,ActionDatasetHeader h)
    {
        static bool Hash(string s)=>s is {Length:64} && s.All(Uri.IsHexDigit);
        static bool ValidFingerprint(CloneActionFingerprint f)=>f is not null && Hash(f.RowsSha256)
            && Hash(f.SnapshotSha256) && Hash(f.TraceSha256) && f.RandomConsumed>=0;
        var e=g.CloneEvidence??throw new InvalidDataException("Missing paired clone evidence.");
        if(e.Reference is null || e.Reference.CloneEvidence is not null)
            throw new InvalidDataException("Missing independent replay summary.");
        ActionDatasetTool.VerifyGame(e.Reference);
        if(!g.Accepted)return; // rejected games have no rows; keep their failure diagnostics, not an approval claim.
        var r=e.Reference;
        if(e.Identity is null || e.ReferenceIdentity is null || e.Identity!=e.ReferenceIdentity
            || e.Identity.Seed!=g.Seed || e.Identity.Teacher!=h.Teacher
            || e.Left is null || e.Right is null || e.Left.Length!=39 || e.Right.Length!=39
            || e.Left.Concat(e.Right).Any(string.IsNullOrWhiteSpace)
            || e.Identity.OrderedDecksSha256!=DeckHash(e.Left,e.Right)
            || !e.InitialVerified || !e.FinalVerified || e.Clones!=g.WrittenRows
            || e.BaselineRoots!=g.WrittenRows || e.VerifiedRoots!=g.WrittenRows
            || e.BoundaryFailure is not null || e.BoundaryFailureStep is not null
            || !ValidFingerprint(e.CloneFingerprint) || !ValidFingerprint(e.ReferenceFingerprint)
            || e.CloneFingerprint!=e.ReferenceFingerprint || !r.Accepted || r.Game!=g.Game || r.Seed!=g.Seed
            || r.LeftDeck!=g.LeftDeck || r.RightDeck!=g.RightDeck || r.WrittenRows!=g.WrittenRows || r.Winner!=g.Winner
            || new[]{g,r}.Any(q=>q.SnapshotDifference is not null || q.MainFaults<0 || q.MainRejections<0
                || q.ChoiceFaults<0 || q.PendingChoices<0 || q.VmFaults<0
                || q.Unsupported.Any(p=>p.Value<0) || q.Calls.Any(p=>p.Value<0)
                || q.MissingCallProvenance is not {Length:3}
                || !q.MissingCallProvenance.Select(p=>p?.Phase).SequenceEqual(new[]{"teacher","replay","planning"})
                || q.MissingCallProvenance.Any(p=>p is null || p.Sites is null || p.Sites.Count!=0 || p.SitesOmitted!=0 || p.CaptureFailures!=0)))
            throw new InvalidDataException("Paired clone identity/health/reference/work/fingerprint mismatch.");
    }
    internal static int Dump(CardDatabase db,StateEncoder.CardVecs vectors,Dictionary<string,string> opts,string dataDir,string vectorPath)
    {
        if(KismetLibrary.Default is null)throw new InvalidOperationException("Loaded IR required.");
        int games=int.Parse(opts.GetValueOrDefault("games","2"),System.Globalization.CultureInfo.InvariantCulture);
        ulong seed=ulong.Parse(opts.GetValueOrDefault("seed","1"),System.Globalization.CultureInfo.InvariantCulture);
        if(games is <1 or >100 || seed>ulong.MaxValue-(ulong)(games-1))throw new ArgumentException("Expected 1..100 games and non-overflowing seeds.");
        var fixture=CloneEpisodeBenchmark.ResolveFixture(db,opts);
        var policy=ActionDatasetTool.ParseChooseOnePolicy(opts.GetValueOrDefault("choose-one-policy","native"));
        string path=Path.GetFullPath(opts.GetValueOrDefault("out","paired-clone-actions.jsonl.gz"));
        if(File.Exists(path) || File.Exists(path+".quality.json"))throw new IOException("Refusing to overwrite paired clone data or manifest.");
        var header=new ActionDatasetHeader(6,Kind,StateEncoder.Spec,MainActionFeatures.Spec,"actor",
            OracleActionEpisodeRunner.ContractFor(policy),"structural-superset",seed,games,
            ActionDatasetTool.CurrentInputs(dataDir,vectorPath),ClonePlanningAdmission.Contract);
        var quality=new ActionDatasetQuality{Header=header};
        using(var file=new FileStream(path,FileMode.CreateNew))
        using(var zip=new GZipStream(file,CompressionLevel.Fastest))
        using(var writer=new StreamWriter(zip,new UTF8Encoding(false)))
        {
            writer.WriteLine(JsonSerializer.Serialize(header,ActionDatasetTool.Json));
            for(int g=0;g<games;g++)
            {
                var reference=OracleActionEpisodeRunner.Run(db,vectors,fixture.Left,fixture.Right,seed+(ulong)g,policy);
                var clone=OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,fixture.Left,fixture.Right,seed+(ulong)g,policy);
                var q=WritePair(writer,g,fixture.Name,fixture.Name,fixture.Left,fixture.Right,clone,reference,true);
                quality.Games.Add(q);quality.Rows+=q.WrittenRows;
                if(q.Accepted)quality.AcceptedGames++;else quality.RejectedGames++;
            }
        }
        quality.DataSha256=TrainingAdmission.Hash(path);
        File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(quality,TrainingAdmission.Json),new UTF8Encoding(false));
        if(quality.Rows>0)ActionDatasetTool.Verify(path,pairedClone:true);
        Console.WriteLine($"Paired clone dataset: {quality.AcceptedGames} accepted / {quality.RejectedGames} rejected; {quality.Rows} rows; {path}");
        return quality.RejectedGames!=0 || quality.Rows==0?2:0;
    }
}
