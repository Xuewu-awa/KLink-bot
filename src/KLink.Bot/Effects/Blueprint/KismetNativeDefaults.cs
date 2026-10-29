using System.Security.Cryptography;
using System.Text.Json;

namespace KLink.Bot.Effects.Blueprint;

public sealed partial class KismetLibrary
{
    // Explicit executable empty-event contracts, not training-admission exceptions.
    // Exact names only: the ordinary variant fallback never broadens these proofs.
    private readonly Dictionary<string,KismetProgram> _emptyPlayedEvents=new(StringComparer.OrdinalIgnoreCase);

    private void LoadNativeDefaults(string irPath)
    {
        string directory=Path.GetDirectoryName(Path.GetFullPath(irPath))!;
        string path=Path.Combine(directory,"card-ir-native-defaults.json");
        if(!File.Exists(path))return;
        using var json=JsonDocument.Parse(File.ReadAllText(path));var root=json.RootElement;
        static string Hash(string p){using var s=File.OpenRead(p);return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}
        if(root.GetProperty("schema_version").GetInt32()!=1
            || root.GetProperty("base_ir_sha256").GetString()!=Hash(irPath)
            || root.GetProperty("cards_sha256").GetString()!=Hash(Path.Combine(directory,"cards.json")))
            throw new InvalidDataException("Native empty-event contracts do not match the loaded IR/card data");
        string client=root.GetProperty("source_client_sha256").GetString()!;
        if(client.Length!=64 || !client.All(Uri.IsHexDigit))throw new InvalidDataException("Missing native proof client identity");
        foreach(var entry in root.GetProperty("empty_events").EnumerateArray())
        {
            string card=entry.GetProperty("card").GetString()!;
            if(string.IsNullOrWhiteSpace(card) || _cards.ContainsKey(card) || _emptyPlayedEvents.ContainsKey(card)
                || entry.GetProperty("event").GetString()!="OnPlayedFromHand"
                || entry.GetProperty("kind").GetString()!="non_native_empty_script"
                || entry.GetProperty("script_bytes").GetInt32()!=0
                || (entry.GetProperty("function_flags").GetUInt32() & 0x400)!=0)
                throw new InvalidDataException("Conflicting or unsupported native empty-event contract: "+card);
            var steps=new[] {new KismetStep(0,"return",null,Array.Empty<KismetExpr>(),Array.Empty<int>(),null,null,-1,null,null)};
            _emptyPlayedEvents.Add(card,new KismetProgram(steps,0));
        }
    }
}
