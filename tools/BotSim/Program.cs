using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Replay;

namespace KLink.Bot.Sim;

/// <summary>
/// 规则内核的验证运行器。
///
/// 用法：
///   BotSim decks                     列出内置卡组及其解析结果
///   BotSim play [--games N] [--seed S] [--deck-a NAME] [--deck-b NAME] [--verbose]
///   BotSim coverage                  统计：卡组所需调用 vs 内核已实现
///
/// 目标不是「训练」，是**先把内核跑起来并量化缺口**。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var opts = ParseOptions(args.Skip(1));

        try
        {
            string dataDir = FindDataDirectory();
            CardDatabase db = CardDatabase.Load(dataDir);

            // Kismet 解释器的 IR（缺失时内核仍能跑，只是所有卡的效果都会计入未实现）
            string irPath = Path.Combine(dataDir, "card-ir.json");
            if (File.Exists(irPath))
            {
                KLink.Bot.Effects.Blueprint.KismetLibrary.Initialize(irPath);
            }

            Console.WriteLine($"卡牌数据: {db.Count} 张，卡组码映射: {db.DeckCodeIds.Count} 条");
            var lib = KLink.Bot.Effects.Blueprint.KismetLibrary.Default;
            Console.WriteLine(lib is not null
                ? $"蓝图 IR: {lib.CardCount} 张卡可解释"
                : $"蓝图 IR: 未加载（{KLink.Bot.Effects.Blueprint.KismetLibrary.LoadError ?? "文件不存在"}）");
            Console.WriteLine($"手写效果脚本: {CardEffectScripts.Count} 张，触发脚本: {CardEventScripts.Count} 条");
            Console.WriteLine();

            return cmd switch
            {
                "decks" => CmdDecks(db),
                "play" => CmdPlay(db, opts),
                "coverage" => CmdCoverage(db),
                "gaps" => GapReport.Run(db),
                "dispatch-gap" => CmdDispatchGap(db),
                "replay" => CmdReplay(db, opts),
                "selftest" => SelfTest.Run(db),
                // 全卡池蓝图烟雾测试 —— 逐卡跑蓝图，抓异常/未实现原语/步数上限/零变化/非确定性。
                // 为什么是「工具里的一个模式」而不是一次性脚本：内核每次改动都要能重跑（回归价值）。
                "smoke-all-cards" or "smoke" => SmokeAllCards.Run(db, new SmokeAllCards.Options
                {
                    Seed = (ulong)GetInt(opts, "seed", 20261002),
                    Only = opts.GetValueOrDefault("only"),
                    OnlyEntry = opts.GetValueOrDefault("entry"),
                    Limit = GetInt(opts, "limit", int.MaxValue),
                    IncludeNonLive = opts.ContainsKey("include-non-live"),
                    SkipDeterminism = opts.ContainsKey("no-determinism"),
                    Integration = !opts.ContainsKey("no-integration"),
                    TwoPasses = !opts.ContainsKey("no-two-passes"),
                    DebugTrace = opts.ContainsKey("debug-trace"),
                    OutPath = opts.GetValueOrDefault("out") ?? Path.Combine("out", "audit", "smoke-all-cards.tsv"),
                    SummaryPath = opts.GetValueOrDefault("summary") ?? Path.Combine("out", "audit", "smoke-all-cards.txt"),
                    DataDir = dataDir,
                }),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.GetType().Name}: {ex.Message}");
            if (opts.ContainsKey("stack"))
            {
                Console.Error.WriteLine(ex.ToString());
            }
            else
            {
                Console.Error.WriteLine("（加 --stack 查看完整堆栈）");
            }

            return 1;
        }
    }

    // ==================== 命令 ====================

    /// <summary>
    /// 打印「派发表静态缺口」的当前值与**基线常量**。
    ///
    /// 用法：`dotnet run --project tools\BotSim -c Release -- dispatch-gap`
    /// 把打印出来的两个常量填进 <see cref="KLink.Bot.Effects.Blueprint.DispatchGap"/>，守卫自测就会变绿。
    /// 见 <see cref="KLink.Bot.Effects.Blueprint.DispatchGap"/> 的类注释（判据 + 为什么用指纹而不是只比总数）。
    /// </summary>
    private static int CmdDispatchGap(CardDatabase db)
    {
        var gaps = KLink.Bot.Effects.Blueprint.DispatchGap.Compute(db);
        string fp = KLink.Bot.Effects.Blueprint.DispatchGap.Fingerprint(gaps);

        Console.WriteLine("=== 派发表静态缺口（IR 会调用、派发表没有、locals 也兜不住）===");
        Console.WriteLine($"  种类：{gaps.Count}    真缺口调用点：{gaps.Values.Sum()}");
        Console.WriteLine($"  指纹：{fp}");
        Console.WriteLine();
        Console.WriteLine($"  public const int BaselineCount = {gaps.Count};");
        Console.WriteLine($"  public const string BaselineFingerprint = \"{fp}\";");
        Console.WriteLine();
        Console.WriteLine("  ---- 缺口最大的 40 个（真缺口调用点数）----");
        foreach (var (k, v) in gaps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(40))
        {
            Console.WriteLine($"    {v,5}  {k}");
        }

        return 0;
    }

    private static int CmdDecks(CardDatabase db)
    {
        foreach (var (name, code) in MetaDecks.All)
        {
            try
            {
                var parsed = DeckCodeParser.Parse(code);
                var cards = DeckCodeParser.Expand(parsed, db.DeckCodeIds, out var unknown);
                Console.WriteLine($"{name,-14} {parsed.MainCountry}/{parsed.AllyCountry,-8} " +
                                  $"{cards.Count,3} 张 / {parsed.UniqueCards,2} 种" +
                                  (unknown.Count > 0 ? $"   ⚠ 未知码 {unknown.Count}: {string.Join(",", unknown)}" : ""));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-14} [解析失败] {ex.Message}");
            }
        }

        return 0;
    }

    private static int CmdPlay(CardDatabase db, Dictionary<string, string> opts)
    {
        int games = GetInt(opts, "games", 1);
        ulong seed = (ulong)GetInt(opts, "seed", 12345);
        bool verbose = opts.ContainsKey("verbose");
        string deckA = opts.GetValueOrDefault("deck-a", MetaDecks.All[0].Name);
        string deckB = opts.GetValueOrDefault("deck-b", MetaDecks.All[1].Name);

        var leftCards = LoadDeck(db, deckA);
        var rightCards = LoadDeck(db, deckB);

        Console.WriteLine($"对局: {deckA} vs {deckB}   ({leftCards.Count} vs {rightCards.Count} 张)");
        Console.WriteLine($"局数: {games}   种子: {seed}");
        Console.WriteLine();

        int leftWins = 0, rightWins = 0, unfinished = 0, totalTurns = 0;
        long totalPrograms = 0, totalSteps = 0, totalFaults = 0;
        long totalOutOfRange = 0;
        var unsupportedOps = new Dictionary<string, int>(StringComparer.Ordinal);
        var unimplemented = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int g = 0; g < games; g++)
        {
            var engine = new MatchEngine(db, leftCards, rightCards, seed + (ulong)g);
            var botL = new GreedyBot("L");
            var botR = new GreedyBot("R");

            engine.Start();

            int guard = 0;
            while (!engine.State.IsFinished && guard++ < 500)
            {
                var side = engine.State.ActiveSide;
                if (side == Side.Left)
                {
                    botL.PlayTurn(engine, side);
                }
                else
                {
                    botR.PlayTurn(engine, side);
                }
            }

            if (verbose && g == 0)
            {
                foreach (string line in engine.Log.Take(400))
                {
                    Console.WriteLine("  " + line);
                }
                Console.WriteLine();
            }

            totalTurns += engine.State.Turn;
            totalOutOfRange += engine.OutOfRangeAttacksRejected;
            if (!engine.State.IsFinished)
            {
                unfinished++;
            }
            else if (engine.State.Winner == Side.Left)
            {
                leftWins++;
            }
            else
            {
                rightWins++;
            }

            if (g == 0 && !engine.State.IsFinished)
            {
                Console.WriteLine($"⚠ 第 1 局在 {guard} 步 / {engine.State.Turn} 回合内没有分出胜负（可能是死循环）");
            }

            foreach (var (k, v) in engine.UnimplementedCalls)
            {
                unimplemented[k] = unimplemented.GetValueOrDefault(k) + v;
            }

            totalPrograms += engine.Api.Vm.ProgramsExecuted;
            totalSteps += engine.Api.Vm.StepsExecuted;
            totalFaults += engine.Api.Vm.FaultedPrograms;
            foreach (var (k, v) in engine.Api.Vm.UnsupportedOps)
            {
                unsupportedOps[k] = unsupportedOps.GetValueOrDefault(k) + v;
            }
        }

        stopwatch.Stop();

        Console.WriteLine("=========== 结果 ===========");
        Console.WriteLine($"完成: {games} 局，用时 {stopwatch.Elapsed.TotalSeconds:F2}s " +
                          $"({games / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0} 局/秒)");
        Console.WriteLine($"左胜 {leftWins} / 右胜 {rightWins} / 未分胜负 {unfinished}");
        Console.WriteLine($"平均回合数: {(double)totalTurns / Math.Max(1, games):F1}");
        // 哨兵 + 证据：跨前线射程判据（cardsCheckFunctions::CanAttack i=90-99）生效的次数。
        // 旧实现没有这条判据 ⇒ 这里恒 0，而且左方会一路平推（实测 20 局 20:0 / 17.1 回合）。
        Console.WriteLine($"跨前线射程拒绝: {totalOutOfRange:N0} 次（{totalOutOfRange / Math.Max(1, games):N0}/局）");
        Console.WriteLine();
        Console.WriteLine("---- 内核工作量 ----");
        Console.WriteLine($"蓝图程序执行: {totalPrograms:N0} 次（{totalPrograms / Math.Max(1, games):N0}/局）");
        Console.WriteLine($"Kismet 步数:  {totalSteps:N0} 步（{totalSteps / Math.Max(1, games):N0}/局）");
        // 哨兵：只有带候选表缓存的 dll 才会打出非零的命中数（见 CardApi.GetChooseSpawnCards）。
        Console.WriteLine($"候选表缓存:  命中 {CardApi.GcsCacheHits:N0} / 未命中 {CardApi.GcsCacheMisses:N0} "
                          + $"/ 不可缓存 {CardApi.GcsCacheBypassed:N0}");
        Console.WriteLine($"程序异常:     {totalFaults:N0}");
        if (unsupportedOps.Count > 0)
        {
            Console.WriteLine("未支持的指令/情况:");
            foreach (var (k, v) in unsupportedOps.OrderByDescending(kv => kv.Value).Take(12))
            {
                Console.WriteLine($"  {k,-40} ×{v:N0}");
            }
        }

        Console.WriteLine();

        PrintUnimplemented(unimplemented);
        return 0;
    }

    /// <summary>
    /// 读取一份真实对局的线路动作（docs/live-actions.json），解析并报告。
    ///
    /// 这是「拿真实回放验证内核」的入口。目前日志里只有动作、没有局面，
    /// 所以只能验证**协议层**（动作名、参数含义、卡组码引用）；
    /// 拿到带局面的回放后，在这里接上逐帧状态对拍。
    /// </summary>
    private static int CmdReplay(CardDatabase db, Dictionary<string, string> opts)
    {
        // 两种用法：
        //   replay                  解析线路动作（轻量，看协议）
        //   replay --match <id>     真重放：还原初始状态 → 喂动作流 → 逐步比对 HQ 防御
        if (opts.TryGetValue("match", out string? matchId))
        {
            return CmdReplayMatch(db, matchId, opts);
        }

        string path = opts.GetValueOrDefault("file")
            ?? Path.Combine(AppContext.BaseDirectory, "Data", "live-actions.json");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"找不到回放文件: {path}");
            Console.Error.WriteLine("用 --file <路径> 指定，或把 klink bot/docs/live-actions.json 放到 Data/ 下。");
            return 1;
        }

        var replay = WireReplay.Load(path);
        Console.WriteLine($"来源: {Path.GetFileName(replay.Source)}");
        Console.WriteLine(replay.Summarize());
        Console.WriteLine();

        Console.WriteLine("---- 动作明细 ----");
        foreach (var a in replay.Actions)
        {
            string full = a.FullActionType;
            string known = a.ActionType == full ? "" : $"  → {full}";
            Console.WriteLine($"  [{a.ActionId,3}] {a.ActionType,-22}{known}");

            // 把 action_data 的键翻成语义
            var parts = new List<string>();
            foreach (var (k, v) in a.ActionData.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key))
            {
                string label = k switch
                {
                    Engine.WireAction.KeyIndex.CardId => "卡牌ID",
                    Engine.WireAction.KeyIndex.SecondId => "第二ID",
                    Engine.WireAction.KeyIndex.CodeSlotA => "码槽2",
                    Engine.WireAction.KeyIndex.CodeSlotB => "码槽3",
                    Engine.WireAction.KeyIndex.CodeSlotC => "码槽4(卡组码)",
                    Engine.WireAction.KeyIndex.SideMarker => "阵营标记",
                    "side" => "阵营",
                    "reason" => "原因",
                    _ => $"键{k}",
                };

                // 卡组码能否在权威表里查到 —— 这是判断「它是不是卡组码」的硬证据
                // 注意：只对**真正的卡组码槽位**查表。
                // key `0`/`1` 是 cardID，其取值有些恰好也是合法的 2 字符码，
                // 去查表会得到看似合理但完全无关的卡名（假阳性）。
                bool isCodeSlot = a.CardCodes.Contains(v);
                string resolved = isCodeSlot && db.DeckCodeIds.TryGetValue(v, out string? cardName)
                    ? $"  = {cardName}"
                    : "";

                parts.Add($"{label}={v}{resolved}");
            }

            Console.WriteLine($"        {string.Join("  |  ", parts)}");
        }

        Console.WriteLine();
        Console.WriteLine("---- 协议层核对 ----");
        Console.WriteLine($"  全部动作都带 local_subactions=1: {replay.Actions.All(a => a.LocalSubactions)}");
        Console.WriteLine("  → 效果由客户端本地结算（确定性锁步），与离线推断一致");
        Console.WriteLine();
        Console.WriteLine("⚠️ 这份日志只有动作、没有棋盘状态，所以还无法做逐帧状态对拍。");
        Console.WriteLine("   需要客户端侧的状态导出（见 src/KLink.Bot/README.md 的验收标准）。");
        return 0;
    }

    /// <summary>
    /// 真重放一局真实对局，逐步比对 HQ 防御。
    ///
    /// 这是内核验证的主入口：初始状态完全确定，动作流完全确定，
    /// 而 `action_data["84"]` 携带了行动方的 HQ 当前防御力 ——
    /// 首个对不上的位置就是规则理解第一个出错的地方。
    /// </summary>
    private static int CmdReplayMatch(CardDatabase db, string matchId, Dictionary<string, string> opts)
    {
        string dir = opts.GetValueOrDefault("dir")
            ?? FindReplayDirectory();

        string snap = Path.Combine(dir, $"replay-{matchId}.json");
        string acts = Path.Combine(dir, $"replay-{matchId}.actions.json");

        if (!File.Exists(snap) || !File.Exists(acts))
        {
            Console.Error.WriteLine($"找不到对局 {matchId} 的回放：");
            Console.Error.WriteLine($"  {snap}");
            Console.Error.WriteLine($"  {acts}");
            Console.Error.WriteLine("用 --dir <回放目录> 指定。");
            return 1;
        }

        var replay = ReplayData.Load(snap, acts);

        Console.WriteLine($"对局 {replay.MatchId}  {replay.Turns} 回合  {replay.Actions.Count} 动作  " +
                          $"胜方={replay.WinnerSide}");
        Console.WriteLine($"初始状态：{replay.Cards.Count} 张卡");

        var left = replay.Cards.Where(c => c.Owner == Side.Left).ToList();
        var right = replay.Cards.Where(c => c.Owner == Side.Right).ToList();
        Console.WriteLine($"  左（{replay.LeftPlayerId}）：手牌 {left.Count(c => c.Location is CardLocation.HandLeft)}" +
                          $"  牌库 {left.Count(c => c.Location is CardLocation.DeckLeft)}");
        Console.WriteLine($"  右（{replay.RightPlayerId}）：手牌 {right.Count(c => c.Location is CardLocation.HandRight)}" +
                          $"  牌库 {right.Count(c => c.Location is CardLocation.DeckRight)}");
        Console.WriteLine();

        bool verbose = !opts.ContainsKey("quiet");
        ReplayRunner.TraceExceptions = opts.ContainsKey("stack");
        var report = new ReplayRunner(db).Run(replay, verbose);

        Console.WriteLine("=========== 重放结果 ===========");
        Console.WriteLine($"动作总数:       {report.TotalActions}");
        Console.WriteLine($"成功应用到内核: {report.AppliedCount}  ({report.AppliedCount / (double)report.TotalActions:P1})");
        Console.WriteLine($"其中重复记录:   {report.Duplicates}（同一 cardID 被连续 PC，按重复丢弃）");
        Console.WriteLine($"注入手牌:       {report.Injected}（快照手牌不可信，见 ReplayRunner 注释）");
        Console.WriteLine($"未理解的动作:   {report.NotUnderstood}");
        Console.WriteLine($"身份自检:       冲突 {report.IdentityConflicts}  未知卡组码 {report.UnknownCodes}");
        Console.WriteLine($"HQ 字段下标:    {report.HqKey ?? "（未识别，本局无 HQ 采样）"}");
        if (report.CheatActions > 0)
        {
            Console.WriteLine($"⚠️ 本局含 {report.CheatActions} 条 XActionCheat —— " +
                              "是开作弊打的测试局，kredit/HQ 对不上属预期，别当规则错误");
        }

        Console.WriteLine();

        Console.WriteLine("---- HQ 防御力逐步比对（最有价值的指标）----");
        Console.WriteLine($"可比对步数: {report.HqChecked}");
        Console.WriteLine($"一致步数:   {report.HqMatched}  " +
                          $"({(report.HqChecked == 0 ? 0 : report.HqMatched / (double)report.HqChecked):P1})");
        Console.WriteLine($"轨迹干净到: 第 {report.HqCleanTurns} 回合（共 {report.LastCheckedTurn} 回合有可比对采样）");

        if (report.FirstMismatch is { } fm)
        {
            Console.WriteLine();
            Console.WriteLine("⚠️ 首次不一致：");
            Console.WriteLine($"    action {fm.ActionId}  第 {fm.Turn} 回合  {fm.PlayerSide} 方  {fm.ActionType}");
            Console.WriteLine($"    对手 HQ 期望 {fm.ExpectedHq}，内核算出 {fm.ActualHq}（差 {fm.ActualHq - fm.ExpectedHq}）");
            Console.WriteLine("    → 这个位置就是规则理解第一个出错的地方，优先查它");
        }
        else if (report.HqChecked > 0)
        {
            Console.WriteLine();
            Console.WriteLine("✅ 全程 HQ 防御力一致 —— 攻击结算/伤害数值/摧毁判定与客户端吻合");
        }

        if (report.NotUnderstood > 0)
        {
            Console.WriteLine();
            Console.WriteLine("---- 未理解的动作类型 ----");
            foreach (var g in report.NotUnderstoodByType.OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"  {g.Key,-24} ×{g.Count()}   例: {g.First().Failure}");
            }
        }

        if (report.Unimplemented.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("---- 撞到的未实现原语（按次数）----");
            foreach (var kv in report.Unimplemented.Take(25))
            {
                Console.WriteLine($"  {kv.Key,-42} ×{kv.Value}");
            }

            if (report.Unimplemented.Count > 25)
            {
                Console.WriteLine($"  …还有 {report.Unimplemented.Count - 25} 个");
            }
        }

        return 0;
    }

    private static string FindReplayDirectory()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "live-replays"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "klink bot", "docs", "live-replays"),
        };

        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return Path.GetFullPath(candidates[^1]);
    }

    private static int CmdCoverage(CardDatabase db)
    {
        // 目标卡组需要哪些调用
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var cardsNeeded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, code) in MetaDecks.All)
        {
            var parsed = DeckCodeParser.Parse(code);
            var cards = DeckCodeParser.Expand(parsed, db.DeckCodeIds, out _);
            foreach (string c in cards)
            {
                cardsNeeded.Add(c);
                var def = db.Find(c);
                if (def is null)
                {
                    continue;
                }

                foreach (string call in def.ExternalCalls)
                {
                    needed.Add(call);
                }
            }
        }

        var implemented = new HashSet<string>(CardEffectScripts.ImplementedCards, StringComparer.Ordinal);

        Console.WriteLine($"目标卡组用到的唯一卡: {cardsNeeded.Count} 张");
        Console.WriteLine($"这些卡需要的调用:    {needed.Count} 个");
        Console.WriteLine($"已写效果脚本的卡:    {implemented.Count} 张");
        Console.WriteLine();

        var missing = cardsNeeded.Where(c => !implemented.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Console.WriteLine($"还没有效果脚本的卡: {missing.Count} 张");
        Console.WriteLine();
        Console.WriteLine("（效果脚本是编排层，需要一张一张写；下面的清单就是待办）");
        Console.WriteLine();
        foreach (string c in missing.Take(60))
        {
            var def = db.Find(c);
            Console.WriteLine($"  {c,-46} {def?.Kredits,2}费 {def?.Type,-10} {Truncate(def?.Text, 60)}");
        }

        if (missing.Count > 60)
        {
            Console.WriteLine($"  … 另有 {missing.Count - 60} 张");
        }

        return 0;
    }

    // ==================== 辅助 ====================

    private static void PrintUnimplemented(Dictionary<string, int> stats)
    {
        if (stats.Count == 0)
        {
            Console.WriteLine("未实现的调用: 无");
            return;
        }

        Console.WriteLine($"===== 内核缺口（未实现的调用）共 {stats.Count} 种 =====");
        Console.WriteLine("这是衡量进度的核心指标：数字越小，能跑对的卡越多。");
        Console.WriteLine();

        var cardLevel = stats.Where(kv => kv.Key.StartsWith("<card:", StringComparison.Ordinal)).ToList();
        var other = stats.Where(kv => !kv.Key.StartsWith("<card:", StringComparison.Ordinal)).ToList();

        if (other.Count > 0)
        {
            Console.WriteLine("触发式效果:");
            foreach (var (k, v) in other.OrderByDescending(kv => kv.Value).Take(30))
            {
                Console.WriteLine($"  {k,-60} ×{v}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"没有效果脚本的卡: {cardLevel.Count} 种（共触发 {cardLevel.Sum(kv => kv.Value)} 次）");
        foreach (var (k, v) in cardLevel.OrderByDescending(kv => kv.Value).Take(30))
        {
            Console.WriteLine($"  {k,-60} ×{v}");
        }
    }

    private static List<string> LoadDeck(CardDatabase db, string deckName)
    {
        var entry = MetaDecks.All.FirstOrDefault(d => d.Name == deckName);
        if (entry.Code is null)
        {
            throw new ArgumentException($"内置卡组里没有 '{deckName}'。可用: {string.Join(", ", MetaDecks.All.Select(d => d.Name))}");
        }

        var parsed = DeckCodeParser.Parse(entry.Code);
        var cards = DeckCodeParser.Expand(parsed, db.DeckCodeIds, out var unknown);
        if (unknown.Count > 0)
        {
            Console.WriteLine($"  注意: {deckName} 有 {unknown.Count} 个卡组码找不到卡名: {string.Join(",", unknown)}");
        }

        return cards;
    }

    private static string FindDataDirectory()
    {
        // 优先输出目录旁的 Data\，其次回退到 klink bot\docs\
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Data"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "klink bot", "docs"),
        };

        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(Path.Combine(full, "card-effects.json")))
            {
                // cards-from-fmodel.json 在别处，拷一份到同目录
                string fmodel = Path.Combine(full, "cards-from-fmodel.json");
                if (!File.Exists(fmodel))
                {
                    string? alt = FindUpwards("cards-from-fmodel.json");
                    if (alt is not null)
                    {
                        File.Copy(alt, fmodel, overwrite: false);
                    }
                }

                return full;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 card-effects.json。请先运行 klink bot/tools/gen-card-effects.py，或检查 csproj 的 CopyToOutputDirectory。");
    }

    private static string? FindUpwards(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tem", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        foreach (string a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    result[pending] = "true";
                }

                pending = a[2..];
            }
            else if (pending is not null)
            {
                result[pending] = a;
                pending = null;
            }
        }

        if (pending is not null)
        {
            result[pending] = "true";
        }

        return result;
    }

    private static int GetInt(Dictionary<string, string> opts, string key, int fallback)
        => opts.TryGetValue(key, out string? v) && int.TryParse(v, out int i) ? i : fallback;

    private static string Truncate(string? s, int n)
        => s is null ? "" : s.Length <= n ? s : s[..n] + "…";

    private static int Help()
    {
        Console.WriteLine("""
            BotSim —— KARDS 规则内核验证运行器

              BotSim decks                    列出内置卡组及解析结果
              BotSim play [选项]              跑对局
                  --games N                   局数（默认 1）
                  --seed S                    随机种子（默认 12345）
                  --deck-a NAME --deck-b NAME 选择卡组
                  --verbose                   打印第一局的日志
              BotSim coverage                 统计内核还缺哪些卡的效果

              BotSim smoke-all-cards [选项]     ★ 全卡池蓝图烟雾测试（逐卡跑蓝图）
                  --seed S                    播种值 = 假 match_id（默认 20261002）
                  --only 子串                 只跑卡名含该子串的卡
                  --entry 程序名              只跑这一个入口点
                  --limit N                   只跑前 N 张卡（冒烟）
                  --include-non-live          连 UI/动画入口也跑（默认只跑引擎会派发的）
                  --no-determinism            跳过「同种子跑两次」的确定性验证
                  --no-integration            跳过 PlayCard 编排路径那一节
                  --no-two-passes             跳过第二趟（跨用例污染检查）
                  --debug-trace               打开 VM 逐语句 trace（拿到被吞掉的异常消息）
                  --out 路径 / --summary 路径  TSV / 摘要落盘位置
                  （默认写到 out\audit\smoke-all-cards.tsv / .txt）

            数据来源：klink bot/docs/card-effects.json（反编译产物）
            """);
        return 0;
    }
}


