namespace KLink.Bot.Engine;

/// <summary>
/// Copies the reachable card graph, preserving reference aliases and cycles.
/// Catalog definitions are shared and must remain read-only. This is not an engine checkpoint.
/// Collection contents and current enumeration order are copied; internal removed-entry
/// slots are not. Subsequent remove/add enumeration may differ from the source.
/// Unsupported payloads/comparers fail explicitly; no partial result is returned.
/// </summary>
public sealed class CardGraphClone
{
    private readonly Dictionary<object, object> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CardInstance, CardInstance> _cards = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<CardInstance> Roots { get; private set; } = [];
    public IReadOnlyDictionary<CardInstance, CardInstance> Cards { get; }

    private CardGraphClone()
        => Cards = new System.Collections.ObjectModel.ReadOnlyDictionary<CardInstance, CardInstance>(_cards);

    public static CardGraphClone Create(IEnumerable<CardInstance> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var context = new CardGraphClone();
        context.Roots = Array.AsReadOnly(roots.Select(card =>
            context.Copy(card ?? throw new ArgumentException("Null card root", nameof(roots)))).ToArray());
        return context;
    }

    internal T Copy<T>(T value) => (T)CopyValue(value)!;

    private object? CopyValue(object? value)
    {
        if (value is null) return null;
        if (value is string or bool or byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal or char || value.GetType().IsEnum)
            return value;
        if (_copies.TryGetValue(value, out var existing)) return existing;
        switch (value)
        {
            case CardInstance card:
                var shell = card.CloneShell();
                _copies.Add(card, shell);
                _cards.Add(card, shell);
                shell.RemapGraph(this);
                return shell;
            case CardBuff buff:
                var buffShell = buff.CloneShell();
                _copies.Add(buff, buffShell);
                buffShell.RemapGraph(this);
                return buffShell;
            case CardCustomJson json:
                var jsonCopy = new CardCustomJson();
                _copies.Add(json, jsonCopy);
                jsonCopy.CopyFrom(json);
                return jsonCopy;
            case List<int> list: return CopyList(list);
            case List<string> list: return CopyList(list);
            case List<object?> list: return CopyList(list);
            case List<CardInstance> list: return CopyList(list);
            case List<Dictionary<string, object?>> list: return CopyList(list);
            case List<KeyValuePair<(int, bool), CardBuff>> list: return CopyList(list);
            case HashSet<string> set: return CopySet(set);
            case HashSet<object?> set: return CopySet(set);
            case Dictionary<string, object?> map: return CopyMap(map);
            case Dictionary<object, object?> map: return CopyMap(map);
            case Dictionary<string, List<int>> map: return CopyMap(map);
            case Dictionary<string, HashSet<object?>> map: return CopyMap(map);
            case Dictionary<(int, bool), CardBuff> map: return CopyMap(map);
            case int[] array: return CopyArray(array);
            case string[] array: return CopyArray(array);
            case CardInstance[] array: return CopyArray(array);
            case object?[] array: return CopyArray(array);
            case ValueTuple<int, bool> pair: return pair;
            case KeyValuePair<(int, bool), CardBuff> pair:
                return new KeyValuePair<(int, bool), CardBuff>(pair.Key, Copy(pair.Value));
            default:
                throw new NotSupportedException($"Card graph payload: {value.GetType().FullName}");
        }
    }

    private List<T> CopyList<T>(List<T> source)
    {
        CheckExactType<List<T>>(source);
        var copy = new List<T>(source.Count);
        _copies.Add(source, copy);
        foreach (var item in source) copy.Add(Copy(item));
        return copy;
    }

    private T[] CopyArray<T>(T[] source)
    {
        CheckExactType<T[]>(source);
        var copy = new T[source.Length];
        _copies.Add(source, copy);
        for (int i = 0; i < source.Length; i++) copy[i] = Copy(source[i]);
        return copy;
    }

    private static void CheckComparer<T>(IEqualityComparer<T> comparer)
    {
        if (ReferenceEquals(comparer, EqualityComparer<T>.Default)
            || ReferenceEquals(comparer, StringComparer.Ordinal)
            || ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase)
            || ReferenceEquals(comparer, ReferenceEqualityComparer.Instance)) return;
        throw new NotSupportedException($"Card graph comparer: {comparer.GetType().FullName}");
    }

    private HashSet<T> CopySet<T>(HashSet<T> source)
    {
        CheckExactType<HashSet<T>>(source);
        CheckComparer(source.Comparer);
        var copy = new HashSet<T>(source.Comparer);
        _copies.Add(source, copy);
        foreach (var item in source) copy.Add(Copy(item));
        return copy;
    }

    private Dictionary<TKey, TValue> CopyMap<TKey, TValue>(Dictionary<TKey, TValue> source) where TKey : notnull
    {
        CheckExactType<Dictionary<TKey, TValue>>(source);
        CheckComparer(source.Comparer);
        var copy = new Dictionary<TKey, TValue>(source.Count, source.Comparer);
        _copies.Add(source, copy);
        foreach (var pair in source) copy.Add(Copy(pair.Key), Copy(pair.Value));
        return copy;
    }

    private static void CheckExactType<T>(object source)
    {
        if (source.GetType() != typeof(T))
            throw new NotSupportedException($"Card graph collection subtype: {source.GetType().FullName}");
    }
}

public sealed partial class CardInstance
{
    internal CardInstance CloneShell() => (CardInstance)MemberwiseClone();

    internal void RemapGraph(CardGraphClone context)
    {
        CurrentTarget = context.Copy(CurrentTarget);
        SuppressionExceptionTriggers = context.Copy(SuppressionExceptionTriggers);
        SuppressStrippedKeywords = context.Copy(SuppressStrippedKeywords);
        SuppressStrippedBuffs = context.Copy(SuppressStrippedBuffs);
        Keywords = context.Copy(Keywords);
        ReceivedAbilitiesFromCards = context.Copy(ReceivedAbilitiesFromCards);
        SuppressStrippedAbilities = context.Copy(SuppressStrippedAbilities);
        RemovedGameplayTags = context.Copy(RemovedGameplayTags);
        CustomJson = context.Copy(CustomJson);
        BlueprintSets = context.Copy(BlueprintSets);
        BlueprintVariables = context.Copy(BlueprintVariables);
        BuffsBySource = context.Copy(BuffsBySource);
        CardsBuffedByThisCard = context.Copy(CardsBuffedByThisCard);
    }
}

public sealed partial class CardBuff
{
    internal CardBuff CloneShell() => (CardBuff)MemberwiseClone();
    internal void RemapGraph(CardGraphClone context)
        => _customAbilityDetails = context.Copy(_customAbilityDetails);
}
