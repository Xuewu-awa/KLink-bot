using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? NativeSalvageFaction(CardDatabase db)
    {
        string[] factions = { "NotAvailable", "Germany", "Britain", "Japan", "Soviet", "USA",
            "France", "Italy", "Poland", "Finland", "Anzac", "Allies", "Neutral" };
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int sourceFaction in Enumerable.Range(0, 13))
        foreach (int instigatorFaction in Enumerable.Range(0, 13))
        foreach (bool gold in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            var instigator = state.CreateWithId(PlainUnit, side, 30, CardLocation.Discard, 0);
            var original = state.CreateWithId(PlainUnit, side.Opposite(), 90, CardLocation.Discard, 1, isGold: gold);
            instigator.FactionId = instigatorFaction; original.FactionId = sourceFaction;
            int ownCalls = 0; bool correct = true;
            state.CardCreated += card => {
                ownCalls++;
                int faction = int.TryParse(card.SalvageFaction, out int raw) ? raw : CardDatabase.FactionIdOf(card.SalvageFaction ?? "");
                correct &= card.IsSalvaged && card.SalvagedCardId == original.CardId && faction == instigatorFaction
                    && card.IsGold == gold && card.Owner == side && card.Location == side.HandOf();
                card.CardSeen = false;
            };
            var ids = engine.Api.SalvageMultipleUnits(new[] { original.CardId }, instigator.CardId);
            if (!correct || ownCalls != 1 || ids.Count != 1 || state.ById(ids[0])?.CardSeen != true)
                return $"salvage faction/seen {side}/original={factions[sourceFaction]}/instigator={factions[instigatorFaction]}/gold={gold}: own={ownCalls}, correct={correct}";
        }
        return null;
    }

    private static string? NativeSalvagePipeline(CardDatabase db)
    {
        const string name = "card_unit_typhoon_mk_ib", watcherName = "card_unit_brewster_f2a";
        const string t35 = "OnOtherCardCreatedAlterCard", t56 = "OnOtherCardSalvaged", t57 = "OnOtherCardSpawnedInHand", own = "OnCardSpawnedInHand";
        var watcherLocals = (IDictionary<string, IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(watcherName)!.Locals;
        var ownLocals = (IDictionary<string, IReadOnlyList<KismetStep>>)KismetLibrary.Default.Find(name)!.Locals;
        var cache = (Dictionary<string, Dictionary<string, KismetProgram>>)typeof(KismetLibrary)
            .GetField("_localCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(KismetLibrary.Default)!;
        var installed = new List<(IDictionary<string, IReadOnlyList<KismetStep>> Locals, string Name, string Trigger, KismetProgram? Prior)>();
        void Install(IDictionary<string, IReadOnlyList<KismetStep>> locals, string trigger, KismetExpr subject, KismetExpr extra)
        {
            if (locals.ContainsKey(trigger)) throw new InvalidOperationException("salvage probe would replace real IR");
            string cardName = ReferenceEquals(locals, ownLocals) ? name : watcherName;
            KismetProgram? prior = null;
            if (cache.TryGetValue(cardName, out var programs)) programs.Remove(trigger, out prior);
            locals.Add(trigger, new[] {
                // Marker allocation also invokes T35. Ignore it to keep this probe bounded.
                new KismetStep(0, "jumpIfNot", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null,
                    trigger == t35 ? new KismetExpr { Math = "EqualEqual_NameName", Args = new[] {
                        new KismetExpr { Var = "name", Context = subject }, new KismetExpr { Name = name } } }
                        : new KismetExpr { Int = 1 }, 6, null, null),
                new KismetStep(1, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), new KismetExpr { Str = trigger }, null, -1, "_auditSalvagePhase", null) { DestinationScope = "instance" },
                new KismetStep(2, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), subject, null, -1, "_auditSalvageSubject", null) { DestinationScope = "instance" },
                new KismetStep(3, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), extra, null, -1, "_auditSalvageExtra", null) { DestinationScope = "instance" },
                new KismetStep(4, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), new KismetExpr { Math = "Add_IntInt", Args = new[] { new KismetExpr { Var = "_auditSalvageCount", Scope = "instance" }, new KismetExpr { Int = 1 } } }, null, -1, "_auditSalvageCount", null) { DestinationScope = "instance" },
                new KismetStep(5, "call", "SpawnCardInDeckBySide", new[] { new KismetExpr { Int = (int)Side.Right }, new KismetExpr { Str = "card_unit_routed_troops" }, new KismetExpr { Int = 30 }, new KismetExpr { Int = 1 } }, Array.Empty<int>(), null, null, -1, null, null),
                new KismetStep(6, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null),
            });
            installed.Add((locals, cardName, trigger, prior));
        }
        try
        {
            Install(watcherLocals, t35, new() { Var = "cardPlayed" }, new() { Var = "method" });
            Install(watcherLocals, t56, new() { Var = "newCardSalvagedID" }, new() { Var = "cardSalvagedID" });
            Install(watcherLocals, t57, new() { Var = "spawnedCardID" }, new() { Var = "spawnedSide" });
            Install(ownLocals, own, new() { Int = 0 }, new() { Var = "spawnedSide" });
            foreach (Side side in new[] { Side.Left, Side.Right })
            foreach (int requested in new[] { 0, 1, 2, 3 })
            foreach (int available in new[] { 0, 1, 3 })
            foreach (bool mutate in new[] { false, true })
            {
                var (engine, state) = EmptyBoard(db);
                var watcher = state.CreateWithId(watcherName, side.Opposite(), 20, CardLocation.Discard, 0);
                watcher.Keywords.Clear(); watcher.Definition = new CardDefinition { Name = watcherName, UsedTriggers = new[] { t35, t56, t57 } };
                var instigator = state.CreateWithId(PlainUnit, side, 30, CardLocation.Discard, 0); instigator.FactionId = 1;
                var original = state.CreateWithId(name, side.Opposite(), 90, CardLocation.Discard, 0);
                for (int i = 0; i < GameState.HandCapacity - available; i++) state.CreateWithId(PlainUnit, side, 100 + i, side.HandOf(), i);
                int count = Math.Min(requested, available), seenMarkers = 0; bool correct = true;
                var copies = new List<CardInstance>(); var events = new List<(string Phase, int Id)>();
                var phaseCounts = new Dictionary<CardInstance, int>();
                List<SubAction> Notices() => state.ActionLog.SelectMany(x => x.SubActions).Where(x =>
                    x.Name is "ZActionSalvageMultipleUnits" or "ZActionCardsSeen" || x.Name == "ZActionCreateNonVisualCard"
                    && x.Values.Any(v => v.Name == "card_type" && v.Text == name)).ToList();
                var created = state.CardCreated;
                state.CardCreated = card => {
                    created?.Invoke(card);
                    if (card.Name == name)
                    {
                        int faction = int.TryParse(card.SalvageFaction, out int rawFaction) ? rawFaction : CardDatabase.FactionIdOf(card.SalvageFaction ?? "");
                        correct &= Notices().Count == copies.Count && faction == (copies.Count == 0 || !mutate ? 1 : 2);
                        card.CardSeen = false; copies.Add(card); events.Add(("create", card.CardId));
                    }
                    if (card.Name != "card_unit_routed_troops") return;
                    var receivers = copies.Prepend(watcher).Where(x => Convert.ToInt32(x.BlueprintVariables.GetValueOrDefault("_auditSalvageCount")) > phaseCounts.GetValueOrDefault(x)).ToArray();
                    if (receivers.Length != 1) { correct = false; return; }
                    var receiver = receivers[0]; phaseCounts[receiver] = Convert.ToInt32(receiver.BlueprintVariables["_auditSalvageCount"]);
                    string phase = (string)receiver.BlueprintVariables["_auditSalvagePhase"]!;
                    object? subject = receiver.BlueprintVariables.GetValueOrDefault("_auditSalvageSubject");
                    int id = phase == own ? receiver.CardId : subject is CardInstance c ? c.CardId : Convert.ToInt32(subject);
                    int extra = Convert.ToInt32(receiver.BlueprintVariables.GetValueOrDefault("_auditSalvageExtra"));
                    events.Add((phase, id)); seenMarkers++;
                    if (phase == t35)
                    {
                        correct &= extra == 3 && subject == copies[^1] && copies[^1].CardSeen && Notices().Count == copies.Count;
                        if (mutate) { copies[^1].CardSeen = false; instigator.FactionId = 2; }
                    }
                    if (phase == t56) correct &= extra == original.CardId && copies.Count == events.Count(x => x.Phase == t56);
                    if (phase == own || phase == t57)
                        correct &= copies.Count == count && extra == (int)side && Notices().Count == count + 2
                            && state.ById(id)?.CardSeen == !mutate;
                };
                var ids = engine.Api.SalvageMultipleUnits(Enumerable.Repeat(original.CardId, requested).ToArray(), instigator.CardId);
                var expected = new List<(string, int)>();
                foreach (var copy in copies) { expected.Add(("create", copy.CardId)); expected.Add((t35, copy.CardId)); expected.Add((t56, copy.CardId)); }
                foreach (var copy in copies) { expected.Add((own, copy.CardId)); expected.Add((t57, copy.CardId)); }
                var notices = Notices();
                var expectedValues = new[] { ActionValue2.Str("cardsToSalvage", string.Join(";", Enumerable.Repeat(original.CardId, count))), ActionValue2.Int("instigatorID", instigator.CardId), ActionValue2.Str("createdCardIDs", string.Join(";", ids)) };
                if (!correct || copies.Count != count || seenMarkers != count * 4 || !events.SequenceEqual(expected) || !ids.SequenceEqual(copies.Select(x => x.CardId))
                    || notices.Count != count + 2 || notices[count].Name != "ZActionSalvageMultipleUnits" || !notices[count].Values.SequenceEqual(expectedValues)
                    || notices[count + 1].Name != "ZActionCardsSeen" || notices[count + 1].Values.Single(x => x.Name == "cardIDs").Text != string.Join(";", ids)
                    || notices[count + 1].Values.Single(x => x.Name == "seenValues").Text != string.Join(";", Enumerable.Repeat("1", count)))
                    return $"salvage pipeline {side}/requested={requested}/available={available}/mutate={mutate}: correct={correct}, copies={copies.Count}, markers={seenMarkers}, notices={notices.Count}, events={string.Join(',', events)}";
            }
            return null;
        }
        finally
        {
            // Other synthetic tests use these same fixtures. Restore their cached
            // programs as well as the mutable IR so test order cannot change behavior.
            foreach (var (locals, cardName, trigger, prior) in installed)
            {
                locals.Remove(trigger);
                if (cache.TryGetValue(cardName, out var programs))
                {
                    programs.Remove(trigger);
                    if (prior is not null) programs[trigger] = prior;
                }
            }
        }
    }
}
