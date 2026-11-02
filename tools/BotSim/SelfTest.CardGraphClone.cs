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
