using System.Reflection;
using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static CardInstance CloneTestCard(int id) => new()
    {
        CardId = id, Name = "clone-test", Owner = Side.Left,
        Definition = new CardDefinition { Name = "clone-test", Attack = 7, Kredits = 3 }
    };

    private static string? CardGraphCyclesAndAliases(CardDatabase db)
    {
        var a = CloneTestCard(1);
        var b = CloneTestCard(1); // IDs are not object identities.
        a.CurrentTarget = b;
        b.CurrentTarget = a;
        a.BlueprintVariables["self"] = a;
        a.BlueprintVariables["vars"] = a.BlueprintVariables;
        var list = new List<object?> { a, b, null };
        list.Add(list);
        a.BlueprintVariables["list"] = list;
        b.BlueprintVariables["alias"] = list;
        var map = new Dictionary<object, object?>(ReferenceEqualityComparer.Instance) { [a] = b };
        map[map] = list;
        a.BlueprintVariables["map"] = map;
        var set = new HashSet<object?> { a, b, list };
        set.Add(set);
        a.BlueprintSets["cards"] = set;
        b.BlueprintVariables["set"] = set;
        a.BlueprintVariables["array"] = new CardInstance[] { a, b };
        var array = new object?[2];
        array[0] = array; array[1] = b;
        a.BlueprintVariables["cycle-array"] = array;
        var graph = CardGraphClone.Create(new[] { a, a });
        var x = graph.Roots[0]; var y = graph.Cards[b];
        var copiedList = (List<object?>)x.BlueprintVariables["list"]!;
        var copiedMap = (Dictionary<object, object?>)x.BlueprintVariables["map"]!;
        var copiedSet = x.BlueprintSets["cards"];
        var copiedArray = (object?[])x.BlueprintVariables["cycle-array"]!;
        if (graph.Cards.Count != 2 || !ReferenceEquals(x, graph.Roots[1])
            || ReferenceEquals(x, a) || ReferenceEquals(y, b) || ReferenceEquals(x, y)
            || !ReferenceEquals(x.CurrentTarget, y) || !ReferenceEquals(y.CurrentTarget, x)
            || !ReferenceEquals(x.BlueprintVariables["self"], x)
            || !ReferenceEquals(x.BlueprintVariables["vars"], x.BlueprintVariables)
            || !ReferenceEquals(copiedList[0], x) || !ReferenceEquals(copiedList[1], y)
            || !ReferenceEquals(copiedList[3], copiedList)
            || !ReferenceEquals(y.BlueprintVariables["alias"], copiedList)
            || !ReferenceEquals(copiedMap[x], y) || !ReferenceEquals(copiedMap[copiedMap], copiedList)
            || !ReferenceEquals(y.BlueprintVariables["set"], copiedSet)
            || !copiedSet.Contains(x) || !copiedSet.Contains(copiedSet)
            || !ReferenceEquals(copiedArray[0], copiedArray) || !ReferenceEquals(copiedArray[1], y)
            || x.BlueprintVariables["array"] is not CardInstance[] cards || !ReferenceEquals(cards[1], y))
            return "graph identity, cycle or runtime array type lost";
        copiedList.Clear(); copiedMap.Clear(); copiedSet.Clear(); copiedArray[1] = null;
        if (list.Count != 4 || map.Count != 2 || set.Count != 4 || array[1] != b)
            return "copy mutation reached original";
        b.BlueprintVariables.Clear(); a.CurrentTarget = null;
        if (y.BlueprintVariables.Count != 2 || x.CurrentTarget != y)
            return "original mutation reached copy";
        if (CardGraphClone.Create(Array.Empty<CardInstance>()).Cards.Count != 0)
            return "empty graph differs";
        return null;
    }

    private static string? CardGraphLedgersAndRawState(CardDatabase db)
    {
        var a = CloneTestCard(2);
        // Seed backing fields directly to catch accidental use of projected setters.
        SeedCloneScalars(a);
        var buff = new CardBuff();
        SeedCloneScalars(buff);
        buff.CustomAbilityDetails.Add("custom");
        a.BuffsBySource[(3, true)] = buff;
        a.SuppressStrippedBuffs = new() { new((3, true), buff) };
        a.BlueprintVariables["buff"] = buff;
        a.BlueprintVariables["details"] = buff.CustomAbilityDetails;
        a.SuppressStrippedKeywords = new() { "old", "next" };
        a.SuppressStrippedAbilities = new(StringComparer.OrdinalIgnoreCase) { ["Ability"] = new() { 4, 9 } };
        a.ReceivedAbilitiesFromCards["Ability"] = a.SuppressStrippedAbilities["Ability"];
        a.CardsBuffedByThisCard.AddRange(new[] { 4, 8 });
        a.Keywords.Add("pinned"); a.RemovedGameplayTags.Add("Tag");
        a.SuppressionExceptionTriggers.Add("trigger");
        a.CustomJson.Set("KEY", "1", CardJsonType.Boolean);
        a.CustomJson.Set("é", "[1,\"x\"]", CardJsonType.IntArray);
        a.CustomJson.Set("É", "1", CardJsonType.String);
        a.BlueprintVariables["json"] = a.CustomJson;
        var x = CardGraphClone.Create(new[] { a }).Roots[0];
        var c = x.BuffsBySource[(3, true)];
        foreach (var pair in new[] { (Original: (object)a, Copy: (object)x), (Original: (object)buff, Copy: (object)c) })
        foreach (var field in CloneFields(pair.Original.GetType()).Where(f => f.FieldType.IsValueType))
            if (!Equals(field.GetValue(pair.Original), field.GetValue(pair.Copy)))
                return $"raw scalar lost: {field.Name}";
        if (!ReferenceEquals(a.Definition, x.Definition)
            || ReferenceEquals(buff, c) || ReferenceEquals(buff.CustomAbilityDetails, c.CustomAbilityDetails)
            || !ReferenceEquals(c, x.SuppressStrippedBuffs![0].Value)
            || !ReferenceEquals(c, x.BlueprintVariables["buff"])
            || !ReferenceEquals(c.CustomAbilityDetails, x.BlueprintVariables["details"])
            || !ReferenceEquals(x.ReceivedAbilitiesFromCards["Ability"], x.SuppressStrippedAbilities!["ABILITY"])
            || !ReferenceEquals(x.CustomJson, x.BlueprintVariables["json"])
            || !x.RemovedGameplayTags.Contains("TAG") || x.Keywords.Contains("PINNED")
            || !x.CustomJson.HasType("key", CardJsonType.Boolean)
            || !x.CustomJson.HasType("é", CardJsonType.IntArray)
            || !x.CustomJson.HasType("É", CardJsonType.String)
            || !a.CustomJson.Keys.SequenceEqual(x.CustomJson.Keys)
            || !a.CardsBuffedByThisCard.SequenceEqual(x.CardsBuffedByThisCard)
            || !a.SuppressStrippedKeywords.SequenceEqual(x.SuppressStrippedKeywords!))
            return "ledger, comparer, JSON type or alias differs";
        foreach (var field in CloneFields(typeof(CardInstance)).Where(f => !f.FieldType.IsValueType && f.FieldType != typeof(string) && f.FieldType != typeof(CardDefinition)))
            if (field.GetValue(a) is object source && ReferenceEquals(source, field.GetValue(x)))
                return $"mutable card field shared: {field.Name}";
        c.CustomAbilityDetails.Clear(); x.SuppressStrippedAbilities!["Ability"].Clear();
        x.CustomJson.Set("key", "changed", CardJsonType.String);
        x.Keywords.Clear(); x.CardsBuffedByThisCard.Clear();
        if (buff.CustomAbilityDetails.Count != 1 || a.ReceivedAbilitiesFromCards["Ability"].Count != 2
            || !a.CustomJson.HasType("key", CardJsonType.Boolean) || a.Keywords.Count != 1
            || a.CardsBuffedByThisCard.Count != 2) return "mutable ledger leaked";
        // Unset fallback fields must stay unset when the copy's definition changes.
        var fallback = CardGraphClone.Create(new[] { CloneTestCard(10) }).Roots[0];
        fallback.Definition = new CardDefinition { Attack = 19, Kredits = 11 };
        if (fallback.PublicAttack != 19 || fallback.PublicKreditCost != 11)
            return "unset raw field became pinned to old template";
        return null;
    }

    private static IEnumerable<FieldInfo> CloneFields(Type type)
        => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static void SeedCloneScalars(object value)
    {
        foreach (var field in CloneFields(value.GetType()))
        {
            var type = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
            if (type == typeof(int)) field.SetValue(value, -137);
            else if (type == typeof(bool)) field.SetValue(value, true);
            else if (type.IsEnum) field.SetValue(value, Enum.ToObject(type, 1));
            else if (type == typeof((int, int))) field.SetValue(value, (-17, 29));
        }
    }

    private static string? CardGraphCoverageAndRejection(CardDatabase db)
    {
        // Explicit field-name inventory: a newly added reference must get a disposition.
        var names = new[] { "StaticTemplateIdentity", "Name", "Definition", "CurrentTarget", "SalvageFaction",
            "SuppressionExceptionTriggers", "SuppressStrippedKeywords", "SuppressStrippedBuffs",
            "SuppressStrippedCustomAbility", "Keywords", "ReceivedAbilitiesFromCards", "SuppressStrippedAbilities",
            "RemovedGameplayTags", "CustomJson", "BlueprintSets", "BlueprintVariables", "BuffsBySource", "CardsBuffedByThisCard" };
        var expected = names.Select(n => $"<{n}>k__BackingField").Order().ToArray();
        var actual = CloneFields(typeof(CardInstance)).Where(f => !f.FieldType.IsValueType).Select(f => f.Name).Order().ToArray();
        if (!expected.SequenceEqual(actual)) return "CardInstance reference field inventory changed";
        if (!CloneFields(typeof(CardBuff)).Where(f => !f.FieldType.IsValueType).Select(f => f.Name)
            .SequenceEqual(new[] { "_customAbilityDetails" })) return "CardBuff reference field inventory changed";
        foreach (var type in new[] { typeof(CardInstance), typeof(CardBuff) })
        foreach (var field in CloneFields(type).Where(f => f.FieldType.IsValueType))
        {
            var scalar = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
            if (scalar != typeof(int) && scalar != typeof(bool) && !scalar.IsEnum && scalar != typeof((int, int)))
                return $"new value field requires reference-containment review: {field.Name}";
        }
        if (!CloneFields(typeof(CardCustomJson)).Select(f => f.Name).Order()
            .SequenceEqual(new[] { "_types", "_values" })) return "CardCustomJson field inventory changed";
        foreach (var unsupported in new object[] { new object(), new Version(1, 2), new CardBuff[1], new CloneTestList(),
            new HashSet<string>(StringComparer.InvariantCulture), new Dictionary<string, object?>(StringComparer.InvariantCultureIgnoreCase) })
        {
            var a = CloneTestCard(1);
            a.BlueprintVariables["unsupported"] = unsupported;
            try { CardGraphClone.Create(new[] { a }); return "unsupported payload/comparer accepted"; }
            catch (NotSupportedException) { }
            if (!ReferenceEquals(a.BlueprintVariables["unsupported"], unsupported)) return "failure mutated source";
        }
        return null;
    }

    private sealed class CloneTestList : List<int> { }

    private static string? CardGraphIdentityContainerContinuation(CardDatabase db)
    {
        foreach (bool referenceComparer in new[] { false, true })
        {
            var cards = Enumerable.Range(0, 70).Select(CloneTestCard).ToArray();
            var root = cards[0];
            IEqualityComparer<object> comparer = referenceComparer ? ReferenceEqualityComparer.Instance : EqualityComparer<object>.Default;
            var map = new Dictionary<object, object?>(comparer);
            var set = new HashSet<object?>(referenceComparer ? ReferenceEqualityComparer.Instance : EqualityComparer<object?>.Default);
            root.BlueprintVariables["map"] = map;
            root.BlueprintSets["set"] = set;
            map[map] = set; set.Add(set); set.Add(null);
            for (int i = 0; i < 45; i++) { map.Add(cards[i], cards[i]); set.Add(cards[i]); }
            foreach (int i in new[] { 22, 5, 37, 2 }) { map.Remove(cards[i]); set.Remove(cards[i]); }
            int capacity = map.EnsureCapacity(0);
            set.EnsureCapacity(capacity);
            // Default object equality uses each boxed int's value as hash: these
            // seven distinct keys occupy the same bucket before remapping.
            for (int i = 1; i <= 7; i++)
            {
                object collidingKey = i * capacity;
                map[collidingKey] = cards[i]; set.Add(collidingKey);
            }
            var graph = CardGraphClone.Create(cards);
            var target = graph.Cards[root];
            var copiedMap = (Dictionary<object, object?>)target.BlueprintVariables["map"]!;
            var copiedSet = target.BlueprintSets["set"];
            object? Remap(object? item) => item is CardInstance card ? graph.Cards[card]
                : ReferenceEquals(item, map) ? copiedMap : ReferenceEquals(item, set) ? copiedSet : item;
            var rng = new Random(533);
            for (int step = 0; step < 450; step++)
            {
                if (step == 0)
                    for (int i = 1; i <= 7; i++)
                    {
                        object key = map.Keys.First(k => k is int number && number == i * capacity);
                        if (!map.Remove(key) || !copiedMap.Remove(Remap(key)!)
                            || !set.Remove(key) || !copiedSet.Remove(Remap(key))) return "collision-chain removal differs";
                    }
                var card = cards[rng.Next(cards.Length)];
                var copy = graph.Cards[card];
                if (rng.Next(3) == 0)
                {
                    if (map.Remove(card) != copiedMap.Remove(copy) || set.Remove(card) != copiedSet.Remove(copy))
                        return $"remapped removal differs at {step}";
                }
                else { map[card] = card; copiedMap[copy] = copy; set.Add(card); copiedSet.Add(copy); }
                if (step == 125) { map.EnsureCapacity(257); copiedMap.EnsureCapacity(257); set.EnsureCapacity(257); copiedSet.EnsureCapacity(257); }
                if (step % 71 == 0) { map.TrimExcess(); copiedMap.TrimExcess(); set.TrimExcess(); copiedSet.TrimExcess(); }
                if (!map.Keys.Select(Remap).SequenceEqual(copiedMap.Keys)
                    || !map.Values.Select(Remap).SequenceEqual(copiedMap.Values)
                    || !set.Select(Remap).SequenceEqual(copiedSet)
                    || map.Any(p => !copiedMap.TryGetValue(Remap(p.Key)!, out var v) || !ReferenceEquals(v, Remap(p.Value)))
                    || set.Any(p => !copiedSet.Contains(Remap(p))))
                    return $"rehash, lookup or ordering differs at {step}";
            }
            copiedMap.Clear(); copiedSet.Clear();
            if (map.Count == 0 || set.Count == 0) return "shared identity-container buffers";
        }
        // Exercise allocated-empty/all-removed/cleared containers and case folding.
        foreach (var comparer in new IEqualityComparer<string>[] { EqualityComparer<string>.Default, StringComparer.Ordinal, StringComparer.OrdinalIgnoreCase })
        for (int mode = 0; mode < 4; mode++)
        {
            var root = CloneTestCard(1);
            var map = new Dictionary<string, object?>(comparer);
            var set = new HashSet<string>(comparer);
            root.BlueprintVariables["nested"] = map;
            root.BlueprintVariables["set"] = set;
            if (mode > 0) { map.EnsureCapacity(17); set.EnsureCapacity(17); }
            if (mode > 1) { map["A"] = 1; set.Add("A"); map.Remove("A"); set.Remove("A"); }
            if (mode > 2) { map.Clear(); set.Clear(); }
            var copy = CardGraphClone.Create(new[] { root }).Roots[0];
            var cm = (Dictionary<string, object?>)copy.BlueprintVariables["nested"]!;
            var cs = (HashSet<string>)copy.BlueprintVariables["set"]!;
            foreach (string key in new[] { "A", "a", "B", "b", "é", "É" })
            { map[key] = key; cm[key] = key; set.Add(key); cs.Add(key); }
            if (!map.SequenceEqual(cm) || !set.SequenceEqual(cs)) return "empty-container or comparer continuation differs";
        }
        return null;
    }

    private static string? CardGraphEmptyStorageMetadata(CardDatabase db)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach(var comparer in new IEqualityComparer<string>[] { EqualityComparer<string>.Default,
            StringComparer.Ordinal, StringComparer.OrdinalIgnoreCase })
        for(int mode=0; mode<5; mode++)
        {
            var root=CloneTestCard(1);
            var map=new Dictionary<string,object?>(comparer);
            var set=new HashSet<string>(comparer);
            if(mode>0) { map.EnsureCapacity(17); set.EnsureCapacity(17); }
            if(mode>1) { map["old"]=root; set.Add("old"); map.Remove("old"); set.Remove("old"); }
            if(mode>2) { map.Clear(); set.Clear(); }
            if(mode>3) { map.TrimExcess(); set.TrimExcess(); }
            // Materialized views must not retain the source owner on a clone.
            _=map.Keys; _=map.Values;
            root.BlueprintVariables["map"]=root.BlueprintVariables["alias"]=map;
            root.BlueprintVariables["set"]=set;
            var copy=CardGraphClone.Create(new[]{root}).Roots[0];
            var cm=(Dictionary<string,object?>)copy.BlueprintVariables["map"]!;
            var cs=(HashSet<string>)copy.BlueprintVariables["set"]!;
            if(ReferenceEquals(map,cm) || ReferenceEquals(set,cs)
                || !ReferenceEquals(cm,copy.BlueprintVariables["alias"])) return "empty graph identity/alias";
            foreach(var pair in new[]{(Source:(object)map,Copy:(object)cm),(Source:(object)set,Copy:(object)cs)})
            {
                var type=pair.Source.GetType();
                foreach(string name in new[]{"_count","_freeCount","_freeList","_version","_fastModMultiplier","_comparer"})
                {
                    var field=type.GetField(name,flags)!;
                    if(!Equals(field.GetValue(pair.Source),field.GetValue(pair.Copy))) return "empty metadata: "+name;
                }
                foreach(string name in new[]{"_buckets","_entries"})
                {
                    var field=type.GetField(name,flags)!;
                    var a=(Array?)field.GetValue(pair.Source); var b=(Array?)field.GetValue(pair.Copy);
                    if((a is null)!=(b is null) || (a is not null && (ReferenceEquals(a,b) || a.Length!=b!.Length)))
                        return "empty storage: "+name;
                }
            }
            if(typeof(Dictionary<string,object?>).GetField("_keys",flags)!.GetValue(cm) is not null
                || typeof(Dictionary<string,object?>).GetField("_values",flags)!.GetValue(cm) is not null)
                return "source cached views retained";
            if(map.EnsureCapacity(0)!=cm.EnsureCapacity(0) || set.EnsureCapacity(0)!=cs.EnsureCapacity(0))
                return "empty capacity lost";
            foreach(string key in new[]{"A","a","B","b","é","É"})
            { map[key]=key; cm[key]=key; set.Add(key); cs.Add(key); }
            if(!map.SequenceEqual(cm) || !set.SequenceEqual(cs)) return "empty continuation/comparer";
            cm["copy-only"]=1; cs.Add("copy-only");
            map["source-only"]=2; set.Add("source-only");
            if(map.ContainsKey("copy-only") || set.Contains("copy-only") || cm.ContainsKey("source-only") || cs.Contains("source-only"))
                return "empty storage mutation leaked";
        }
        foreach(bool isSet in new[]{false,true})
        foreach(string badField in new[]{"_freeList","_freeCount","_count","_buckets"})
        {
            var root=CloneTestCard(1);
            object value=isSet ? new HashSet<string>() : new Dictionary<string,object?>();
            root.BlueprintVariables["corrupt"]=value;
            value.GetType().GetField(badField,flags)!.SetValue(value,
                badField=="_buckets" ? (object)new int[3] : badField=="_freeList" ? -2 : 1);
            try { CardGraphClone.Create(new[]{root}); return "invalid empty metadata accepted: "+badField; }
            catch(NotSupportedException) { }
        }
        return null;
    }

    private static string? CardGraphContainerContinuation(CardDatabase db)
    {
        // Rebuilding contents passes initial equality, but fails the first insertion.
        foreach (int seed in new[] { 0, 17, 223 })
        {
            var card = CloneTestCard(1);
            var map = card.BlueprintVariables;
            var set = card.Keywords;
            map.EnsureCapacity(107); set.EnsureCapacity(107);
            for (int i = 0; i < 40; i++) { map[$"key-{i}"] = i; set.Add($"key-{i}"); }
            foreach (int i in new[] { 7, 2, 31, 0, 18 }) { map.Remove($"key-{i}"); set.Remove($"key-{i}"); }
            card.CustomJson.Set("first", "1", CardJsonType.Number);
            card.CustomJson.Set("hole", "x", CardJsonType.String);
            card.CustomJson.Set("last", "true", CardJsonType.Boolean);
            card.CustomJson.Remove("hole");
            // Force cached views to exist before cloning.
            _ = map.Keys; _ = map.Values;
            var copy = CardGraphClone.Create(new[] { card }).Roots[0];
            if (map.EnsureCapacity(0) != copy.BlueprintVariables.EnsureCapacity(0)
                || set.EnsureCapacity(0) != copy.Keywords.EnsureCapacity(0)) return "capacity lost";
            var rng = new Random(seed);
            for (int step = 0; step < 400; step++)
            {
                string key = $"key-{rng.Next(120)}";
                switch (rng.Next(6))
                {
                    case 0: map.Remove(key); copy.BlueprintVariables.Remove(key);
                        set.Remove(key); copy.Keywords.Remove(key);
                        card.CustomJson.Remove(key); copy.CustomJson.Remove(key); break;
                    case 1 when step % 11 == 0:
                        map.TrimExcess(); copy.BlueprintVariables.TrimExcess();
                        set.TrimExcess(); copy.Keywords.TrimExcess(); break;
                    case 2 when step % 71 == 0:
                        map.Clear(); copy.BlueprintVariables.Clear(); set.Clear(); copy.Keywords.Clear();
                        card.CustomJson.Clear(); copy.CustomJson.Clear(); break;
                    default:
                        map[key] = step; copy.BlueprintVariables[key] = step;
                        set.Add(key); copy.Keywords.Add(key);
                        card.CustomJson.Set(key, step.ToString(), CardJsonType.Number);
                        copy.CustomJson.Set(key, step.ToString(), CardJsonType.Number); break;
                }
                if (!map.SequenceEqual(copy.BlueprintVariables) || !set.SequenceEqual(copy.Keywords)
                    || !card.CustomJson.SequenceEqual(copy.CustomJson)
                    || card.CustomJson.Any(p => card.CustomJson.TypeOf(p.Key) != copy.CustomJson.TypeOf(p.Key)))
                    return $"container continuation differs: seed={seed}, step={step}";
                if (step % 53 == 0)
                    copy = CardGraphClone.Create(new[] { card }).Roots[0];
            }
            copy.BlueprintVariables["copy-only"] = 1;
            copy.Keywords.Add("copy-only");
            if (map.ContainsKey("copy-only") || set.Contains("copy-only")) return "container buffer shared";
        }
        return null;
    }

    private static string? CardGraphPayloadsAndOrdering(CardDatabase db)
    {
        var a = CloneTestCard(1);
        var variables = a.BlueprintVariables;
        variables["removed"] = 0;
        variables["ints"] = new List<int> { 3, -4 };
        variables.Remove("removed");
        variables["cards"] = new List<CardInstance> { a };
        variables["strings"] = new List<string> { "z", "a" };
        variables["int-array"] = new int[] { 8, 2 };
        variables["string-array"] = new string[] { "x", "y" };
        variables["structs"] = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase) { ["Card"] = a, ["value"] = 1.25d }
        };
        variables["primitives"] = new List<object?>
            { null, true, (byte)3, (sbyte)-1, (short)-2, (ushort)4, 5, 6u, 7L, 8ul, 1.5f, 2.5d, 3.5m, 'x', Side.Left };
        var x = CardGraphClone.Create(new[] { a }).Roots[0];
        var copied = x.BlueprintVariables;
        if (!variables.Keys.SequenceEqual(copied.Keys)
            || !((List<int>)copied["ints"]!).SequenceEqual(new[] { 3, -4 })
            || ((List<CardInstance>)copied["cards"]!)[0] != x
            || !((List<string>)copied["strings"]!).SequenceEqual(new[] { "z", "a" })
            || !((int[])copied["int-array"]!).SequenceEqual(new[] { 8, 2 })
            || !((string[])copied["string-array"]!).SequenceEqual(new[] { "x", "y" })
            || ((List<Dictionary<string, object?>>)copied["structs"]!)[0]["CARD"] != x
            || !((List<object?>)variables["primitives"]!).SequenceEqual((List<object?>)copied["primitives"]!))
            return "payload type, value, comparer or current ordering differs";
        foreach (var key in variables.Keys)
            if (ReferenceEquals(variables[key], copied[key])) return $"payload shared: {key}";
        try { CardGraphClone.Create(new CardInstance[] { null! }); return "null root accepted"; }
        catch (ArgumentException) { }
        return null;
    }
}
