using KLink.Bot.Engine;
using System.Globalization;

namespace KLink.Bot.NNTrain;

// Explicit orientation is a collection diagnostic, not the default training distribution.
internal static class DumpMatchup
{
    internal static (int Left,int Right)? Parse(string text,int count)
    {
        if(text is null)return null;
        var parts=text.Split(',');
        if(parts.Length!=2 || !int.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out int l)
            || !int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out int r)
            || l>=count || r>=count || l==r)
            throw new ArgumentException("--matchup requires two distinct deck indices in range: L,R");
        return (l,r);
    }

    internal static (int Left,int Right) Select(ulong seed,int game,int count,(int Left,int Right)? fixedPair)
    {
        if(fixedPair.HasValue)return fixedPair.Value;
        // Preserve the original default RNG stream and draws exactly.
        var pick=new DeterministicRandom(seed ^ (0x9E3779B97F4A7C15UL*((ulong)game+1)));
        int di=pick.Next(count),dj=pick.Next(count-1);
        if(dj>=di)dj++;
        bool swap=pick.Next(2)==1;
        return swap?(dj,di):(di,dj);
    }
}
