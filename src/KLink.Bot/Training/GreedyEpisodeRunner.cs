using KLink.Bot.Bots;
using KLink.Bot.Engine;
using KLink.Bot.NN;

namespace KLink.Bot.Training;

public enum EpisodeSampling { Turn, Decision }
public enum EpisodeSampleKind { TurnEnd, MainDecision, Choice }
public sealed record EpisodeSample(float[] Observation, int Turn, Side Actor, EpisodeSampleKind Kind);
public sealed record GreedyEpisodeResult(IReadOnlyList<EpisodeSample> Samples, long ChoiceFaults,
    long DriverFaults, int PendingChoices, long MainDecisions, long ChoiceDecisions, string? DriverError);

/// <summary>
/// Bounded baseline driver: 500 turn calls, at most 200 greedy steps each.
/// Samples always use Left perspective, including choices by Right. Decision
/// mode samples before policy probes/replies, not a certified legal-action mask.
/// </summary>
public static class GreedyEpisodeRunner
{
    public static GreedyEpisodeResult Run(MatchEngine engine, StateEncoder.CardVecs vectors,
        EpisodeSampling sampling, int every = 1, Func<PolicyChoiceRequest, int>? choose = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(vectors);
        if (sampling is not (EpisodeSampling.Turn or EpisodeSampling.Decision) || every <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampling));
        var samples = new List<EpisodeSample>();
        long main = 0, choices = 0, boundaries = 0, driverFaults = 0, choiceFaults = 0;
        string? driverError = null;
        ChoicePolicyAdapter? adapter = null;
        void Capture(Side actor, EpisodeSampleKind kind)
        {
            if (boundaries++ % every == 0)
                samples.Add(new(StateEncoder.Encode(engine.State, vectors, Side.Left), engine.State.Turn, actor, kind));
        }
        try
        {
            adapter = new ChoicePolicyAdapter(engine, vectors, request =>
            {
                choices++;
                if (sampling == EpisodeSampling.Decision) Capture(request.Actor, EpisodeSampleKind.Choice);
                return choose is null ? request.Options.Count == 0 && request.CanCancel ? -1 : 0 : choose(request);
            });
            engine.Start();
            var bot = new GreedyBot();
            for (int turnCall = 0; !engine.State.IsFinished && turnCall < 500; turnCall++)
            {
                Side side = engine.State.ActiveSide;
                if (sampling == EpisodeSampling.Turn) bot.PlayTurn(engine, side);
                else
                {
                    for (int action = 0; !engine.State.IsFinished && action < 200; action++)
                    {
                        if (engine.State.ActiveSide != side || engine.PendingSelectionCount != 0) break;
                        main++;
                        Capture(side, EpisodeSampleKind.MainDecision);
                        var result = bot.PlayStep(engine, side);
                        if (result != GreedyStepResult.ActionApplied || adapter.Faults != 0) break;
                    }
                }
                if (adapter.Faults != 0 || engine.PendingSelectionCount != 0) break;
                if (sampling == EpisodeSampling.Turn && engine.State.Turn % every == 0)
                    samples.Add(new(StateEncoder.Encode(engine.State, vectors, Side.Left), engine.State.Turn, side, EpisodeSampleKind.TurnEnd));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            driverFaults++;
            driverError = error.GetType().Name;
        }
        finally
        {
            choiceFaults = adapter?.Faults ?? 0;
            adapter?.Dispose();
        }
        return new(samples.AsReadOnly(), choiceFaults, driverFaults, engine.PendingSelectionCount, main, choices, driverError);
    }
}
