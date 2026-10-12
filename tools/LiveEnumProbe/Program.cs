using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

if (args.Length != 3)
    throw new ArgumentException("LiveEnumProbe <CardMoveReason.uasset> <jmap> <output.json>");
// Read the serialized names/value pairs, rather than infer values from display order.
var asset = new UAsset(args[0], EngineVersion.VER_UE5_6, new Usmap(args[1]));
var export = asset.Exports.OfType<EnumExport>().Single();
var result = new
{
    source = Path.GetFullPath(args[0]),
    uasset_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant(),
    uexp_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(args[0], ".uexp")))).ToLowerInvariant(),
    cpp_form = export.Enum.CppForm.ToString(),
    names = export.Enum.Names.Select(pair => new { name = pair.Item1.ToString(), value = pair.Item2 }).ToArray(),
};
File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Read {result.names.Length} serialized enum entries: {args[2]}");
