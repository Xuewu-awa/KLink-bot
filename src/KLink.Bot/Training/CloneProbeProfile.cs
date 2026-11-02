using System.Diagnostics;

namespace KLink.Bot.Training;

// Internal, single-thread engineering instrumentation. Never a policy feature.
// "total" includes all other stages; their measurements are not additive to total.
internal sealed class CloneProbeProfile
{
    internal sealed record Sample(long Count, double Milliseconds, long AllocatedBytes);
    private readonly Dictionary<string,Sample> _samples = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string,Sample> Samples => new Dictionary<string,Sample>(_samples);
    internal Scope Measure(string stage) => new(this,stage);

    internal readonly struct Scope : IDisposable
    {
        private readonly CloneProbeProfile _profile;
        private readonly string _stage;
        private readonly long _start, _bytes;
        internal Scope(CloneProbeProfile profile,string stage)
        {
            _profile=profile; _stage=stage;
            _bytes=GC.GetAllocatedBytesForCurrentThread(); _start=Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            double ms=Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            long bytes=GC.GetAllocatedBytesForCurrentThread()-_bytes;
            var old=_profile._samples.GetValueOrDefault(_stage);
            _profile._samples[_stage]=new((old?.Count??0)+1,(old?.Milliseconds??0)+ms,(old?.AllocatedBytes??0)+bytes);
        }
    }
}
