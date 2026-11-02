using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

internal static class CloneActionDatasetSelfTest
{
    internal static int Run(CardDatabase db,StateEncoder.CardVecs vectors)
    {
        int checks=0;
        void Assert(bool value,string name){if(!value)throw new InvalidOperationException(name);checks++;}
        bool Rejected(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        var deck=Enumerable.Repeat("card_unit_120mm_m1_gun",39).ToArray();
        var reference=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,1);
        var clone=OracleActionEpisodeRunner.RunExperimentalClonedPlanning(db,vectors,deck,deck,1);
        var inputs=new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json","KLink.Bot.dll","NNTrain.dll"}
            .ToDictionary(k=>k,_=>new string('0',64));
        inputs["KLink.Bot.dll"]=ValueTrainingContract.CurrentSimulatorHash();
        var header=new ActionDatasetHeader(6,CloneActionDatasetTool.Kind,StateEncoder.Spec,MainActionFeatures.Spec,"actor",
            reference.Identity.Teacher,"structural-superset",1,1,inputs,ClonePlanningAdmission.Contract);
        using var buffer=new StringWriter();
        var game=CloneActionDatasetTool.WritePair(buffer,0,"m1","m1",deck,deck,clone,reference,true);
        Assert(game.Accepted && game.WrittenRows==clone.Labels.Count,"healthy pair emits complete rows");
        using var rejectedBuffer=new StringWriter();
        var bad=CloneActionDatasetTool.WritePair(rejectedBuffer,0,"m1","m1",deck,deck,
            clone with {Errors=["forced_late_failure"]},reference,true);
        Assert(!bad.Accepted && bad.WrittenRows==0 && rejectedBuffer.ToString()=="","late failure emits no prefix");
        bad=CloneActionDatasetTool.WritePair(rejectedBuffer,0,"m1","m1",deck,deck,clone,reference with {Errors=["failed_reference"]},true);
        Assert(!bad.Accepted && rejectedBuffer.ToString()=="","failed reference emits no rows");
        var other=deck.ToArray();other[0]="card_unit_light_infantry";
        bad=CloneActionDatasetTool.WritePair(rejectedBuffer,0,"m1","m1",other,deck,clone,reference,true);
        Assert(!bad.Accepted && rejectedBuffer.ToString()=="","wrong deck provenance emits no rows");
        bad=CloneActionDatasetTool.WritePair(rejectedBuffer,0,"m1","m1",deck,deck,clone,reference,false);
        Assert(!bad.Accepted && rejectedBuffer.ToString()=="","missing IR emits no rows");
        var nan=clone.Labels.ToArray();var obs=nan[0].AfterObservation.ToArray();obs[0]=float.NaN;
        nan[0]=nan[0] with {AfterObservation=obs};
        bad=CloneActionDatasetTool.WritePair(rejectedBuffer,0,"m1","m1",deck,deck,clone with {Labels=nan},reference,true);
        Assert(!bad.Accepted && bad.CloneEvidence.CloneFingerprint is null && rejectedBuffer.ToString()=="","nonfinite rejection retains summary without serializing labels");
        var malformed=clone.Labels.ToArray();var options=malformed[0].Request.Candidates.ToArray();
        options[0]=options[0] with {Command=options[0].Command with {SourceId=-1}};
        malformed[0]=malformed[0] with {Request=malformed[0].Request with {Candidates=options}};
        var malformedReference=reference with {Labels=malformed};
        Assert(ClonePlanningAdmission.Check(clone with {Labels=malformed},malformedReference,true).Length==0,
            "pair equality alone does not enforce serialized command shape");
        bad=CloneActionDatasetTool.WritePair(rejectedBuffer,0,"m1","m1",deck,deck,
            clone with {Labels=malformed},malformedReference,true);
        Assert(!bad.Accepted && bad.Reasons.Contains("invalid_serialized_action_row") && rejectedBuffer.ToString()=="",
            "writer rejects matching malformed commands before first row");
        var quality=new ActionDatasetQuality{Header=header,AcceptedGames=1,Rows=game.WrittenRows,Games=[game]};
        string goodJson=JsonSerializer.Serialize(quality,ActionDatasetTool.Json);
        ActionDatasetQuality Fresh()=>JsonSerializer.Deserialize<ActionDatasetQuality>(goodJson,ActionDatasetTool.Json);
        string dir=Path.Combine(Path.GetTempPath(),"klink-clone-data-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"data.jsonl.gz"),modelPath=Path.Combine(dir,"model.json");
        void Save(ActionDatasetQuality q,string rows=null,ActionDatasetHeader stored=null)
        {
            using(var file=File.Create(path))using(var zip=new GZipStream(file,CompressionLevel.Fastest))
            using(var writer=new StreamWriter(zip,new UTF8Encoding(false)))
            {writer.WriteLine(JsonSerializer.Serialize(stored??q.Header,ActionDatasetTool.Json));writer.Write(rows??buffer.ToString());}
            q.DataSha256=TrainingAdmission.Hash(path);
            File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(q,ActionDatasetTool.Json));
        }
        void RejectMutation(string name,Action<ActionDatasetQuality> mutate)
        {var q=Fresh();mutate(q);Save(q);Assert(Rejected(()=>ActionDatasetTool.Verify(path,true)),name);}
        try
        {
            Save(Fresh());Assert(ActionDatasetTool.Verify(path,true).Rows==game.WrittenRows,"compressed clone pair roundtrip");
            Assert(Rejected(()=>ActionDatasetTool.Verify(path)),"default verifier refuses clone schema");
            var mixed=Fresh();
            var failed=CloneActionDatasetTool.WritePair(rejectedBuffer,1,"m1","m1",deck,deck,
                clone with {Errors=["forced_late_failure"]},reference,true);
            failed.Seed=2;failed.CloneEvidence.Reference.Seed=2;
            mixed.Header=mixed.Header with {RequestedGames=2};mixed.Games.Add(failed);mixed.RejectedGames=1;Save(mixed);
            var mixedRead=ActionDatasetTool.Verify(path,true);
            Assert(mixedRead.RejectedGames==1 && mixedRead.Rows==game.WrittenRows
                && mixedRead.Games[1].WrittenRows==0,"mixed games retain rejected diagnostics without rows");
            Save(Fresh());

            Assert(Rejected(()=>AfterstateDatasetTool.Build(new(){{"actions",path},{"out",Path.Combine(dir,"afterstate.gz")}})),"default afterstate consumer refuses clone schema");
            RejectMutation("missing clone evidence",q=>q.Games[0].CloneEvidence=null);
            RejectMutation("missing reference",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {Reference=null});
            RejectMutation("wrong version",q=>q.Header=q.Header with {Version=5});
            RejectMutation("wrong kind",q=>q.Header=q.Header with {Kind="KLink.MainActionLabels"});
            RejectMutation("missing planning contract",q=>q.Header=q.Header with {PlanningContract=null});
            RejectMutation("unknown teacher",q=>q.Header=q.Header with {Teacher="unknown"});
            RejectMutation("wrong clone work",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {Clones=1});
            RejectMutation("missing verified roots",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {VerifiedRoots=0});
            RejectMutation("missing initial check",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {InitialVerified=false});
            RejectMutation("missing final check",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {FinalVerified=false});
            RejectMutation("boundary failure",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {BoundaryFailure="unsupported"});
            RejectMutation("wrong deck order/content",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {Left=other});
            RejectMutation("different reference identity",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {ReferenceIdentity=reference.Identity with {Seed=2}});
            RejectMutation("wrong reference work",q=>q.Games[0].CloneEvidence.Reference.PlanningPrefixRoots=0);
            RejectMutation("failed reference health",q=>q.Games[0].CloneEvidence.Reference.VmFaults=1);
            RejectMutation("negative clone health",q=>q.Games[0].VmFaults=-1);
            RejectMutation("missing reference provenance",q=>q.Games[0].CloneEvidence.Reference.MissingCallProvenance=[]);
            RejectMutation("omitted clone provenance",q=>q.Games[0].MissingCallProvenance[0]=q.Games[0].MissingCallProvenance[0] with {SitesOmitted=1});
            RejectMutation("snapshot mismatch",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {CloneFingerprint=game.CloneEvidence.CloneFingerprint with {SnapshotSha256=new string('1',64)}});
            RejectMutation("trace mismatch",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {CloneFingerprint=game.CloneEvidence.CloneFingerprint with {TraceSha256=new string('1',64)}});
            RejectMutation("RNG mismatch",q=>q.Games[0].CloneEvidence=q.Games[0].CloneEvidence with {CloneFingerprint=game.CloneEvidence.CloneFingerprint with {RandomConsumed=0}});
            RejectMutation("clone prefix roots must be zero",q=>q.Games[0].PlanningPrefixRoots=1);
            RejectMutation("null accepted provenance entry",q=>q.Games[0].MissingCallProvenance[0]=null);
            RejectMutation("null games",q=>q.Games=null);
            RejectMutation("null input bindings",q=>q.Header=q.Header with {InputsSha256=null});
            RejectMutation("both RNG counts negative",q=>{
                var e=q.Games[0].CloneEvidence;
                q.Games[0].CloneEvidence=e with {CloneFingerprint=e.CloneFingerprint with {RandomConsumed=-1},
                    ReferenceFingerprint=e.ReferenceFingerprint with {RandomConsumed=-1}};
            });
            var legacy=Fresh();legacy.Header=legacy.Header with {Version=5,Kind="KLink.MainActionLabels",PlanningContract=null};
            legacy.Games[0].CloneEvidence=null;legacy.Games[0].PlanningPrefixRoots=(long)game.WrittenRows*(game.WrittenRows-1)/2;
            Save(legacy);Assert(ActionDatasetTool.Verify(path).Rows==game.WrittenRows,"legacy v5 roundtrip remains valid");
            Assert(Rejected(()=>ActionDatasetTool.Verify(path,true)),"clone opt-in refuses legacy v5");
            var lines=buffer.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries);
            var row=JsonSerializer.Deserialize<ActionDatasetRow>(lines[0],ActionDatasetTool.Json);
            var changed=row.Observation.ToArray();changed[0]+=1;
            lines[0]=JsonSerializer.Serialize(row with {Observation=changed},ActionDatasetTool.Json);
            Save(Fresh(),string.Join('\n',lines)+"\n");
            Assert(Rejected(()=>ActionDatasetTool.Verify(path,true)),"row hash independently recomputed despite updated data hash");
            lines[0]=JsonSerializer.Serialize(row with {ChosenIndex=-1},ActionDatasetTool.Json);Save(Fresh(),string.Join('\n',lines)+"\n");
            Assert(Rejected(()=>ActionDatasetTool.Verify(path,true)),"shared row verifier rejects invalid labels");
            Save(Fresh(),string.Join('\n',lines.Skip(1))+"\n");Assert(Rejected(()=>ActionDatasetTool.Verify(path,true)),"missing row rejected");
            Save(Fresh(),stored:header with {Seed=2});Assert(Rejected(()=>ActionDatasetTool.Verify(path,true)),"stored header mismatch");
            Save(Fresh());File.AppendAllText(path,"tamper");Assert(Rejected(()=>ActionDatasetTool.Verify(path,true)),"compressed data tampering");
            Save(Fresh());var current=ActionDatasetTool.Verify(path,true);
            var contract=new MainActionTrainingContract{Version=2,PlanningContract=MainActionTrainingContract.PairedClonePlanning,
                DatasetQualitySha256=TrainingAdmission.Hash(path+".quality.json"),Teacher=header.Teacher,
                DatasetSha256=current.DataSha256,InputsSha256=inputs,TrainingGames=[0],ValidationGames=[1]};
            contract.RequireInputs(header.Teacher,inputs,ClonePlanningAdmission.Contract);Assert(true,"clone model accepts explicit matching mode");
            Assert(Rejected(()=>contract.RequireInputs(header.Teacher,inputs)),"default model input contract refuses clone mode");
            Assert(Rejected(()=>(contract with {Version=1}).Validate()),"legacy model cannot carry clone provenance");
            Assert(Rejected(()=>(contract with {DatasetQualitySha256=null}).Validate()),"clone model requires manifest binding");
            var model=MainActionModel.Create(contract,2,1);model.Save(modelPath);
            Assert(Rejected(()=>MainActionModel.Load(modelPath)),"default model loader refuses experimental clone model");
            Assert(JsonSerializer.Serialize(MainActionModel.Load(modelPath,true).Contract)==JsonSerializer.Serialize(contract),"explicit clone model roundtrip");
            bool badOption=false;try{CloneActionDatasetTool.OptIn(new(){{"planning-mode","typo"}});}catch(ArgumentException){badOption=true;}
            Assert(badOption && !CloneActionDatasetTool.OptIn(new()) && CloneActionDatasetTool.OptIn(new(){{"planning-mode","paired-clone"}}),"explicit opt-in parser no silent fallback");
        }
        finally{Directory.Delete(dir,true);}
        Console.WriteLine($"Clone dataset: {checks} checks passed");return 0;
    }
}
