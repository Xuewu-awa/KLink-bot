using KLink.Bot.Engine;
using KLink.Bot.NN;
using System.Text.Json;

namespace KLink.Bot.Training;

public enum PolicyChoiceKind { HandTarget, OfferedCard }

/// <summary>A copied offer, never a mutable engine card. Index preserves duplicate templates.</summary>
public sealed record PolicyCardOption(int Index, int CardId, string Name, int Attack, int Defense, int KreditCost);

public sealed record PolicyChoiceRequest(long Sequence, PolicyChoiceKind Kind, Side Actor,
    int Turn, int SelectingCardId, string SelectingCardName, bool CanCancel, IReadOnlyList<PolicyCardOption> Options,
    IReadOnlyList<float> Observation)
{
    public int ContractVersion => 1;
    public string ObservationSpec => StateEncoder.Spec;
}

/// <summary>
/// Synchronous training boundary for existing engine choice hooks. The policy returns
/// an offered index, or -1 for the existing hand-target ID0 reply. No implicit first pick.
/// This does not enumerate main actions or suspend synchronous Forecast callbacks.
/// Hosts must reject episodes with Faults > 0, even if a VM catches the exception.
/// </summary>
public sealed class ChoicePolicyAdapter : IDisposable
{
    private readonly MatchEngine _engine;
    private readonly StateEncoder.CardVecs _vectors;
    private readonly Func<PolicyChoiceRequest, int> _choose;
    private readonly Func<CardInstance, IReadOnlyList<CardInstance>, CardInstance?> _offered;
    private readonly Func<CardInstance, IReadOnlyList<CardInstance>, int?> _hand;
    private bool _disposed, _deciding;

    public long CompletedChoices { get; private set; }
    public long Faults { get; private set; }

    public ChoicePolicyAdapter(MatchEngine engine, StateEncoder.CardVecs vectors,
        Func<PolicyChoiceRequest, int> choose)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(choose);
        if (engine.PendingSelectionCount != 0 || engine.ChooseSpawnCard is not null
            || engine.PickCardToDraw is not null || engine.PickHandTargetId is not null
            || engine.PickHandTarget is not null || engine.ChooseHandTarget is not null)
            throw new InvalidOperationException("Attach before choices begin, without existing choice handlers.");
        _engine = engine; _vectors = vectors; _choose = choose;
        _offered = (source, cards) => cards[Decide(source, cards, PolicyChoiceKind.OfferedCard)];
        _hand = (source, cards) =>
        {
            int index = Decide(source, cards, PolicyChoiceKind.HandTarget);
            return index == -1 ? 0 : cards[index].CardId;
        };
        engine.ChooseSpawnCard = _offered;
        engine.PickHandTargetId = _hand;
    }

    private int Decide(CardInstance source, IReadOnlyList<CardInstance> cards, PolicyChoiceKind kind)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_deciding) throw new InvalidOperationException("A choice policy cannot reenter this adapter.");
            _deciding = true;
            try
            {
                bool canCancel = kind == PolicyChoiceKind.HandTarget;
                if (source.Owner is not (Side.Left or Side.Right))
                    throw new InvalidOperationException("Choice actor must be a player side.");
                var offered = cards.ToArray();
                PolicyChoiceRequest Describe() => new(CompletedChoices + 1, kind, source.Owner,
                    _engine.State.Turn, source.CardId, source.Name, canCancel,
                    Array.AsReadOnly(offered.Select((c, i) =>
                        new PolicyCardOption(i, c.CardId, c.Name, c.Attack, c.Defense, c.KreditCost)).ToArray()),
                    Array.AsReadOnly(StateEncoder.Encode(_engine.State, _vectors, source.Owner)));
                var request = Describe();
                string boundary = DecisionBoundary(source, offered);
                string described = JsonSerializer.Serialize(request);
                var handlers = (_engine.ChooseSpawnCard, _engine.PickCardToDraw,
                    _engine.PickHandTargetId, _engine.PickHandTarget, _engine.ChooseHandTarget);
                int index = _choose(request);
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (cards.Count != offered.Length || cards.Where((c, i) => !ReferenceEquals(c, offered[i])).Any()
                    || handlers != (_engine.ChooseSpawnCard, _engine.PickCardToDraw,
                        _engine.PickHandTargetId, _engine.PickHandTarget, _engine.ChooseHandTarget)
                    || boundary != DecisionBoundary(source, offered)
                    || described != JsonSerializer.Serialize(Describe()))
                    throw new InvalidOperationException("Policy changed the engine or offer during a synchronous choice.");
                if (index < 0 ? index != -1 || !canCancel : index >= cards.Count)
                    throw new InvalidOperationException("Policy returned an index outside the current offer.");
                CompletedChoices++;
                return index;
            }
            finally { _deciding = false; }
        }
        catch { Faults++; throw; }
    }

    // Internal change detector, not complete isolation or a rollback mechanism.
    // Include detached Forecast/templates: they need not occur in AllCards.
    // Hidden battle snapshots are never exposed to the policy request.
    // VM private state, transient changes and concurrency remain outside scope.
    private string DecisionBoundary(CardInstance source, IReadOnlyList<CardInstance> offered)
    {
        var state = _engine.State;
        object Card(CardInstance c) => new
        {
            Snapshot = c.Snapshot(), c.ChooseOne, CurrentTargetId = c.CurrentTarget?.CardId
        };
        return JsonSerializer.Serialize(new
        {
            Snapshot = state.SnapshotJson(), state.Random.State,
            Trace = JsonSerializer.Serialize(state.ActionLog),
            state.StopFurtherActions, state.StopAttack, state.IsFinished, state.Winner, state.WinnerReason,
            state.ClientSide, _engine.PendingSelectionCount,
            Choices = state.AllCards.Select(c => new { c.CardId, c.ChooseOne, CurrentTargetId = c.CurrentTarget?.CardId }).ToArray(),
            Source = Card(source), Offered = offered.Select(Card).ToArray(),
            _engine.Api.Vm.ProgramsExecuted, _engine.Api.Vm.FaultedPrograms, Faults
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Never overwrite a handler installed by a later owner.
        if (_engine.ChooseSpawnCard == _offered) _engine.ChooseSpawnCard = null;
        if (_engine.PickHandTargetId == _hand) _engine.PickHandTargetId = null;
        _disposed = true;
    }
}
