using System.Text.Json;
using KLink.Bot.Engine;

namespace KLink.Bot.NNTrain;

internal static class AdmissionSelfTest
{
    internal static int Run()
    {
        var empty=new Dictionary<string,int>();int checks=0;
        void Assert(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks++;}
        string[] Check(IReadOnlyDictionary<string,int> unsupported=null,IReadOnlyDictionary<string,int> calls=null,bool finished=true,Side winner=Side.Left,long faults=0,bool ir=true,int samples=1,bool finite=true)
            => TrainingAdmission.Check(finished,winner,faults,unsupported??empty,calls??empty,ir,samples,finite);
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
        string dir=Path.Combine(Path.GetTempPath(),"klink-admission-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string path=Path.Combine(dir,"data.bin");
            void Reject(string reason){bool failed=false;try{TrainingAdmission.VerifyDataset(path);}catch(InvalidDataException){failed=true;}Assert(failed,reason);}
            using(var w=new BinaryWriter(File.Create(path))){w.Write(0x324C4B41);w.Write(1);w.Write(2);foreach(float x in new[] {0.2f,1f,0f,1f})w.Write(x);}
            Reject("missing quality report");
            var report=new QualityReport {RequestedGames=1,AcceptedGames=1,Rows=1,Dim=1,DeckCount=2,DataSha256=TrainingAdmission.Hash(path),Games=new(){new QualityGame {Game=0,Accepted=true,WrittenSamples=1}}};
            using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))
                Assert(TrainingAdmission.Hash(path)==report.DataSha256,"hash while dump writer remains open");
            void Save()=>File.WriteAllText(path+".quality.json",JsonSerializer.Serialize(report,TrainingAdmission.Json));
            Save();TrainingAdmission.VerifyDataset(path);checks++;
            report.Rows=0;Save();Reject("empty clean set");report.Rows=1;
            report.Games[0].Reasons=["vm_fault"];Save();Reject("inconsistent admission");report.Games[0].Reasons=[];Save();
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
