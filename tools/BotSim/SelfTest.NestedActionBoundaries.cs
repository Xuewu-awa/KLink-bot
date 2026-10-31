using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    /// <summary>
    /// Regression coverage for the single current-window contract.
    ///
    /// This deliberately tests observable action-log behavior only.  It does
    /// not assert that a nested Blueprint callback is a new protocol root;
    /// that distinction remains an authority/open-path question.
    /// </summary>
    private static string? NestedActionBoundaryWindows(CardDatabase db)
    {
        var notify = typeof(MatchEngine).GetMethod("NotifyStopFurtherActions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (notify is null) return "NotifyStopFurtherActions reflection hook missing";

        var (engine, state) = EmptyBoard(db);
        const Side side = Side.Left;

        // A prebuilt root with subactions is a real root-log shape.  A later
        // RecordAction must move the boundary forward and leave that root
        // untouched when the current window is cancelled.
        var preset = new SubAction("ZActionPlayCardFromHand",
            new[] { ActionValue2.Int("cardID", 10) });
        engine.RecordAction("outer", side, new(), new[] { preset });
        var outer = state.ActionLog[^1];
        engine.RecordAction("inner", side, new());
        engine.FireSubAction("ZActionPlayCardFromHand",
            new[] { ActionValue2.Int("cardID", 20) });
        notify.Invoke(engine, null);

        if (outer.SubActions.Count != 1
            || !outer.SubActions[0].Values.SequenceEqual(preset.Values))
            return "current cancellation rewrote a previous preset root";

        var innerPlay = state.ActionLog[^1].SubActions.Single();
        if (innerPlay.Name != "ZActionPlayCardFromHand"
            || innerPlay.Values.LastOrDefault()?.Name != "actionCancelled"
            || innerPlay.Values.LastOrDefault()?.Value != 1)
            return "current root did not receive the cancellation marker";

        // A subaction emitted between roots belongs to neither later window;
        // this guards the half-open [start, ActionLog.Count) boundary.
        engine.RecordAction("outer2", side, new());
        engine.FireSubAction("ZActionPlayCardFromHand",
            new[] { ActionValue2.Int("cardID", 30) });
        var between = state.ActionLog[^1];
        engine.RecordAction("inner2", side, new());
        engine.FireSubAction("ZActionPlayCardFromHand",
            new[] { ActionValue2.Int("cardID", 40) });
        notify.Invoke(engine, null);
        if (!between.SubActions[0].Values.SequenceEqual(
                new[] { ActionValue2.Int("cardID", 30) }))
            return "subaction before the current root was rewritten";

        // EndTurnCore starts a fresh boundary before effects and then records
        // its own root.  Historical action records must remain immutable.
        engine.RecordAction("historical", side, new());
        engine.FireSubAction("ZActionPlayCardFromHand",
            new[] { ActionValue2.Int("cardID", 50) });
        var historical = state.ActionLog[^1];
        engine.EndTurn(side);
        if (!historical.SubActions[0].Values.SequenceEqual(
                new[] { ActionValue2.Int("cardID", 50) }))
            return "end-turn boundary rewrote historical action";

        // The queued autoplay path owns one root per card and clears the
        // source queue only after the whole loop.  Use ordinary units so this
        // remains independent of countermeasure reachability.
        var queuedA = state.CreateWithId("card_unit_panzer_35_t", Side.Right, 70,
            CardLocation.HandRight, 0);
        var queuedB = state.CreateWithId("card_unit_panzer_35_t", Side.Right, 71,
            CardLocation.HandRight, 1);
        state.SetKredits(Side.Right, 20);
        int rootsBefore = state.ActionLog.Count(a =>
            a.ActionType == "XActionPlayCardFromHand");
        state.AddAutoPlayCards(queuedA.CardId);
        state.AddAutoPlayCards(queuedB.CardId);
        engine.ExecuteQueuedAutoPlayActions();
        int rootsAfter = state.ActionLog.Count(a =>
            a.ActionType == "XActionPlayCardFromHand");
        if (state.GetAutoPlayCards().Length != 0)
            return "queued autoplay cards were not cleared after drain";
        if (rootsAfter - rootsBefore != 2)
            return $"queued autoplay roots mismatch: {rootsAfter - rootsBefore}/2";

        return engine.Api.Vm.FaultedPrograms == 0 ? null
            : $"nested boundary probe faulted {engine.Api.Vm.FaultedPrograms} VM programs";
    }
}
