using System.Text.Json;

namespace KLink.Bot.Cards;

/// <summary>
/// RULE-238: immutable BP_Logic.blacklistedCards snapshot with explicit simulated UTC.
/// Canonical card names only; the cutoff is inclusive, as in the native signed comparison.
/// </summary>
public sealed class CardBlacklistPolicy
{
    private readonly Dictionary<string, long> _endTicks;

    public CardBlacklistPolicy(IReadOnlyDictionary<string, long> endTicks, long serverUtcTicks)
    {
        _endTicks = new(endTicks, StringComparer.Ordinal);
        ServerUtcTicks = serverUtcTicks;
    }

    public long ServerUtcTicks { get; }

    public CardBlacklistPolicy WithServerUtcTicks(long ticks) => new(_endTicks, ticks);

    /// <summary>
    /// RULE-239: apply the stable, valid/connected session branch after card_type
    /// has been resolved to canonical names. This merges without clearing;
    /// repeated keys overwrite in input order, including expired dates.
    /// </summary>
    public CardBlacklistPolicy WithSessionEntries(
        IEnumerable<KeyValuePair<string, long>> canonicalEntries, bool sessionValid, bool connected)
    {
        if (!sessionValid || !connected) return this;
        var merged = new Dictionary<string, long>(_endTicks, StringComparer.Ordinal);
        foreach (var entry in canonicalEntries) merged[entry.Key] = entry.Value;
        return new(merged, ServerUtcTicks);
    }

    public bool IsBlacklisted(string? canonicalName) => canonicalName is not null
        && _endTicks.TryGetValue(canonicalName, out long end) && end >= ServerUtcTicks;

    /// <summary>Explicitly load the logical map, not an assumed session-array conversion.</summary>
    public static CardBlacklistPolicy Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var entries = root.GetProperty("blacklisted_cards").EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetInt64(), StringComparer.Ordinal);
        return new(entries, root.GetProperty("server_utc_ticks").GetInt64());
    }
}
