namespace KLink.Bot.Engine;

/// <summary>
/// Detached state graph only, not a usable MatchEngine checkpoint. The caller must
/// establish quiescence, freeze the shared catalog and bind destination callbacks.
/// Unknown payloads fail explicitly. No source delegates are retained.
/// </summary>
public sealed class GameStateClone
{
    public GameState State { get; }
    public IReadOnlyDictionary<CardInstance, CardInstance> Cards { get; }

    private GameStateClone(GameState source)
    {
        var context = new CardGraphClone(includeState: true);
        State = context.Copy(source);
        Cards = context.Cards;
    }

    public static GameStateClone Create(GameState source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(source);
    }
}

public sealed partial class GameState
{
    internal GameState(GameState source, CardGraphClone context)
    {
        // This transient scope has a finally callback on the source stack. It
        // cannot be continued by a detached state, even if the dictionary copies.
        if (source._pendingDeckRemovals.Count != 0)
            throw new NotSupportedException("GameState clone during scoped deck removal");
        context.RegisterStateCopy(source, this);

        Database = source.Database; // frozen catalog contract
        CardReservations = source.CardReservations; // immutable policy objects
        CardBlacklist = source.CardBlacklist;
        Seed = source.Seed;
        Random = context.Copy(source.Random);

        ShouldExecuteWaitPlayFromHand = source.ShouldExecuteWaitPlayFromHand;
        Turn = source.Turn;
        ActiveSide = source.ActiveSide;
        StartingSide = source.StartingSide;
        ClientSide = source.ClientSide;
        UnitDestroyedThisTurn = source.UnitDestroyedThisTurn;
        OperationKreditsSpentThisTurn = source.OperationKreditsSpentThisTurn;
        StopAttack = source.StopAttack;
        HasPlayedWeatherCardThisTurn = source.HasPlayedWeatherCardThisTurn;
        FrontlineOwner = source.FrontlineOwner;
        StopFurtherActions = source.StopFurtherActions;
        IsFinished = source.IsFinished;
        Winner = source.Winner;
        WinnerReason = source.WinnerReason;
        _cardsCreatedThisTurn = source._cardsCreatedThisTurn;
        _createdCountTurn = source._createdCountTurn;
        ClientIdTurnOverride = source.ClientIdTurnOverride;
        CollectRandomTrace = source.CollectRandomTrace;
        TrackGeneratedCardTrust = source.TrackGeneratedCardTrust;
        LeftAllyFaction = source.LeftAllyFaction;
        RightAllyFaction = source.RightAllyFaction;
        LeftMainFaction = source.LeftMainFaction;
        RightMainFaction = source.RightMainFaction;

        // Use one memo for all indexes, historical cards, detached HQs, queues
        // and VM payloads. Rebuilding indexes from ById would lose other roots.
        _cardsBySide = context.Copy(source._cardsBySide);
        _pendingDeckRemovals = context.Copy(source._pendingDeckRemovals);
        _byCardId = context.Copy(source._byCardId);
        _battleCardOrder = context.Copy(source._battleCardOrder);
        _autoPlayerCardIds = context.Copy(source._autoPlayerCardIds);
        _waitPlayFromHandCards = context.Copy(source._waitPlayFromHandCards);
        _buffsToRemoveEndOfTurn = context.Copy(source._buffsToRemoveEndOfTurn);
        _kreditSlotsLost = context.Copy(source._kreditSlotsLost);
        GameplayRestrictions = context.Copy(source.GameplayRestrictions);
        GameplaySideEffects = context.Copy(source.GameplaySideEffects);
        TriggerQueue = context.Copy(source.TriggerQueue);
        DestroyedCardIdsByTurn = context.Copy(source.DestroyedCardIdsByTurn);
        DestroyedCardIdsAll = context.Copy(source.DestroyedCardIdsAll);
        _destroyedBySide = context.Copy(source._destroyedBySide);
        _kredits = context.Copy(source._kredits);
        _maxKredits = context.Copy(source._maxKredits);
        _hqDamagedThisTurn = context.Copy(source._hqDamagedThisTurn);
        _fatigue = context.Copy(source._fatigue);
        ActiveBondFactions = context.Copy(source.ActiveBondFactions);
        FrontlineLimiters = context.Copy(source.FrontlineLimiters);
        _nextCardId = context.Copy(source._nextCardId);
        ActionLog = context.Copy(source.ActionLog);
        CardsPlayedThisTurn = context.Copy(source.CardsPlayedThisTurn);
        CardsPlayedFromHandByTurn = context.Copy(source.CardsPlayedFromHandByTurn);
        UnimplementedCalls = context.Copy(source.UnimplementedCalls);
        RandomTrace = context.Copy(source.RandomTrace);
        GeneratedCardIds = context.Copy(source.GeneratedCardIds);
        VerifiedGeneratedCardIds = context.Copy(source.VerifiedGeneratedCardIds);
        _locationCards = context.Copy(source._locationCards);
        _locationCardIds = context.Copy(source._locationCardIds);

        // Rebuilt by a future destination engine; never copy source captures.
        CardMoved = null;
        CardCreated = null;
        CardCreationModifiers = null;
    }
}

public sealed partial class CardGraphClone
{
    internal void RegisterStateCopy(object source, object copy) => _copies.Add(source, copy);

    // These payloads are admitted only for the state graph. The public card-only
    // copier keeps its existing whitelist. All containers use the same memo.
    private bool TryCopyStateValue(object value, out object? copy)
    {
        copy = value switch
        {
            GameState state => new GameState(state, this),
            UeRandomStream random => CopyRandom(random),
            GameState.GameplayRestriction restriction => CopyRestriction(restriction),
            GameState.GameplaySideEffect effect => CopySideEffect(effect),
            GameAction action => new GameAction(action, this),
            SubAction action => new SubAction(action, this),
            ActionValue2 scalar => scalar, // sealed immutable strings/ints
            List<CardInstance>[] array => CopyArray(array),
            List<int>[] array => CopyArray(array),
            List<GameState.GameplayRestriction> list => CopyList(list),
            List<GameState.GameplaySideEffect> list => CopyList(list),
            List<GameAction> list => CopyList(list),
            List<SubAction> list => CopyList(list),
            List<ActionValue2> list => CopyList(list),
            SubAction[] array => CopyArray(array),
            ActionValue2[] array => CopyArray(array),
            HashSet<int> set => CopySet(set),
            Dictionary<int, CardInstance> map => CopyMap(map),
            Dictionary<int, List<int>> map => CopyMap(map),
            Dictionary<int, List<CardInstance>> map => CopyMap(map),
            Dictionary<string, int> map => CopyMap(map),
            Dictionary<(Side, int), int> map => CopyMap(map),
            ValueTuple<Side, int> key => key,
            _ => null,
        };
        return copy is not null;
    }

    private UeRandomStream CopyRandom(UeRandomStream source)
    {
        var copy = source.Clone();
        _copies.Add(source, copy);
        return copy;
    }

    private GameState.GameplayRestriction CopyRestriction(GameState.GameplayRestriction source)
    {
        var copy = new GameState.GameplayRestriction { Side = source.Side, Type = source.Type,
            SourceCardId = source.SourceCardId, TurnsRemaining = source.TurnsRemaining };
        _copies.Add(source, copy);
        return copy;
    }

    private GameState.GameplaySideEffect CopySideEffect(GameState.GameplaySideEffect source)
    {
        var copy = new GameState.GameplaySideEffect { Side = source.Side, Tag = source.Tag,
            SourceCardId = source.SourceCardId, DurationPolicy = source.DurationPolicy,
            TurnsRemaining = source.TurnsRemaining, EffectValue = source.EffectValue };
        _copies.Add(source, copy);
        return copy;
    }
}

public sealed partial record GameAction
{
    internal GameAction(GameAction source, CardGraphClone context)
        : this(source.ActionId, source.ActionType, source.Player, source.TurnNumber, null!, null!)
    {
        context.RegisterStateCopy(source, this);
        SubActions = context.Copy(source.SubActions);
        ActionData = context.Copy(source.ActionData);
    }
}

public sealed partial record SubAction
{
    internal SubAction(SubAction source, CardGraphClone context) : this(source.Name, null!)
    {
        context.RegisterStateCopy(source, this);
        Values = context.Copy(source.Values);
    }
}
