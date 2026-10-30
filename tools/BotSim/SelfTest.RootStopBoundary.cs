using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static string? PlayRootStopBoundary(CardDatabase db) => RootStopBoundary(db, 0, 1, 2);
    private static string? MoveRootStopBoundary(CardDatabase db) => RootStopBoundary(db, 3);
    private static string? AttackRootStopBoundary(CardDatabase db) => RootStopBoundary(db, 4);

    private static string? RootStopBoundary(CardDatabase db, params int[] routes)
    {
        const string name = "card_unit_m36_destroyer", hook = "OnOtherCardAttackSwitchTarget";
        // Synthetic T30 reads flags before T31's separate StopAttack reset.
        using var probe = InstallSelectionDrawProbe(name, hook);
        var locals = (IDictionary<string, IReadOnlyList<KismetStep>>)KismetLibrary.Default!.Find(name)!.Locals!;
        KismetStep Observe(int index, string key, string getter) => new(index, "call", "JSON_SetBool",
            new[] { new KismetExpr { Int = 60 }, new KismetExpr { Str = key }, new KismetExpr { Call = getter } },
            Array.Empty<int>(), null, null, -1, null, null) { ByteOffset = index };
        locals[hook] = new[] {
            Observe(0, "audit_stop", "GetStopFurtherActions"), Observe(1, "audit_attack", "GetStopAttack"),
            new KismetStep(2, "call", "SetStopFurtherActions", new[] { new KismetExpr { Bool = true } },
                Array.Empty<int>(), null, null, -1, null, null) { ByteOffset = 2 },
            new KismetStep(3, "call", "SetStopAttack", new[] { new KismetExpr { Bool = true } },
                Array.Empty<int>(), null, null, -1, null, null) { ByteOffset = 3 },
            new KismetStep(4, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), null, null, -1, null, null)
                { ByteOffset = 4 }
        };
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int route in routes)
        foreach (bool reject in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db); state.ActiveSide = side; state.SetKredits(side, 20);
            string cardName = route == 1 ? "card_event_war_bonds" : route == 2 ? "card_event_ultra" : "card_unit_panzer_35_t";
            var actor = state.CreateWithId(cardName, side, 70, route < 3 ? side.HandOf() : side.HqOf(), 1);
            actor.EnteredPlayOnTurn = state.Turn - 1; actor.SetRemainingAttacks(1);
            var target = state.CreateWithId("card_unit_panzer_35_t", side.Opposite(), 80, CardLocation.BoardFrontline, 0);
            if (route == 3) state.Move(target, CardLocation.Discard, notifyMoved: false);
            var observer = state.CreateWithId(name, side, 60, side.HqOf(), 2);
            observer.Definition = new CardDefinition { Name = name, Type = "tank", UsedTriggers = new[] { hook } };
            observer.Keywords.Clear();
            state.CreateWithId("card_unit_panzer_35_t", side, 91, side.DeckOf(), 0);
            state.CreateWithId("card_unit_panzer_35_t", side, 92, side.DeckOf(), 1);
            bool seen = false, clean = false;
            var original = state.CardMoved;
            state.CardMoved = (card, oldLocation, newLocation, owner, reason) => {
                if (card == actor) {
                    seen = true; clean = !state.StopFurtherActions && !state.StopAttack;
                    if (route == 3) { state.StopFurtherActions = true; state.StopAttack = true; }
                }
                original?.Invoke(card, oldLocation, newLocation, owner, reason);
            };
            if (reject) state.Move(actor, CardLocation.NotAvailable, notifyMoved: false);
            state.StopFurtherActions = true; state.StopAttack = true;
            int before = state.ActionLog.Count;
            bool accepted = route < 3 ? engine.PlayCard(actor)
                : route == 3 ? engine.MoveUnit(actor, 0, out _) : engine.Attack(actor, target, out _);
            if (reject)
            {
                if (accepted || seen || !state.StopFurtherActions || !state.StopAttack || state.ActionLog.Count != before)
                    return $"rejected root changed action boundary: {side}/{route}";
                continue;
            }
            if (!accepted) return $"root rejected: {side}/{route}";
            if (route == 4)
            {
                seen = observer.CustomJson.ContainsKey("audit_stop") && observer.CustomJson.ContainsKey("audit_attack");
                clean = seen && !engine.Api.JsonGetBool(observer, "audit_stop") && !engine.Api.JsonGetBool(observer, "audit_attack");
            }
            else if (route == 2) { seen = true; clean = !state.StopFurtherActions && !state.StopAttack; }
            if (!seen || !clean || engine.Api.Vm.FaultedPrograms != 0)
                return $"first body observer saw stale stops: {side}/{route}, seen={seen}, clean={clean}";
            // Move's later effect phase resets StopFurtherActions; T31 resets
            // StopAttack. Neither is a second blanket root initialization.
            if (route == 3 && (state.StopFurtherActions || !state.StopAttack)
                || route == 4 && (!state.StopFurtherActions || state.StopAttack))
                return $"new callback flags lost or T31 phase reset missing: {side}/{route}";
        }
        return null;
    }
}
