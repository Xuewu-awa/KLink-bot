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
