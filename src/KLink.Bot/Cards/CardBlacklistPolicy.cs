using System.Text.Json;

namespace KLink.Bot.Cards;

/// <summary>
/// Immutable BP_Logic.blacklistedCards map with explicit simulated UTC.
/// RULE-244: FString-derived keys use symbolic FName identity, not pool indices.
/// The cutoff is inclusive, as in the native signed comparison.
/// </summary>
public sealed class CardBlacklistPolicy
{
    private readonly Dictionary<NameKey, long> _endTicks;

    public CardBlacklistPolicy(IReadOnlyDictionary<string, long> endTicks, long serverUtcTicks)
    {
        _endTicks = [];
        foreach (var entry in endTicks) _endTicks[NameKey.From(entry.Key)] = entry.Value;
        ServerUtcTicks = serverUtcTicks;
    }

    // Dictionaries are privately owned and never mutated after construction.
    private CardBlacklistPolicy(Dictionary<NameKey, long> endTicks, long serverUtcTicks)
    {
        _endTicks = endTicks;
        ServerUtcTicks = serverUtcTicks;
    }

    public long ServerUtcTicks { get; }

    public CardBlacklistPolicy WithServerUtcTicks(long ticks) => new(_endTicks, ticks);

    /// <summary>
    /// Apply the stable, valid/connected session branch. card_type strings are
    /// converted to symbolic FName keys before merging without clearing;
    /// aliases overwrite in input order, including expired dates.
    /// </summary>
    public CardBlacklistPolicy WithSessionEntries(
        IEnumerable<KeyValuePair<string, long>> canonicalEntries, bool sessionValid, bool connected)
    {
        if (!sessionValid || !connected) return this;
        var merged = new Dictionary<NameKey, long>(_endTicks);
        foreach (var entry in canonicalEntries) merged[NameKey.From(entry.Key)] = entry.Value;
        return new(merged, ServerUtcTicks);
    }

    public bool IsBlacklisted(string? canonicalName) => canonicalName is not null
        && _endTicks.TryGetValue(NameKey.From(canonicalName), out long end) && end >= ServerUtcTicks;

    /// <summary>Explicitly load the logical map; no session lifecycle is inferred.</summary>
    public static CardBlacklistPolicy Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var entries = new Dictionary<NameKey, long>();
        foreach (var entry in root.GetProperty("blacklisted_cards").EnumerateObject())
            entries[NameKey.From(entry.Name)] = entry.Value.GetInt64();
        return new(entries, root.GetProperty("server_utc_ticks").GetInt64());
    }

    // RULE-240..243: successful mode=1 UTF-16 construction, ASCII-only fold,
    // numeric suffix, zero/None and oversized-base substitution. This models
    // equality only; it does not reproduce allocation IDs, locks or failures.
    private readonly record struct NameKey(string Base, int Number)
    {
        public static NameKey From(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            int nul = text.IndexOf('\0');
            int length = nul < 0 ? text.Length : nul;
            int start = length;
            while (start > 0 && text[start - 1] is >= '0' and <= '9') start--;
            int digits = length - start;
            int number = 0;
            if (digits is > 0 and <= 10 && start > 0 && text[start - 1] == '_'
                && (digits == 1 || text[start] != '0'))
            {
                long value = 0;
                for (int i = start; i < length; i++) value = value * 10 + text[i] - '0';
                if (value < int.MaxValue)
                {
                    number = (int)value + 1;
                    length = start - 1;
                }
            }
            // Empty view discards the suffix Number; None with a nonempty
            // spelling retains it (None_0 differs from the zero FName).
            if (length == 0) return new("", 0);
            char[] chars = length >= 1024
                ? "ERROR_NAME_SIZE_EXCEEDED".ToCharArray()
                : text[..length].ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + ('a' - 'A'));
            string key = new(chars);
            return new(key == "none" ? "" : key, number);
        }
    }
}
