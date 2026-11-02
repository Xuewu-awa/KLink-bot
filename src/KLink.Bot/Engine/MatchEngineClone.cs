using KLink.Bot.Cards;
using KLink.Bot.Effects;

namespace KLink.Bot.Engine;

public sealed partial class MatchEngine
{
    private int _operationDepth;

    private OperationScope EnterOperation() => new(this);

    private readonly struct OperationScope : IDisposable
    {
        private readonly MatchEngine _engine;
        internal OperationScope(MatchEngine engine) { _engine = engine; engine._operationDepth++; }
        public void Dispose() => _engine._operationDepth--;
    }

    // LegalTargets retains deferred predicate evaluation. Guard each predicate
    // call too, without changing when the query reads mutable target state.
    private bool EvaluateLegalAttackTarget(CardInstance attacker, CardInstance target)
    {
        using var operation = EnterOperation();
        return CanReachAcrossFrontline(attacker, target)
            && CanAttackByCardRestrictions(attacker, target, out _)
            && AttackTargetGate(attacker, target).Can;
    }

    /// <summary>
    /// Copy between completed calls on a single thread, with no direct API/state
    /// operation in progress. Catalog and IR must be frozen. External selectors,
    /// custom callbacks and pending resolutions are unsupported. This guard is
    /// not synchronization. Candidate evaluation does not yet use this method.
    /// </summary>
    public MatchEngine CloneAtIdle()
    {
        ValidateCloneBoundary();
        return new MatchEngine(this, new CardGraphClone(includeState: true, requireRegisteredDatabase: true));
    }

    private void ValidateCloneBoundary()
    {
        if (_operationDepth != 0 || _effectResolutionDepth != 0
            || _processingForcedEndTurn || _pendingForcedEndTurnSide is not null
            || _pendingForcedEndTurnInstigator != 0 || _pendingSelections is not null
            || _pendingLocalRuleReceipts.Count != 0 || _processingAutoPlayCards
            || _processingQueuedAutoPlayActions || State.TriggerQueue.Count != 0
            || State.GetAutoPlayCards().Length != 0 || State.GetWaitPlayFromHandCards().Length != 0)
            throw new NotSupportedException($"MatchEngine clone requires an idle resolution boundary "
                + $"(operation={_operationDepth}, effect={_effectResolutionDepth}, selections={_pendingSelections is not null}, "
                + $"receipts={_pendingLocalRuleReceipts.Count}, forced={_pendingForcedEndTurnSide}/{_pendingForcedEndTurnInstigator}, "
                + $"processing={_processingForcedEndTurn}/{_processingAutoPlayCards}/{_processingQueuedAutoPlayActions}, "
                + $"triggers={State.TriggerQueue.Count}, autoplay={State.GetAutoPlayCards().Length}, wait={State.GetWaitPlayFromHandCards().Length})");
        if (PickCardToDraw is not null || ChooseSpawnCard is not null || PickHandTarget is not null
            || PickHandTargetId is not null || ChooseHandTarget is not null)
            throw new NotSupportedException("MatchEngine clone cannot copy external selectors");
        if (State.CardMoved != FireLocationMoved || State.CardCreated != FireCardCreated
            || State.CardCreationModifiers != FireCardCreationModifiers)
            throw new NotSupportedException("MatchEngine clone requires owned state callbacks");
        if (!ReferenceEquals(_db, State.Database))
            throw new NotSupportedException("MatchEngine clone encountered a foreign state database");
        Api.ValidateCloneDatabase(_db);
        ValidateDeck(LeftDeckList);
        ValidateDeck(RightDeckList);
    }

    private static void ValidateDeck(IReadOnlyList<string> deck)
    {
        if (deck.GetType() != typeof(string[]) && deck.GetType() != typeof(List<string>))
            throw new NotSupportedException("MatchEngine clone supports string[] and List<string> decks");
    }

    private MatchEngine(MatchEngine source, CardGraphClone context)
    {
        context.RegisterStateCopy(source, this);
        _db = source._db.CloneView(context);
        State = context.Copy(source.State);
        LeftDeckList = context.Copy(source.LeftDeckList);
        RightDeckList = context.Copy(source.RightDeckList);
        Log = context.Copy(source.Log);
        _actionId = source._actionId;
        _currentActionLogStart = source._currentActionLogStart;
        OutOfRangeAttacksRejected = source.OutOfRangeAttacksRejected;
        EnforceGeneratedCardTrust = source.EnforceGeneratedCardTrust;
        BlockUntrustedOrders = source.BlockUntrustedOrders;
        LastHandTarget = source.LastHandTarget; // sealed records with immutable scalar/string data
        LastPick = source.LastPick;
        Api = new CardApi(this, source.Api, context);
        State.CardMoved = FireLocationMoved;
        State.CardCreated = FireCardCreated;
        State.CardCreationModifiers = FireCardCreationModifiers;
        // Pending queues/scopes are rejected above; selectors remain null.
    }
}
