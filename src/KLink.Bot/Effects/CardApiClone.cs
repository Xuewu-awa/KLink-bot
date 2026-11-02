using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

public sealed partial class CardApi
{
    internal void ValidateCloneDatabase(Cards.CardDatabase database)
    {
        if (_staticPoolDb is not null && !ReferenceEquals(_staticPoolDb, database))
            throw new NotSupportedException("CardApi clone encountered a foreign pool database");
    }

    // Component only: caller must establish whole-engine quiescence, copy state
    // with this same memo and bind destination callbacks. No source closures.
    internal CardApi(MatchEngine destination, CardApi source, CardGraphClone context)
    {
        if (source._playedCardBroadcastDepth != 0 || source._resolvingTriggerQueue
            || source._triggerDepth != 0 || source.Vm.IsExecuting)
            throw new NotSupportedException("CardApi clone during effect execution");

        _engine = destination;
        context.RegisterStateCopy(source, this);
        _dispatch = BuildDispatch();
        _playedCardBroadcastDone = context.Copy(source._playedCardBroadcastDone);
        _triggerBuffers = context.Copy(source._triggerBuffers);
        TriggerTrace = context.Copy(source.TriggerTrace);
        AuraTrace = context.Copy(source.AuraTrace);
        _staticPoolDb = context.Copy(source._staticPoolDb);
        _staticPool = context.Copy(source._staticPool);
        _staticPoolReservationPolicy = source._staticPoolReservationPolicy;
        _staticPoolBlacklistPolicy = source._staticPoolBlacklistPolicy;
        _staticPoolVariants = context.Copy(source._staticPoolVariants);
        _staticPoolGeneration = source._staticPoolGeneration;
        _gcsPurityCache = context.Copy(source._gcsPurityCache);
        _gcsCache = context.Copy(source._gcsCache);
        Vm = new Blueprint.KismetVm(this, source.Vm, context);
        // The three active-scope fields remain zero/false after the gate above.
    }
}
