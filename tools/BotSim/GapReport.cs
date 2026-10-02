using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

/// <summary>
/// 距离「模拟器可用」还差多少 —— **量化工具**。
///
/// 为什么要它：以前只有 `coverage`，而那个命令数的是「手写效果脚本」的覆盖，
/// 那套方案早就换成 Kismet 解释器了，所以它的数字是**过期的**、
/// 会给出错误的进度感。这里改成直接量真实缺口：
///
/// 1. 卡组用到的卡里，有多少**根本没有蓝图逻辑**（必须手写）
/// 2. 有多少用到了**派发表里还没有的原语**（列出来并排序）
/// 3. 有多少含 **`ctx` 限定读取**（已知 VM 没实现 → 阵营守卫恒真）
/// 4. 有多少是 `_bal` / `_vet` 数据变体（数值还没解出来）
///
/// 用法：<c>BotSim gaps</c>
/// </summary>
internal static class GapReport
{
    public static int Run(CardDatabase db)
    {
        var lib = KismetLibrary.Default;
        if (lib is null)
        {
            Console.Error.WriteLine("蓝图 IR 未加载，无法量化缺口。");
            return 1;
        }

        var api = new KLink.Bot.Effects.CardApi(
            new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 0));
        var implemented = new HashSet<string>(api.ImplementedNames, StringComparer.Ordinal);

        // ---- 卡组 → 唯一卡 ----
        var needed = new SortedSet<string>(StringComparer.Ordinal);
        int deckCount = 0;
        foreach (var (_, code) in MetaDecks.All)
        {
            deckCount++;
            try
            {
                var parsed = DeckCodeParser.Parse(code);
                foreach (string name in DeckCodeParser.Expand(parsed, db.DeckCodeIds, out _))
                {
                    needed.Add(name);
                }
            }
            catch (Exception)
            {
                // 卡组码解析失败不该让整份报告挂掉
            }
        }

        int noProgram = 0, hasCtx = 0, variants = 0, notInDb = 0, clean = 0, hasMissing = 0;
        var missingCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        var noProgramCards = new List<string>();
        var variantCards = new List<string>();
        var dirtyCards = new List<string>();
        int ctxSteps = 0, totalSteps = 0;

        foreach (string name in needed)
        {
            var def = db.Find(name);
            if (def is null)
            {
                notInDb++;
                continue;
            }

            if (CardDatabase.ResolveBaseName(name) != name)
            {
                variants++;
                variantCards.Add(name);
            }

            string resolved = lib.ResolveCardName(def.Name) ?? def.Name;
            var card = lib.Find(resolved);
            if (card is null)
            {
                noProgram++;
                noProgramCards.Add(name);
                continue;
            }

            bool cardHasCtx = false, cardHasMissing = false;
            foreach (var step in card.Steps)
            {
                totalSteps++;

                if (step.Function is { Length: > 0 } fn
                    && !fn.StartsWith("ExecuteUbergraph", StringComparison.Ordinal)
                    && !implemented.Contains(fn)
                    && !fn.Contains('.'))          // math:Conv_StringToInt 之类单独统计
                {
                    missingCalls[fn] = missingCalls.GetValueOrDefault(fn) + 1;
                    cardHasMissing = true;
                }

                if (HasContext(step.Source) || HasContext(step.Condition)
                    || HasContext(step.Receiver) || step.Args.Any(HasContext))
                {
                    ctxSteps++;
                    cardHasCtx = true;
                }
            }

            if (cardHasCtx)
            {
                hasCtx++;
            }

            if (cardHasMissing)
            {
                hasMissing++;
            }

            if (!cardHasCtx && !cardHasMissing
                && CardDatabase.ResolveBaseName(name) == name)
            {
                clean++;
            }
            else
            {
                dirtyCards.Add(name);
            }
        }

        Console.WriteLine($"卡组数:            {deckCount}");
        Console.WriteLine($"用到的唯一卡:      {needed.Count}");
        Console.WriteLine($"  ⚠ 不在卡库:      {notInDb}");
        Console.WriteLine($"  ⚠ 没有蓝图逻辑:  {noProgram}   ← 必须手写，解释器救不了");
        Console.WriteLine($"  ⚠ 含 ctx 限定读取:{hasCtx}   ← VM 只解析没用，阵营/目标守卫恒真");
        Console.WriteLine($"  ⚠ 引用未实现原语:{hasMissing}");
        Console.WriteLine($"  ⚠ _bal/_vet 变体:{variants}   ← 数值待 BalancedCards 解出");
        Console.WriteLine();
        Console.WriteLine($"✅ 当前可完全信任:  {clean} / {needed.Count}  " +
                          $"({clean / (double)Math.Max(1, needed.Count):P1})");
        Console.WriteLine($"⚠️ 至少有一处可疑:  {dirtyCards.Count} / {needed.Count}  " +
                          $"({dirtyCards.Count / (double)Math.Max(1, needed.Count):P1})");
        Console.WriteLine();
        Console.WriteLine($"蓝图步数:          {totalSteps}");
        Console.WriteLine($"其中含 ctx 的步:   {ctxSteps}  ({ctxSteps / (double)Math.Max(1, totalSteps):P1})");
        Console.WriteLine();

        Console.WriteLine($"还缺的原语（共 {missingCalls.Count} 个，按被引用次数）:");
        if (missingCalls.Count == 0)
        {
            Console.WriteLine("  （无）");
        }
        else
        {
            foreach (var kv in missingCalls.OrderByDescending(k => k.Value).Take(30))
            {
                Console.WriteLine($"  {kv.Key,-42} 被 {kv.Value} 个步引用");
            }

            if (missingCalls.Count > 30)
            {
                Console.WriteLine($"  …还有 {missingCalls.Count - 30} 个");
            }
        }

        if (noProgramCards.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"没有蓝图逻辑的卡（{noProgramCards.Count} 张）:");
            foreach (string n in noProgramCards.Take(20))
            {
                Console.WriteLine($"  {n}");
            }

            if (noProgramCards.Count > 20)
            {
                Console.WriteLine($"  …还有 {noProgramCards.Count - 20} 张");
            }
        }

        return 0;
    }

    /// <summary>该表达式树里是否出现 `ctx` 限定读取（含嵌套）。</summary>
    private static bool HasContext(KismetExpr? e)
    {
        if (e is null)
        {
            return false;
        }

        if (e.Context is not null)
        {
            return true;
        }

        foreach (var a in e.Args)
        {
            if (HasContext(a))
            {
                return true;
            }
        }

        foreach (var a in e.Array)
        {
            if (HasContext(a))
            {
                return true;
            }
        }

        return false;
    }
}
