using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static readonly string[] ChoiceDeploymentUnits =
        ["card_unit_1er_regiment_etranger", "card_unit_51e_regiment",
         "card_unit_120mm_m1_gun", "card_unit_5th_rangers"];

    private static string? ChoiceUnitDeploymentBranches(CardDatabase db)
    {
        // These four exported assets have two non-targeted options each.
        // This is a bounded deployment check, not a claim about every card effect.
        foreach (string name in ChoiceDeploymentUnits)
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int branch in new[] { 0, 1 })
        foreach (bool occupied in new[] { false, true })
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = side; state.SetKredits(side, 12);
            var source = state.CreateWithId(name, side, 70, side.HandOf(), 0);
            var draw = state.CreateWithId("card_unit_arado_ar_196", side, 71, side.DeckOf(), 0);
            CardInstance? unrelated = occupied
                ? state.CreateWithId("card_unit_panzer_35_t", side.Opposite(), 80, side.Opposite().HqOf(), 1) : null;
            int unrelatedDefense = unrelated?.Defense ?? 0;
            int attack = source.Attack, defense = source.Defense, operation = source.OperationCost;
            if (source.SelectTargetOnPlayedFromHand || source.Definition.ChooseOneOptionCount != 2
                || source.Definition.ChooseOneSelectTargets.Count != 2
                || source.Definition.ChooseOneSelectTargets.Any(x => x))
                return "choice unit metadata changed; re-audit target preparation: " + name;
            source.ChooseOne = 1 - branch;
            source.BlueprintVariables["ChooseOne"] = 77;
            if (!MainActionPolicyAdapter.ApplyRoot(engine, side, new(MainActionKind.Play, 70, ChooseOne: branch)))
                return $"non-targeted choice deployment rejected: {name}/{side}/{branch}/{occupied}";
            if (source.Location != side.HqOf() || source.CurrentTarget is not null
                || source.ChooseOne != branch || !Equals(source.BlueprintVariables["ChooseOne"], 77)
                || source.SelectTargetOnPlayedFromHand || engine.Api.Vm.FaultedPrograms != 0
                || (unrelated?.Defense ?? 0) != unrelatedDefense)
                return $"choice unit commit/target/member mismatch: {name}/{side}/{branch}/{occupied}";

            bool correct = name switch
            {
                "card_unit_1er_regiment_etranger" => source.Attack == attack && source.Defense == defense
                    && source.HasBlitz == (branch == 0) && source.HasGuard == (branch == 1)
                    && draw.Location == (branch == 0 ? side.HandOf() : side.DeckOf())
                    && state.Hq(side).Defense == 20 + (branch == 1 ? 4 : 0),
                "card_unit_51e_regiment" => source.Attack == attack + (branch == 0 ? 3 : 0)
                    && source.Defense == defense + (branch == 1 ? 3 : 0)
                    && source.HasBlitz == (branch == 0) && source.HasGuard == (branch == 1),
                "card_unit_5th_rangers" => source.Attack == attack + (branch == 1 ? 4 : 0)
                    && source.Defense == defense + (branch == 1 ? 4 : 0)
                    && source.OperationCost == (branch == 0 ? 0 : operation),
                "card_unit_120mm_m1_gun" => source.CustomAbility == "custom"
                    && source.Attack == attack && source.Defense == defense,
                _ => false
            };
            if (!correct) return $"choice deployment effect mismatch: {name}/{side}/{branch}/{occupied}";
            if (name == "card_unit_120mm_m1_gun")
            {
                var air = state.CreateWithId("card_unit_arado_ar_196", side.Opposite(), 81, side.Opposite().HqOf(), 2);
                var tank = state.CreateWithId("card_unit_panzer_35_t", side.Opposite(), 82, side.Opposite().HqOf(), 3);
                if (engine.Api.ExecuteOnDealDamageAddDamage(source, air, 3, false, false, false) != (branch == 0 ? 6 : 3)
                    || engine.Api.ExecuteOnDealDamageAddDamage(source, tank, 3, false, false, false) != (branch == 1 ? 6 : 3))
                    return "deployed M1 gun lost selected native damage branch";
            }
        }
        return null;
    }

    private static string? ChoiceUnitDeploymentInvalidOptions(CardDatabase db)
    {
        foreach (string name in ChoiceDeploymentUnits)
        foreach (Side side in new[] { Side.Left, Side.Right })
        foreach (int branch in new[] { -2, -1, 2, int.MaxValue })
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = side; state.SetKredits(side, 12);
            var source = state.CreateWithId(name, side, 70, side.HandOf(), 0);
            source.ChooseOne = 1;
            string before = state.SnapshotJson();
            if (MainActionPolicyAdapter.ApplyRoot(engine, side, new(MainActionKind.Play, 70, ChooseOne: branch))
                || state.SnapshotJson() != before || source.ChooseOne != 1)
                return $"invalid choice unit option mutated root: {name}/{side}/{branch}";
        }
        return null;
    }
}
