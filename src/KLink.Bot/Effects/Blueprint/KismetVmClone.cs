using KLink.Bot.Engine;

namespace KLink.Bot.Effects.Blueprint;

public sealed partial class KismetVm
{
    // A Frame's locals and flow stack live on the managed call stack. They cannot
    // be resumed by copying persistent fields, including during nested calls.
    private int _executionDepth;

    internal bool IsExecuting => _executionDepth != 0;

    // Component-only operation for the future engine checkpoint. The caller must
    // supply the destination API and one memo shared with the destination state.
    // No public checkpoint or replay replacement is exposed by this constructor.
    internal KismetVm(CardApi destination, KismetVm source, CardGraphClone context)
    {
        if (source.IsExecuting)
            throw new NotSupportedException("KismetVm clone during program execution");
        _api = destination;
        context.RegisterStateCopy(source, this);
        Trace = context.Copy(source.Trace);
        StepTrace = context.Copy(source.StepTrace);
        ProgramsExecuted = source.ProgramsExecuted;
        StepsExecuted = source.StepsExecuted;
        UnsupportedInstructions = source.UnsupportedInstructions;
        FaultedPrograms = source.FaultedPrograms;
        UnresolvedJumps = source.UnresolvedJumps;
        FlowReturns = source.FlowReturns;
        StepLimitHits = source.StepLimitHits;
        UnimplementedCalls = context.Copy(source.UnimplementedCalls);
        UnsupportedOps = context.Copy(source.UnsupportedOps);
        CaptureMissingCallSites = source.CaptureMissingCallSites;
        _missingCallSites = context.Copy(source._missingCallSites);
        MissingCallSitesOmitted = source.MissingCallSitesOmitted;
        MissingCallSiteCaptureFailures = source.MissingCallSiteCaptureFailures;
    }
}
