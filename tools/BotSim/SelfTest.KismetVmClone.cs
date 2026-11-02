using System.Reflection;
using KLink.Bot.Cards;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static CardGraphClone VmCloneContext() => (CardGraphClone)Activator.CreateInstance(
        typeof(CardGraphClone), BindingFlags.Instance | BindingFlags.NonPublic, null, [true], null)!;

    private static T CopyVmGraph<T>(CardGraphClone memo, T value) => (T)typeof(CardGraphClone)
        .GetMethod("Copy", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(T))
        .Invoke(memo, [value])!;

    private static KismetVm CopyVm(KismetVm source, CardApi destination, CardGraphClone memo)
    {
        try
        {
            return (KismetVm)Activator.CreateInstance(typeof(KismetVm), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [destination, source, memo], null)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException)
        { throw ex.InnerException; }
    }

    private static string? VmCloneFieldsAndAliases(CardDatabase db)
    {
        string[] properties = ["Trace", "StepTrace", "ProgramsExecuted", "StepsExecuted", "UnsupportedInstructions",
            "FaultedPrograms", "UnimplementedCalls", "UnsupportedOps", "UnresolvedJumps", "FlowReturns", "StepLimitHits",
            "CaptureMissingCallSites", "MissingCallSitesOmitted", "MissingCallSiteCaptureFailures"];
        string[] fields = ["_api", "_executionDepth", "_missingCallSites"];
        if (!properties.Select(n => $"<{n}>k__BackingField").Concat(fields).Order()
            .SequenceEqual(CloneFields(typeof(KismetVm)).Select(f => f.Name).Order()))
            return "VM field inventory changed; review every new field";
        var (sourceEngine, state) = EmptyBoard(db);
        var (destination, _) = EmptyBoard(db);
        var source = sourceEngine.Api.Vm;
        source.Trace = source.StepTrace = state.RandomTrace;
        source.Trace.Add("retained");
        int value = 10;
        foreach (var field in CloneFields(typeof(KismetVm)))
        {
            if (field.FieldType == typeof(int) && field.Name != "_executionDepth") field.SetValue(source, ++value);
            if (field.FieldType == typeof(long)) field.SetValue(source, (long)++value);
        }
        source.CaptureMissingCallSites = false;
        source.UnimplementedCalls.Add("retained", 7);
        source.UnimplementedCalls.Add("hole", 8); source.UnimplementedCalls.Remove("hole");
        source.UnsupportedOps.Add("retained-op", 9);
        var sites = (Dictionary<KismetMissingCallSite, int>)CloneFields(typeof(KismetVm))
            .Single(f => f.Name == "_missingCallSites").GetValue(source)!;
        var site = new KismetMissingCallSite("missing", "card", "asset", "fn", 4, 0, 7, 9, "$", "hash");
        sites.Add(site, 12);
        var memo = VmCloneContext();
        var stateCopy = CopyVmGraph(memo, state);
        var copy = CopyVm(source, destination.Api, memo);
        foreach (var field in CloneFields(typeof(KismetVm)))
            if (field.FieldType.IsValueType && !Equals(field.GetValue(source), field.GetValue(copy)))
                return "VM raw scalar differs: " + field.Name;
        if (!ReferenceEquals(copy.Trace, copy.StepTrace) || !ReferenceEquals(copy.Trace, stateCopy.RandomTrace)
            || ReferenceEquals(copy.Trace, source.Trace) || !copy.MissingCallSites.SequenceEqual(source.MissingCallSites)
            || !copy.UnimplementedCalls.SequenceEqual(source.UnimplementedCalls)
            || !copy.UnsupportedOps.SequenceEqual(source.UnsupportedOps)) return "VM graph alias or diagnostics lost";
        copy.Trace!.Add("copy-only"); copy.UnimplementedCalls["retained"]++; copy.UnsupportedOps.Clear();
        var copySites = (Dictionary<KismetMissingCallSite, int>)copy.MissingCallSites;
        copySites[site]++;
        if (source.Trace.Count != 1 || source.UnimplementedCalls["retained"] != 7
            || source.UnsupportedOps.Count != 1 || sites[site] != 12) return "copy mutated source diagnostics";
        source.Trace.Clear(); source.UnimplementedCalls.Clear(); sites.Clear();
        if (copy.Trace.Count != 2 || copy.UnimplementedCalls.Count != 1 || copySites[site] != 13)
            return "source mutated copied diagnostics";
        var again = CopyVm(copy, destination.Api, VmCloneContext());
        if (ReferenceEquals(again.Trace, copy.Trace) || !ReferenceEquals(again.Trace, again.StepTrace)
            || !again.MissingCallSites.SequenceEqual(copy.MissingCallSites)) return "repeated clone lost isolation";
        return null;
    }

    private static string? VmCloneContinuation(CardDatabase db)
    {
        var (sourceEngine, sourceState) = EmptyBoard(db);
        var (destination, destinationState) = EmptyBoard(db);
        var source = sourceEngine.Api.Vm;
        var program = new KismetProgram([
            ProvenanceStep(0, "call", "MissingVmCloneCall"),
            ProvenanceStep(1, "math", "MissingVmCloneMath"),
            ProvenanceStep(2, "return")], 0) { AssetName = "vm-clone-test" };
        EffectContext Context(MatchEngine engine) => new() { Engine = engine, State = engine.State, Controller = Side.Left };
        source.Run(program, Context(sourceEngine));
        source.Trace = source.StepTrace = new List<string> { "before-clone" };
        var copy = CopyVm(source, destination.Api, VmCloneContext());
        var initialSourceState = sourceState.SnapshotJson();
        int initialSourcePrograms = source.ProgramsExecuted;
        copy.Run(program, Context(destination));
        if (sourceState.SnapshotJson() != initialSourceState || source.ProgramsExecuted != initialSourcePrograms
            || source.Trace.Count != 1 || destinationState.UnimplementedCalls.GetValueOrDefault("MissingVmCloneCall") != 1
            || destinationState.UnimplementedCalls.GetValueOrDefault("math:MissingVmCloneMath") != 1)
            return "copied VM API still targets source state";
        source.Run(program, Context(sourceEngine));
        if (source.ProgramsExecuted != copy.ProgramsExecuted || source.StepsExecuted != copy.StepsExecuted
            || !source.Trace.SequenceEqual(copy.Trace!) || !source.UnimplementedCalls.SequenceEqual(copy.UnimplementedCalls)
            || !source.UnsupportedOps.SequenceEqual(copy.UnsupportedOps)
            || !source.MissingCallSites.SequenceEqual(copy.MissingCallSites)) return "VM diagnostic continuation diverged";
        // Missing entry returns before the normal interpreter try/catch.
        copy.Run(program with { Entry = 999 }, Context(destination));
        CopyVm(copy, destination.Api, VmCloneContext());
        return null;
    }

    private static string? VmCloneExecutionBoundary(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var (destination, _) = EmptyBoard(db);
        var vm = engine.Api.Vm;
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var depthField = CloneFields(typeof(KismetVm)).Single(f => f.Name == "_executionDepth");
        int Depth() => (int)depthField.GetValue(vm)!;
        var depths = new List<int>(); int rejected = 0;
        void TryCopy()
        {
            depths.Add(Depth());
            try { CopyVm(vm, destination.Api, VmCloneContext()); }
            catch (NotSupportedException) { rejected++; }
        }
        var dispatch = (Dictionary<string, Func<EffectContext, object?, object?[], object?>>)typeof(CardApi)
            .GetField("_dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine.Api)!;
        var nested = new KismetProgram([ProvenanceStep(0, "call", "VmCloneInner")], 0);
        dispatch["VmCloneInner"] = (_, _, _) => { TryCopy(); return null; };
        dispatch["VmCloneOuter"] = (_, _, _) =>
        {
            TryCopy(); vm.RunLocalProgramMulti(nested, ctx, null, "result"); TryCopy(); return null;
        };
        vm.RunLocalProgram(new KismetProgram([ProvenanceStep(0, "call", "VmCloneOuter")], 0), ctx, null, "result");
        if (rejected != 3 || !depths.SequenceEqual(new[] { 1, 2, 1 }) || Depth() != 0 || vm.FaultedPrograms != 0)
            return "nested execution was copyable or depth did not unwind";
        vm.Run(new KismetProgram([new KismetStep(0, "call", "VmCloneFault", null!, [],
            null, null, -1, null, null)], 0), ctx);
        if (Depth() != 0 || vm.FaultedPrograms != 1) return "faulted execution left an active frame";
        // A failure before RunFrame's interpreter catch must unwind as well.
        try { vm.Run(new KismetProgram(null!, 0), ctx); return "invalid program did not fail"; }
        catch (NullReferenceException) { }
        if (Depth() != 0) return "pre-interpreter failure left an active frame";
        CopyVm(vm, destination.Api, VmCloneContext());
        return null;
    }
}
