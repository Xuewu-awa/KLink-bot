using System.Text;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

/// <summary>
/// **全卡池蓝图烟雾测试** —— 对 <c>card-ir.json</c> 里每一张有 <c>steps</c> 的卡、
/// 每一个「引擎真的会派发的入口点」跑一次它自己的程序，捕获五类问题。
///
/// ## 为什么要有它（这是这个工具存在的唯一理由）
///
/// 回放对拍是**唯一**的正确性判据，但它的**覆盖率极低**：
/// `klink bot/docs/fresh-replays/` 里那 7 局回放实测只打出过 **43 张卡**，
/// 而卡池有 2021 张。于是绝大多数修复**根本无法用回放验证** ——
/// 有子代理拿 before/after 的审计文本 sha256 逐份相同，证明了「零覆盖」。
///
/// 这个工具补的是**另一半**：不判对错，只判「跑不跑得动」——
/// 逐卡把蓝图程序跑一遍，看它是抛异常、撞未实现原语、撞步数上限、
/// 还是跑完了一点状态都没变。**2000 张一次跑完**。
///
/// ## ⚠️ 它是「覆盖/烟雾测试」，不是「正确性判据」
///
/// **能抓**：CLR/VM 异常、撞到未实现原语、撞步数上限、程序跳转没解析、
/// 非确定性（同种子两次结果不同）、「该有效果却零状态变化」。
/// **抓不到**：语义错（跑得通但算错 —— 重甲该不该减免、某效果该扣多少血、
/// 条件门读错对象）、数值/顺序错（那需要跟客户端对拍）。
/// ⇒ **「没报错」不等于「是对的」。** 报告里必须带这句。
///
/// ## 每个用例都用一个全新的引擎
///
/// `MatchEngine` + 固定 <c>match_id</c> 播种（<see cref="GameState.Random"/> 是
/// `UeRandomStream`，播种值就是 match_id）⇒ **可复现**，而且随机游标从 0 开始，
/// 「这张卡消耗了几次随机数」是一个干净的数字。
///
/// ## 设计取舍（为什么这么做）
///
/// 1. **直接跑卡自己的程序**（<see cref="KismetVm.Run"/> / <see cref="CardApi.RunCardEffect"/>），
///    而不是走 <c>MatchEngine.PlayCard</c>：状态变化 100% 归因于这张卡，
///    不会被场上其他订阅卡的触发污染（D 类判据才站得住）。
///    引擎编排层（PlayCard/部署/时序）由 `--smoke-integration` 那一节单独覆盖。
/// 2. **只跑「活入口点」**（<see cref="LiveEntrypoints"/>）：IR 的 `entrypoints`
///    里有 845 个是 UI/动画/时间轴回调（`Timeline__FinishedFunc` / `OnActorMouseExit` /
///    `BndEvt__…__DelegateSignature`），引擎**永远不会**派发它们。
///    把它们算进「未实现原语」会把真信号淹掉。
/// 3. **非退化局面**：双方 HQ + 5 个兵种（infantry/tank/artillery/fighter/bomber）
///    × 多阵营 + 手牌 + 牌库 + 满 kredit + 前线归属。
///    目的是让「随机选卡」的候选池非空 —— 否则会误判成「什么都没做」。
/// </summary>
internal static class SmokeAllCards
{
    // ==================================================================
    //  活入口点 —— 引擎**真的会派发**的程序名
    // ==================================================================

    /// <summary>
    /// 引擎源码里出现过的程序名字面量 —— 也就是「内核有可能派发到的入口」。
    ///
    /// 出处（可复算）：
    /// <code>
    /// Get-ChildItem src\KLink.Bot -Recurse -Filter *.cs |
    ///   Select-String -Pattern '"On[A-Za-z_0-9]+"' |
    ///   % { $_.Matches.Value.Trim('"') } | Sort-Object -Unique
    /// </code>
    /// 得到 66 个字面量，去掉 <c>"OnOther"</c>（那是 <c>FireTrigger</c> 里的前缀常量，
    /// 不是程序名）⇒ 下面这 65 个。
    ///
    /// ⚠️ 为什么不干脆「IR 里注册了就都算」：`entrypoints` 里 845 条是 UI 回调
    /// （`Timeline__UpdateFunc` / `OnActorClicked` / `FlyTo` …），
    /// 它们永远不会被派发，跑出来的「未实现原语」是纯噪声。
    /// 为什么不干脆「只算 event-contracts.json 里的」：那份有 172 条，
    /// 也含 `OnActorClicked` / `BndEvt__…` 这类 UI 名，同样不干净。
    /// 而「引擎源码里出现过」是**可复算、可证伪**的判据。
    /// </summary>
    private static readonly HashSet<string> LiveEntrypoints = new(StringComparer.Ordinal)
    {
        "OnAfterAttack",
        "OnAfterChangeAttack",
        "OnAfterDefenseIsSet",
        "OnAfterExtraKreditSlotGain",
        "OnAfterGainDefense",
        "OnAfterLeaveBoard",
        "OnAfterOtherCardAttacks",
        "OnAfterOtherCardChangeAttack",
        "OnAfterOtherCardDefenseIsSet",
        "OnAfterOtherCardGainDefense",
        "OnAfterOtherCardLeaveBoardOrOwner",
        "OnAfterOtherCardSuppressed",
        "OnBecomingVeteran",
        "OnBeforeAttack",
        "OnBeforeDestroyed",
        "OnBeforeOtherCardAttacks",
        "OnBeforeOtherCardDeploymentTrigger",
        "OnBeforeOtherCardDestroyed",
        "OnBeforeOtherCardPlayedFromHand",
        "OnBeforeStartOfTurn",
        "OnCardDealDamage",
        "OnCardDealDamage_ModifyDamageDealt",
        "OnCardDrawnFromDeck",
        "OnCardLocationMoved",
        "OnCardReset",
        "OnCardSpawnedInHand",
        "OnCreateCard",
        "OnDeploymentEffectTriggered",
        "OnDestroyed",
        "OnDestructionEffectTriggered",
        "OnEndOfTurn",
        "OnEnterPlay",
        "OnFrontlineOwnershipChange",
        "OnFullyRepaired",
        "OnHandTargetSelected",
        "OnLeaveBoardOrOwner",
        "OnMoveToFrontline",
        "OnOtherCardAbilitiesChanged",
        "OnOtherCardBecomingVeteran",
        "OnOtherCardDealDamage",
        "OnOtherCardDealDamageAddDamage",
        "OnOtherCardDestroyed",
        "OnOtherCardDiscarded",
        "OnOtherCardDrawnFromDeck",
        "OnOtherCardEnterPlay",
        "OnOtherCardFullyRepaired",
        "OnOtherCardLeaveBoardOrOwner",
        "OnOtherCardLocationMoved",
        "OnOtherCardMoveToFrontline",
        "OnOtherCardPlayedFromHand",
        "OnOtherCardReceiveDamage",
        "OnOtherCardReset",
        "OnOtherCardSpawnedInHand",
        "OnOtherCardSuppressed",
        "OnOtherCardSurvivedCombat",
        "OnOtherEndOfTurn",
        "OnOtherFrontlineOwnershipChange",
        "OnOtherStartOfTurn",
        "OnPlayedFromHand",
        "OnReceiveDamage",
        "OnStartOfGame",
        "OnStartOfTurn",
        "OnSuppressed",
        "OnSurvivedCombat",
    };

    // ==================================================================
    //  选项与入口
    // ==================================================================

    public sealed class Options
    {
        /// <summary>播种值 = 假 match_id（默认 20261002，取自最后一次改动日期，纯为了好认）。</summary>
        public ulong Seed { get; init; } = 20261002;

        /// <summary>只跑某一张卡（调试用）。</summary>
        public string? Only { get; init; }

        /// <summary>只跑某一个入口点（调试用）。</summary>
        public string? OnlyEntry { get; init; }

        /// <summary>只跑前 N 张卡（冒烟用）。</summary>
        public int Limit { get; init; } = int.MaxValue;

        /// <summary>连 UI/动画入口也跑（默认关，见 <see cref="LiveEntrypoints"/>）。</summary>
        public bool IncludeNonLive { get; init; }

        /// <summary>跳过「同种子跑两次」的确定性验证（快一半，但少一节结论）。</summary>
        public bool SkipDeterminism { get; init; }

        /// <summary>额外跑一节「真实编排路径」（<c>PlayCard</c>），默认开。</summary>
        public bool Integration { get; init; } = true;

        /// <summary>跑第二趟，检查进程级可变状态的跨用例污染（默认开）。</summary>
        public bool TwoPasses { get; init; } = true;

        /// <summary>TSV 落盘路径。</summary>
        public string OutPath { get; init; } = Path.Combine("out", "audit", "smoke-all-cards.tsv");

        /// <summary>文本摘要落盘路径。</summary>
        public string SummaryPath { get; init; } = Path.Combine("out", "audit", "smoke-all-cards.txt");

        /// <summary>数据目录（`card-ir.json` / `event-contracts.json` 所在处）。</summary>
        public string DataDir { get; init; } = ".";

        /// <summary>诊断：打开 VM 的逐语句 trace（会拿到被吞掉的异常消息）。</summary>
        public bool DebugTrace { get; init; }
    }

    /// <summary><see cref="Options.DebugTrace"/> 的进程内开关（<see cref="Execute"/> 拿不到 Options）。</summary>
    private static bool _debugTrace;

    /// <summary>VM 逐语句 trace（只在 <see cref="Options.DebugTrace"/> 打开时收集）。</summary>
    private static readonly List<string> _stepTrace = new();

    public static int Run(CardDatabase db, Options opt)
    {
        KismetLibrary? lib = KismetLibrary.Default;
        if (lib is null)
        {
            Console.Error.WriteLine("IR 没加载（card-ir.json 缺失？）—— 烟雾测试跑不了。");
            return 2;
        }

        // 事件契约（`docs/event-contracts.json`）：给事件入参填**按槽位名**的合理值。
        // 没有它的话，程序读 `K2Node_Event_*` 全是 null，绝大多数卡会在第一个
        // `IsValid(targetCard)` 守卫上退出 —— 那会把测试台本身变成噪声源。
        var contracts = LoadContracts(opt.DataDir);
        _debugTrace = opt.DebugTrace;

        // ⚠️ 只跑**真卡**。`card-ir.json` 里除了 1610 张卡，还有 26 个非卡牌蓝图
        //    （`BP_BaseCard` / `BP_CardFunctions` / `WBP_DebugRenderer` …）——
        //    它们在 `CardDatabase` 里没有定义，造局面时 `CreateWithId` 会抛，
        //    跑出来的「异常」全是测试台自己的问题，不是内核的。所以按名字过滤掉。
        int skippedNonCards = 0;
        var cardNames = lib.AllCards
            .Where(kv => kv.Value.Steps.Count > 0)
            .Select(kv => kv.Key)
            .Where(n =>
            {
                if (db.Find(n) is not null)
                {
                    return true;
                }

                skippedNonCards++;
                return false;
            })
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (opt.Only is { } only)
        {
            cardNames = cardNames.Where(n => n.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (opt.Limit < cardNames.Count)
        {
            cardNames = cardNames.Take(opt.Limit).ToList();
        }

        Console.WriteLine($"=== 全卡池蓝图烟雾测试 ===");
        Console.WriteLine($"  IR 里有 steps 的真卡：{cardNames.Count}" +
                          (skippedNonCards > 0 ? $"（另有 {skippedNonCards} 个非卡牌蓝图 BP_*/WBP_* 已排除）" : ""));
        Console.WriteLine($"  播种（假 match_id）：{opt.Seed}");
        Console.WriteLine($"  入口点口径：{(opt.IncludeNonLive ? "全部（含 UI/动画噪声）" : "只跑引擎会派发的活入口点")}");
        Console.WriteLine($"  确定性验证：{(opt.SkipDeterminism ? "关" : "同种子跑两次、逐位比对")}");
        Console.WriteLine($"  事件契约：{contracts.Count} 个事件有槽位表");
        Console.WriteLine();

        // ---- 先把用例表算出来 ----
        //
        // 摆位：单位放场上（半场，`IsLocatedOnBoard` 才成立）；
        // 指令/位置/搞头卡放**手牌**和**弃牌堆**各跑一次 ——
        // 手牌是「在手时生效」那一族，弃牌堆是「打出后仍订阅事件」那一族
        // （见 `CardApi.FireTrigger` 的快照只含棋盘 + 弃牌堆那段注释）。
        var plan = new List<(string Card, string Entry, string Placement)>();
        foreach (string cardName in cardNames)
        {
            if (lib.Find(cardName) is not { } card || card.Steps.Count == 0)
            {
                continue;
            }

            bool isUnit = db.Find(cardName)?.IsUnit ?? false;
            foreach (var (entry, _) in card.Entrypoints.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (opt.OnlyEntry is { } oe && !string.Equals(entry, oe, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!LiveEntrypoints.Contains(entry) && !opt.IncludeNonLive)
                {
                    continue;
                }

                foreach (string placement in isUnit ? new[] { "board" } : new[] { "hand", "discard" })
                {
                    plan.Add((cardName, entry, placement));
                }
            }
        }

        Console.WriteLine($"  用例数（卡 × 入口 × 摆位）：{plan.Count}");
        Console.WriteLine();

        var cases = new List<CaseResult>(plan.Count);
        var coldFingerprints = new string[plan.Count];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < plan.Count; i++)
        {
            var (cardName, entry, placement) = plan[i];

            // ⚠️ **冷启动先跑一次并丢掉**。为什么必须这样：
            //    `CardApiDispatch` 里有一条**进程级**静态缓存 `_gcsCache`
            //    （`GetChooseSpawnCards` 的候选表）。首次执行会真跑一遍扫全池的局部函数，
            //    之后命中缓存 ⇒ **同一个用例第一次和第二次的 VM 步数可以差一万倍**
            //    （实测 `card_event_a_few_good_men`：30331 步 → 3 步）。
            //    不预热的话，「同种子跑两次」测出来的差异里混着这条缓存的影响，
            //    会把「缓存命中」误报成「内核非确定」。
            //    ⚠️ 反过来，冷/热两次的**状态指纹**必须相同 —— 这是缓存透明性的判据，
            //       下面单独一节报出来（`ColdFingerprint != Fingerprint` 就是缓存漏了状态）。
            CaseResult cold = Execute(db, lib, contracts, cardName, entry, placement, opt.Seed);
            coldFingerprints[i] = cold.Fingerprint;

            CaseResult r1 = Execute(db, lib, contracts, cardName, entry, placement, opt.Seed);
            CaseResult r2 = opt.SkipDeterminism
                ? r1
                : Execute(db, lib, contracts, cardName, entry, placement, opt.Seed);

            r1 = r1 with
            {
                Deterministic = r2.Fingerprint == r1.Fingerprint
                                && r2.Rng == r1.Rng
                                && r2.Steps == r1.Steps
                                && string.Join(",", r2.NewUnimpl) == string.Join(",", r1.NewUnimpl),
                SecondFingerprint = r2.Fingerprint,
                SecondRng = r2.Rng,
                SecondNewUnimpl = string.Join(",", r2.NewUnimpl),
                SecondSteps = r2.Steps,
                ColdFingerprint = cold.Fingerprint,
                ColdSteps = cold.Steps,
                ColdRng = cold.Rng,
            };

            // 发现不一致才补算结构化差异 —— 非确定是**罕见**事件，
            // 平时不做逐卡摘要是为了不给热路径加分配。
            if (!r1.Deterministic)
            {
                r1 = r1 with
                {
                    Diff = CharDiff(r1.Fingerprint, r1.SecondFingerprint)
                           + $"  ‖ 未实现 ①[{string.Join(",", r1.NewUnimpl)}] ②[{r1.SecondNewUnimpl}]"
                           + $"  ‖ 步数 {r1.Steps}/{r1.SecondSteps} rng {r1.Rng}/{r1.SecondRng}"
                           + "  ‖  " + DiagnoseNondeterminism(db, lib, contracts, cardName, entry, placement, opt.Seed),
                };
            }

            cases.Add(r1);
        }

        // ---- 第二趟：查「进程级可变状态」的跨用例污染 ----
        //
        // 为什么要有它：内核里有**进程级**的静态卡池模板（`CardApiDispatch._staticPool`），
        // 而 `GetChooseSpawnCards` 的候选表元素**就是这些模板实例的引用**。
        // 只要有任何一个效果改了候选卡，改的就是**全局模板** ——
        // 后面所有用例、所有对局看到的卡池都被污染了。锁步下这是致命的
        // （同一进程里跑的第二局与第一局会不一致）。
        // 判据：同一用例在**第一趟**（冷）和**第二趟**（冷）的状态指纹必须逐位相同。
        var contamination = new List<string>();
        if (opt.TwoPasses && plan.Count > 0)
        {
            Console.WriteLine("  第二趟（跨用例污染检查）…");
            for (int i = 0; i < plan.Count; i++)
            {
                var (cardName, entry, placement) = plan[i];
                CaseResult again = Execute(db, lib, contracts, cardName, entry, placement, opt.Seed);
                if (again.Fingerprint != coldFingerprints[i])
                {
                    contamination.Add($"{cardName} / {entry} / {placement}");
                }
            }
        }

        sw.Stop();
        Console.WriteLine($"跑了 {cases.Count} 个用例（{plan.Select(p => p.Card).Distinct().Count()} 张卡），" +
                          $"耗时 {sw.Elapsed.TotalSeconds:N1}s");
        Console.WriteLine();

        // ---- 事后分析：哪些原语「被调过而且确实改了状态」 ----
        //
        // 这一条是 D 类（零变化）能不能判读的关键：
        // 如果一张卡跑完什么都没变、而且它**一条改动过状态的原语都没调**，
        // 那更可能是「守卫不满足」（测试台局面不满足条件），
        // 而不是「该有效果却没实现」。判据用**观测数据**而不是我手写的动词表。
        var mutating = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (!c.Changed)
            {
                continue;
            }

            foreach (string p in c.Called)
            {
                mutating.Add(p);
            }
        }

        // ⚠️ 光用 `mutating` 判 D2 会**大面积误报**：`Array_Get` / `GetAllCards` /
        //    `IsUnit` 这些纯读原语也出现在「改了状态」的用例里（因为那些用例同时还调了
        //    真正的写原语）。第一版就是这么错的 —— 1279 个用例被判成「调了写原语却零变化」，
        //    里面绝大多数其实是「在卡池里扫了一遍、条件不满足」。
        //    所以再叠一层**名字判据**：名字是查询/纯函数形状的（Get/Is/Has/Array_/Conv_…）
        //    一律不算写原语。两层都过才算 —— 宁可漏报，不要误报。
        var writeCapable = new HashSet<string>(
            mutating.Where(p => !IsPurePrimitiveName(p)), StringComparer.Ordinal);

        foreach (var c in cases)
        {
            c.Kind = Classify(c);
        }

        // ---- 落盘 + 打印 ----
        WriteTsv(cases, opt.OutPath);
        string summary = BuildSummary(cases, opt, mutating, writeCapable);

        if (opt.Integration)
        {
            var integration = RunIntegration(db, lib, cardNames, opt);
            summary += BuildIntegrationSummary(integration);
        }

        summary += BuildCacheAndContaminationSummary(cases, contamination, opt);
        summary += BuildEntrypointCoverageSummary(cases);

        WriteText(summary, opt.SummaryPath);

        Console.Write(summary);
        Console.WriteLine();
        Console.WriteLine($"TSV   → {Path.GetFullPath(opt.OutPath)}");
        Console.WriteLine($"摘要  → {Path.GetFullPath(opt.SummaryPath)}");

        // 退出码：只有「真问题」才非 0（异常 / 非确定性），方便脚本化。
        return cases.Any(c => c.Kind == "A" || !c.Deterministic) ? 1 : 0;
    }

    // ==================================================================
    //  单个用例
    // ==================================================================

    private sealed record CaseResult(
        string Card,
        string Entry,
        string Placement,
        int Steps,
        long Rng,
        bool Changed,
        string Fingerprint,
        int UnresolvedJumps,
        string? ClrException,
        string? VmFault,
        IReadOnlyList<string> NewUnimpl,
        IReadOnlyList<string> Synthetic,
        IReadOnlyList<string> Called,
        IReadOnlyList<string> NewActions)
    {
        /// <summary>分类：A 异常 / B 未实现原语 / C 步数上限 / D 零变化 / OK。</summary>
        public string Kind { get; set; } = "OK";

        public bool Deterministic { get; init; } = true;
        public string SecondFingerprint { get; init; } = "";
        public long SecondRng { get; init; }
        public string SecondNewUnimpl { get; init; } = "";
        public int SecondSteps { get; init; }

        /// <summary>冷启动（本用例在该进程里的第一次执行）的状态指纹 / 步数 / 随机消费。</summary>
        public string ColdFingerprint { get; init; } = "";
        public int ColdSteps { get; init; }
        public long ColdRng { get; init; }

        /// <summary>非确定用例的结构化差异（只在发现不一致时补算）。</summary>
        public string Diff { get; init; } = "";

        /// <summary>逐卡摘要（只在需要 diff 时捕获，避免热路径上多分配）。</summary>
        public IReadOnlyList<string> Digest { get; init; } = Array.Empty<string>();
    }

    private static CaseResult Execute(CardDatabase db, KismetLibrary lib,
                                      IReadOnlyDictionary<string, List<SlotSpec>> contracts,
                                      string cardName, string entry, string placement, ulong seed,
                                      bool captureDigest = false)
    {
        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed);
        var state = engine.State;

        CardInstance subject;
        CardInstance target;
        try
        {
            (subject, target) = BuildWorld(db, state, cardName, placement);
        }
        catch (Exception ex)
        {
            return new CaseResult(cardName, entry, placement, 0, 0, false, "setup-failed", 0,
                $"{ex.GetType().Name}: {ex.Message}", null,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }

        string before = FingerprintOf(state);
        long rng0 = state.Random.ConsumedCount;
        var unimpl0 = new Dictionary<string, int>(state.UnimplementedCalls, StringComparer.Ordinal);
        int actions0 = state.ActionLog.Count;

        KismetVm vm = engine.Api.Vm;
        vm.Trace = new List<string>();
        if (_debugTrace)
        {
            // ⚠️ `RunCore` 把异常**吞掉了**（只记类型名，消息和堆栈都丢了）。
            //    诊断崩溃时必须把 StepTrace 打开 —— 它里面存了 `[!!] 未捕获异常 类型: 消息`。
            vm.StepTrace = _stepTrace;
            _stepTrace.Clear();
        }
        int steps0 = vm.StepsExecuted;
        int jumps0 = vm.UnresolvedJumps;
        int faults0 = vm.FaultedPrograms;

        string? clr = null;
        try
        {
            KismetProgram? program = lib.FindProgram(cardName, entry);
            if (program is null)
            {
                clr = "program-not-found";
            }
            else
            {
                var ctx = MakeContext(engine, subject, target, entry, contracts);
                vm.Run(program, ctx);
            }
        }
        catch (Exception ex)
        {
            clr = $"{ex.GetType().Name}: {ex.Message}";
        }

        string after = FingerprintOf(state);

        // ---- 未实现调用：新增项（含计数增加的） ----
        var newUnimpl = new List<string>();
        var synthetic = new List<string>();
        foreach (var (k, v) in state.UnimplementedCalls)
        {
            int old = unimpl0.GetValueOrDefault(k);
            if (v <= old)
            {
                continue;
            }

            // `<...>` 是内核自己合成的**诊断标记**，不是「没实现的原语」：
            //   `<local-ran:X>`             = locals 兜底**成功跑了**（正向信号！）
            //   `<vm-step-limit:卡:步/预算>` = 撞步数上限（C 类）
            //   `<rng-cursor-desync:…>`     = RNG 游标失同步（回放路径才有）
            //   `<card:X>`                  = 连蓝图逻辑都没有
            if (k.StartsWith("<", StringComparison.Ordinal))
            {
                synthetic.Add($"{k}×{v - old}");
            }
            else
            {
                newUnimpl.Add($"{k}×{v - old}");
            }
        }

        // VM 内部把异常吞掉了（`RunCore` 的 catch），所以必须单独看这两个计数器，
        // 否则「程序抛了」会被静默当成「跑完了什么都没做」。
        string? vmFault = null;
        if (vm.FaultedPrograms > faults0)
        {
            vmFault = string.Join(",",
                vm.UnsupportedOps.Where(kv => kv.Key.StartsWith("<fault:", StringComparison.Ordinal))
                                 .Select(kv => kv.Key));

            if (_debugTrace)
            {
                var msg = _stepTrace.Where(l => l.Contains("[!!]", StringComparison.Ordinal)).ToList();
                if (msg.Count > 0)
                {
                    vmFault += " :: " + string.Join(" ‖ ", msg.Take(3).Select(m => m.Trim()));
                }
            }
        }

        var called = vm.Trace
            .Select(s =>
            {
                int p = s.IndexOf('(');
                return p > 0 ? s[..p] : s;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var newActions = state.ActionLog.Skip(actions0)
            .SelectMany(a => a.SubActions.Select(s => s.Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new CaseResult(
            cardName, entry, placement,
            vm.StepsExecuted - steps0,
            state.Random.ConsumedCount - rng0,
            before != after,
            after,
            vm.UnresolvedJumps - jumps0,
            clr,
            vmFault,
            newUnimpl,
            synthetic,
            called,
            newActions)
        {
            Digest = captureDigest ? CaptureDigest(state) : Array.Empty<string>(),
        };
    }

    /// <summary>字符级差异（第一处不同 ± 一段上下文）—— 指纹是压缩过的一行 JSON，最直接。</summary>
    private static string CharDiff(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        int from = Math.Max(0, i - 90);
        int len = Math.Min(260, Math.Max(a.Length, b.Length) - from);
        string ca = a.Substring(from, Math.Min(len, a.Length - from));
        string cb = b.Substring(from, Math.Min(len, b.Length - from));
        return $"首处不同 @{i}（长度 {a.Length}/{b.Length}）：①…{ca} ②…{cb}";
    }

    /// <summary>
    /// 定位非确定性：**反复**跑同一个用例直到抓到一对不一致的，
    /// 再把那一对的逐卡摘要 diff 出来。
    ///
    /// ⚠️ 为什么要「反复跑」而不是「再跑两次」：非确定性可以是**间歇**的
    /// （第一版只补跑两次，那两次恰好一致 ⇒ diff 报「无差异」，
    /// 看起来像"指纹不同但状态相同"的鬼故事，实际只是没抓到）。
    /// </summary>
    private static string DiagnoseNondeterminism(CardDatabase db, KismetLibrary lib,
                                                 IReadOnlyDictionary<string, List<SlotSpec>> contracts,
                                                 string cardName, string entry, string placement, ulong seed)
    {
        CaseResult? first = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            CaseResult a = Execute(db, lib, contracts, cardName, entry, placement, seed, captureDigest: true);
            CaseResult b = Execute(db, lib, contracts, cardName, entry, placement, seed, captureDigest: true);
            if (a.Fingerprint == b.Fingerprint)
            {
                continue;
            }

            string diff = DiffDigests(a.Digest, b.Digest);
            return $"第 {attempt + 1} 次配对抓到（rng {a.Rng}/{b.Rng}，步数 {a.Steps}/{b.Steps}）：{diff}";
        }

        return first is null
            ? "补跑 40 对都没能重现 —— 说明是**低频间歇**非确定（这本身是更严重的信号）。"
            : "";
    }

    /// <summary>
    /// 逐卡摘要 —— 只在**发现非确定性**时用来定位「到底哪张卡的哪个字段不一样」。
    /// 直接 diff 整个 `SnapshotJson` 只能给出一个字符偏移，没有可读性。
    /// </summary>
    private static IReadOnlyList<string> CaptureDigest(GameState state)
    {
        var list = new List<string>(state.AllCards.Count + 8)
        {
            $"回合={state.Turn} 行动方={state.ActiveSide} 前线={state.FrontlineOwner} " +
            $"受限={state.IsFrontlineLimited} kredit L{state.Kredits(Side.Left)}/{state.MaxKredits(Side.Left)}" +
            $" R{state.Kredits(Side.Right)}/{state.MaxKredits(Side.Right)}",
        };

        // ⚠️ 必须用**完整快照的序列化**，不能手写字段清单 ——
        //    第一版手写了十几个字段，结果两个用例指纹不同、diff 却是「无差异」：
        //    漏掉的那个字段（`CustomAbility`）恰好就是差异所在。
        foreach (var c in state.AllCards)
        {
            list.Add(JsonSerializer.Serialize(c.Snapshot(), GameState.SnapshotJsonOptions));
        }

        foreach (var a in state.ActionLog)
        {
            list.Add($"动作 {a.ActionType} {string.Join(",", a.SubActions.Select(s => s.Name))}");
        }

        return list;
    }

    /// <summary>
    /// 把两份逐卡摘要对成「只在一侧出现的行」。
    ///
    /// ⚠️ **按位置**比对，不是按集合：`ActionLog` 的**顺序**也是状态的一部分
    /// （指纹里就带着子动作名序列），用 `HashSet` 会把「同一批动作换了顺序」
    /// 判成「无差异」—— 第一版正是这么错的。
    /// </summary>
    private static string DiffDigests(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var sbOut = new StringBuilder();
        int n = Math.Max(a.Count, b.Count);
        int shown = 0;
        for (int i = 0; i < n && shown < 12; i++)
        {
            string? x = i < a.Count ? a[i] : null;
            string? y = i < b.Count ? b[i] : null;
            if (string.Equals(x, y, StringComparison.Ordinal))
            {
                continue;
            }

            sbOut.AppendLine();
            sbOut.Append("   ① ").Append(Truncate(x ?? "<缺>", 260));
            sbOut.AppendLine();
            sbOut.Append("   ② ").Append(Truncate(y ?? "<缺>", 260));
            shown++;
        }

        return shown == 0 ? "（逐卡摘要按位置比对完全相同 —— 差异在别处）" : sbOut.ToString();
    }

    /// <summary>
    /// 分类。
    ///
    /// 优先级：A（异常/崩溃）&gt; C（撞步数上限）&gt; B（撞未实现原语）&gt; D（零变化）&gt; OK。
    ///
    /// ⚠️ C 排在 B 前面，因为 `<vm-step-limit:…>` 本身就是写进 `UnimplementedCalls` 的，
    /// 不先摘出来的话每一张撞墙的卡都会同时算进 B，把「还差哪些原语」那张表搞脏。
    /// </summary>
    private static string Classify(CaseResult c)
    {
        if (c.ClrException is not null || c.VmFault is not null)
        {
            return "A";
        }

        if (c.Synthetic.Any(s => s.StartsWith("<vm-step-limit:", StringComparison.Ordinal)))
        {
            return "C";
        }

        if (c.NewUnimpl.Count > 0)
        {
            return "B";
        }

        if (!c.Changed)
        {
            return "D";
        }

        return "OK";
    }

    /// <summary>
    /// 状态指纹 —— 「有没有变化」和「两次跑是否逐位相同」都用它。
    ///
    /// 组成：
    /// 1. `GameState.SnapshotJson()` —— 回合/kredit/前线归属/每张卡的完整快照
    ///    （位置、序号、攻防、费用、关键字、自定义字段），按 CardId 排序，确定性。
    /// 2. `ActionLog` 的长度与**子动作名序列** —— 有些效果只发动作不改状态
    ///    （`EffectContext.Emit`），只比 ① 会把它们误判成「零变化」。
    /// </summary>
    private static string FingerprintOf(GameState state)
    {
        var sb = new StringBuilder(state.SnapshotJson());
        sb.Append("|A").Append(state.ActionLog.Count);
        foreach (var a in state.ActionLog)
        {
            foreach (var s in a.SubActions)
            {
                sb.Append('|').Append(s.Name);
            }
        }

        return sb.ToString();
    }

    // ==================================================================
    //  局面构造
    // ==================================================================

    /// <summary>构造一个**非退化**的局面。见类注释「设计取舍 3」。</summary>
    private static (CardInstance Subject, CardInstance Target) BuildWorld(
        CardDatabase db, GameState state, string cardName, string placement)
    {
        // ---- HQ：位置卡就是 HQ（`CardInstance.IsHq` = `Definition.IsLocationCard`）----
        string hq = db.Find("card_location_london")?.Name
                    ?? db.All.First(c => c.IsLocationCard).Name;
        var lhq = state.CreateWithId(hq, Side.Left, 1, CardLocation.BoardHqLeft, 0);
        var rhq = state.CreateWithId(hq, Side.Right, 41, CardLocation.BoardHqRight, 0);
        lhq.Defense = lhq.MaxDefense = MatchEngine.InitialHqDefense;
        rhq.Defense = rhq.MaxDefense = MatchEngine.InitialHqDefense;

        // ---- 单位：5 个兵种 × 尽量不同阵营（阵营是很多卡的条件门）----
        // 左半场留一格给被测卡自己（半场 5 格、HQ 占 1 ⇒ 最多 4 个单位）。
        state.CreateWithId(Pick(db, "infantry", "Germany"), Side.Left, 2, CardLocation.BoardHqLeft, 1);
        state.CreateWithId(Pick(db, "tank", "Soviet"), Side.Left, 3, CardLocation.BoardHqLeft, 2);
        state.CreateWithId(Pick(db, "artillery", "USA"), Side.Left, 4, CardLocation.BoardHqLeft, 3);
        state.CreateWithId(Pick(db, "fighter", "Britain"), Side.Left, 5, CardLocation.BoardFrontline, 0);
        state.CreateWithId(Pick(db, "bomber", "Japan"), Side.Left, 6, CardLocation.BoardFrontline, 1);

        state.CreateWithId(Pick(db, "infantry", "Soviet"), Side.Right, 42, CardLocation.BoardHqRight, 1);
        state.CreateWithId(Pick(db, "tank", "Germany"), Side.Right, 43, CardLocation.BoardHqRight, 2);
        state.CreateWithId(Pick(db, "artillery", "Japan"), Side.Right, 44, CardLocation.BoardHqRight, 3);
        state.CreateWithId(Pick(db, "fighter", "USA"), Side.Right, 45, CardLocation.BoardHqRight, 4);
        state.CreateWithId(Pick(db, "bomber", "Britain"), Side.Right, 46, CardLocation.BoardFrontline, 2);
        state.CreateWithId(Pick(db, "infantry", "Italy"), Side.Right, 47, CardLocation.BoardFrontline, 3);
        state.CreateWithId(Pick(db, "tank", "Finland"), Side.Right, 48, CardLocation.BoardFrontline, 4);

        // ---- 手牌 / 牌库：给「随机选卡」一族非空候选池 ----
        // 用不同阵营 + 不同兵种，让「英国指令」「德国单位」这类过滤条件也能命中。
        var filler = new List<string>();
        foreach (string type in new[] { "order", "infantry", "tank", "artillery", "fighter", "bomber", "location" })
        {
            foreach (string? faction in new[] { "Germany", "Britain", "Japan", "Soviet", "USA" })
            {
                filler.Add(Pick(db, type, faction, 1));
            }
        }

        for (int i = 0; i < 5; i++)
        {
            state.CreateWithId(filler[i % filler.Count], Side.Left, 10 + i, CardLocation.HandLeft, i);
            state.CreateWithId(filler[(i + 7) % filler.Count], Side.Right, 50 + i, CardLocation.HandRight, i);
        }

        for (int i = 0; i < 10; i++)
        {
            state.CreateWithId(filler[(i + 3) % filler.Count], Side.Left, 20 + i, CardLocation.DeckLeft, i);
            state.CreateWithId(filler[(i + 11) % filler.Count], Side.Right, 60 + i, CardLocation.DeckRight, i);
        }

        // ---- 场上单位「早就进场」：排除召唤失调干扰 ----
        foreach (var c in state.BoardUnordered().ToList())
        {
            c.EnteredPlayOnTurn = -99;
        }

        // ---- 资源与回合 ----
        state.Turn = 3;                       // ≠1 ⇒ 会摸牌；发号规则用 1000 倍率
        state.ActiveSide = Side.Left;
        state.StartingSide = Side.Left;
        state.FrontlineOwner = Side.Left;
        foreach (Side s in new[] { Side.Left, Side.Right })
        {
            state.SetKredits(s, 20);
            state.SetMaxKredits(s, 20);
        }

        // ---- 被测卡自己 ----
        CardInstance subject = placement switch
        {
            "board" => state.CreateWithId(cardName, Side.Left, 900, CardLocation.BoardHqLeft, 4),
            "discard" => state.CreateWithId(cardName, Side.Left, 900, CardLocation.Discard, 0),
            _ => state.CreateWithId(cardName, Side.Left, 900, CardLocation.HandLeft, 5),
        };

        subject.EnteredPlayOnTurn = -99;

        return (subject, state.RequireById(42));
    }

    /// <summary>挑一张指定兵种（尽量指定阵营）的真实卡，按名字排序取第 <paramref name="index"/> 张。</summary>
    private static string Pick(CardDatabase db, string type, string? faction, int index = 0)
    {
        IEnumerable<CardDefinition> list = db.All
            .Where(c => string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase));

        if (faction is not null)
        {
            var byFaction = list
                .Where(c => string.Equals(c.Faction, faction, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byFaction.Count > 0)
            {
                list = byFaction;
            }
        }

        var arr = list.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        if (arr.Count == 0)
        {
            // 该兵种在卡库里没有 —— 退回任意一张单位卡，保证局面不退化。
            arr = db.All.Where(c => c.IsUnit).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        }

        return arr[Math.Min(index, arr.Count - 1)].Name;
    }

    // ==================================================================
    //  事件入参（`docs/event-contracts.json`）
    // ==================================================================

    private sealed record SlotSpec(string Bare, string Type);

    private static Dictionary<string, List<SlotSpec>> LoadContracts(string baseDir)
    {
        var result = new Dictionary<string, List<SlotSpec>>(StringComparer.Ordinal);
        string? path = FindContractsFile(baseDir);
        if (path is null)
        {
            Console.WriteLine("  ⚠ 没找到 event-contracts.json —— 事件入参只能靠 VM 的名字兜底，");
            Console.WriteLine("    会有更多卡在守卫上退出（假阴性变多）。");
            return result;
        }

        Console.WriteLine($"  事件契约来源：{path}");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var ev in doc.RootElement.EnumerateObject())
        {
            if (!ev.Value.TryGetProperty("slots", out var slots) || slots.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            var list = new List<SlotSpec>();
            foreach (var slot in slots.EnumerateObject())
            {
                string bare = slot.Name.StartsWith("K2Node_Event_", StringComparison.Ordinal)
                    ? slot.Name["K2Node_Event_".Length..]
                    : slot.Name;

                // 去掉 `_1` / `_2`（「同名槽位第 N 次声明」，不是入参下标）——
                // 与 `KismetVm.ResolveEventVar` 的口径**必须一致**，否则填了也读不到。
                int us = bare.LastIndexOf('_');
                if (us > 0 && int.TryParse(bare[(us + 1)..], out _))
                {
                    bare = bare[..us];
                }

                string type = slot.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                list.Add(new SlotSpec(bare, type));
            }

            if (list.Count > 0)
            {
                result[ev.Name] = list;
            }
        }

        return result;
    }

    /// <summary>
    /// 找 <c>event-contracts.json</c>。
    ///
    /// ⚠️ 为什么不能只看数据目录：`KLink.Bot.csproj` 的 `None Include` 是一份**显式清单**，
    /// 里面**没有**这个文件 —— 所以 `bin\...\Data\` 下不存在它。
    /// 而它是「按槽位名给事件入参填值」的唯一依据（没有它，程序读 `K2Node_Event_*`
    /// 全是 null，绝大多数卡在第一个 `IsValid(targetCard)` 守卫上就退出）。
    /// 所以这里按「数据目录 → 仓库根下的 `klink bot/docs` → 从 exe 往上找」依次回退。
    /// </summary>
    private static string? FindContractsFile(string dataDir)
    {
        foreach (string candidate in new[]
                 {
                     Path.Combine(dataDir, "event-contracts.json"),
                     Path.Combine("klink bot", "docs", "event-contracts.json"),
                 })
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "klink bot", "docs", "event-contracts.json");
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// 按事件契约给入参填**合理值**。
    ///
    /// ⚠️ 这是测试台的**近似**，不是复刻：真实对局里这些值由引擎按具体事件填。
    /// 我们能做的是「给一张真实存在的卡 + 合理的位置/阵营」，让守卫有机会通过 ——
    /// 否则每个程序都在第一个 `IsValid(...)` 上退出，跑出来的全是假阴性。
    /// </summary>
    private static EffectContext MakeContext(MatchEngine engine, CardInstance subject, CardInstance target,
                                             string entry, IReadOnlyDictionary<string, List<SlotSpec>> contracts)
    {
        var named = new Dictionary<string, object?>(StringComparer.Ordinal);
        var positional = new List<object?>();

        if (contracts.TryGetValue(entry, out var slots))
        {
            foreach (var slot in slots)
            {
                // 这几个名字**故意不填**，让 VM 自己的语义兜底（它比我们更清楚）：
                //   turnnumber      → `State.Turn`
                //   instigatorID    → 程序自己那张卡的 CardId
                //   goingToLocation / oldLocation / newLocation → ctx 上的专门字段
                // 填了反而会用一个假的回合号/位置覆盖掉正确的值。
                if (slot.Bare is "turnnumber" or "instigatorID" or "goingToLocation" or "oldLocation" or "newLocation")
                {
                    continue;
                }

                object? v = ValueFor(slot, target, subject);
                positional.Add(v);
                if (v is not null)
                {
                    named[slot.Bare] = v;
                }
            }
        }

        return new EffectContext
        {
            Engine = engine,
            State = engine.State,
            Self = subject,
            Target = target,
            Controller = subject.Owner,
            EventArgs = positional,
            NamedArgs = named,
            GoingToLocation = CardLocation.Discard,
            OldLocation = CardLocation.BoardHqLeft,
            NewLocation = CardLocation.BoardFrontline,
        };
    }

    private static object? ValueFor(SlotSpec slot, CardInstance target, CardInstance subject)
    {
        switch (slot.Type)
        {
            case "BaseCardObject":
            case "BP_BaseCard_C":
            case "BP_HandCardLook_C":
            case "Object":
            case "Actor":
                // 「事件里的那张卡」= 对面的真实单位（有攻防、在场上，守卫能过）。
                return target;

            case "Int":
                if (slot.Bare is "damage" or "attackCost" or "kreditCost")
                {
                    return 1;
                }

                return target.CardId;

            case "Bool":
                return false;

            case "Double":
            case "Float":
                return 0.0;

            case "ECardLocationEnum":
                return (int)CardLocation.BoardFrontline;

            case "ESideEnum":
                return (int)subject.Owner;

            case "String":
            case "Name":
            case "Text":
                return "";

            default:
                // 结构体/枚举/变换/数组 —— 不猜，交给 VM 的兜底。
                return null;
        }
    }

    /// <summary>
    /// 名字看起来是不是**纯查询/纯函数**（不写状态）。
    ///
    /// ⚠️ 这是**启发式**，不是判据 —— 它只用来把 D 类（零变化）里
    /// 「在卡池里扫了一遍、条件不满足」和「真的调了写原语却没生效」分开。
    /// 所以宁可把边界名字算成「纯」（漏报），也不要算成「写」（误报）。
    /// 出处：内核自己的原语命名约定 —— 查询类一律 `Get*/Is*/Has*/Can*/Was*/Which*`，
    /// 纯算术/数组/集合/字符串操作有固定的前缀（`Array_*` / `Conv_*` / `math:*` …）。
    /// </summary>
    private static bool IsPurePrimitiveName(string name)
    {
        // `math:xxx` —— 里面只有少数几个是纯算术，`math:GetOrSpawnActor` 是生成器。
        if (name.StartsWith("math:", StringComparison.Ordinal))
        {
            string inner = name[5..];
            return inner.StartsWith("Conv_", StringComparison.Ordinal)
                   || inner is "Format" or "Max" or "Min" or "Abs" or "Sqrt" or "Floor" or "Ceil" or "Round";
        }

        foreach (string prefix in new[]
                 {
                     "Get", "Is", "Has", "Can", "Was", "Which", "Show", "Notify", "Log",
                     "Enum", "Conv_", "Array_", "JSON_", "Map_", "Concat", "Boolean",
                     "Equal", "NotEqual", "Less", "Greater", "Select",
                 })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 精确的纯函数名（前缀判据会误伤同前缀的写原语，所以单独列）
        return name is "Add_IntInt" or "Add_FloatFloat" or "Add_DoubleDouble" or "Add_Int64Int64"
            or "Subtract_IntInt" or "Subtract_FloatFloat" or "Subtract_DoubleDouble"
            or "Multiply_IntInt" or "Multiply_FloatFloat" or "Divide_IntInt" or "Divide_FloatFloat"
            or "Percent_IntInt" or "Abs_Int" or "Min_IntInt" or "Max_IntInt" or "SelectInt" or "IsValid"
            or "Not_PreBool" or "GetEnumeratorUserFriendlyName"
            // 局部集合操作（`Set_Add` / `Set_Clear` …）—— 前缀 `Set` 会被误当成写原语
            or "Set_Length" or "Set_Add" or "Set_Clear" or "Set_Remove"
            or "Set_Contains" or "Set_IsEmpty" or "Set_IsNotEmpty"
            or "Array_Length" or "Array_Get" or "Array_Add" or "Array_AddUnique" or "Array_Append"
            or "Array_Clear" or "Array_Contains" or "Array_IsEmpty" or "Array_IsNotEmpty"
            or "Array_IsValidIndex" or "Array_LastIndex" or "Array_Remove" or "Array_RemoveItem"
            or "Array_Reverse" or "Map_Find" or "Map_Contains" or "MakeArray" or "MakeSet";
    }

    // ==================================================================
    //  入口点覆盖
    // ==================================================================

    /// <summary>
    /// 报「活入口点」的覆盖情况 —— 哪些入口点**没有任何卡注册**。
    ///
    /// 为什么值得单列：内核会派发、但 IR 里没有任何卡订阅的入口点，
    /// 说明内核在派发一个**没人听**的事件（要么是内核多做的，要么是
    /// 卡侧真的没有这种卡）。反过来，「IR 注册了、内核从不派发」的那些
    /// 才是事件系统的缺口 —— 那部分在 <see cref="LiveEntrypoints"/> 的注释里说明。
    /// </summary>
    private static string BuildEntrypointCoverageSummary(List<CaseResult> cases)
    {
        var sb = new StringBuilder();
        var used = new HashSet<string>(cases.Select(c => c.Entry), StringComparer.Ordinal);
        var unused = LiveEntrypoints.Where(e => !used.Contains(e)).OrderBy(e => e, StringComparer.Ordinal).ToList();

        sb.AppendLine();
        sb.AppendLine("=== 附：入口点覆盖 ===");
        sb.AppendLine($"  引擎会派发的活入口点：{LiveEntrypoints.Count} 个，本次跑到 {used.Count} 个");
        sb.AppendLine($"  IR 里**没有任何卡注册**的活入口点（{unused.Count} 个）：");
        sb.AppendLine($"     {(unused.Count == 0 ? "（无）" : string.Join(", ", unused))}");
        return sb.ToString();
    }

    // ==================================================================
    //  冷/热启动 + 跨用例污染
    // ==================================================================

    /// <summary>
    /// 单独一节报「进程级静态状态」的两条证据。
    ///
    /// 为什么这两条值得单列：**锁步游戏的致命问题是「同一份输入两次跑结果不同」**，
    /// 而进程级静态状态（缓存 / 全局卡池模板）正是这类问题的温床 ——
    /// 它让「第一局」和「第二局」的行为不一样，而两边客户端各跑各的进程。
    ///
    /// 判据（都是**状态指纹**，不含步数）：
    /// 1. 冷启动 vs 热启动：`ColdFingerprint != Fingerprint` ⇒ 缓存**改变了状态**
    ///    （那就不是"透明的缓存"，是 bug）。
    ///    步数不同是**预期的**（缓存命中跳过整段程序执行），单独统计，不当问题。
    /// 2. 跨用例污染：同一用例在第一趟和第二趟的冷指纹不同 ⇒ 前面某个用例改了全局状态。
    /// </summary>
    private static string BuildCacheAndContaminationSummary(
        List<CaseResult> cases, List<string> contamination, Options opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== 附：进程级静态状态（缓存 / 全局卡池模板）===");

        var coldDiffers = cases.Where(c => c.ColdFingerprint != c.Fingerprint).ToList();
        var stepsDiffer = cases.Where(c => c.ColdSteps != c.Steps).ToList();
        var rngDiffer = cases.Where(c => c.ColdRng != c.Rng).ToList();

        sb.AppendLine("  ① 冷启动 vs 热启动");
        sb.AppendLine($"     状态指纹不同（**这是问题**，缓存不透明）  {coldDiffers.Count,6}");
        sb.AppendLine($"     随机消费次数不同（**这是问题**）          {rngDiffer.Count,6}");
        sb.AppendLine($"     只有 VM 步数不同（预期，缓存命中跳过执行） {stepsDiffer.Count,6}");
        foreach (var c in coldDiffers.Take(20))
        {
            sb.AppendLine($"       ⚠️ {c.Card} / {c.Entry} / {c.Placement}：冷 {c.ColdSteps} 步 → 热 {c.Steps} 步");
            sb.AppendLine($"          {Truncate(CharDiff(c.ColdFingerprint, c.Fingerprint), 500)}");
        }

        sb.AppendLine();
        sb.AppendLine($"  ② 跨用例污染（{(opt.TwoPasses ? "第二趟冷指纹 vs 第一趟冷指纹" : "未跑（--no-two-passes）")}）");
        if (!opt.TwoPasses)
        {
            sb.AppendLine("     （跳过）");
        }
        else if (contamination.Count == 0)
        {
            sb.AppendLine("     ✅ 全部用例两趟冷指纹逐位相同 —— 没有观测到跨用例的进程级污染");
        }
        else
        {
            sb.AppendLine($"     ❌ **{contamination.Count} 个用例两趟结果不同** —— 前面某个用例改了全局状态：");
            foreach (string c in contamination.Take(30))
            {
                sb.AppendLine($"        {c}");
            }
        }

        return sb.ToString();
    }

    // ==================================================================
    //  集成路径（`MatchEngine.PlayCard`）—— 次要探针
    // ==================================================================

    /// <summary>
    /// 走**真实的编排路径**（<see cref="MatchEngine.PlayCard"/>）再跑一遍有
    /// <c>OnPlayedFromHand</c> 的卡。
    ///
    /// 为什么要单独一节：上面的主用例是**直接跑卡自己的程序**（为了归因干净），
    /// 于是引擎编排层（离手 → 落场/进弃牌堆 → OnEnterPlay → 部署 → 战吼 → 收尾）
    /// 一行都没走到。这一节补上它，抓的是「这条链上有东西抛异常」。
    ///
    /// ⚠️ **归因是有污染的**：`PlayCard` 会派发事件给场上的其它卡，
    /// 所以这里报出来的未实现原语**不一定来自被测试的那张卡**。
    /// 因此这一节的主判据只有两条：**异常** 与 **确定性**。
    /// 局面也刻意压到最小（双方 HQ + 一张对面单位当目标），把污染面收到最小。
    /// </summary>
    private sealed record IntegrationResult(
        string Card,
        bool Played,
        string? ClrException,
        string? VmFault,
        IReadOnlyList<string> NewUnimpl,
        string Fingerprint,
        bool Deterministic);

    private static List<IntegrationResult> RunIntegration(CardDatabase db, KismetLibrary lib,
                                                          List<string> cardNames, Options opt)
    {
        var results = new List<IntegrationResult>();
        foreach (string cardName in cardNames)
        {
            if (lib.Find(cardName) is not { } card
                || !card.Entrypoints.Keys.Any(k => k.StartsWith("OnPlayedFromHand", StringComparison.Ordinal)))
            {
                continue;
            }

            IntegrationResult r1 = PlayOnce(db, cardName, opt.Seed);
            IntegrationResult r2 = opt.SkipDeterminism ? r1 : PlayOnce(db, cardName, opt.Seed);
            results.Add(r1 with
            {
                Deterministic = r2.Fingerprint == r1.Fingerprint
                                && string.Join(",", r2.NewUnimpl) == string.Join(",", r1.NewUnimpl),
            });
        }

        return results;
    }

    private static IntegrationResult PlayOnce(CardDatabase db, string cardName, ulong seed)
    {
        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed);
        var state = engine.State;

        string hq = db.Find("card_location_london")?.Name ?? db.All.First(c => c.IsLocationCard).Name;
        var lhq = state.CreateWithId(hq, Side.Left, 1, CardLocation.BoardHqLeft, 0);
        var rhq = state.CreateWithId(hq, Side.Right, 41, CardLocation.BoardHqRight, 0);
        lhq.Defense = lhq.MaxDefense = MatchEngine.InitialHqDefense;
        rhq.Defense = rhq.MaxDefense = MatchEngine.InitialHqDefense;

        // 一张对面单位：让「必须有目标」的牌也能通过目标门。
        var target = state.CreateWithId(Pick(db, "infantry", "Soviet"), Side.Right, 42, CardLocation.BoardHqRight, 1);
        target.EnteredPlayOnTurn = -99;

        var card = state.CreateWithId(cardName, Side.Left, 900, CardLocation.HandLeft, 0);

        // 牌库给几张，让「抽牌」类效果有东西可抽。
        for (int i = 0; i < 5; i++)
        {
            state.CreateWithId(Pick(db, "infantry", null, i), Side.Left, 20 + i, CardLocation.DeckLeft, i);
        }

        state.Turn = 3;
        state.ActiveSide = Side.Left;
        state.StartingSide = Side.Left;
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);
        state.SetKredits(Side.Right, 20);
        state.SetMaxKredits(Side.Right, 20);

        var unimpl0 = new Dictionary<string, int>(state.UnimplementedCalls, StringComparer.Ordinal);
        KismetVm vm = engine.Api.Vm;
        int faults0 = vm.FaultedPrograms;

        string? clr = null;
        bool played = false;
        try
        {
            played = engine.PlayCard(card, target);
        }
        catch (Exception ex)
        {
            clr = $"{ex.GetType().Name}: {ex.Message}";
        }

        var newUnimpl = new List<string>();
        foreach (var (k, v) in state.UnimplementedCalls)
        {
            if (v > unimpl0.GetValueOrDefault(k) && !k.StartsWith("<", StringComparison.Ordinal))
            {
                newUnimpl.Add($"{k}×{v - unimpl0.GetValueOrDefault(k)}");
            }
        }

        string? vmFault = vm.FaultedPrograms > faults0
            ? string.Join(",", vm.UnsupportedOps.Where(kv => kv.Key.StartsWith("<fault:", StringComparison.Ordinal))
                                                .Select(kv => kv.Key))
            : null;

        return new IntegrationResult(cardName, played, clr, vmFault, newUnimpl, FingerprintOf(state), true);
    }

    private static string BuildIntegrationSummary(List<IntegrationResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== 附：真实编排路径（`MatchEngine.PlayCard`）—— 次要探针 ===");
        sb.AppendLine("  ⚠️ 归因有污染：PlayCard 会派发事件给场上其它卡，这里报的未实现原语");
        sb.AppendLine("     不一定来自被测试的那张卡。所以这一节只认**异常**与**确定性**两条判据。");
        sb.AppendLine($"  用例：{results.Count}（有 OnPlayedFromHand 的卡）");

        var crashed = results.Where(r => r.ClrException is not null || r.VmFault is not null).ToList();
        var notPlayed = results.Where(r => !r.Played).ToList();
        var nonDet = results.Where(r => !r.Deterministic).ToList();
        var withUnimpl = results.Where(r => r.NewUnimpl.Count > 0).ToList();

        sb.AppendLine($"  打出来了（CanPlay 通过）    {results.Count - notPlayed.Count,6}");
        sb.AppendLine($"  打不出来（CanPlay 拒绝）    {notPlayed.Count,6}   ← 多为「无合法目标 / 费用」");
        sb.AppendLine($"  抛异常/VM 内部吞掉的异常    {crashed.Count,6}");
        sb.AppendLine($"  两次跑结果不同（不确定）    {nonDet.Count,6}");
        sb.AppendLine($"  路径上出现未实现原语的卡    {withUnimpl.Count,6}（含其它订阅卡的贡献，仅供定位）");

        foreach (var g in crashed.GroupBy(r => r.ClrException ?? r.VmFault ?? "?").Take(20))
        {
            sb.AppendLine($"    ×{g.Count(),5} {Truncate(g.Key, 100)}");
            sb.AppendLine($"            卡（前 8）：{string.Join(", ", g.Select(r => r.Card).Take(8))}");
        }

        foreach (var r in nonDet.Take(20))
        {
            sb.AppendLine($"    ⚠️ 非确定：{r.Card}");
        }

        return sb.ToString();
    }

    // ==================================================================
    //  自测钩子（给 `BotSim selftest` 用）
    // ==================================================================

    /// <summary>
    /// 自测：造一个局面，断言形状符合设计（双方 HQ + 5 兵种 + 手牌 + 牌库 + kredit）。
    ///
    /// 为什么值得守：D 类（零状态变化）的判读**完全依赖**「局面非退化」这个前提。
    /// 局面一旦退化成空棋盘，所有卡都会「零变化」，D 类会瞬间涨到 100% ——
    /// 那时候报告会看起来"卡全是坏的"，实际是测试台塌了。
    /// </summary>
    internal static string? SelfTestBoardShape(CardDatabase db)
    {
        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), 20261002);
        var state = engine.State;
        var (subject, _) = BuildWorld(db, state, "card_event_aans", "hand");

        if (!state.Cards(Side.Left, CardLocation.BoardHqLeft).Any(c => c.IsHq)) return "左 HQ 不在半场";
        if (!state.Cards(Side.Right, CardLocation.BoardHqRight).Any(c => c.IsHq)) return "右 HQ 不在半场";

        var types = state.BoardUnordered().Select(c => c.Definition.Type ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (string t in new[] { "infantry", "tank", "artillery", "fighter", "bomber" })
        {
            if (!types.Contains(t))
            {
                return $"场上缺 {t}（实际有 {string.Join(",", types.OrderBy(x => x))}）";
            }
        }

        if (state.Board(Side.Left).Count < 3) return $"左半场单位太少：{state.Board(Side.Left).Count}";
        if (state.Board(Side.Right).Count < 3) return $"右半场单位太少：{state.Board(Side.Right).Count}";
        if (state.Hand(Side.Left).Count < 5) return $"左手牌太少：{state.Hand(Side.Left).Count}";
        if (state.Deck(Side.Left).Count < 10) return $"左牌库太少：{state.Deck(Side.Left).Count}";
        if (state.Kredits(Side.Left) < 10) return $"左 kredit 太少：{state.Kredits(Side.Left)}";
        if (subject.Location != CardLocation.HandLeft) return $"被测卡不在手牌：{subject.Location}";
        if (state.FrontlineOwner != Side.Left) return $"前线归属不是 Left：{state.FrontlineOwner}";

        // 卡池必须非空 —— 否则「随机选卡」那一族会误判成「什么都没做」
        if (state.Database.Count < 1000) return $"卡池太小：{state.Database.Count}";

        return null;
    }

    /// <summary>
    /// 自测：同一张卡、同一种子跑两次，**逐位相同**（指纹 / 步数 / 随机消费 / 未实现集合）。
    ///
    /// ⚠️ 这里必须先预热一次：内核有进程级静态缓存（`CardApiDispatch._gcsCache`），
    /// 冷启动与热启动的 **VM 步数**本来就不同（见 `Run` 里那段长注释）。
    /// 不预热的话这个自测会误报。
    /// </summary>
    internal static string? SelfTestDeterminism(CardDatabase db)
    {
        if (KismetLibrary.Default is not { } lib)
        {
            return "KismetLibrary.Default 是 null（IR 没加载）";
        }

        var contracts = LoadContracts(".");
        var samples = new (string Card, string Entry, string Placement)[]
        {
            ("card_event_aans", "OnPlayedFromHand", "hand"),
            ("card_unit_royal_west_kents", "OnEnterPlay", "board"),
            ("card_event_committed_crew", "OnOtherCardPlayedFromHand", "discard"),
        };

        foreach (var (card, entry, placement) in samples)
        {
            if (lib.Find(card) is null)
            {
                return $"IR 里没有 {card}";
            }

            // 预热（填缓存），再取两次可比的执行
            Execute(db, lib, contracts, card, entry, placement, 20261002);
            CaseResult a = Execute(db, lib, contracts, card, entry, placement, 20261002);
            CaseResult b = Execute(db, lib, contracts, card, entry, placement, 20261002);

            if (a.Fingerprint != b.Fingerprint)
            {
                return $"{card}/{entry}/{placement} 两次状态指纹不同："
                     + CharDiff(a.Fingerprint, b.Fingerprint);
            }

            if (a.Rng != b.Rng || a.Steps != b.Steps)
            {
                return $"{card}/{entry}/{placement} 两次步数/随机消费不同：" +
                       $"steps {a.Steps}/{b.Steps}，rng {a.Rng}/{b.Rng}";
            }
        }

        return null;
    }

    /// <summary>
    /// 自测：扫一批卡**不能抛异常、不能撞步数上限**。
    ///
    /// 取前 40 张（按卡名序）是有意的：这个样本稳定、跑得快（约 1 秒），
    /// 而且覆盖了指令 + 单位 + 触发式卡。它守的是「测试台本身没崩」，
    /// 不是「全部卡都正确」—— 后者由人工读摘要。
    /// </summary>
    internal static string? SelfTestNoCrash(CardDatabase db)
    {
        if (KismetLibrary.Default is not { } lib)
        {
            return "KismetLibrary.Default 是 null（IR 没加载）";
        }

        var contracts = LoadContracts(".");
        var names = lib.AllCards.Keys
            .Where(n => lib.Find(n) is { Steps.Count: > 0 } && db.Find(n) is not null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Take(40)
            .ToList();

        if (names.Count < 40)
        {
            return $"样本不足：只取到 {names.Count} 张";
        }

        foreach (string name in names)
        {
            var card = lib.Find(name)!;
            bool isUnit = db.Find(name)?.IsUnit ?? false;
            foreach (var (entry, _) in card.Entrypoints)
            {
                if (!LiveEntrypoints.Contains(entry))
                {
                    continue;
                }

                foreach (string placement in isUnit ? new[] { "board" } : new[] { "hand" })
                {
                    CaseResult r = Execute(db, lib, contracts, name, entry, placement, 20261002);
                    if (r.ClrException is not null)
                    {
                        return $"{name}/{entry}/{placement} 抛异常：{r.ClrException}";
                    }

                    if (r.VmFault is not null)
                    {
                        return $"{name}/{entry}/{placement} VM 内部吞掉异常：{r.VmFault}";
                    }

                    if (r.Synthetic.Any(s => s.StartsWith("<vm-step-limit:", StringComparison.Ordinal)))
                    {
                        return $"{name}/{entry}/{placement} 撞步数上限：{string.Join(";", r.Synthetic)}";
                    }
                }
            }
        }

        return null;
    }

    // ==================================================================
    //  输出
    // ==================================================================

    private static void WriteTsv(List<CaseResult> cases, string path)
    {
        var sb = new StringBuilder();
        // `fp` 列 = 状态指纹的短哈希。加它是为了**跨运行逐用例比对**：
        //   TSV_A（缓存开）vs TSV_B（`KLINK_GCS_NOCACHE=1`）逐行比 `fp`，
        //   就能把「进程级静态缓存是否改变了对局状态」变成一条可复现的判据，
        //   而不是靠"冷/热两次恰好不一致"这种间歇信号。
        sb.AppendLine("card\tentry\tplacement\tkind\tsteps\trng\tchanged\tdeterministic\tjumps\t"
                    + "fp\tclr_exception\tvm_fault\tnew_unimpl\tsynthetic\tcalled\tnew_actions");
        foreach (var c in cases.OrderBy(c => c.Card, StringComparer.Ordinal).ThenBy(c => c.Entry, StringComparer.Ordinal))
        {
            sb.Append(c.Card).Append('\t')
              .Append(c.Entry).Append('\t')
              .Append(c.Placement).Append('\t')
              .Append(c.Kind).Append('\t')
              .Append(c.Steps).Append('\t')
              .Append(c.Rng).Append('\t')
              .Append(c.Changed ? "1" : "0").Append('\t')
              .Append(c.Deterministic ? "1" : "0").Append('\t')
              .Append(c.UnresolvedJumps).Append('\t')
              .Append(FingerprintHash(c.Fingerprint)).Append('\t')
              .Append(Flat(c.ClrException)).Append('\t')
              .Append(Flat(c.VmFault)).Append('\t')
              .Append(string.Join(";", c.NewUnimpl)).Append('\t')
              .Append(string.Join(";", c.Synthetic)).Append('\t')
              .Append(string.Join(";", c.Called)).Append('\t')
              .Append(string.Join(";", c.NewActions))
              .AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Flat(string? s) => s is null ? "" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>状态指纹的短哈希（TSV 的 `fp` 列，用于跨运行逐用例比对）。</summary>
    private static string FingerprintHash(string fingerprint)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static void WriteText(string text, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string BuildSummary(List<CaseResult> cases, Options opt,
                                       HashSet<string> mutating, HashSet<string> writeCapable)
    {
        var sb = new StringBuilder();
        var byKind = cases.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.ToList());

        int Count(string k) => byKind.GetValueOrDefault(k)?.Count ?? 0;

        sb.AppendLine("=== 全卡池蓝图烟雾测试 · 摘要 ===");
        sb.AppendLine($"播种={opt.Seed}  用例={cases.Count}  卡={cases.Select(c => c.Card).Distinct().Count()}  " +
                      $"入口={cases.Select(c => c.Entry).Distinct().Count()}");
        sb.AppendLine();
        sb.AppendLine($"  OK（跑通且有状态变化）  {Count("OK"),6}");
        sb.AppendLine($"  A  抛异常/崩溃          {Count("A"),6}");
        sb.AppendLine($"  B  撞未实现原语         {Count("B"),6}");
        sb.AppendLine($"  C  撞步数上限           {Count("C"),6}");
        sb.AppendLine($"  D  零状态变化           {Count("D"),6}");
        sb.AppendLine();

        // ---- 确定性 ----
        var nonDet = cases.Where(c => !c.Deterministic).ToList();
        sb.AppendLine($"=== ★ 确定性（同种子跑两次，逐位比对）===");
        sb.AppendLine(nonDet.Count == 0
            ? "  ✅ 全部用例两次结果逐位相同（指纹 / RNG 消费次数 / 步数 / 未实现集合）"
            : $"  ❌ **{nonDet.Count} 个用例两次结果不同** —— 锁步游戏的致命问题：");
        foreach (var c in nonDet.Take(30))
        {
            sb.AppendLine($"     {c.Card} / {c.Entry} / {c.Placement}：" +
                          $"rng {c.Rng}→{c.SecondRng}，步数 {c.Steps}");
            sb.AppendLine($"       差异：{Truncate(c.Diff, 900)}");
        }

        // ⚠️ 这条限制**必须写出来**，否则「✅ 全绿」会被误读成「内核完全确定」。
        sb.AppendLine("  ⚠️ 这条结论有一个**前提**：每个用例的两次执行都在「缓存已热」状态下比较");
        sb.AppendLine("     （冷启动先跑一次并丢弃，见 `Run` 里那段注释）。");
        sb.AppendLine("     也就是说它证明的是「同一个用例连续跑两次一致」，");
        sb.AppendLine("     **不**证明「结果与进程历史无关」—— 后者是下面「进程级静态状态」那一节，");
        sb.AppendLine("     那里在本次运行里抓到了反例。");

        sb.AppendLine();

        // ---- A 异常 ----
        var a = byKind.GetValueOrDefault("A") ?? new List<CaseResult>();
        sb.AppendLine($"=== (A) 抛异常的卡：{a.Select(c => c.Card).Distinct().Count()} 张 / {a.Count} 个用例 ===");
        foreach (var g in a.GroupBy(c => c.ClrException ?? c.VmFault ?? "?").OrderByDescending(g => g.Count()).Take(20))
        {
            var cards = g.Select(c => c.Card).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            sb.AppendLine($"  ×{g.Count(),5}  {Truncate(g.Key, 110)}");
            sb.AppendLine($"          卡（前 8）：{string.Join(", ", cards.Take(8))}");
        }

        if (a.Count == 0)
        {
            sb.AppendLine("  （没有）");
        }

        sb.AppendLine();
        // ---- B 未实现原语（最有价值的那张表）----
        var b = byKind.GetValueOrDefault("B") ?? new List<CaseResult>();
        var prims = new Dictionary<string, (int Cases, HashSet<string> Cards)>(StringComparer.Ordinal);
        foreach (var c in b)
        {
            foreach (string raw in c.NewUnimpl)
            {
                int x = raw.LastIndexOf('×');
                string name = x > 0 ? raw[..x] : raw;
                if (!prims.TryGetValue(name, out var e))
                {
                    e = (0, new HashSet<string>(StringComparer.Ordinal));
                }

                e.Cases++;
                e.Cards.Add(c.Card);
                prims[name] = e;
            }
        }

        sb.AppendLine($"=== (B) 撞到未实现原语的卡：{b.Select(c => c.Card).Distinct().Count()} 张 / " +
                      $"{b.Count} 个用例 / {prims.Count} 个原语 ===");
        sb.AppendLine("  ★ 这张表最有价值：它告诉我们「还差哪些原语」以及「每个原语影响多少张卡」。");
        sb.AppendLine("  （「用例」列 = 有多少个 (卡,入口,摆位) 组合撞到它；「卡」列 = 有多少张**不同的卡**。）");
        sb.AppendLine($"  {"用例",6} {"卡",5}  原语 / 受影响的卡（前 6）");
        foreach (var (name, e) in prims.OrderByDescending(kv => kv.Value.Cards.Count)
                                       .ThenByDescending(kv => kv.Value.Cases)
                                       .ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(70))
        {
            var cards = e.Cards.OrderBy(n => n, StringComparer.Ordinal).Take(6).ToList();
            sb.AppendLine($"  {e.Cases,6} {e.Cards.Count,5}  {name}");
            sb.AppendLine($"                      {string.Join(", ", cards)}" +
                          (e.Cards.Count > cards.Count ? $" …（共 {e.Cards.Count} 张）" : ""));
        }

        if (prims.Count == 0)
        {
            sb.AppendLine("  （没有）");
        }

        sb.AppendLine();

        // ---- C 步数上限 ----
        var cList = byKind.GetValueOrDefault("C") ?? new List<CaseResult>();
        sb.AppendLine($"=== (C) 撞步数上限的卡：{cList.Select(c => c.Card).Distinct().Count()} 张 / {cList.Count} 个用例 ===");
        foreach (var c in cList.OrderByDescending(c => c.Steps).Take(40))
        {
            sb.AppendLine($"  {c.Steps,8} 步  {c.Card} / {c.Entry} / {c.Placement}  " +
                          $"{string.Join(";", c.Synthetic.Where(s => s.StartsWith("<vm-step-limit:", StringComparison.Ordinal)))}");
        }

        if (cList.Count == 0)
        {
            sb.AppendLine("  （没有）");
        }

        sb.AppendLine();

        // ---- D 零变化 ----
        var d = byKind.GetValueOrDefault("D") ?? new List<CaseResult>();
        sb.AppendLine($"=== (D) 零状态变化的卡：{d.Select(c => c.Card).Distinct().Count()} 张 / {d.Count} 个用例 ===");
        sb.AppendLine("  ⚠️ 要区分两种情况，别一律当 bug。判据是**启发式**，不是定论：");
        sb.AppendLine("     D1「测试台局面不满足条件」= 程序跑完，**一条像「写」的原语都没调**");
        sb.AppendLine("        （只在卡池/手牌里扫了一遍、条件判断完就返回）");
        sb.AppendLine("     D2「该有效果却没生效」    = 调了名字像写原语的东西、状态却没变（可疑，要看）");
        sb.AppendLine("     判据细节：先取「在别的用例里确实伴随状态变化」的原语，再滤掉名字是查询形状的");
        sb.AppendLine($"     （Get/Is/Has/Array_/Conv_…）。两层都过的原语共 {writeCapable.Count} 个。");
        sb.AppendLine("     ⚠️ 这只说明「测试台局面下没效果」，**不**等于「真实对局里也没效果」。");

        var d2 = d.Where(c => c.Called.Any(writeCapable.Contains)).ToList();
        var d1 = d.Where(c => !c.Called.Any(writeCapable.Contains)).ToList();
        sb.AppendLine($"  D1（没调任何像写原语的原语）    {d1.Count,6} 个用例 / " +
                      $"{d1.Select(c => c.Card).Distinct().Count()} 张卡");
        sb.AppendLine($"  D2（调了却零变化，可疑）        {d2.Count,6} 个用例 / " +
                      $"{d2.Select(c => c.Card).Distinct().Count()} 张卡");
        sb.AppendLine();
        sb.AppendLine("  ---- D2 明细（按步数降序前 50；步数越多越可能是「真跑了却没生效」）----");
        foreach (var c in d2.OrderByDescending(c => c.Steps).Take(50))
        {
            sb.AppendLine($"  {c.Steps,7} 步  {c.Card} / {c.Entry} / {c.Placement}  调了：" +
                          $"{string.Join(",", c.Called.Where(writeCapable.Contains).Take(8))}");
        }

        if (d2.Count == 0)
        {
            sb.AppendLine("  （没有 —— 零变化的用例全都是守卫没满足，不是「该动没动」）");
        }

        sb.AppendLine();
        sb.AppendLine("  ---- D1 按步数分布（步数越少越像「开头就退出了」）----");
        foreach (var g in d1.GroupBy(c => c.Steps >= 100 ? "≥100" : c.Steps >= 50 ? "50-99" : c.Steps >= 20 ? "20-49"
                                              : c.Steps >= 10 ? "10-19" : c.Steps >= 5 ? "5-9" : "0-4")
                            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"     步数 {g.Key,-6} {g.Count(),6} 个用例");
        }

        sb.AppendLine($"  ---- D1 卡名（前 30，按卡名序）----");
        sb.AppendLine("     " + string.Join(", ",
            d1.Select(c => c.Card).Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(30)));

        sb.AppendLine();

        // ---- E 随机消费异常 ----
        var rngCases = cases.Where(c => c.Rng != 0).ToList();
        var e1 = cases.Where(c => c.Rng != c.SecondRng).ToList();
        sb.AppendLine($"=== (E) 随机消费异常 ===");
        sb.AppendLine($"  消耗过随机数的用例        {rngCases.Count,6} / {cases.Count}");
        sb.AppendLine($"  两次跑消耗次数不同（不确定）{e1.Count,6}");
        if (e1.Count > 0)
        {
            foreach (var c in e1.Take(30))
            {
                sb.AppendLine($"     {c.Card} / {c.Entry} / {c.Placement}：{c.Rng} → {c.SecondRng}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("  ---- 随机消费最多的 25 个用例（每张卡消耗几次随机数）----");
        foreach (var c in rngCases.OrderByDescending(c => c.Rng).Take(25))
        {
            sb.AppendLine($"  {c.Rng,6} 次  {c.Card} / {c.Entry} / {c.Placement}");
        }

        sb.AppendLine();

        // ---- VM 诊断 ----
        //
        // ⚠️ `UnresolvedJumps` 在这里**几乎全是良性噪声**，别被数字吓到。
        //
        // 根因（已用静态证据钉死）：`KismetLibrary.BuildEntryProgram` 重建入口程序时，
        // 只把「从入口可达的语句」收进闭包，**没有**把派发返回地址
        // （`steps[0].JumpTarget`，即合成 `pushFlow` 的目标）收进去。
        // 于是事件体跑完、最后一次 `popFlow` 弹回那个地址时，
        // `KismetVm.PopAndJump` 在 `byIndex` 里找不到它 ⇒ `UnresolvedJumps++` ⇒ 结束本次事件。
        //
        // 为什么**行为上等价**（不是 bug）：静态扫全 IR 的 692 张「首条是 pushFlow」的卡，
        // 那个返回地址**692/692 全部指向 `return`** —— 弹回 `return` 和
        // 「找不到目标 ⇒ 结束事件」是同一条路。（静态扫全部 1636 个蓝图：
        // 跳转目标缺失 0 张 / 0 处 ⇒ 没有真正解析不了的跳转。）
        //
        // ⇒ 它只说明「`UnresolvedJumps` 恒为 0」这条注释里的期望不成立，
        //   不能拿它当守卫判据。行为不受影响。
        var jumps = cases.Where(c => c.UnresolvedJumps > 0).ToList();
        var expectedJumps = jumps.Where(c => DispatchReturnIsReturn(c.Card)).ToList();
        var oddJumps = jumps.Where(c => !DispatchReturnIsReturn(c.Card)).ToList();
        sb.AppendLine($"=== VM 诊断 ===");
        sb.AppendLine($"  未解析跳转（UnresolvedJumps > 0）的用例：{jumps.Count}");
        sb.AppendLine($"    · {expectedJumps.Count} 个：用例自己的卡首条语句是 `pushFlow` ⇒ 未解析的目标就是" +
                      "「派发返回地址」（良性）。");
        sb.AppendLine($"    · {oddJumps.Count} 个：用例自己的卡首条**不是** `pushFlow` ⇒ 这次未解析跳转来自" +
                      "**被触发的别的卡**的程序（`DamageCard` / `DestroyCard` 这类原语内部会 `FireTrigger`），" +
                      "根因同上，只是归属不在本用例的卡上。");
        sb.AppendLine("    ⇒ 结论：这两条路都不代表「跳转表真的坏了」。静态复核：");
        sb.AppendLine("       · 1636 个蓝图的 `steps` + 881 个 `locals` 函数体，跳转目标缺失 **0 张 / 0 处**；");
        sb.AppendLine("       · 692 张「首条是 pushFlow」的卡，派发返回地址 **692/692 都指向 `return`**。");
        sb.AppendLine("       ⇒ `UnresolvedJumps` **不能当守卫判据**（它的注释写着「应恒为 0」，实测不成立）。");

        sb.AppendLine();
        sb.AppendLine("=== ⚠️ 这个测试能抓什么、抓不到什么 ===");
        sb.AppendLine("  能抓：异常/崩溃、撞到未实现原语、撞步数上限、程序跳转没解析、");
        sb.AppendLine("        非确定性（同种子两次不同）、「该有效果却零变化」。");
        sb.AppendLine("  抓不到：语义错（跑得通但算错 —— 重甲该不该减免、该扣多少血、");
        sb.AppendLine("          条件门读错对象）、数值/顺序错（那要跟客户端对拍）。");
        sb.AppendLine("  ⇒ 它是「覆盖/烟雾测试」，不是「正确性判据」。**没报错 ≠ 是对的。**");
        sb.AppendLine();
        sb.AppendLine($"  另外：入参是按 event-contracts.json 的**槽位名**近似填的（{mutating.Count} 个原语" +
                      "在本次运行里被观测到「调了就会改状态」）。");
        sb.AppendLine("  局面也是合成的（双方 HQ + 5 兵种 + 手牌 + 牌库）。");
        sb.AppendLine("  真实对局里事件入参不同、局面不同 ⇒ 这里没报错不代表真实路径也没问题。");

        return sb.ToString();
    }

    /// <summary>
    /// 这张卡的「派发返回地址」是不是一个 `return`（见 VM 诊断那一节的长注释）。
    /// 是 ⇒ 未解析的那次跳转与「直接结束事件」等价，属于良性诊断噪声。
    /// </summary>
    private static bool DispatchReturnIsReturn(string cardName)
    {
        if (KismetLibrary.Default?.Find(cardName) is not { } card || card.Steps.Count == 0)
        {
            return false;
        }

        if (card.Steps[0].Op != "pushFlow")
        {
            return false;
        }

        int ret = card.Steps[0].JumpTarget;
        return card.Steps.Any(s => s.Index == ret && s.Op == "return");
    }

    private static string Short(string s) => s.Length <= 24 ? s : s[..24] + "…";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
