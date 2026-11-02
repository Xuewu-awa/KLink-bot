using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

internal static class ActionDatasetSelfTest
{
    internal static int Run(CardDatabase db,StateEncoder.CardVecs vectors)
    {
        int checks=0;
        void Assert(bool value,string name){if(!value)throw new InvalidOperationException(name);checks++;}
        var fixture=CloneEpisodeBenchmark.ResolveFixture(db,new());
        Assert(fixture.Left.Length==39 && fixture.Left.SequenceEqual(fixture.Right),"default benchmark fixture retained");
        var m1=CloneEpisodeBenchmark.ResolveFixture(db,new(){{"fixture","m1"}});
        Assert(m1.Left.All(c=>c=="card_unit_120mm_m1_gun") && m1.Right.Length==39,"effect fixture exact inputs");
        var names=MetaDecks.All.Take(2).Select(d=>d.Name).ToArray();
        var meta=CloneEpisodeBenchmark.ResolveFixture(db,new(){{"left-deck",names[0]},{"right-deck",names[1]}});
        Assert(meta.Left.Length>0 && meta.Right.Length>0 && meta.Name.Contains(names[0]),"meta fixture explicit inputs");
        void RejectFixture(Dictionary<string,string> opts)
        {bool rejected=false;try{CloneEpisodeBenchmark.ResolveFixture(db,opts);}catch(ArgumentException){rejected=true;}
            Assert(rejected,"invalid fixture cannot silently fall back");}
        RejectFixture(new(){{"left-deck",names[0]}});
        RejectFixture(new(){{"left-deck",names[0]},{"right-deck",names[1]},{"fixture","m1"}});
        RejectFixture(new(){{"fixture","missing"}});
        RejectFixture(new(){{"left-deck","missing"},{"right-deck",names[1]}});
        string[] deck=Enumerable.Range(0,39).Select(i=>i%2==0?"card_unit_arado_ar_196":"card_unit_light_infantry").ToArray();
        var episode=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,1);
        Assert(ActionDatasetTool.AdmissionReasons(episode,true).Length==0,"clean oracle episode admitted");
        var diagnostic=episode with {PlanningCloneFailure="test guard",PlanningCloneFailureStep=3,
            Errors=new[]{"planning_clone_unsupported"}};
        using(var doc=JsonDocument.Parse(JsonSerializer.Serialize(CloneEpisodeBenchmark.Diagnostics(diagnostic),ActionDatasetTool.Json)))
        {
            var root=doc.RootElement;
            Assert(root.GetProperty("planningCloneFailureStep").GetInt32()==3
                && root.GetProperty("planningCloneFailure").GetString()=="test guard"
                && root.GetProperty("errors")[0].GetString()=="planning_clone_unsupported"
                && root.GetProperty("missingCalls").GetArrayLength()==3,"rejected benchmark retains boundary and phase provenance");
        }
        using var buffer=new StringWriter();
        var experimental=episode with {PlanningMode="experimental-owned-idle-clone/v1"};
        Assert(ActionDatasetTool.AdmissionReasons(experimental,true).Contains("experimental_planning_not_admitted")
            && ActionDatasetTool.WriteAdmittedLabels(buffer,0,experimental,true)==0 && buffer.ToString()=="",
            "experimental clone mode cannot emit training rows even with valid replay counters");
        var rejected=episode with {Errors=new[]{"forced_late_failure"}};
        Assert(ActionDatasetTool.WriteAdmittedLabels(buffer,0,rejected,true)==0 && buffer.ToString()=="",
            "rejected episode cannot leak its buffered prefix");
        Assert(ActionDatasetTool.WriteAdmittedLabels(buffer,0,episode,false)==0 && buffer.ToString()=="","IR gate cannot leak rows");
        int written=ActionDatasetTool.WriteAdmittedLabels(buffer,0,episode,true);
        Assert(written==episode.Labels.Count && buffer.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Length==written,
            "clean episode writes all labels");
        using var repeat=new StringWriter();
        ActionDatasetTool.WriteAdmittedLabels(repeat,0,OracleActionEpisodeRunner.Run(db,vectors,deck,deck,1),true);
        Assert(buffer.ToString()==repeat.ToString(),"fixed-seed labels repeat byte-for-byte");

        string dir=Path.Combine(Path.GetTempPath(),"klink-actions-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"data.jsonl.gz");
        var hashes=new[]{"cards.json","card-ir.json","card-effects.json","card-vectors.json","KLink.Bot.dll","NNTrain.dll"}
            .ToDictionary(k=>k,_=>new string('0',64));
        var header=new ActionDatasetHeader(5,"KLink.MainActionLabels",StateEncoder.Spec,MainActionFeatures.Spec,"actor",
            OracleActionEpisodeRunner.TeacherContract,"structural-superset",1,2,hashes);
        float[] Features(params float[] prefix)=>prefix.Concat(new float[MainActionFeatures.Dim-prefix.Length]).ToArray();
        var option=new MainActionOption(0,new(MainActionKind.EndTurn),Array.AsReadOnly(Features(0,0,0,1)));
        float[] ChangedFeature(int index,float value){var f=option.Features.ToArray();f[index]=value;return f;}
        var good=new ActionDatasetRow(0,0,Side.Left,1,new float[StateEncoder.Dim],[option],0,["legal-mask/unverified"],new float[StateEncoder.Dim]);
        ActionDatasetQuality Quality()=>new(){Header=header,AcceptedGames=1,RejectedGames=1,Rows=1,Games=[
            new(){Game=0,Seed=1,Accepted=true,Finished=true,Winner=Side.Left,BufferedRows=1,WrittenRows=1,ComparedSteps=1,AppliedActions=1,PlanningDecisions=1},
            new(){Game=1,Seed=2,Accepted=false,BufferedRows=5,Reasons=["forced_late_failure"]}]};
        void Save(ActionDatasetQuality q,ActionDatasetRow row,ActionDatasetHeader stored=null)
        {
            using(var file=File.Create(path))using(var zip=new GZipStream(file,CompressionLevel.Fastest))
            using(var writer=new StreamWriter(zip,new UTF8Encoding(false)))
            {writer.WriteLine(JsonSerializer.Serialize(stored??q.Header,ActionDatasetTool.Json));writer.WriteLine(JsonSerializer.Serialize(row,ActionDatasetTool.Json));}
            q.DataSha256=TrainingAdmission.Hash(path);
            File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(q,ActionDatasetTool.Json));
        }
        void Reject(string name){bool failed=false;try{ActionDatasetTool.Verify(path);}catch(InvalidDataException){failed=true;}Assert(failed,name);}
        try
        {
            var complete=Quality();complete.Rows=written;
            complete.Games[0].BufferedRows=complete.Games[0].WrittenRows=complete.Games[0].ComparedSteps=written;
            complete.Games[0].AppliedActions=written;
            complete.Games[0].Winner=episode.Replay.State.Winner;
            complete.Games[0].PlanningDecisions=written;complete.Games[0].PlanningPrefixRoots=episode.PlanningPrefixRoots;
            using(var file=File.Create(path))using(var zip=new GZipStream(file,CompressionLevel.Fastest))
            using(var writer=new StreamWriter(zip,new UTF8Encoding(false)))
            {writer.WriteLine(JsonSerializer.Serialize(header,ActionDatasetTool.Json));writer.Write(buffer.ToString());}
            complete.DataSha256=TrainingAdmission.Hash(path);
            File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(complete,ActionDatasetTool.Json));
            Assert(ActionDatasetTool.Verify(path).Rows==written,"full emitted episode round-trips compressed verifier");
            Save(Quality(),good);Assert(ActionDatasetTool.Verify(path).Rows==1,"valid compressed header/manifest/row");
            var mutations=new (string Name,Func<ActionDatasetRow,ActionDatasetRow> Mutate)[]{
                ("rejected game row",r=>r with {Game=1}),
                ("step gap",r=>r with {Step=1}),
                ("invalid actor",r=>r with {Actor=Side.NotAvailable}),
                ("invalid turn",r=>r with {Turn=0}),
                ("wrong observation width",r=>r with {Observation=[]}),
                ("missing afterstate observation",r=>r with {AfterObservation=null}),
                ("invalid label",r=>r with {ChosenIndex=1}),
                ("missing explicit mask gap",r=>r with {CoverageGaps=[]}),
                ("duplicate candidate",r=>r with {Candidates=[option,option with {Index=1}]}),
                ("wrong candidate index",r=>r with {Candidates=[option with {Index=1}]}),
                ("bad command shape",r=>r with {Candidates=[option with {Command=new(MainActionKind.EndTurn,7)}]}),
                ("null candidate",r=>r with {Candidates=[null]}),
                ("null command",r=>r with {Candidates=[option with {Command=null}]}),
                ("null features",r=>r with {Candidates=[option with {Features=null}]}),
                ("wrong feature width",r=>r with {Candidates=[option with {Features=Array.Empty<float>()}]}),
                ("wrong kind onehot",r=>r with {Candidates=[option with {Features=new float[MainActionFeatures.Dim]}]}),
                ("legacy scalar width",r=>r with {Candidates=[option with {Features=option.Features.Take(18).ToArray()}]}),
                ("invalid vector marker",r=>r with {Candidates=[option with {Features=ChangedFeature(18,.5f)}]}),
                ("endturn source vector",r=>r with {Candidates=[option with {Features=ChangedFeature(18,1)}]}),
                ("unmarked source payload",r=>r with {Candidates=[option with {Features=ChangedFeature(19,1)}]}),
                ("unmarked target payload",r=>r with {Candidates=[option with {Features=ChangedFeature(110,1)}]}),
                ("choice on end turn",r=>r with {Candidates=[option with {Command=new(MainActionKind.EndTurn,ChooseOne:0)}]}),
                ("choice feature on end turn",r=>r with {Candidates=[option with {Features=Features(option.Features.Take(16).Concat(new float[]{1,2}).ToArray())}]})};
            foreach(var m in mutations){Save(Quality(),m.Mutate(good));Reject(m.Name);}
            var q=Quality();q.Games[0].MainRejections=1;Save(q,good);Reject("accepted root rejection");
            q=Quality();q.Games[0].BufferedRows=2;Save(q,good);Reject("accepted incomplete prefix");
            q=Quality();q.Games[1].WrittenRows=1;Save(q,good);Reject("rejected nonzero row manifest");
            Save(Quality(),good,header with {Teacher="other"});Reject("compressed header differs");
            q=Quality();q.Header=q.Header with {InputsSha256=new()};Save(q,good);Reject("missing input bindings");
            q=Quality();q.Games[0].PlanningDecisions=0;Save(q,good);Reject("missing isolated planning count");
            q=Quality();q.Games[0].PlanningPrefixRoots=1;Save(q,good);Reject("wrong isolated prefix count");
            q=Quality();q.Header=q.Header with {Version=1,Teacher="greedy-step/v1+first-index/v1"};Save(q,good);Reject("legacy unisolated teacher rejected");
            q=Quality();q.Header=q.Header with {Version=2,Teacher="greedy-isolated-prefix/v2+first-index/v1"};Save(q,good);Reject("legacy no-branch contract rejected");
            q=Quality();q.Header=q.Header with {Version=3};Save(q,good);Reject("legacy scalar-only dataset version rejected");
            var play=option with {Command=new(MainActionKind.Play,70,ChooseOne:1),Features=Features(1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2,2)};
            Save(Quality(),good with {Candidates=[play]});Assert(ActionDatasetTool.Verify(path).Rows==1,"branch one roundtrip");
            q=Quality();q.Header=q.Header with {Teacher=OracleActionEpisodeRunner.LastChoiceTeacherContract};
            Save(q,good with {Candidates=[play]});Assert(ActionDatasetTool.Verify(path).Rows==1,"fixed-last branch one roundtrip");
            var first=play with {Command=play.Command with {ChooseOne=0},Features=Features(play.Features.Take(16).Concat(new float[]{1,2}).ToArray())};
            Save(q,good with {Candidates=[first]});Reject("fixed-last label must match policy");
            q=Quality();q.Header=q.Header with {Teacher="unknown-teacher"};Save(q,good);Reject("unknown teacher rejected");
            Assert(ActionDatasetTool.ParseChooseOnePolicy("native")==OracleChooseOnePolicy.Native
                && ActionDatasetTool.ParseChooseOnePolicy("last")==OracleChooseOnePolicy.Last,"explicit teacher policy parser");
            bool badPolicy=false;try{ActionDatasetTool.ParseChooseOnePolicy("typo");}catch(ArgumentException){badPolicy=true;}
            Assert(badPolicy,"unknown teacher policy rejected");
            Save(Quality(),good with {Candidates=[play with {Command=play.Command with {ChooseOne=2}}]});Reject("branch out of range");
            Save(Quality(),good with {Candidates=[play with {Command=play.Command with {ChooseOne=0}}]});Reject("branch features disagree");
            Assert(ActionDatasetTool.AdmissionReasons(episode with {PlanningCalls=new Dictionary<string,int>{{"<unknown-probe-call>",1}}},true)
                .Contains("unimplemented:<unknown-probe-call>"),"discarded probe diagnostics still block admission");
            // Identical runtime sites must retain teacher/replay/planning ownership.
            var program=new KismetProgram(new[] {
                new KismetStep(0,"math","Format",[],[],null,null,-1,null,null),
                new KismetStep(1,"return",null,[],[],null,null,-1,null,null)},0)
                {AssetName="provenance_probe",FunctionName="shared",SourceEntry=0};
            foreach(var engine in new[] {episode.Teacher,episode.Replay})
                engine.Api.Vm.RunLocalProgram(program,new EffectContext {Engine=engine,State=engine.State,Controller=Side.Left},null,"result");
            var site=episode.Teacher.Api.Vm.MissingCallSites.Keys.Single();
            var diagnosticEpisode=episode with {PlanningMissingCallSites=[new(site,3_000_000_000L)],
                PlanningMissingCallSitesOmitted=7,PlanningMissingCallSiteCaptureFailures=2};
            var provenance=ActionDatasetTool.CaptureCallProvenance(diagnosticEpisode);
            Assert(provenance.Select(p=>p.Phase).SequenceEqual(new[] {"teacher","replay","planning"})
                && provenance[0].Sites.Single().Count==1 && provenance[1].Sites.Single().Count==1
                && provenance[2].Sites.Single().Count==3_000_000_000L && provenance[2].SitesOmitted==7
                && provenance[2].CaptureFailures==2,"phase ownership and long planning work counts");
            q=Quality();q.Games[1].MissingCallProvenance=provenance;Save(q,good);
            var restored=ActionDatasetTool.Verify(path).Games[1].MissingCallProvenance;
            Assert(JsonSerializer.Serialize(restored,ActionDatasetTool.Json)==JsonSerializer.Serialize(provenance,ActionDatasetTool.Json),
                "rejected episode provenance survives quality manifest roundtrip");
            Assert(ActionDatasetTool.AdmissionReasons(diagnosticEpisode,true).Contains("unimplemented:math:Format"),
                "provenance cannot exempt retained engine missing call");
            using var noLeak=new StringWriter();
            Assert(ActionDatasetTool.WriteAdmittedLabels(noLeak,0,diagnosticEpisode,true)==0 && noLeak.ToString()=="",
                "diagnosed rejected episode still writes no action rows");
            Save(Quality(),good);using(var file=new FileStream(path,FileMode.Append))file.WriteByte(42);Reject("data hash tampering");
        }
        finally{File.Delete(path);File.Delete(path+".quality.json");Directory.Delete(dir);}
        Console.WriteLine($"Action dataset: {checks} checks passed");return 0;
    }
}
