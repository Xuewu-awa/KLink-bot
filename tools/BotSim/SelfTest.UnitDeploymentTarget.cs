using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static readonly string[] DeploymentTargetCards =
        ["card_unit_mosquito", "card_unit_tiger_moth", "card_unit_british_sherman_firefly"];

    private static string? UnitDeploymentRejectsExplicitTargets(CardDatabase db)
    {
        foreach (var name in DeploymentTargetCards)
        foreach (var side in new[] { Side.Left, Side.Right })
        foreach (var kind in new[] { "wrong_side", "hq", "hand", "missing", "covert", "wrong_type" })
        {
            if (kind == "wrong_type" && name != "card_unit_british_sherman_firefly") continue;
            var (e, source, units) = ActionTargetSmoke.Fixture(db, side, name);
            var targetSide = name == "card_unit_tiger_moth" ? side : side.Opposite();
            if (kind == "wrong_side") targetSide = targetSide.Opposite();
            var target = e.State.CreateWithId(kind == "wrong_type" ? "card_unit_arado_ar_196" : "card_unit_panzer_35_t",
                targetSide, 85, targetSide.HqOf(), 3);
            if (kind == "hand") e.State.Move(target, targetSide.HandOf(), 0);
            if (kind == "covert")
            {
                // Public target gate rejects only enemy unrevealed covert.
                if (targetSide == side) continue;
                target.Keywords.Add(Keyword.Covert); target.IsRevealed = false;
            }
            int id = kind == "missing" ? 99999 : kind == "hq" ? (targetSide == Side.Left ? 1 : 41) : 85;
            var observed = units.Append(target).ToArray();
            string before = TargetRootEffects(e, source, observed);
            if (MainActionPolicyAdapter.ApplyRoot(e, side, new(MainActionKind.Play, 70, id)))
                return $"invalid unit deployment accepted: {name}/{side}/{kind}";
            if (before != TargetRootEffects(e, source, observed) || e.Api.Vm.FaultedPrograms != 0)
                return $"rejected deployment changed effects or faulted: {name}/{side}/{kind}";
        }
        return null;
    }

    private static string? UnitDeploymentAcceptsExplicitTargets(CardDatabase db)
    {
        foreach (var name in DeploymentTargetCards)
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var (e, source, units) = ActionTargetSmoke.Fixture(db, side, name);
            var targetSide = name == "card_unit_tiger_moth" ? side : side.Opposite();
            var target = e.State.CreateWithId("card_unit_panzer_35_t", targetSide, 85, targetSide.HqOf(), 3);
            target.Defense = target.MaxDefense = 10;
            target.Attack = 2;
            // Query bindings must not replace an unrelated earlier override.
            source.BlueprintVariables["targetOverride"] = units[0];
            if (!MainActionPolicyAdapter.ApplyRoot(e, side, new(MainActionKind.Play, 70, 85)))
                return $"valid unit deployment rejected: {name}/{side}";
            int expected = name == "card_unit_tiger_moth" ? 11 : name == "card_unit_mosquito" ? 6 : 5;
            if (target.Defense != expected || (name == "card_unit_tiger_moth" && target.Attack != 11)
                || source.Location != side.HqOf() || e.Api.Vm.FaultedPrograms != 0)
                return $"unit deployment target effect mismatch: {name}/{side}, def={target.Defense}, atk={target.Attack}";
        }
        return null;
    }

    private static string? TrainingPlayRejectsUnsupportedSlot(CardDatabase db)
    {
        foreach (var side in new[] { Side.Left, Side.Right })
        foreach (var slot in new[] { -2, 0, 3, 999 })
        {
            var (e, source, units) = ActionTargetSmoke.Fixture(db, side, "card_unit_arado_ar_196");
            string before = TargetRootEffects(e, source, units);
            if (MainActionPolicyAdapter.ApplyRoot(e, side, new(MainActionKind.Play, 70, Slot: slot))
                || before != TargetRootEffects(e, source, units))
                return "unsupported placement was silently accepted: " + slot;
        }
        return null;
    }

    private static string? UnitDeploymentPredicateOutput(CardDatabase db)
    {
        // Special asset used only to exercise the local output contract, not
        // to certify this card as an ordinary PvP deployment.
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var (e, source, units) = ActionTargetSmoke.Fixture(db, side, "card_unit_superman");
            source.BlueprintVariables["targetOverride"] = units[1];
            foreach (var requested in new[] { units[2], e.State.Hq(side) })
            {
                var check = e.Api.CheckExplicitUnitDeployment(source, requested, out var resolved);
                if (!check.Can || !ReferenceEquals(resolved, requested.IsHq ? null : requested)
                    || !ReferenceEquals(source.BlueprintVariables["targetOverride"], units[1])
                    || e.Api.Vm.FaultedPrograms != 0)
                    return "local output or restored candidate binding mismatch";
            }
        }
        return null;
    }
    private static string? UnitDeploymentNullTargetGate(CardDatabase db)
    {
        foreach (var name in DeploymentTargetCards)
        foreach (var side in new[] { Side.Left, Side.Right })
        foreach (bool legal in new[] { true, false })
        {
            var (e, source, units) = ActionTargetSmoke.Fixture(db, side, name);
            foreach (var unit in units) e.State.Move(unit, unit.Owner.HandOf(), unit.CardId);
            var targetSide = name == "card_unit_tiger_moth" ? side : side.Opposite();
            var target = e.State.CreateWithId("card_unit_panzer_35_t", targetSide, 85,
                legal ? targetSide.HqOf() : targetSide.HandOf(), 3);
            target.Defense = target.MaxDefense = 10;
            target.Attack = 2;
            string before = TargetRootEffects(e, source, units.Append(target).ToArray());
            bool accepted = MainActionPolicyAdapter.ApplyRoot(e, side, new(MainActionKind.Play, 70));
            if (accepted == legal) return $"null deployment decision mismatch: {name}/{side}/{legal}";
            if (legal && before != TargetRootEffects(e, source, units.Append(target).ToArray()))
                return "rejected null deployment changed root effects";
            if (!legal && (source.Location != side.HqOf() || source.CurrentTarget is not null))
                return "targetless deployment did not forward zero target";
            if (!legal && (target.Defense != 10 || target.Attack != 2
                || units.Any(u => u.Defense != 5)))
                return $"targetless deployment changed unrelated unit: {name}/{side}";
            if (e.Api.Vm.FaultedPrograms != 0) return "null deployment query faulted";
        }
        return null;
    }

    private static string? UnitDeploymentSearchWrites(CardDatabase db)
    {
        foreach (var side in new[] { Side.Left, Side.Right })
        foreach (var mode in new[] { "success", "covert", "no_flag", "null_output" })
        {
            var (e, source, units) = ActionTargetSmoke.Fixture(db, side,
                mode == "null_output" ? "card_unit_superman" : "card_unit_mosquito");
            foreach (var unit in units) e.State.Move(unit, unit.Owner.HandOf(), unit.CardId);
            source.SelectTargetOnPlayedFromHand = mode != "no_flag";
            source.BlueprintVariables["targetOverride"] = units[0];
            var target = e.State.CreateWithId("card_unit_panzer_35_t", side.Opposite(), 85,
                mode == "null_output" ? side.Opposite().HandOf() : side.Opposite().HqOf(), 1);
            target.BlueprintVariables["targetOverride"] = units[1];
            if (mode == "covert") { target.Keywords.Add(Keyword.Covert); target.IsRevealed = false; }
            bool found = e.Api.HasUnitDeploymentTarget(source);
            if (found != (mode == "success")) return "deployment search result mismatch: " + mode;
            var expectedSource = mode == "no_flag" ? units[0] : mode == "null_output" ? e.State.ById(41) : target;
            if (!ReferenceEquals(source.BlueprintVariables["targetOverride"], expectedSource))
                return "deployment search source override mismatch: " + mode;
            var expectedCandidate = mode is "success" or "covert" ? null : units[1];
            if (!ReferenceEquals(target.BlueprintVariables["targetOverride"], expectedCandidate)
                || e.Api.Vm.FaultedPrograms != 0)
                return "deployment search candidate clear mismatch: " + mode;
        }
        return null;
    }

}
