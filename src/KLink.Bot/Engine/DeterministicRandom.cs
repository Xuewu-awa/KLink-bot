namespace KLink.Bot.Engine;

/// <summary>
/// 确定性随机数发生器（splitmix64）。
///
/// 为什么必须自己写：客户端是**确定性锁步** —— 双方各自本地结算效果，
/// 必须得到完全一样的结果。所以内核里**禁止**使用 <see cref="System.Random"/>、
/// Guid、DateTime、以及任何依赖字典遍历顺序的东西。
/// 每局对局持有一条独立的随机流，由种子唯一决定。
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong State => _state;

    public ulong NextUInt64()
    {
        // splitmix64
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>返回 [0, maxExclusive) 的均匀整数。maxExclusive &lt;= 0 时返回 0。</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            return 0;
        }

        return (int)(NextUInt64() % (ulong)maxExclusive);
    }

    /// <summary>返回 [minInclusive, maxExclusive)。</summary>
    public int Next(int minInclusive, int maxExclusive)
        => minInclusive + Next(maxExclusive - minInclusive);

    /// <summary>Fisher-Yates 洗牌（原地）。与服务器端 MatchManager.ShuffleCards 的语义一致。</summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public T Pick<T>(IReadOnlyList<T> list) => list[Next(list.Count)];

    /// <summary>用于派生互不相关的子流（例如每个效果一个），避免时序影响主随机流。</summary>
    public DeterministicRandom Fork(ulong salt) => new(NextUInt64() ^ (salt * 0x9E3779B97F4A7C15UL));
}
