using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;
internal static class ClonePlanningAdmissionSelfTest
{
    internal static int Run(CardDatabase db,StateEncoder.CardVecs vectors)
    {
        int checks=0;
        void Assert(bool value,string name){if(!value)throw new InvalidOperationException(name);checks++;}
        var deck=Enumerable.Repeat("card_unit_120mm_m1_gun",39).ToArray();
        var reference=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,1);
        var clone=OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,deck,deck,1);
        string[] Reasons(OracleActionEpisodeResult c,OracleActionEpisodeResult r=null,bool ir=true)=>ClonePlanningAdmission.Check(c,r??reference,ir);
        void Reject(OracleActionEpisodeResult c,string reason)=>Assert(Reasons(c).Contains(reason),reason);
        Assert(Reasons(clone).Length==0,"healthy paired clone accepted by pilot only");
        Assert(ActionDatasetTool.AdmissionReasons(clone,true).Contains("experimental_planning_not_admitted"),"default gate remains closed");
        using var writer=new StringWriter();
        Assert(ActionDatasetTool.WriteAdmittedLabels(writer,0,clone,true)==0 && writer.ToString()=="","pilot approval cannot emit default rows");
        Reject(clone with {Labels=Array.Empty<OracleActionLabel>()},"clone_work_count_mismatch");
        Assert(Reasons(clone,ir:false).Contains("clone:ir_not_loaded"),"IR still required");
        Reject(reference,"clone_mode_mismatch");
        Reject(clone with {Identity=null},"episode_identity_mismatch");
        Reject(clone with {Identity=clone.Identity with {Seed=2}},"episode_identity_mismatch");
        Reject(clone with {Identity=clone.Identity with {OrderedDecksSha256=new string('0',64)}},"episode_identity_mismatch");
        Reject(clone with {Identity=clone.Identity with {Teacher=OracleActionEpisodeRunner.LastChoiceTeacherContract}},"episode_identity_mismatch");
        Reject(clone with {PlanningClones=clone.PlanningClones-1},"clone_work_count_mismatch");
        Reject(clone with {PlanningBaselineRoots=clone.PlanningBaselineRoots-1},"clone_work_count_mismatch");
        Reject(clone with {PlanningBaselineVerifiedRoots=clone.PlanningBaselineVerifiedRoots-1},"clone_work_count_mismatch");
        Reject(clone with {PlanningDecisions=clone.PlanningDecisions+1},"clone_work_count_mismatch");
        Reject(clone with {PlanningClones=-1},"clone_work_count_mismatch");
        Reject(clone with {PlanningPrefixRoots=1},"clone_work_count_mismatch");
        Reject(clone with {PlanningBaselineInitialVerified=false},"clone_baseline_not_verified");
        Reject(clone with {PlanningBaselineFinalVerified=false},"clone_baseline_not_verified");
        Reject(clone with {PlanningCloneFailure="external selectors",PlanningCloneFailureStep=1},"clone_boundary_failed");
        Reject(clone with {Errors=new[]{"planning_clone_unsupported"}},"clone:planning_clone_unsupported");
        Reject(clone with {Errors=new[]{"unfinished"}},"clone:unfinished");
        Reject(clone with {PlanningVmFaults=1},"clone:vm_fault");
        Reject(clone with {PlanningChoiceFaults=1},"clone:choice_fault");
        Reject(clone with {PlanningPendingChoices=1},"clone:pending_choice");
        Reject(clone with {PlanningVmFaults=-1},"negative_diagnostic_count");
        Reject(clone with {PlanningCalls=new Dictionary<string,int>{{"unknown",-1}}},"negative_diagnostic_count");
        Reject(clone with {PlanningUnsupported=new Dictionary<string,int>{{"forced",1}}},"clone:unsupported:forced");
        Reject(clone with {PlanningCalls=new Dictionary<string,int>{{"forced",1}}},"clone:unimplemented:forced");
        Reject(clone with {MainFaults=1},"clone:main_action_fault");
        Reject(clone with {MainRejections=1},"clone:main_action_rejected");
        Reject(clone with {AppliedActions=clone.AppliedActions-1},"clone:unverified_root_count");
        Reject(clone with {PlanningMissingCallSitesOmitted=1},"incomplete_call_provenance");
        Reject(clone with {PlanningMissingCallSiteCaptureFailures=1},"incomplete_call_provenance");
        Reject(clone with {Teacher=reference.Teacher},"execution_engine_ownership_mismatch");
        var changed=clone.Labels.ToArray();changed[0]=changed[0] with {ChosenIndex=-1};
        Reject(clone with {Labels=changed},"invalid_action_labels");
        changed=clone.Labels.ToArray();changed[0]=changed[0] with {Step=1};
        Reject(clone with {Labels=changed},"invalid_action_labels");
        changed=clone.Labels.ToArray();changed[0]=changed[0] with {AfterObservation=new float[1]};
        Reject(clone with {Labels=changed},"clone:missing_afterstate_observation");
        changed=clone.Labels.ToArray();var observation=changed[0].AfterObservation.ToArray();observation[0]+=1;
        changed[0]=changed[0] with {AfterObservation=observation};
        Reject(clone with {Labels=changed},"reference_episode_mismatch");
        Assert(Reasons(clone,reference with {Errors=new[]{"forced_reference_failure"}}).Contains("reference:forced_reference_failure"),"failed reference rejected");
        Assert(Reasons(clone,clone).Contains("reference:experimental_planning_not_admitted"),"clone cannot replace prefix reference");
        var seed2=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,2);
        Assert(Reasons(clone,seed2).Contains("episode_identity_mismatch"),"different seed reference rejected");
        var last=OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,deck,deck,1,OracleChooseOnePolicy.Last);
        var lastReference=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,1,OracleChooseOnePolicy.Last);
        Assert(Reasons(last,lastReference).Length==0,"last-choice full pair accepted");
        Assert(Reasons(last).Contains("episode_identity_mismatch"),"different policy reference rejected");
        Reject(clone with {SnapshotDifference="recorded divergence"},"snapshot_difference_recorded");
        changed=clone.Labels.ToArray();var nonfinite=changed[0].AfterObservation.ToArray();nonfinite[0]=float.NaN;
        changed[0]=changed[0] with {AfterObservation=nonfinite};
        Reject(clone with {Labels=changed},"invalid_action_labels");
        changed=clone.Labels.ToArray();var badObservation=changed[0].Request.Observation.ToArray();badObservation[0]=float.NaN;
        changed[0]=changed[0] with {Request=changed[0].Request with {Observation=badObservation}};
        Reject(clone with {Labels=changed},"invalid_action_labels");
        int traceCount=clone.Replay.State.ActionLog.Count;clone.Replay.State.ActionLog.Clear();
        Reject(clone,"reference_episode_mismatch");
        clone.Replay.State.ActionLog.AddRange(reference.Replay.State.ActionLog);
        Assert(clone.Replay.State.ActionLog.Count==traceCount && Reasons(clone).Length==0,"trace restored for remaining tests");
        clone.Replay.State.Hq(Side.Left).BaseAttack++;
        Reject(clone,"reference_episode_mismatch");
        clone.Replay.State.Hq(Side.Left).BaseAttack--;
        // Read-only admission, repeatability and exact final state/RNG/trace rejection.
        string before=clone.Replay.State.SnapshotJson();var rng=clone.Replay.State.Random.State;
        _=Reasons(clone);Assert(before==clone.Replay.State.SnapshotJson() && rng==clone.Replay.State.Random.State,"gate has no simulation side effects");
        clone.Replay.State.Random.Next(10);
        Reject(clone,"reference_episode_mismatch");
        Console.WriteLine($"Clone admission: {checks} checks passed");return 0;
    }
}
