using System.Diagnostics;
using System.Text.Json;
using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Server;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

// A bounded experiment with the existing enumerator, not a complete action mask.
internal static class PolicyMatchEvaluation
{
    internal sealed record Step(AtomicAction? Action); // null = one complete Greedy PlayStep, including probes
    internal sealed record Attempt(bool ModelLeft, ulong Seed, bool Admitted, string[] Reasons,
        string Winner, int Turn, int Steps, int Decisions, int Candidates, int Rejected,
        long Choices, double Seconds, string FinalFingerprint, string[] Trace);

    internal static int Best(IReadOnlyList<(float Score, bool Terminal, bool Wins)> scores)
    {
        if (scores.Count == 0) throw new InvalidDataException("No accepted candidates.");
        float Value(int i)
        {
            if (!float.IsFinite(scores[i].Score) || scores[i].Score is < 0 or > 1)
                throw new InvalidDataException("Invalid win-model score.");
            return scores[i].Terminal ? scores[i].Wins ? 1 : 0 : scores[i].Score;
        }
        int best = 0;
        float value = Value(0);
        for (int i = 1; i < scores.Count; i++)
        {
            float next = Value(i);
            // Known wins precede saturated estimates; known losses follow them.
            int Rank(int j) => scores[j].Terminal ? scores[j].Wins ? 2 : 0 : 1;
            if (next > value || next == value && Rank(i) > Rank(best)) { best = i; value = next; }
        }
        return best;
    }

    internal static string[] Health(MatchEngine e, ChoicePolicyAdapter choices)
        // Intermediate probes need not be terminal or contain a training sample.
        // Still reject an invalid winner if the probe has actually terminated.
        => TrainingAdmission.Check(true, e.State.IsFinished ? e.State.Winner : Side.Left, e.Api.Vm.FaultedPrograms,
            e.Api.Vm.UnsupportedOps, e.UnimplementedCalls,
            KLink.Bot.Effects.Blueprint.KismetLibrary.Default is not null, 1, true,
            choices.Faults, 0, e.PendingSelectionCount);

    internal static int Run(Dictionary<string, string> opts, CardDatabase db, StateEncoder.CardVecs vecs, string dataDir, string vectorDir)
    {
        string Required(string key) => opts.TryGetValue(key, out var v) ? v : throw new ArgumentException("Missing --" + key);
        var allowed = new[] { "model", "training-data", "out", "matchup", "seed", "max-steps", "max-seconds", "plan" };
        if (opts.Keys.Except(allowed).Any()) throw new ArgumentException("Unknown policy evaluation option.");
        string output = Path.GetFullPath(Required("out"));
        if (File.Exists(output)) throw new IOException("Refusing to overwrite evaluation output.");
        string plan = Required("plan"), modelPath = Required("model"), trainingPath = Required("training-data");
        // Caller freezes the plan before training; bind the exact bytes in every result.
        string planHash = TrainingAdmission.Hash(plan);
        var model = NnModel.Load(modelPath);
        var contract = ValueTrainingContract.Read(model);
        contract.RequireTarget("win"); contract.RequireSampling("policy-and-choice-before/v1"); contract.RequireRuntime();
        var quality = TrainingAdmission.VerifyDataset(trainingPath);
        if (quality.GateVersion != 2 || quality.DataSha256 != contract.DatasetSha256)
            throw new InvalidDataException("Original Gate2 training dataset required.");
        var inputs = ValueTrainingContract.SelectInputs(quality.InputsSha256);
        foreach (string key in inputs.Keys.ToArray())
            inputs[key] = TrainingAdmission.Hash(Path.Combine(key == "card-vectors.json" ? vectorDir : dataDir, key));
        contract.RequireDataset(quality.SamplingMode, quality.ObservationSpec, quality.ObservationPerspective,
            quality.ChoicePolicy, ValueTrainingContract.CurrentSimulatorHash(), inputs);
        ulong seed = ulong.Parse(Required("seed"));
        if (quality.Games.Any(g => g.Seed == seed)) throw new InvalidDataException("Evaluation seed overlaps training attempts.");
        var pair = DumpMatchup.Parse(Required("matchup"), MetaDecks.All.Count)
            ?? throw new ArgumentException("Ordered matchup required.");
        int maxSteps = int.Parse(opts.GetValueOrDefault("max-steps", "1000"));
        int maxSeconds = int.Parse(opts.GetValueOrDefault("max-seconds", "60"));
        if (maxSteps <= 0 || maxSeconds <= 0) throw new ArgumentException("Positive limits required.");
        List<string> Deck(int index)
        {
            var parsed = DeckCodeParser.Parse(MetaDecks.All[index].Code);
            var cards = DeckCodeParser.Expand(parsed, db.DeckCodeIds, out var unknown);
            if (unknown.Count != 0) throw new InvalidDataException("Unknown deck codes.");
            return cards;
        }
        var left = Deck(pair.Left); var right = Deck(pair.Right);
        var baseline = SafePlay(false); var treatment = SafePlay(true);
        bool comparable = baseline.Admitted && treatment.Admitted;
        var report = new
        {
            SchemaVersion = 1, PlanSha256 = planHash, ModelSha256 = TrainingAdmission.Hash(modelPath),
            TrainingDataSha256 = quality.DataSha256, SimulatorSha256 = ValueTrainingContract.CurrentSimulatorHash(), Inputs = inputs,
            ToolSha256 = TrainingAdmission.Hash(typeof(PolicyMatchEvaluation).Assembly.Location),
            Matchup = new[] { pair.Left, pair.Right }, Seed = seed, MaxSteps = maxSteps, MaxSeconds = maxSeconds,
            Scope = "Left-only win model; existing NnPolicy candidate enumeration; one-ply afterstates; first-index choices. No complete legal mask, calibrated probability, PvP certification or strength claim.",
            Baseline = baseline, Treatment = treatment, Comparable = comparable,
            LeftWinDelta = comparable ? (int?)((treatment.Winner == "Left" ? 1 : 0) - (baseline.Winner == "Left" ? 1 : 0)) : null
        };
        using (var stream = new FileStream(output, FileMode.CreateNew)) JsonSerializer.Serialize(stream, report, TrainingAdmission.Json);
        Console.WriteLine($"policy evaluation seed={seed}: comparable={comparable}, baseline={baseline.Winner}, model={treatment.Winner}");
        return 0; // Failed attempts are data in the report, never silently replaced.

        Attempt SafePlay(bool useModel)
        {
            try { return Play(useModel); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new(useModel, seed, false, ["setup_fault:" + ex.GetType().Name + ":" + ex.Message],
                    "NotAvailable", 0, 0, 0, 0, 0, 0, 0, "", []);
            }
        }

        Attempt Play(bool useModel)
        {
            var sw = Stopwatch.StartNew();
            var tape = new List<Step>(); var trace = new List<string>(); var reasons = new List<string>();
            int decisions = 0, candidates = 0, rejected = 0;
            var engine = new MatchEngine(db, left, right, seed);
            using var adapter = Attach(engine);
            var policy = new NnPolicy(model, vecs, Side.Left, true, false);
            ChoicePolicyAdapter Attach(MatchEngine e) => new(e, vecs, request => request.Options.Count == 0 && request.CanCancel ? -1 : 0);
            void Budget() { if (sw.Elapsed.TotalSeconds > maxSeconds) throw new InvalidOperationException("time_limit"); }
            void Check(MatchEngine e, ChoicePolicyAdapter a)
            {
                Budget();
                var bad = Health(e, a);
                if (bad.Length != 0) throw new InvalidOperationException(string.Join(";", bad));
            }
            (MatchEngine Engine, ChoicePolicyAdapter Adapter) Rebuild()
            {
                Budget();
                var e = new MatchEngine(db, left, right, seed);
                // This adapter remains attached for the candidate applied after replay.
                var a = Attach(e);
                try
                {
                    e.Start(); Check(e, a);
                    foreach (var step in tape) { Apply(e, step); Check(e, a); }
                    if (ActionJournal.Fingerprint(e) != ActionJournal.Fingerprint(engine))
                        throw new InvalidOperationException("replay_mismatch");
                    return (e, a);
                }
                catch { a.Dispose(); throw; }
            }
            try
            {
                engine.Start(); Check(engine, adapter);
                while (!engine.State.IsFinished && tape.Count < maxSteps)
                {
                    Budget();
                    Step step;
                    if (useModel && engine.State.ActiveSide == Side.Left)
                    {
                        decisions++;
                        string root = ActionJournal.Fingerprint(engine);
                        // CanPlay/target predicates can update engine state. Enumerate
                        // on a disposable reconstruction, then test each action from
                        // a fresh, unprobed reconstruction of the real prefix.
                        var enumeration = Rebuild();
                        using var enumerationAdapter = enumeration.Adapter;
                        var actions = policy.Enumerate(enumeration.Engine);
                        Check(enumeration.Engine, enumeration.Adapter);
                        if (ActionJournal.Fingerprint(engine) != root) throw new InvalidOperationException("enumerator_mutated_root");
                        var accepted = new List<AtomicAction>();
                        var scores = new List<(float Score, bool Terminal, bool Wins)>();
                        foreach (var action in actions)
                        {
                            candidates++;
                            var rebuilt = Rebuild();
                            using var trialAdapter = rebuilt.Adapter;
                            var trial = rebuilt.Engine;
                            bool ok = action.Apply(trial);
                            // Check faults even when the attempted action was rejected.
                            var bad = Health(trial, trialAdapter);
                            if (bad.Length != 0) throw new InvalidOperationException("candidate:" + string.Join(";", bad));
                            if (!ok) { rejected++; continue; }
                            accepted.Add(action);
                            scores.Add((policy.Evaluate(trial), trial.State.IsFinished, trial.State.Winner == Side.Left));
                        }
                        if (ActionJournal.Fingerprint(engine) != root) throw new InvalidOperationException("probe_mutated_root");
                        step = new(accepted[Best(scores)]);
                    }
                    else step = new(null);
                    trace.Add($"{engine.State.Turn}:{engine.State.ActiveSide}:{step.Action?.ToString() ?? "GreedyStep"}");
                    Apply(engine, step); tape.Add(step); Check(engine, adapter);
                    // Compare every executed prefix, including the terminal prefix.
                    using var replayAdapter = Rebuild().Adapter;
                }
                if (!engine.State.IsFinished) reasons.Add("step_limit");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { reasons.Add(ex.GetType().Name + ":" + ex.Message); }
            reasons.AddRange(TrainingAdmission.Check(engine.State.IsFinished, engine.State.Winner,
                engine.Api.Vm.FaultedPrograms, engine.Api.Vm.UnsupportedOps, engine.UnimplementedCalls, true,
                tape.Count, true, adapter.Faults, 0, engine.PendingSelectionCount));
            return new(useModel, seed, reasons.Count == 0, reasons.Distinct().ToArray(), engine.State.Winner.ToString(),
                engine.State.Turn, tape.Count, decisions, candidates, rejected, adapter.CompletedChoices,
                sw.Elapsed.TotalSeconds, ActionJournal.Fingerprint(engine), trace.ToArray());
        }
    }

    private static void Apply(MatchEngine e, Step step)
    {
        if (e.State.IsFinished || e.PendingSelectionCount != 0) throw new InvalidOperationException("Blocked step.");
        if (step.Action is not null)
        {
            if (!step.Action.Apply(e)) throw new InvalidOperationException("Recorded action rejected.");
        }
        else
        {
            var result = new GreedyBot().PlayStep(e, e.State.ActiveSide);
            if (result is GreedyStepResult.NotActive or GreedyStepResult.PendingSelection)
                throw new InvalidOperationException("Greedy step blocked.");
        }
    }

    internal static int SelfTest()
    {
        int checks = 0;
        void Check(bool ok) { checks++; if (!ok) throw new Exception("Policy evaluation selftest " + checks); }
        Check(Best([(0.9f, false, false), (0.1f, true, true)]) == 1);
        Check(Best([(0.9f, true, false), (0.1f, false, false)]) == 1);
        Check(Best([(1, false, false), (0, true, true)]) == 1);
        Check(Best([(0, true, false), (0, false, false)]) == 1);
        Check(Best([(0.5f, false, false), (0.5f, false, false)]) == 0);
        try { Best([(float.NaN, true, true)]); Check(false); } catch (InvalidDataException) { Check(true); }
        try { Best([(1.1f, false, false)]); Check(false); } catch (InvalidDataException) { Check(true); }
        try { Best([(-0.1f, false, false)]); Check(false); } catch (InvalidDataException) { Check(true); }
        try { Best([]); Check(false); } catch (InvalidDataException) { Check(true); }
        Console.WriteLine($"policy evaluation selftest: {checks} passed"); return 0;
    }
}
