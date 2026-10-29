using KLink.Bot.Engine;
using KLink.Bot.NN;

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
                var options = Array.AsReadOnly(cards.Select((c, i) =>
                    new PolicyCardOption(i, c.CardId, c.Name, c.Attack, c.Defense, c.KreditCost)).ToArray());
                var observation = Array.AsReadOnly(StateEncoder.Encode(_engine.State, _vectors, source.Owner));
                var request = new PolicyChoiceRequest(CompletedChoices + 1, kind, source.Owner,
                    _engine.State.Turn, source.CardId, source.Name, canCancel, options, observation);
                int index = _choose(request);
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (index < 0 ? index != -1 || !canCancel : index >= cards.Count)
                    throw new InvalidOperationException("Policy returned an index outside the current offer.");
                CompletedChoices++;
                return index;
            }
            finally { _deciding = false; }
        }
        catch { Faults++; throw; }
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
