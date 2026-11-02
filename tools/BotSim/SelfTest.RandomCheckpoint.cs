using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? UeRandomStreamClonePreservesCursor(CardDatabase db)
    {
        foreach (ulong seed in new ulong[] { 0, 1, 1000193900, uint.MaxValue, ulong.MaxValue })
        foreach (int prefix in new[] { 0, 1, 3, 257 })
        {
            var original = new UeRandomStream(seed);
            for (int i = 0; i < prefix; i++) original.GetFraction();
            var copy = original.Clone();
            if (ReferenceEquals(original, copy) || !Same(original, copy))
                return $"clone lost state: seed={seed}, prefix={prefix}";

            // A fresh stream seeded from the current cursor would pass sequence
            // parity but lose InitialSeed and ConsumedCount. Check all three.
            for (int i = 0; i < 64; i++)
                if (original.GetFraction() != copy.GetFraction() || !Same(original, copy))
                    return "clone continuation differs";

            uint rootSeed = original.Seed;
            long rootCount = original.ConsumedCount;
            copy.RandRange(-17, 37);
            if (original.Seed != rootSeed || original.ConsumedCount != rootCount)
                return "advancing clone mutated original";
            original.RandRange(-17, 37);
            if (!Same(original, copy)) return "independent advances differ";

            uint advancedSeed = original.Seed;
            copy.Reset();
            if (copy.Seed != unchecked((uint)original.InitialSeed)
                || copy.ConsumedCount != rootCount + 1
                || original.Seed != advancedSeed)
                return "clone Reset changed reset seed, draw count or original";
            original.Reset();
            if (!Same(original, copy)) return "reset parity differs";

            // Initialize/Reset currently retain the cumulative counter; cloning
            // must preserve that behavior even after explicit reseeding.
            copy.Initialize(-12345);
            copy.GetFraction();
            var reseeded = copy.Clone();
            if (!Same(copy, reseeded) || copy.GetFraction() != reseeded.GetFraction())
                return "reseeded clone differs";
            if (original.InitialSeed != unchecked((int)(uint)seed)
                || original.ConsumedCount != rootCount + 1)
                return "reseeded clone mutated original";
        }
        return null;
    }

    private static bool Same(UeRandomStream left, UeRandomStream right)
        => left.InitialSeed == right.InitialSeed && left.Seed == right.Seed
            && left.ConsumedCount == right.ConsumedCount;
}
