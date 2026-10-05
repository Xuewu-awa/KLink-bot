using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Replay;

namespace KLink.Bot.ServerBridgeTest;

/// <summary>
/// **回放审计** —— 逐条动作跟踪场上单位，回答两个问题：
///
/// <list type="number">
/// <item>内核这一局**到底杀没杀过单位**？（没杀过 ⇒ 人类的效果我们没实现）</item>
/// <item>有没有**死亡单位被移动/攻击**？（⇒ 内核自己的门漏了）</item>
/// </list>
///
/// ## 为什么需要它（雪雾 2026-10-01 实测）
///
/// bot 移动了它自己认为「1/1 活着」的单位，而**客户端那边它已经死了** ——
/// 用户看到"AI 在移动死亡单位"。
/// bot 日志里 `未应用=0`，但**"动作被接受"不等于"状态一致"**：
/// 效果没实现时动作会被当成 no-op 接受，状态就悄悄漂开了。
///
/// 回放里**只有动作、没有中间状态**，所以必须自己重放并逐步取样。
/// </summary>
internal static class ReplayAudit
{
    /// <param name="identityCorrection">
    /// 是否启用身份校正（见 <c>ReplayRunner.TryCorrectIdentity</c>）。
    /// 关掉它 = **修复前**的基线，用来做前后对比与单卡门控归因。
    /// </param>
    /// <param name="identityOnly">
    /// 非 null 时只对这一张卡校正（单卡门控实验）。
    /// </param>
    /// <param name="dumpLog">
    /// 逐条动作打印**内核自己的日志**（`MatchEngine.Log`）增量。
    ///
    /// 为什么需要：审计只给「期望/实际 HQ」这种**结果**，
    /// 而"为什么少扣了 1 点"必须看内核当时做了什么 —— 例如
    /// 对局 389594 `#72 t15 AC` 之后右 HQ 该是 19 而我们算 20，
    /// 光看数字分不清是"伤害算小了"还是"某个 OnAfterAttack 没跑"。
    /// 日志按动作分段，才能把每句话归到具体哪条动作上。
    /// </param>
    public static int Run(string repoRoot, string replayBase, int verboseLimit = 0,
                          bool identityCorrection = false, string? identityOnly = null,
                          bool rngTrace = false, bool dumpLog = false, bool dupStartKredit = false,
                          bool boardTrace = false)
    {
        string snapPath = replayBase + ".json";
        string actsPath = replayBase + ".actions.json";
        if (!File.Exists(snapPath) || !File.Exists(actsPath))
        {
            Console.Error.WriteLine($"缺文件：{snapPath} / {actsPath}");
            return 2;
        }

        string dataDir = Path.Combine(repoRoot, "tem", "fyserver", "bin", "Release", "net10.0", "BotData");
        if (!Directory.Exists(dataDir))
        {
            dataDir = Path.Combine(repoRoot, "klink bot", "docs");
        }

        var db = CardDatabase.Load(dataDir);
        string ir = Path.Combine(dataDir, "card-ir.json");
        if (File.Exists(ir))
        {
            KLink.Bot.Effects.Blueprint.KismetLibrary.Initialize(ir);
        }

        var replay = ReplayData.Load(snapPath, actsPath);
        Console.WriteLine($"=== 回放审计 {Path.GetFileName(replayBase)} ===");
        Console.WriteLine($"  卡 {replay.Cards.Count} 张，动作 {replay.Actions.Count} 条，" +
                          $"左={replay.LeftPlayerId} 右={replay.RightPlayerId}");
        Console.WriteLine();

        // 逐条动作后的状态取样
        var deaths = new List<string>();          // 观察到的"从场上离场"
        var deadMoveAttempts = new List<(int ActionId, string Text)>();
        var seenDead = new HashSet<int>();        // 已经判死的 cardID
        var lastLoc = new Dictionary<int, CardLocation>();
        var wasOnBoard = new HashSet<int>();
        int step = 0;
        int killEvents = 0;

        var runner = new ReplayRunner(db);
        runner.IdentityCorrection = identityCorrection;
        runner.CollectRandomTrace = rngTrace;
        runner.KreditSlotOnDuplicateStart = dupStartKredit;
        if (identityOnly is not null)
        {
            runner.IdentityCorrectionOnly = new HashSet<string>(StringComparer.Ordinal) { identityOnly };
        }

        int seenLog = 0;
        var report = runner.Run(replay, verbose: false, onStepped: (act, st) =>
        {
            step++;

            // 逐动作分段打印内核日志（见 dumpLog 的说明）
            if (dumpLog)
            {
                // ★ 每条动作**之后**打印双方 HQ。
                //
                // 为什么必须打：审计的 ④ 只在「对不上」时才报，而「HQ 从哪一步开始偏」
                // 要的是**每一步的值** —— 只看失配点会漏掉「第一次偏开的那一步
                // 恰好没带 HQ 字段」这种情况（214436 的 bot 侧动作就不带 HQ 字段）。
                // 有了逐动作 HQ，才能把「客户端写 24 / 我们 22」归到具体哪一条动作。
                Console.WriteLine($"-- #{act.ActionId} t{act.TurnNumber} {act.ActionType} " +
                                  $"{act.PlayerId} --  [内核 回合={st.Turn} " +
                                  $"HQ 左={st.HqDefense(Side.Left)} 右={st.HqDefense(Side.Right)}]");
                while (seenLog < runner.EngineLogCount)
                {
                    Console.WriteLine("      " + runner.EngineLog(seenLog++));
                }
            }

            if (boardTrace)
            {
                static string Units(GameState state, CardLocation location) =>
                    string.Join(" ", state.CardsUnordered()
                        .Where(c => c.Location == location)
                        .OrderBy(c => c.LocationNumber)
                        .Select(c => $"{c.Name}#{c.CardId}@{c.LocationNumber}"));

                int leftSupport = st.Cards(Side.Left, Side.Left.HqOf()).Count();
                int rightSupport = st.Cards(Side.Right, Side.Right.HqOf()).Count();
                int frontline = st.CardsUnordered()
                    .Count(c => c.Location == CardLocation.BoardFrontline);
                Console.WriteLine($"   [BOARD] 半场 {leftSupport}/{GameState.HalfBoardCapacity} vs " +
                                  $"{rightSupport}/{GameState.HalfBoardCapacity}；" +
                                  $"前线 {frontline}/{st.FrontlineCapacity}（归属={st.FrontlineOwner}）");
                Console.WriteLine($"   [BOARD]   L半场: {Units(st, Side.Left.HqOf())}");
                Console.WriteLine($"   [BOARD]   R半场: {Units(st, Side.Right.HqOf())}");
                Console.WriteLine($"   [BOARD]   前线: {Units(st, CardLocation.BoardFrontline)}");
            }

            // ⚠️ **先**快照"这条动作之前谁已经死了" ——
            //    否则会把"这条动作里先合法攻击、结算时才死"的单位误判成
            //    "动了已判死的单位"（第一版就这么误报了 2 次）。
            var deadBefore = new HashSet<int>(seenDead);

            foreach (var c in st.CardsUnordered())
            {
                if (c.IsHq) continue;

                var prevLoc = lastLoc.GetValueOrDefault(c.CardId, CardLocation.NotAvailable);
                lastLoc[c.CardId] = c.Location;

                if (c.Location.IsBoard())
                {
                    wasOnBoard.Add(c.CardId);
                }

                // ★ 判死：**曾经在场上**，现在不在场上了（进了弃牌堆 / 被移除）。
                //
                //   不能用 `Defense <= 0` 判 —— `CheckDeaths` 会在**同一条动作内**
                //   把它移走，逐动作取样时根本看不到 `Defense <= 0` 那个瞬间
                //   （我第一版就是这么漏的，错报成"0 次判死"）。
                if (wasOnBoard.Contains(c.CardId)
                    && !c.Location.IsBoard()
                    && !seenDead.Contains(c.CardId)
                    && c.Location is CardLocation.Discard or CardLocation.NotAvailable)
                {
                    seenDead.Add(c.CardId);
                    killEvents++;
                    deaths.Add($"    #{act.ActionId} t{act.TurnNumber} {act.ActionType} 之后：" +
                               $"{c.Name}#{c.CardId} 离场（{prevLoc} → {c.Location}）");
                }
            }

            // 这条动作本身有没有动一个"**在这条动作之前就**已判死"的单位
            if (act.ActionType is "ML" or "AC")
            {
                int id = act.CardId;
                if (deadBefore.Contains(id))
                {
                    deadMoveAttempts.Add((act.ActionId,
                        $"    #{act.ActionId} t{act.TurnNumber} **{act.ActionType} 动了已判死的单位** " +
                        $"{st.ById(id)?.Name}#{id}"));
                }
            }

            if (verboseLimit > 0 && step <= verboseLimit)
            {
                Console.WriteLine($"  [{step,3}] #{act.ActionId} t{act.TurnNumber} {act.ActionType}");
            }
        });

        var engine = report.Engine;
        Console.WriteLine($"  应用 {report.AppliedCount}/{report.TotalActions} 条" +
                          (report.TotalActions - report.AppliedCount > 0
                              ? $"（⚠ {report.TotalActions - report.AppliedCount} 条没应用）" : ""));
        Console.WriteLine();

        Console.WriteLine($"=== ① 内核观察到的「判死」事件：{killEvents} 次 ===");
        foreach (string d in deaths.Take(40)) Console.WriteLine(d);
        if (deaths.Count > 40) Console.WriteLine($"    …（共 {deaths.Count} 条）");
        Console.WriteLine();

        var appliedDeadMoves = deadMoveAttempts
            .Where(x => report.Steps.FirstOrDefault(s => s.ActionId == x.ActionId)?.Applied == true)
            .Select(x => x.Text)
            .ToList();
        var rejectedDeadMoves = deadMoveAttempts
            .Where(x => report.Steps.FirstOrDefault(s => s.ActionId == x.ActionId)?.Applied != true)
            .Select(x => x.Text)
            .ToList();

        Console.WriteLine($"=== ② 死亡单位仍被移动/攻击：{appliedDeadMoves.Count} 次 ===");
        foreach (string d in appliedDeadMoves.Take(40)) Console.WriteLine(d);
        if (appliedDeadMoves.Count == 0) Console.WriteLine("    （没有 —— 所有此类动作都被规则门拒绝）");
        if (rejectedDeadMoves.Count > 0)
        {
            Console.WriteLine($"=== ②a 死亡单位动作被拒：{rejectedDeadMoves.Count} 次 ===");
            foreach (string d in rejectedDeadMoves.Take(40)) Console.WriteLine(d);
            Console.WriteLine("    （这些是动作流中的旧/过期尝试，不代表内核放行了死亡单位）");
        }
        Console.WriteLine();

        // ④ ★★ **HQ 对不上** —— 这是"我们的状态与客户端漂开"的**直接信号**。
        //    每条动作都带一个「对手 HQ 血量」字段（服务端记录的），
        //    内核算出来的值若与它对不上，说明我们在那一步之前就已经错了。
        var hqBad = report.Steps.Where(s => !s.HqMatches).ToList();
        Console.WriteLine($"=== ④ HQ 对不上的动作：{hqBad.Count} 条 ===");
        // ⚠️ 必须分侧看：**只有人类的动作是 ground truth**。
        //    bot 那一侧的动作是**旧内核**（没有身份校正）算出来的，
        //    用新内核重放它，本来就可能落在"另一个局面"上 —— 那不算保真度信号。
        Console.WriteLine($"    （其中人类 left {hqBad.Count(s => s.PlayerSide == "left")} 条、" +
                          $"bot right {hqBad.Count(s => s.PlayerSide == "right")} 条）");
        foreach (var s in hqBad.Take(30))
        {
            Console.WriteLine($"    #{s.ActionId} t{s.Turn} {s.ActionType}（{s.PlayerSide}）" +
                              $" 期望 {s.ExpectedHq} 实际 {s.ActualHq}");
        }
        if (hqBad.Count == 0) Console.WriteLine("    （没有 —— 我们算的 HQ 与动作流一直一致）");
        Console.WriteLine();
        Console.WriteLine("    ⚠️ 判读须知（2026-10-02，214436 查死）：`XActionStartOfTurn` 那一条");
        Console.WriteLine("       客户端是在**自己回合开始触发器跑之前**采样的，而本器是在");
        Console.WriteLine("       `EndTurn(对方) → StartTurn(我方)` 里**先跑触发器、后处理这条标记**");
        Console.WriteLine("       ⇒ 只要有一张「回合开始时对敌方 HQ 造成伤害」的卡在场");
        Console.WriteLine("       （`card_unit_garrison`：「At the start of your turn, deal 1 damage");
        Console.WriteLine("       to the enemy HQ if this unit was not attacked last turn.」），");
        Console.WriteLine("       这条 StartOfTurn 就会**必然**差那笔伤害 —— 而**同一回合的");
        Console.WriteLine("       EndOfTurn 会重新对上**（两边的状态是收敛的，只是采样点不同）。");
        Console.WriteLine("       判据：StartOfTurn 差、同回合 EndOfTurn 不差 ⇒ 采样假象，不是漂开。");
        Console.WriteLine();

        // ⑤ 未应用的动作 + 原因
        var bad = report.Steps.Where(s => !s.Applied).ToList();
        Console.WriteLine($"=== ⑤ 未应用的动作：{bad.Count} 条 ===");
        foreach (var s in bad.Take(20))
        {
            Console.WriteLine($"    #{s.ActionId} t{s.Turn} {s.ActionType}（{s.PlayerSide}）：{s.Failure}");
        }
        if (bad.Count == 0) Console.WriteLine("    （没有）");
        Console.WriteLine();

        // ⑤b ★★ **首个「人类动作」失败点** —— 这才是要查的地方
        //
        // 为什么单独标出来：**第一次漂开的位置才是根因**，后面全是它的连锁后果。
        // 而且只有**人类动作**是 ground truth —— bot 自己的动作是**旧内核**生成的，
        // 用新内核重放自然会被拒，那不是保真度信号。
        //
        // ⚠️ 这条以前没有，所以每次都要人工从一长串失败里找"第一条人类的"。
        var firstHumanFail = report.Steps
            .Where(s => !s.Applied && string.Equals(s.PlayerSide, "left", StringComparison.Ordinal))
            .OrderBy(s => s.ActionId)
            .FirstOrDefault();

        Console.WriteLine("=== ⑤b ★ 首个「人类动作」失败点（根因通常在这里）===");
        if (firstHumanFail is null)
        {
            Console.WriteLine("    ✅ **没有人类动作失败** ⇒ 这一局我们的重建与客户端完全对齐");
        }
        else
        {
            Console.WriteLine($"    #{firstHumanFail.ActionId} t{firstHumanFail.Turn} " +
                              $"{firstHumanFail.ActionType}：{firstHumanFail.Failure}");
            Console.WriteLine($"    （之前 {report.Steps.Count(s => s.ActionId < firstHumanFail.ActionId && !s.Applied)} 条失败都是 bot 自己的动作，不算信号）");
            if (firstHumanFail.Failure?.Contains("位置=Discard", StringComparison.Ordinal) == true
                && firstHumanFail.ActionType is "ML" or "AC")
            {
                Console.WriteLine("    ⇒ 这是对已离场单位的过期动作；内核已正确拒绝，不作为状态漂开点。");
            }
            else
            {
                Console.WriteLine("    ⇒ **从这里往回查**：这一步之前我们的状态就已经与客户端不同了。");
                Console.WriteLine("       建议：对比这一步之前最近几条人类动作里的 cardID 与位置，看我们从哪一步开始摆错。");
            }
        }
        Console.WriteLine();

        // ⑤c ★★ **身份不一致** —— 「效果随机/复制出来的卡，内核选中的与客户端不是同一张」。
        //
        // 为什么单列一段：这是**成体系的一类**，不是单卡 bug。随机族
        // （`card_event_atlantic_convoy`）与复制族（`card_event_seac`）的效果在锁步下
        // 由各客户端**本地**结算，内核的 RNG 种子是 `replay.MatchId`，
        // 不可能和官方客户端抽到同一张。判据是**卡组码**（不能用卡名 —— 有卡号撞车）。
        //
        // 修法见 `ReplayRunner.TryCorrectIdentity`：动作流自带卡组码，
        // 内核在动作引用到那张卡时就地把它校正成客户端说的那张。
        // 这一段同时报「发现多少条」（= 影响面）与「改了多少条」（= 修复覆盖）。
        var idEvents = report.IdentityEvents;
        int idCards = idEvents.Select(e => e.CardId).Distinct().Count();
        Console.WriteLine($"=== ⑤c ★ 身份不一致（动作自带卡组码 ≠ 内核里那张卡的卡组码）：" +
                          $"{idEvents.Count} 条 / {idCards} 张卡（已校正 {report.IdentityCorrectedCount} 条）===");
        foreach (var e in idEvents.Take(40))
        {
            Console.WriteLine($"    #{e.ActionId} t{e.Turn} {e.ActionType} cardID={e.CardId} " +
                              $"内核={e.KernelName}（码 {e.KernelCode ?? "?"}） → " +
                              $"动作码 {e.ActionCode} = {e.ActionName} [{e.Note}]");
        }

        if (idEvents.Count == 0) Console.WriteLine("    （没有 —— 内核选的卡与客户端一直一致）");
        Console.WriteLine();

        // 供 `out/audit/audit-identity-mismatch.ps1` 汇总的机器可读行
        Console.WriteLine($"⑤c 汇总：条数={idEvents.Count} 张数={idCards} " +
                          $"已校正={report.IdentityCorrectedCount} " +
                          $"内核码未知={idEvents.Count(e => e.KernelCode is null)}");
        Console.WriteLine();

        // ⑤d ★★ **目标非法** —— 「这条出牌动作的目标，客户端会拒」。
        //
        // 为什么单列一段：客户端选目标要过两道门（枚举主循环
        // `_deps/BP_Logic.g.cs:1235-1355`）：
        //   ① 卡自己的 `CanPlayFromHand`（「只能指定空军 / 老兵 / 敌方 / 友方」在这里）
        //   ② 规则库的 `CanSelectAsTarget`（隐蔽 / 敌方指令 / 费用 / 被指方自身）
        // 动作流里若记着一个**客户端认为非法**的目标，说明发出这条动作的那一侧
        // （通常是**旧内核**的 bot）选了非法目标 —— 客户端会**静默不执行**
        // （记牌器 +1、场上无变化 = 玩家报告的「虚空牌」），而我们这边把效果落了地
        // ⇒ 状态从这一刻起漂开。**这里只报告，不改行为**（见
        // `ReplayRunner.TargetGateEvents` 的注释：改成"跳过效果"需要先证明
        // 客户端在那一步确实什么都不做，否则是拿一个未验证的假设换另一个）。
        var tgEvents = report.TargetGateEvents;
        Console.WriteLine($"=== ⑤d ★ 目标过不了客户端的门（客户端会静默不执行）：{tgEvents.Count} 条 ===");
        foreach (var g in tgEvents.Take(40))
        {
            Console.WriteLine($"    #{g.ActionId} t{g.Turn} {g.PlayerSide} PC " +
                              $"{g.CardName}#{g.CardId} → " +
                              (g.Missing ? $"**没有目标**（动作里的 targetID={g.TargetId}）"
                                         : $"{g.TargetName}#{g.TargetId}") +
                              $" 判据={g.Reason}");
        }
        if (tgEvents.Count == 0)
        {
            Console.WriteLine("    （没有 —— 动作流里每一条出牌的目标，客户端的门都认）");
        }
        Console.WriteLine();

        // ⑥ ★★ **撞到但没实现的原语** —— 这是"效果静默失效"的直接证据。
        //
        //    为什么必须看它：如果一张卡的效果脚本走到某个没实现的原语就中止，
        //    那么**伤害/生成/抽牌全都不会发生**，而动作本身仍会被记成"已应用"
        //    （`未应用=0` 也照样）。于是客户端扣了血、我们没扣 ⇒ 状态漂开
        //    ⇒ 表现成"AI 移动已经死掉的单位"。
        if (report.Engine is { } e6)
        {
            // ⚠️ 把**合成**的游标失同步条目分出来：它们是 `ReplayRunner` 按
            //    `<rng-cursor-desync:内核卡->动作码卡:消费次数>` 写进去的信号，
            //    不是"没实现的原语"。混在一起会让「⑥ 未实现种数」这条判据失真。
            //
            //    2026-10-02 再分出两类：`<unresolved-cardid:...>`（兜底造卡）与
            //    `<cardid-collision-skip:...>`（发号避让），理由同上 ——
            //    它们是**发号/身份**这条线的证据，不是原语缺口。见 ⑥c。
            static bool IsSynthetic(string k) =>
                k.StartsWith("<rng-cursor-desync:", StringComparison.Ordinal)
                || k.StartsWith("<unresolved-cardid:", StringComparison.Ordinal)
                || k.StartsWith("<cardid-collision-skip:", StringComparison.Ordinal);

            var all = e6.State.UnimplementedCalls;
            var unimpl = all.Where(kv => !IsSynthetic(kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
            var desync = all.Where(kv => kv.Key.StartsWith("<rng-cursor-desync:", StringComparison.Ordinal)).ToList();
            var phantom = all.Where(kv => kv.Key.StartsWith("<unresolved-cardid:", StringComparison.Ordinal)).ToList();
            var idSkip = all.Where(kv => kv.Key.StartsWith("<cardid-collision-skip:", StringComparison.Ordinal)).ToList();

            Console.WriteLine($"=== ⑥ 撞到但**没实现**的原语：{unimpl.Count} 种 ===");
            foreach (var kv in unimpl.OrderByDescending(kv => kv.Value).Take(25))
            {
                Console.WriteLine($"    {kv.Key} ×{kv.Value}");
            }

            if (unimpl.Count == 0) Console.WriteLine("    （没有）");
            Console.WriteLine();

            Console.WriteLine($"=== ⑥a ★ RNG 游标失同步信号：{desync.Count} 条 ===");
            foreach (var kv in desync.Take(20))
            {
                Console.WriteLine($"    {kv.Key}");
            }

            if (desync.Count == 0) Console.WriteLine("    （没有 —— 内核选中的卡与客户端声明的卡组码一直一致）");
            Console.WriteLine();

            // ⑥c ★★ **发号侧的两类硬信号**。
            //
            // 为什么单列：这两类都是「客户端有这张卡、我们没有」的**直接**证据，
            // 而且它们以前**完全静默** —— 兜底造卡让动作看起来"应用成功"，
            // 避让跳号则让后续所有生成卡号悄悄偏开。
            // 判读：⑥c 有数 ⇒ 该往回查**那一步之前**的生成卡/发号，而不是这一步。
            Console.WriteLine($"=== ⑥c ★ 发号侧信号：兜底造卡 {phantom.Count} 条 / 避让跳号 {idSkip.Count} 条 ===");
            foreach (var kv in phantom.Take(20))
            {
                Console.WriteLine($"    {kv.Key} ×{kv.Value}" +
                                  "   ← 动作引用了一个**内核里没有的卡ID**：客户端发过这张卡，我们没发");
            }

            foreach (var kv in idSkip.Take(20))
            {
                Console.WriteLine($"    {kv.Key} ×{kv.Value}" +
                                  "   ← 目标号已被占用而跳过：蓝图里**没有**这一步，号段开始与客户端偏开");
            }

            if (phantom.Count == 0 && idSkip.Count == 0)
            {
                Console.WriteLine("    （没有 —— 动作引用的卡ID我们全都自己发出来过，且号段没有跳号）");
            }

            Console.WriteLine();

            Console.WriteLine($"=== ⑦ ★ RNG 游标：本局共消耗 {report.RandomConsumed} 个随机数 ===");
            Console.WriteLine("    （客户端 `cardsRandomStream` 的游标；漏一个消费点就少、多一个就多。");
            Console.WriteLine("     用 `--rng-trace` 可以打印逐次消费的流水账。）");
            if (report.RandomTrace.Count > 0)
            {
                Console.WriteLine($"    ---- 逐次消费流水（{report.RandomTrace.Count} 条，全部）----");
                foreach (string line in report.RandomTrace)
                {
                    Console.WriteLine("    " + line);
                }
            }

            Console.WriteLine();
        }

        // ⑥b ★ **静态缺口**（这一局**没跑到**、但以后一定会撞上的那些）。
        //
        // 为什么单列一段：⑥ 是"这一局撞到的"，受卡组与对局过程影响，
        // **跑一局看不到全貌**。⑥b 是"IR 里被调用、派发表没有、locals 也兜不住"的
        // **全集**（计算见 `KLink.Bot.Effects.Blueprint.DispatchGap`），
        // 与卡组无关 —— 它才是"完备性"的那个数。
        //
        // ⚠️ 这个数字有**防回归守卫**：`BotSim selftest` 的
        // 「派发表静态缺口守卫」把它和冻结基线（种类数 + 集合指纹）逐位比对，
        // 集合只要变了（多一个或少一个）就报警。所以这里报的数**只应该降**。
        try
        {
            var gaps = KLink.Bot.Effects.Blueprint.DispatchGap.Compute(db);
            Console.WriteLine($"=== ⑥b ★ 派发表静态缺口（全集，与卡组无关）：{gaps.Count} 种 / " +
                              $"{gaps.Values.Sum()} 个真缺口调用点 ===");
            Console.WriteLine($"    指纹 {KLink.Bot.Effects.Blueprint.DispatchGap.Fingerprint(gaps)}" +
                              "（守卫基线在 tools/BotSim/DispatchGap.cs，只降不升）");
            foreach (var kv in gaps.OrderByDescending(kv => kv.Value).Take(15))
            {
                Console.WriteLine($"    {kv.Key} ×{kv.Value}");
            }

            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"=== ⑥b 派发表静态缺口：算不出来（{ex.GetType().Name}: {ex.Message}）===");
            Console.WriteLine();
        }

        // ③ 人类造成的伤害总量 —— 判断"人类的效果我们实现了没有"
        if (engine is not null)
        {
            var st = engine.State;
            Console.WriteLine("=== ③ 终局场上状态（右侧 = bot）===");
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                Console.WriteLine($"  {side.ToWire()}：HQ {st.HqDefense(side)}，" +
                                  $"场上 {st.Board(side).Count} 个");
                foreach (var c in st.Board(side).OrderBy(c => c.CardId))
                {
                    Console.WriteLine($"     #{c.CardId} {c.Name} @{c.Location}#{c.LocationNumber} " +
                                      $"{c.Attack}/{c.Defense} 活着={c.AliveOnBoard}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 判读 ===");
        if (killEvents == 0)
        {
            Console.WriteLine("  ⚠️ **内核整局没有杀过任何单位** —— 人类的效果（指令/攻击）");
            Console.WriteLine("     很可能根本没实现 ⇒ 我们这边的血量一直没扣 ⇒ 状态漂开。");
            Console.WriteLine("     这解释了「客户端认为已死、我们移动它」。");
        }
        else if (appliedDeadMoves.Count > 0)
        {
            Console.WriteLine("  ⚠️ 有**死亡单位被移动** ⇒ 内核自己的门漏了（不是漂开）。");
        }
        else
        {
            Console.WriteLine("  ✅ 内核杀过单位、也没有移动死亡单位 ⇒ 漂开发生在别处。");
            if (rejectedDeadMoves.Count > 0)
            {
                Console.WriteLine("     另有死亡单位动作被拒，属于动作流中的旧/过期尝试，不算内核放行。");
            }
        }

        return 0;
    }
}
