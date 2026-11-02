using System.Text.Json;
using KLink.Bot.Engine;

namespace KLink.Bot.Training;

// Batch-local scratch space, never shared with an engine or policy. Compare exact
// serialized bytes, not a hash or a reduced subset of MatchSnapshot fields.
internal sealed class ProbeSnapshotComparison : IDisposable
{
    private readonly string? _text;
    private readonly byte[]? _utf8;
    private readonly MemoryStream? _scratch;
    private readonly CloneProbeProfile? _profile;

    internal ProbeSnapshotComparison(GameState state, bool utf8, CloneProbeProfile? profile = null)
    {
        _profile = profile;
        var snapshot = Build(state);
        using var measurement = profile?.Measure("snapshot.serialize");
        if (utf8)
        {
            _utf8 = JsonSerializer.SerializeToUtf8Bytes(snapshot, GameState.SnapshotJsonOptions);
            _scratch = new MemoryStream(_utf8.Length);
        }
        else _text = JsonSerializer.Serialize(snapshot, GameState.SnapshotJsonOptions);
    }

    private MatchSnapshot Build(GameState state)
    {
        using var measurement = _profile?.Measure("snapshot.build");
        return state.Snapshot();
    }

    internal bool Matches(GameState state)
    {
        var snapshot = Build(state);
        using var measurement = _profile?.Measure("snapshot.serialize");
        if (_scratch is null)
            return _text == JsonSerializer.Serialize(snapshot, GameState.SnapshotJsonOptions);
        _scratch.SetLength(0);
        _scratch.Position = 0;
        JsonSerializer.Serialize(_scratch, snapshot, GameState.SnapshotJsonOptions);
        return _utf8.AsSpan().SequenceEqual(_scratch.GetBuffer().AsSpan(0, checked((int)_scratch.Length)));
    }

    public void Dispose() => _scratch?.Dispose();
}
