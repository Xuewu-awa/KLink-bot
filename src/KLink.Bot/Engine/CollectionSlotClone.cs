using System.Reflection;
using System.Linq.Expressions;

namespace KLink.Bot.Engine;

/// <summary>
/// Narrow .NET 10 x64 layout adapter for supported Dictionary/HashSet instances.
/// Preserves occupied slots, free-list order and capacity, then rehashes remapped
/// keys without moving slots. Immutable string maps retain checked bucket chains.
/// No caller-owned enumerator is copied.
/// </summary>
internal static class CollectionSlotClone
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Dictionary<TKey, TValue>(Dictionary<TKey, TValue> source,
        Dictionary<TKey, TValue> target, Func<TKey, TKey> key, Func<TValue, TValue> value) where TKey : notnull
    {
        var layout = MapLayout<TKey, TValue>.Cache.Value;
        if (source.Count == 0 && layout.TryCopyUnallocated(source, target)) return;
        CopyDictionary(source, target, key, value, layout);
    }

    // Keep delegate creation beyond the unallocated-storage gate. A Count==0
    // collection can still own allocated slots/free chains and must use Copy.
    internal static void Dictionary<TKey, TValue>(Dictionary<TKey, TValue> source,
        Dictionary<TKey, TValue> target, CardGraphClone context) where TKey : notnull
    {
        var layout = MapLayout<TKey, TValue>.Cache.Value;
        if (source.Count == 0 && layout.TryCopyUnallocated(source, target)) return;
        if (typeof(TKey) == typeof(string) && typeof(TValue) == typeof(string))
        {
            // Strings cannot be remapped by the graph memo. Keep the stored hashes
            // with the exact raw comparer, including its randomized hash seed.
            layout.Copy(source, target, null, null, null, preserveStringSlots: true);
            return;
        }
        CopyDictionary(source, target, context.Copy, context.Copy, layout);
    }

    private static void CopyDictionary<TKey, TValue>(Dictionary<TKey, TValue> source,
        Dictionary<TKey, TValue> target, Func<TKey, TKey> key, Func<TValue, TValue> value, Layout layout) where TKey : notnull
    {
        var comparer = (IEqualityComparer<TKey>?)layout.Comparer.GetValue(source) ?? EqualityComparer<TKey>.Default;
        layout.Copy(source, target, item => key((TKey)item!), item => value((TValue)item!),
            item => unchecked((uint)comparer.GetHashCode((TKey)item!)));
    }

    internal static void Set<T>(HashSet<T> source, HashSet<T> target, CardGraphClone context)
    {
        var layout = SetLayout<T>.Cache.Value;
        if (source.Count == 0 && layout.TryCopyUnallocated(source, target)) return;
        CopySet(source, target, context.Copy, layout);
    }

    private static void CopySet<T>(HashSet<T> source, HashSet<T> target, Func<T, T> value, Layout layout)
    {
        var comparer = (IEqualityComparer<T>?)layout.Comparer.GetValue(source) ?? EqualityComparer<T>.Default;
        layout.Copy(source, target, item => value((T)item!), null,
            item => item is null ? 0 : unchecked((uint)comparer.GetHashCode((T)item)));
    }

    private static class MapLayout<TKey, TValue> where TKey : notnull
    {
        internal static readonly Lazy<Layout> Cache = new(() => new(typeof(Dictionary<TKey, TValue>), typeof(TKey), typeof(TValue)));
    }

    private static class SetLayout<T>
    {
        internal static readonly Lazy<Layout> Cache = new(() => new(typeof(HashSet<T>), typeof(T), null));
    }

    private sealed class Layout
    {
        private readonly FieldInfo[] _scalars;
        private readonly FieldInfo _buckets, _entries, _count, _freeCount, _freeList, _hash, _next, _key;
        private readonly FieldInfo? _value, _keysView, _valuesView;
        private readonly bool _map;
        private readonly Func<Array, int, int>? _stringNext;
        private readonly Func<Array, int, uint>? _stringHash;
        internal FieldInfo Comparer { get; }

        internal Layout(Type type, Type keyType, Type? valueType)
        {
            if (Environment.Version.Major != 10 || IntPtr.Size != 8)
                throw new NotSupportedException("Collection slot cloning requires the reviewed .NET 10 x64 layout.");
            _map = valueType is not null;
            var fields = type.GetFields(Fields).ToDictionary(f => f.Name);
            var expected = new List<string> { "_buckets", "_entries", "_fastModMultiplier", "_count", "_freeList", "_freeCount", "_version", "_comparer" };
            if (_map) expected.AddRange(new[] { "_keys", "_values" });
            if (!fields.Keys.Order().SequenceEqual(expected.Order())) Fail(type);
            FieldInfo Get(string name, Type fieldType)
            {
                var field = fields[name];
                if (field.FieldType != fieldType) Fail(type);
                return field;
            }
            _buckets = Get("_buckets", typeof(int[]));
            _count = Get("_count", typeof(int));
            _freeList = Get("_freeList", typeof(int));
            _freeCount = Get("_freeCount", typeof(int));
            _scalars = new[] { Get("_fastModMultiplier", typeof(ulong)), _count, _freeList,
                _freeCount, Get("_version", typeof(int)) };
            Comparer = Get("_comparer", typeof(IEqualityComparer<>).MakeGenericType(keyType));
            _entries = fields["_entries"];
            var entryType = _entries.FieldType.GetElementType();
            if (!_entries.FieldType.IsSZArray || entryType is null || !entryType.IsValueType) Fail(type);
            var entries = entryType!.GetFields(Fields).ToDictionary(f => f.Name);
            string[] entryNames = _map ? ["hashCode", "next", "key", "value"] : ["HashCode", "Next", "Value"];
            if (!entries.Keys.Order().SequenceEqual(entryNames.Order())) Fail(type);
            FieldInfo Entry(string name, Type fieldType)
            {
                var field = entries[name];
                if (field.FieldType != fieldType) Fail(type);
                return field;
            }
            _hash = Entry(_map ? "hashCode" : "HashCode", _map ? typeof(uint) : typeof(int));
            _next = Entry(_map ? "next" : "Next", typeof(int));
            _key = Entry(_map ? "key" : "Value", keyType);
            if (_map)
            {
                _value = Entry("value", valueType!);
                _keysView = Get("_keys", type.GetNestedType("KeyCollection")!.MakeGenericType(type.GenericTypeArguments));
                _valuesView = Get("_values", type.GetNestedType("ValueCollection")!.MakeGenericType(type.GenericTypeArguments));
            }
            if (_map && keyType == typeof(string) && valueType == typeof(string))
            {
                // Typed reads of the reviewed Entry fields avoid boxing each entry
                // and scalar during immutable-string validation. Cached per layout.
                _stringNext = ReadEntryField<int>(_next);
                _stringHash = ReadEntryField<uint>(_hash);
            }
        }

        private Func<Array, int, T> ReadEntryField<T>(FieldInfo field)
        {
            var array = Expression.Parameter(typeof(Array), "entries");
            var index = Expression.Parameter(typeof(int), "index");
            var entry = Expression.ArrayIndex(Expression.Convert(array, _entries.FieldType), index);
            return Expression.Lambda<Func<Array, int, T>>(Expression.Field(entry, field), array, index).Compile();
        }

        private static void Fail(Type type)
            => throw new NotSupportedException($"Unreviewed collection layout: {type.FullName}");

        internal bool TryCopyUnallocated(object source, object target)
        {
            if (_entries.GetValue(source) is not null) return false;
            int freeList = (int)_freeList.GetValue(source)!;
            if (_buckets.GetValue(source) is not null || (int)_count.GetValue(source)! != 0
                || (int)_freeCount.GetValue(source)! != 0 || (freeList != 0 && freeList != -1))
                throw new NotSupportedException("Invalid unallocated collection metadata.");
            CopyStorage(source, target, null, null);
            return true;
        }

        private void CopyStorage(object source, object target, Array? entries, int[]? buckets)
        {
            foreach (var scalar in _scalars) scalar.SetValue(target, scalar.GetValue(source));
            Comparer.SetValue(target, Comparer.GetValue(source));
            _entries.SetValue(target, entries);
            _buckets.SetValue(target, buckets);
            // Cached views capture their owning dictionary; rebuild lazily on target.
            _keysView?.SetValue(target, null);
            _valuesView?.SetValue(target, null);
        }

        internal void Copy(object source, object target, Func<object?, object?>? key,
            Func<object?, object?>? value, Func<object?, uint>? hash, bool preserveStringSlots = false)
        {
            if (preserveStringSlots && (!_map || _key.FieldType != typeof(string) || _value!.FieldType != typeof(string)))
                throw new NotSupportedException("Raw slot copying requires immutable string keys and values.");
            var sourceEntries = (Array?)_entries.GetValue(source);
            var sourceBuckets = (int[]?)_buckets.GetValue(source);
            int count = (int)_count.GetValue(source)!;
            int freeCount = (int)_freeCount.GetValue(source)!;
            int freeList = (int)_freeList.GetValue(source)!;
            if (count < 0 || freeCount < 0 || freeCount > count
                || (sourceEntries is null) != (sourceBuckets is null)
                || count > (sourceEntries?.Length ?? 0)
                || (sourceEntries is not null && (sourceBuckets!.Length != sourceEntries.Length || sourceBuckets.Length == 0)))
                throw new NotSupportedException("Invalid collection slot metadata.");

            // Never mutate the original entry buffer, including boxed Entry structs.
            var entries = (Array?)sourceEntries?.Clone();
            var buckets = sourceBuckets is null ? null : preserveStringSlots
                ? (int[])sourceBuckets.Clone() : new int[sourceBuckets.Length];
            CopyStorage(source, target, entries, buckets);

            // Never-allocated collections use the CLR's zero-initialized freeList.
            if (sourceEntries is null)
            {
                if (count != 0 || freeCount != 0 || (freeList != 0 && freeList != -1))
                    throw new NotSupportedException("Invalid unallocated collection metadata.");
                return;
            }

            int active = 0;
            for (int index = 0; index < count; index++)
            {
                if (preserveStringSlots)
                {
                    if (_stringNext!(entries!, index) >= -1) active++;
                    continue;
                }
                object entry = entries!.GetValue(index)!;
                int next = (int)_next.GetValue(entry)!;
                if (next < -1) continue; // encoded free-list link
                active++;
                var newKey = key!(_key.GetValue(entry));
                _key.SetValue(entry, newKey);
                if (_value is not null) _value.SetValue(entry, value!(_value.GetValue(entry)));
                uint code = hash!(newKey);
                _hash.SetValue(entry, _map ? (object)code : unchecked((int)code));
                int bucket = (int)(code % (uint)buckets!.Length);
                _next.SetValue(entry, buckets[bucket] - 1);
                entries.SetValue(entry, index);
                buckets[bucket] = index + 1;
            }
            if (active != count - freeCount) throw new NotSupportedException("Invalid occupied collection slots.");
            if (preserveStringSlots)
            {
                // A raw bucket copy must be validated rather than repaired by rehashing.
                var visited = new bool[count];
                int reached = 0;
                for (int bucket = 0; bucket < buckets!.Length; bucket++)
                {
                    int index = buckets[bucket] - 1;
                    while (index != -1)
                    {
                        if ((uint)index >= (uint)count || visited[index]
                            || _stringNext!(entries!, index) < -1
                            || _stringHash!(entries!, index) % (uint)buckets.Length != (uint)bucket)
                            throw new NotSupportedException("Invalid immutable string bucket chain.");
                        visited[index] = true;
                        reached++;
                        index = _stringNext!(entries!, index);
                    }
                }
                if (reached != active) throw new NotSupportedException("Incomplete immutable string bucket chains.");
            }
            // Check the exact encoded LIFO free chain, without changing any link.
            var seen = new HashSet<int>();
            while (freeList != -1)
            {
                if (freeList < 0 || freeList >= count || !seen.Add(freeList))
                    throw new NotSupportedException("Invalid collection free chain.");
                int link = preserveStringSlots ? _stringNext!(entries!, freeList)
                    : (int)_next.GetValue(entries!.GetValue(freeList))!;
                if (link >= -1) throw new NotSupportedException("Free chain points to an occupied slot.");
                freeList = -3 - link;
            }
            if (seen.Count != freeCount) throw new NotSupportedException("Incomplete collection free chain.");
        }
    }
}
