using System.Security.Cryptography;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

// Audit artifact only: never used by training admission. Serialize exactly as
// KismetVm.CaptureMissingCall does, including synthetic dispatch and identities.
if (args.Length != 3) throw new ArgumentException("Usage: <IR path> <data directory> <output>");
var ir = Path.GetFullPath(args[0]);
KismetLibrary.Initialize(ir);
var lib = KismetLibrary.Default ?? throw new InvalidOperationException(KismetLibrary.LoadError);
var options = new JsonSerializerOptions { MaxDepth = 256 };
var programs = new List<object>();
foreach (string asset in new[] { "card_event_carrier_battle", "card_event_seize_the_initiative",
    "card_unit_115th_separate_btn", "card_unit_75th_rifle_regiment" })
{
    var card = lib.Find(asset) ?? throw new InvalidOperationException(asset);
    foreach (string name in card.Entrypoints.Keys.Concat(card.Locals.Keys).Distinct(StringComparer.Ordinal))
    {
        bool local = card.Locals.ContainsKey(name);
        var p = (local ? lib.FindLocalProgram(asset, name) : lib.FindProgram(asset, name))
            ?? throw new InvalidOperationException(asset + "." + name);
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(p, options);
        programs.Add(new { asset, container = local ? name : "steps", function = name,
            runtimeProgramSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            serializedProgramBase64 = Convert.ToBase64String(raw) });
    }
}
// Reproduce the existing real-IR local chain in a disposable simulator, not the
// client. Actual VM provenance independently checks the serialization contract.
var db = CardDatabase.Load(Path.GetFullPath(args[1]));
var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 1);
var state = engine.State;
const string real = "card_unit_75th_rifle_regiment";
var self = state.CreateWithId(real, Side.Left, 70, CardLocation.BoardHqLeft, 1);
engine.Api.Vm.RunLocalProgram(lib.FindLocalProgram(real, "SpawnCopy")!,
    new EffectContext { Engine = engine, State = state, Controller = Side.Left, Self = self }, null, "result");
var observed = engine.Api.Vm.MissingCallSites.Select(x => new { site = x.Key, count = x.Value }).ToArray();
if (engine.Api.Vm.FaultedPrograms != 0 || observed.Length != 2
    || engine.Api.Vm.MissingCallSiteCaptureFailures != 0 || engine.Api.Vm.MissingCallSitesOmitted != 0)
    throw new InvalidOperationException("Real local-chain observation is incomplete or changed");
var output = new { schema = "display-runtime-program-export/v1", hashSchema = "kismet-program-json/v1",
    libraryAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(KismetLibrary).Assembly.Location))).ToLowerInvariant(),
    irSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ir))).ToLowerInvariant(),
    programs, observed, omitted = engine.Api.Vm.MissingCallSitesOmitted,
    captureFailures = engine.Api.Vm.MissingCallSiteCaptureFailures, admissionExempt = false };
File.WriteAllText(args[2], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Exported {programs.Count} runtime programs; {observed.Length} observed calls; no exemptions");
