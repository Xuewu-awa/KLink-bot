using System.Text.Json;
using KLink.Bot.Engine;

namespace KLink.Bot.NNTrain;

internal static class AdmissionSelfTest
{
    internal static int Run()
    {
        var empty=new Dictionary<string,int>();int checks=0;
        void Assert(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks++;}
        string[] Check(IReadOnlyDictionary<string,int> unsupported=null,IReadOnlyDictionary<string,int> calls=null,bool finished=true,Side winner=Side.Left,long faults=0,bool ir=true,int samples=1,bool finite=true,long choice=0,long driver=0,int pending=0)
            => TrainingAdmission.Check(finished,winner,faults,unsupported??empty,calls??empty,ir,samples,finite,choice,driver,pending);
        Assert(Check().Length==0,"clean episode");
        Assert(Check(calls:new Dictionary<string,int>{{"<local-ran:helper>",3},{"<frontline-blocked-full>",2},{"<frontline-blocked-by-opponent>",1}}).Length==0,"known diagnostic markers");
        Assert(Check(unsupported:new Dictionary<string,int>{{"<pop-unresolved>",1}}).Contains("unsupported:<pop-unresolved>"),"unfinished flow must reject");
        Assert(Check(calls:new Dictionary<string,int>{{"UnknownRule",1},{"<new-marker>",1}}).Length==2,"unknown call and marker must reject");
        Assert(Check(faults:1).Contains("vm_fault"),"fault");
        Assert(Check(finished:false).Contains("unfinished"),"unfinished");
        Assert(Check(winner:Side.NotAvailable).Contains("invalid_winner"),"winner");
        Assert(Check(ir:false).Contains("ir_not_loaded"),"IR");
        Assert(Check(samples:0).Contains("no_samples"),"empty observations");
        Assert(Check(finite:false).Contains("nonfinite_observation"),"NaN or infinity");
        Assert(Check(unsupported:new Dictionary<string,int>{{"zero",0}}).Length==0,"zero counter");
        Assert(Check(choice:1).Contains("choice_fault"),"choice fault without VM fault");
        Assert(Check(driver:1).Contains("driver_fault"),"driver exception");
        Assert(Check(pending:1).Contains("pending_choice"),"unresolved choice");
        string dir=Path.Combine(Path.GetTempPath(),"klink-admission-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string path=Path.Combine(dir,"data.bin");
            void Reject(string reason){bool failed=false;try{TrainingAdmission.VerifyDataset(path);}catch(InvalidDataException){failed=true;}Assert(failed,reason);}
            int dim=KLink.Bot.NN.StateEncoder.Dim;
            using(var w=new BinaryWriter(File.Create(path))){w.Write(0x324C4B41);w.Write(dim);w.Write(2);for(int i=0;i<dim;i++)w.Write(0.2f);foreach(float x in new[] {1f,0f,1f})w.Write(x);}
            Reject("missing quality report");
            var report=new QualityReport {RequestedGames=1,AcceptedGames=1,Rows=1,Dim=dim,DeckCount=2,DataSha256=TrainingAdmission.Hash(path),Games=new(){new QualityGame {Game=0,Accepted=true,WrittenSamples=1,SampleKinds=[0],SampleActors=[1]}}};
            using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))
                Assert(TrainingAdmission.Hash(path)==report.DataSha256,"hash while dump writer remains open");
            void Save()=>File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(report,TrainingAdmission.Json));
            Save();TrainingAdmission.VerifyDataset(path);checks++;
            report.Rows=0;Save();Reject("empty clean set");report.Rows=1;
            report.Games[0].Reasons=["vm_fault"];Save();Reject("inconsistent admission");report.Games[0].Reasons=[];Save();
            var game=report.Games[0];
            game.ChoiceFaults=1;Save();Reject("accepted choice fault manifest");game.ChoiceFaults=0;
            game.DriverFaults=1;Save();Reject("accepted driver fault manifest");game.DriverFaults=0;
            game.PendingChoices=1;Save();Reject("accepted pending manifest");game.PendingChoices=0;
            game.SampleKinds=[1];Save();Reject("wrong sampling boundary");game.SampleKinds=[0];
            game.SampleActors=[0];Save();Reject("invalid sample actor");game.SampleActors=[1];
            report.SamplingMode="unknown";Save();Reject("unknown sampling contract");report.SamplingMode="turn-end/v1";
            report.GateVersion=1;game.SampleKinds=[];game.SampleActors=[];Save();TrainingAdmission.VerifyDataset(path);checks++;
            report.GateVersion=2;report.SamplingMode="policy-and-choice-before/v1";game.SampleKinds=[1];game.SampleActors=[1];game.MainDecisions=1;
            Save();TrainingAdmission.VerifyDataset(path);checks++;
            using(var stream=File.Open(path,FileMode.Open,FileAccess.Write)){stream.Position=12;stream.WriteByte(0xff);}Reject("data tampering");
        }
        finally
        {
            // Only exact files created by this test; no recursive cleanup.
            File.Delete(Path.Combine(dir,"data.bin"));File.Delete(Path.Combine(dir,"data.bin.quality.json"));Directory.Delete(dir);
        }
        Console.WriteLine($"Training admission: {checks} checks passed");return 0;
    }
}
