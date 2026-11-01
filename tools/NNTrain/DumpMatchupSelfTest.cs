using KLink.Bot.Engine;

namespace KLink.Bot.NNTrain;

internal static class DumpMatchupSelfTest
{
    internal static int Run()
    {
        int checks=0;
        void Check(bool ok){checks++;if(!ok)throw new InvalidOperationException("Dump matchup contract failed");}
        Check(DumpMatchup.Parse(null,22) is null);
        foreach(var bad in new[]{"","0","0,0","-1,2","0,22","22,0","1,2,3","a,2"," 1,2","+1,2","1, 2","2147483648,2"})
        {
            bool rejected=false;try{DumpMatchup.Parse(bad,22);}catch(ArgumentException){rejected=true;}
            Check(rejected);
        }
        foreach(ulong seed in new ulong[]{0,1,919191,ulong.MaxValue})
        for(int g=0;g<64;g++)
        {
            var rng=new DeterministicRandom(seed^(0x9E3779B97F4A7C15UL*((ulong)g+1)));
            int a=rng.Next(22),b=rng.Next(21);if(b>=a)b++;bool swap=rng.Next(2)==1;
            var expected=swap?(b,a):(a,b);
            Check(DumpMatchup.Select(seed,g,22,null)==expected);
            Check(DumpMatchup.Select(seed,g,22,DumpMatchup.Parse("0,21",22))==(0,21));
            Check(DumpMatchup.Select(seed,g,22,DumpMatchup.Parse("21,0",22))==(21,0));
        }
        Console.WriteLine($"Dump matchup: {checks} checks passed (collection contract only)");
        return 0;
    }
}
