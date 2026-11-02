using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.NN;
using KLink.Bot.Training;

namespace KLink.Bot.NNTrain;

// Measures the current replay-prefix teacher, without changing training admission.
internal static class ActionEpisodeBenchmark
{
    internal static int Run(CardDatabase db, StateEncoder.CardVecs vectors,
        Dictionary<string,string> opts, string dataDir, string vectorPath)
    {
        int repeats=int.Parse(opts.GetValueOrDefault("repeats","3"),CultureInfo.InvariantCulture);
        ulong seed=ulong.Parse(opts.GetValueOrDefault("seed","1"),CultureInfo.InvariantCulture);
        if(repeats is <1 or >5)throw new ArgumentException("--repeats must be 1..5.");
        string output=Path.GetFullPath(opts.GetValueOrDefault("out","action-episode-benchmark.json"));
        bool irLoaded=KismetLibrary.Default is not null;
        if(!irLoaded)throw new InvalidOperationException("Benchmark requires loaded card IR.");
        var inputs=ActionDatasetTool.CurrentInputs(dataDir,vectorPath);
        string[] deck=Enumerable.Range(0,39)
            .Select(i=>i%2==0?"card_unit_arado_ar_196":"card_unit_light_infantry").ToArray();
        string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        object Fingerprint(OracleActionEpisodeResult episode)
        {
            using var text=new StringWriter(CultureInfo.InvariantCulture);
            text.NewLine="\n"; // Portable canonical labels; gzip bytes are not the equality oracle.
            int rows=ActionDatasetTool.WriteAdmittedLabels(text,0,episode,irLoaded);
            return new {
                rows, bufferedRows=episode.Labels.Count,
                candidates=episode.Labels.Sum(l=>(long)l.Request.Candidates.Count),
                reasons=ActionDatasetTool.AdmissionReasons(episode,irLoaded),
                labelsSha256=Hash(text.ToString()), episode.Errors, episode.ComparedSteps,
                episode.AppliedActions,episode.PlanningDecisions,episode.PlanningPrefixRoots,
                episode.MainFaults,episode.MainRejections,episode.ChoiceFaults,
                episode.PlanningVmFaults,episode.PlanningChoiceFaults,episode.PlanningPendingChoices,
                teacherSnapshotSha256=Hash(episode.Teacher.State.SnapshotJson()),
                replaySnapshotSha256=Hash(episode.Replay.State.SnapshotJson()),
                teacherTraceSha256=Hash(JsonSerializer.Serialize(episode.Teacher.State.ActionLog)),
                replayTraceSha256=Hash(JsonSerializer.Serialize(episode.Replay.State.ActionLog)),
                episode.Replay.State.IsFinished,episode.Replay.State.Winner,
                randomState=episode.Replay.State.Random.State,
                randomConsumed=episode.Replay.State.Random.ConsumedCount
            };
        }

        // One full warm-up, including serialization/compression. Input loading is outside timing.
        (OracleActionEpisodeResult Episode, object Timing) Measure()
        {
            long allocated=GC.GetAllocatedBytesForCurrentThread();
            var clock=Stopwatch.StartNew();
            var episode=OracleActionEpisodeRunner.Run(db,vectors,deck,deck,seed);
            double episodeMs=clock.Elapsed.TotalMilliseconds;
            long episodeBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            int rows;long compressedBytes;
            using(var memory=new MemoryStream())
            {
                using(var zip=new GZipStream(memory,CompressionLevel.Fastest,true))
                using(var writer=new StreamWriter(zip,new UTF8Encoding(false)))
                    rows=ActionDatasetTool.WriteAdmittedLabels(writer,0,episode,irLoaded);
                compressedBytes=memory.Length;
            }
            double totalMs=clock.Elapsed.TotalMilliseconds;
            long totalBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            return (episode,new {episodeMs,totalMs,episodeBytes,totalBytes,rows,compressedBytes});
        }
        Console.WriteLine("Warming full replay-prefix episode (synthetic mixed-unit fixture)...");
        var warm=Measure();
        object expected=Fingerprint(warm.Episode);
        string expectedJson=JsonSerializer.Serialize(expected,ActionDatasetTool.Json);
        bool admitted=ActionDatasetTool.AdmissionReasons(warm.Episode,irLoaded).Length==0;
        var samples=new List<object>();
        for(int i=0;i<repeats;i++)
        {
            var sample=Measure();
            if(JsonSerializer.Serialize(Fingerprint(sample.Episode),ActionDatasetTool.Json)!=expectedJson)
                throw new InvalidOperationException("Full episode fingerprint changed between repeats.");
            samples.Add(sample.Timing);
            Console.WriteLine($"Repeat {i+1}/{repeats}: {JsonSerializer.Serialize(sample.Timing,ActionDatasetTool.Json)}");
        }
        var report=new {
            version=1,kind="KLink.ActionEpisodeBenchmark",seed,repeats,
            fixture="39 alternating arado_ar_196/light_infantry per side; synthetic, not constructed-legal",
            teacher=OracleActionEpisodeRunner.TeacherContract,inputsSha256=inputs,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            scope="Full public Oracle episode plus admission and admitted-row UTF8/gzip in memory. Excludes startup, input loading, hashes, warm-up, file IO, header/manifest and optimizer epochs. Allocation is current-thread managed bytes, not peak memory.",
            admitted,fingerprint=expected,samples
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions(ActionDatasetTool.Json){WriteIndented=true}));
        Console.WriteLine($"Benchmark report: {output}");
        return admitted?0:2;
    }
}
