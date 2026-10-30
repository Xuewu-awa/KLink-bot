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
}
