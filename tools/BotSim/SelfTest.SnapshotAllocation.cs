using System.Collections;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    // Frozen projections from a1cada7. This is a serialization/ownership oracle,
    // not a statement about the authority or completeness of the PvP buff rules.
    private static CardSnapshot LegacySnapshotBuffProjection(CardInstance card, CardSnapshot snapshot)
    {
        var BuffsBySource = card.BuffsBySource;
        return snapshot with
        {
            SuppressLedgerSources = BuffsBySource.Where(kv => kv.Value.HasSuppressLedgerEntry)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .Select(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}").ToArray(),
            AttackZeroEntrySources = BuffsBySource.Where(kv => kv.Value.HasZeroAttackEntry && kv.Value.Attack == 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .Select(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}").ToArray(),
            AttackSetValuesBySource = BuffsBySource.Where(kv => kv.Value.AttackSetValue.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.AttackSetValue!.Value),
            AttackEmptyDetailsBySource = BuffsBySource.Where(kv => kv.Value.AttackEmptyDetail.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.AttackEmptyDetail!.Value),
            DefenseZeroEntrySources = BuffsBySource.Where(kv => kv.Value.HasZeroDefenseEntry && kv.Value.Defense == 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .Select(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}").ToArray(),
            DefenseSetValuesBySource = BuffsBySource.Where(kv => kv.Value.DefenseSetValue.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.DefenseSetValue!.Value),
            AlpineBuffsBySource = BuffsBySource.Where(kv => kv.Value.Alpine != 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.Alpine),
            GuardBuffsBySource = BuffsBySource.Where(kv => kv.Value.Guard != 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.Guard),
            KreditCostZeroEntrySources = BuffsBySource.Where(kv => kv.Value.HasZeroKreditCostEntry && kv.Value.KreditCost == 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .Select(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}").ToArray(),
            PermanentHeavyArmorBySource = BuffsBySource.Where(kv => kv.Value.HeavyArmor != 0)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.HeavyArmor),
            TemporaryHeavyArmorBySource = BuffsBySource.Where(kv => kv.Value.TemporaryHeavyArmorGive != 0)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.TemporaryHeavyArmorGive),
            HeavyArmorSetValuesBySource = BuffsBySource.Where(kv => kv.Value.HeavyArmorSetValue.HasValue)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.HeavyArmorSetValue!.Value),
            HeavyArmorEmptyDetailsBySource = BuffsBySource.Where(kv => kv.Value.HeavyArmorEmptyDetail.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.HeavyArmorEmptyDetail!.Value),
            DefenseEmptyDetailsBySource = BuffsBySource.Where(kv => kv.Value.DefenseEmptyDetail.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.DefenseEmptyDetail!.Value),
            KreditCostEmptyDetailsBySource = BuffsBySource.Where(kv => kv.Value.KreditCostEmptyDetail.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.KreditCostEmptyDetail!.Value),
            KreditCostSetValuesBySource = BuffsBySource.Where(kv => kv.Value.KreditCostSetValue.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.KreditCostSetValue!.Value),
            OperationCostZeroEntrySources = BuffsBySource.Where(kv => kv.Value.HasZeroOperationCostEntry && kv.Value.OperationCost == 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .Select(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}").ToArray(),
            OperationCostSetValuesBySource = BuffsBySource.Where(kv => kv.Value.OperationCostSetValue.HasValue)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.OperationCostSetValue!.Value),
            SmokescreenBuffsBySource = BuffsBySource.Where(kv => kv.Value.Smokescreen != 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.Smokescreen),
            PinnedBuffsBySource = BuffsBySource.Where(kv => kv.Value.Pinned != 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.Pinned),
            DestructionBuffsBySource = BuffsBySource.Where(kv => kv.Value.Destruction != 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.Destruction),
            CustomAbilityBuffDetails = BuffsBySource.Where(kv => kv.Value.CustomAbilityDetails.Count > 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}",
                    kv => kv.Value.CustomAbilityDetails.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
            BlitzBuffsBySource = BuffsBySource.Where(kv => kv.Value.Blitz != 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.Blitz),
            FuryBuffsBySource = BuffsBySource.Where(kv => kv.Value.Fury != 0)
                .OrderBy(kv => kv.Key.SourceCardId).ThenBy(kv => kv.Key.Temporary)
                .ToDictionary(kv => $"{kv.Key.SourceCardId}/{kv.Key.Temporary}", kv => kv.Value.Fury),
        };
    }

    private static string? SnapshotEmptyBuffProjection(CardDatabase db)
    {
        var card = CloneTestCard(1);
        string Encode(CardSnapshot value) => JsonSerializer.Serialize(value, GameState.SnapshotJsonOptions);
        var cases = new List<CardBuff?> { null, new(), new() { CustomAbilityDetails = new(StringComparer.Ordinal) { "z", "a", "中文" } } };
        // Sparse cases exercise every scalar presence/filter independently,
        // including zero-valued nullable entries and negative numeric values.
        foreach (var field in typeof(CardBuff).GetProperties().Where(p => p.CanWrite
            && p.Name is not ("SourceCardId" or "Temporary")))
        {
            object[] values = field.PropertyType == typeof(bool) ? new object[] { true }
                : field.PropertyType == typeof(int) || field.PropertyType == typeof(int?) ? new object[] { 0, -3, 7 }
                : Array.Empty<object>();
            foreach (var value in values)
            {
                var buff = new CardBuff(); field.SetValue(buff, value); cases.Add(buff);
            }
        }
        foreach (var buff in cases)
        {
            card.BuffsBySource.Clear();
            if (buff is not null) card.BuffsBySource[(9, true)] = buff;
            var snapshot = card.Snapshot();
            var expected = LegacySnapshotBuffProjection(card, snapshot);
            if (Encode(snapshot) != Encode(expected)) return "snapshot sparse projection differs";
            if (buff is null)
                foreach (var field in typeof(CardSnapshot).GetProperties().Where(p => p.PropertyType == typeof(string[])))
                    if (!ReferenceEquals(field.GetValue(snapshot), field.GetValue(expected)))
                        return "empty array identity differs: " + field.Name;
        }
        // Combined unsorted sources, the same source in both duration slots,
        // all ledger presences and independently copied custom ability details.
        card.BuffsBySource.Clear();
        foreach (var key in new[] { (9, true), (-2, false), (9, false), (3, true) })
        {
            var buff = new CardBuff();
            foreach (var field in typeof(CardBuff).GetProperties().Where(p => p.CanWrite))
            {
                if (field.PropertyType == typeof(bool)) field.SetValue(buff, true);
                if (field.PropertyType == typeof(int) || field.PropertyType == typeof(int?)) field.SetValue(buff, key.Item1);
            }
            buff.CustomAbilityDetails.Add("z"); buff.CustomAbilityDetails.Add("a");
            card.BuffsBySource[key] = buff;
        }
        string? CheckSaved(string label)
        {
            var snapshot = card.Snapshot();
            var expected = LegacySnapshotBuffProjection(card, snapshot);
            string saved = Encode(snapshot);
            if (saved != Encode(expected)) return label + " bytes";
            var second = card.Snapshot();
            foreach (var field in typeof(CardSnapshot).GetProperties().Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
            {
                var firstMap = field.GetValue(snapshot)!;
                var secondMap = field.GetValue(second)!;
                var legacyMap = field.GetValue(expected)!;
                if (ReferenceEquals(firstMap, secondMap)) return label + " shared mutable dictionary " + field.Name;
                var comparer = field.PropertyType.GetProperty("Comparer")!;
                if (!Equals(comparer.GetValue(firstMap), comparer.GetValue(legacyMap))) return label + " comparer " + field.Name;
                var valueType = field.PropertyType.GenericTypeArguments[1];
                object? value = valueType.IsArray ? Array.CreateInstance(valueType.GetElementType()!, 1)
                    : valueType == typeof(string) ? "snapshot-only"
                    : valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
                ((IDictionary)firstMap)["snapshot-only"] = value;
            }
            if (Encode(second) != saved || Encode(card.Snapshot()) != saved) return label + " snapshot mutation leaked";
            foreach (var buff in card.BuffsBySource.Values) buff.CustomAbilityDetails.Clear();
            card.BuffsBySource.Clear();
            return Encode(second) == saved ? null : label + " source mutation changed saved snapshot";
        }
        var error = CheckSaved("populated");
        return error ?? CheckSaved("empty");
    }
}
