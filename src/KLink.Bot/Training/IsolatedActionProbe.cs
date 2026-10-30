using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.NN;

namespace KLink.Bot.Training;

public enum SimulatorExecutability { Unknown, Accepted, Rejected }
public sealed record ConfirmedRoot(Side Actor, MainActionCommand Command);
public sealed record CandidateExecutionProbe(int Index, SimulatorExecutability Status, string Reason);
public sealed record ActionProbeLimits(int MaxCandidates = 128, int MaxPrefixRoots = 4096,
    long MaxTotalPrefixRoots = 20000);
public sealed record ActionProbeReport(IReadOnlyList<CandidateExecutionProbe> Candidates,
    IReadOnlyList<string> Errors, int EnginesCreated, long PrefixRootsApplied)
{
    public string Contract => IsolatedActionProbe.Contract;
    public bool AllDeterminate => Errors.Count == 0 && Candidates.Count > 0
        && Candidates.All(c => c.Status != SimulatorExecutability.Unknown);
}

/// <summary>
/// Diagnostic only. Each candidate gets a new seed/prefix engine; no live root is
/// executed and no successor state is returned. Accepted/Rejected describe the
/// current simulator's root return, not authoritative legality or a policy mask.
/// Snapshot/trace/request checks do not prove equivalence of all VM private state.
/// Shared static libraries and definition objects are not process-isolated.
/// </summary>
public static class IsolatedActionProbe
{
    public const string Contract = "simulator-executability/experimental-v1;fresh-engine-per-candidate;choice=first-index/v1;not-legal-mask";

    public static ActionProbeReport Run(CardDatabase database, StateEncoder.CardVecs vectors,
        IReadOnlyList<string> left, IReadOnlyList<string> right, ulong seed,
        IReadOnlyList<ConfirmedRoot> prefix, MatchEngine reference, ActionProbeLimits? limits = null)
        => RunCore(database, vectors, left, right, seed, prefix, reference, limits ?? new());

    internal static ActionProbeReport RunCore(CardDatabase database, StateEncoder.CardVecs vectors,
        IReadOnlyList<string> left, IReadOnlyList<string> right, ulong seed,
        IReadOnlyList<ConfirmedRoot> prefix, MatchEngine reference, ActionProbeLimits limits,
        Action<MatchEngine,int>? afterRebuild = null, Action<MatchEngine,int>? afterApply = null,
        bool reverse = false)
    {
        if (limits.MaxCandidates <= 0 || limits.MaxPrefixRoots < 0 || limits.MaxTotalPrefixRoots < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var results = new List<CandidateExecutionProbe>(); var errors = new List<string>();
        int engines = 0; long roots = 0;
        ActionProbeReport Report() => new(Array.AsReadOnly(results.OrderBy(c=>c.Index).ToArray()),
            Array.AsReadOnly(errors.Distinct(StringComparer.Ordinal).ToArray()), engines, roots);
        // Capture copies, never restore a serialized subset into the reference.
        static string Trace(MatchEngine e) => JsonSerializer.Serialize(e.State.ActionLog);
        static string Clock(MatchEngine e) => JsonSerializer.Serialize(new {
            e.State.Turn,e.State.ActiveSide,e.State.IsFinished,e.State.Winner,
            e.State.Random.State,e.State.Random.ConsumedCount,e.PendingSelectionCount });
        static string Diagnostics(MatchEngine e) => JsonSerializer.Serialize(new {
            e.Api.Vm.ProgramsExecuted,e.Api.Vm.FaultedPrograms,e.Api.Vm.UnsupportedOps,e.UnimplementedCalls });
        static bool Informational(string key) => key.StartsWith("<local-ran:",StringComparison.Ordinal) && key.EndsWith('>')
            || key is "<frontline-blocked-full>" or "<frontline-blocked-by-opponent>";
        static string? Health(MatchEngine e, ChoicePolicyAdapter? choice = null)
        {
            if (KismetLibrary.Default is null) return "ir_not_loaded";
            if (e.Api.Vm.FaultedPrograms != 0) return "vm_fault";
            if (choice?.Faults > 0) return "choice_fault";
            if (e.PendingSelectionCount != 0) return "pending_choice";
            if (e.Api.Vm.UnsupportedOps.Any(p=>p.Value>0)) return "unsupported_op";
            if (e.UnimplementedCalls.Any(p=>p.Value>0 && !Informational(p.Key))) return "unimplemented_call";
            return null;
        }
        try
        {
            var initialHealth = Health(reference);
            if (initialHealth is not null) { errors.Add("reference_"+initialHealth); return Report(); }
            var actor = reference.State.ActiveSide;
            var request = new MainActionPolicyAdapter(reference,vectors).DescribeCandidates(actor);
            if (!request.Observation.All(float.IsFinite) || request.Candidates.Any(c=>!c.Features.All(float.IsFinite)))
            { errors.Add("nonfinite_request"); return Report(); }
            string expectedRequest = JsonSerializer.Serialize(request), snapshot = reference.State.SnapshotJson(),
                trace = Trace(reference), clock = Clock(reference), diagnostics = Diagnostics(reference);
            if (prefix.Count > limits.MaxPrefixRoots || request.Candidates.Count > limits.MaxCandidates
                || (long)prefix.Count*request.Candidates.Count > limits.MaxTotalPrefixRoots)
            { errors.Add("probe_work_limit"); return Report(); }
            // Freeze caller-owned containers for the duration of this synchronous batch.
            var history = prefix.ToArray(); var leftDeck = left.ToArray(); var rightDeck = right.ToArray();
            var order = reverse ? request.Candidates.Reverse() : request.Candidates;
            foreach (var option in order)
            {
                var status = SimulatorExecutability.Unknown; string reason = "not_executed";
                try
                {
                    var probe = new MatchEngine(database,leftDeck,rightDeck,seed); engines++;
                    using var choices = new ChoicePolicyAdapter(probe,vectors,r=>r.Options.Count==0 && r.CanCancel ? -1 : 0);
                    probe.Start();
                    string? failure = Health(probe,choices);
                    foreach (var root in history)
                    {
                        if (failure is not null) break;
                        roots++;
                        if (!MainActionPolicyAdapter.ApplyRoot(probe,root.Actor,root.Command)) failure = "prefix_rejected";
                        failure ??= Health(probe,choices);
                    }
                    if (failure is null)
                    {
                        afterRebuild?.Invoke(probe,option.Index);
                        failure = Health(probe,choices);
                        if (failure is null && (clock!=Clock(probe) || snapshot!=probe.State.SnapshotJson()
                            || trace!=Trace(probe) || expectedRequest!=JsonSerializer.Serialize(
                                new MainActionPolicyAdapter(probe,vectors).DescribeCandidates(actor))))
                            failure = "prefix_mismatch";
                    }
                    if (failure is not null) reason = failure;
                    else
                    {
                        long completed = choices.CompletedChoices;
                        bool accepted = MainActionPolicyAdapter.ApplyRoot(probe,actor,option.Command);
                        afterApply?.Invoke(probe,option.Index);
                        failure = Health(probe,choices);
                        // A root that required a nested policy answer is not a plain
                        // binary legality fact, even when first-index resolved it.
                        if (failure is not null) reason = failure;
                        else if (choices.CompletedChoices != completed) reason = "choice_policy_dependent";
                        else { status = accepted ? SimulatorExecutability.Accepted : SimulatorExecutability.Rejected;
                            reason = accepted ? "simulator_returned_true" : "simulator_returned_false"; }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { reason = "probe_exception:"+ex.GetType().Name; }
                results.Add(new(option.Index,status,reason));
            }
            // Detect observable cross-instance contamination; do not silently use
            // earlier labels if the reference changed anywhere during the batch.
            if (clock!=Clock(reference) || snapshot!=reference.State.SnapshotJson() || trace!=Trace(reference)
                || expectedRequest!=JsonSerializer.Serialize(new MainActionPolicyAdapter(reference,vectors).DescribeCandidates(actor))
                || diagnostics!=Diagnostics(reference) || Health(reference) is not null)
            {
                errors.Add("reference_changed");
                results = results.Select(c=>c with {Status=SimulatorExecutability.Unknown,Reason="reference_changed"}).ToList();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errors.Add("batch_exception:"+ex.GetType().Name);
            results = results.Select(c=>c with {Status=SimulatorExecutability.Unknown,Reason="batch_exception"}).ToList();
        }
        return Report();
    }
}
