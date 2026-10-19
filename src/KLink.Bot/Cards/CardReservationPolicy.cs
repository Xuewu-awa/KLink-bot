using System.Text.Json;

namespace KLink.Bot.Cards;

/// <summary>A copied, immutable session reservation record. Names are canonical card names.</summary>
public sealed class CardReservationChange
{
    private readonly HashSet<string> _toBeReserved;
    private readonly HashSet<string> _comingBack;

    public CardReservationChange(long activationTicks, IEnumerable<string> toBeReserved,
        IEnumerable<string> comingBack)
    {
        ActivationTicks = activationTicks;
        _toBeReserved = new(toBeReserved, StringComparer.Ordinal);
        _comingBack = new(comingBack, StringComparer.Ordinal);
    }

    public long ActivationTicks { get; }
    internal bool MovesToReserved(string name) => _toBeReserved.Contains(name);
    internal bool ComesBack(string name) => _comingBack.Contains(name);
}

/// <summary>
/// RULE-234/236: a stable session snapshot and an explicit simulated server time.
/// Updating time creates a new policy; no host clock or process memory is read here.
/// </summary>
public sealed class CardReservationPolicy
{
    private readonly CardReservationChange[] _changes;

    public CardReservationPolicy(IEnumerable<CardReservationChange> changes, long serverUtcTicks)
    {
        _changes = changes.ToArray();
        ServerUtcTicks = serverUtcTicks;
    }

    public long ServerUtcTicks { get; }

    public CardReservationPolicy WithServerUtcTicks(long ticks) => new(_changes, ticks);

    public bool IsReserved(string? canonicalName, bool staticDefault)
    {
        if (canonicalName is null) return staticDefault;
        long latest = 0;
        bool result = staticDefault;
        foreach (var change in _changes)
        {
            bool move = change.MovesToReserved(canonicalName);
            if (!move && !change.ComesBack(canonicalName)) continue;
            if (change.ActivationTicks > ServerUtcTicks || change.ActivationTicks < latest) continue;
            latest = change.ActivationTicks;
            result = move;
        }
        return result;
    }

    /// <summary>Load an explicitly chosen snapshot; loading does not install it into any match.</summary>
    public static CardReservationPolicy Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var changes = root.GetProperty("changes").EnumerateArray().Select(row =>
            new CardReservationChange(row.GetProperty("activation_ticks").GetInt64(),
                Names(row.GetProperty("to_be_reserved")), Names(row.GetProperty("coming_back"))));
        return new(changes, root.GetProperty("server_utc_ticks").GetInt64());
    }

    private static string[] Names(JsonElement array) => array.EnumerateArray()
        .Select(value => value.GetString() ?? throw new JsonException("Card name cannot be null."))
        .ToArray();
}
