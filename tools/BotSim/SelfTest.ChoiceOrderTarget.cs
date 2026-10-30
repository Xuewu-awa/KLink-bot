using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static readonly string[] MixedTargetOrders =
        ["card_event_barrage", "card_event_hidden_plans", "card_event_refit_bia"];

    private static (MatchEngine Engine, CardInstance Source, CardInstance[] Units, CardInstance Good)
        ChoiceTargetFixture(CardDatabase db, Side side, string name, Side targetSide)
    {
        var (e, source, units) = ActionTargetSmoke.Fixture(db, side, name);
        var good = e.State.CreateWithId(name == "card_event_refit_bia" ? "card_unit_spitfire" : "card_unit_1st_infantry_regiment_us",
            targetSide, 85, targetSide.HqOf(), 3);
        good.Defense = good.MaxDefense = 5;
        if (name == "card_event_hidden_plans")
        { good.Keywords.Add(Keyword.Covert); good.IsRevealed = true; }
        return (e, source, units.Append(good).ToArray(), good);
    }

    private static string? ChoiceOrderRejectsWrongTargets(CardDatabase db)
    {
        foreach (var name in MixedTargetOrders)
        foreach (var side in new[] { Side.Left, Side.Right })
        foreach (bool oldFlag in new[] { false, true })
        foreach (var kind in new[] { "wrong_type", "zero", "missing", "hand", "hq" })
        {
            var (e, source, units, good) = ChoiceTargetFixture(db, side, name, side);
            source.SelectTargetOnPlayedFromHand = oldFlag;
            int id = kind switch { "zero" => 0, "missing" => 99999,
                "hand" => 80, "hq" => side == Side.Left ? 1 : 41, _ => 80 };
            if (kind == "hand") e.State.Move(units[0], side.Opposite().HandOf(), 0);
            source.BlueprintVariables["ChooseOne"] = 77;
            string before = TargetRootEffects(e, source, units);
            if (MainActionPolicyAdapter.ApplyRoot(e, side, new(MainActionKind.Play, 70, id, ChooseOne: 1)))
                return $"choice accepted invalid target: {name}/{side}/{oldFlag}/{kind}";
            if (before != TargetRootEffects(e, source, units))
                return "choice rejection changed resources/effects";
            // Missing IDs are rejected before assigning a branch or refreshing its flag.
            if (kind != "missing" && (!source.SelectTargetOnPlayedFromHand || source.ChooseOne != 1))
                return "selected targeted branch did not refresh flag";
            if (!Equals(source.BlueprintVariables["ChooseOne"], 77) || e.Api.Vm.FaultedPrograms != 0)
                return "derived choice field changed or target query faulted";
        }
        return null;
    }

    private static string? ChoiceOrderAcceptsPredicateTargets(CardDatabase db)
    {
        foreach (var name in MixedTargetOrders)
        foreach (var side in new[] { Side.Left, Side.Right })
        foreach (var targetSide in new[] { Side.Left, Side.Right })
        foreach (bool oldFlag in new[] { false, true })
        {
            var (e, source, units, good) = ChoiceTargetFixture(db, side, name, targetSide);
            source.SelectTargetOnPlayedFromHand = oldFlag;
            if (!MainActionPolicyAdapter.ApplyRoot(e, side, new(MainActionKind.Play, 70, good.CardId, ChooseOne: 1)))
                return $"choice rejected predicate target: {name}/{side}/{targetSide}/{oldFlag}";
            int expected = name == "card_event_barrage" ? 3 : name == "card_event_hidden_plans" ? 6 : 5;
            if (good.Defense != expected || (name == "card_event_refit_bia" && !good.HasBlitz)
                || !source.SelectTargetOnPlayedFromHand || e.Api.Vm.FaultedPrograms != 0)
                return $"choice target effect/flag mismatch: {name}, defense={good.Defense}";
        }
        return null;
    }

    private static string? ChoiceOrderNoTargetBranchClearsStaleFlag(CardDatabase db)
    {
        foreach (var name in MixedTargetOrders)
        foreach (var side in new[] { Side.Left, Side.Right })
        foreach (bool oldFlag in new[] { false, true })
        {
            var (e, source, units) = ActionTargetSmoke.Fixture(db, side, name);
            foreach (var unit in units) e.State.Move(unit, unit.Owner.HandOf(), unit.CardId);
            source.SelectTargetOnPlayedFromHand = oldFlag;
            source.ChooseOne = 1;
            source.BlueprintVariables["ChooseOne"] = 77;
            if (!MainActionPolicyAdapter.ApplyRoot(e, side, new(MainActionKind.Play, 70, ChooseOne: 0)))
                return $"no-target branch blocked by stale flag: {name}/{side}/{oldFlag}";
            if (source.SelectTargetOnPlayedFromHand || source.ChooseOne != 0
                || !Equals(source.BlueprintVariables["ChooseOne"], 77) || e.Api.Vm.FaultedPrograms != 0)
                return "no-target branch flag or independent field mismatch";
        }
        return null;
    }
}
