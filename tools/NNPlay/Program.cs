using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
// ⚠️ `NnPolicy` / `AtomicAction` 已从本工程**搬进内核库** `KLink.Bot.Server` ——
//    因为服务端（fyserver）也要用同一份策略，而它不该引用一个 Exe 工具工程。
//    tools/NNPlay 现在只是库的一个消费者。
using KLink.Bot.Server;

namespace KLink.Bot.NNPlayTool;

/// <summary>
/// NNPlay —— 让**训练出来的神经网络**下场真打一局。
///
/// <code>
///   NNPlay play --model out/nn-model.bin [--nn-side left|right] [--seed 12345]
///               [--deck-nn NAME] [--deck-opp NAME] [--log out/nn-vs-greedy.log]
///               [--moves] [--verify-all] [--no-ir] [--dump-journal] [--top 6]
/// </code>
///
/// 每一手都是：枚举所有合法动作 → 逐个试算（见 <see cref="Replayer"/>）→
/// 把结果局面编码成 <see cref="StateEncoder.Dim"/> 维（<see cref="StateEncoder"/>，
/// 与训练同一份代码）→ 模型算「我方胜率」→ 取胜率最高的那一步。对手是 <see cref="GreedyBot"/>。
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
            return cmd switch
            {
                "play" => Play(opts),
                "help" or "--help" or "-h" => Help(),
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
            return 1;
        }
    }

    private static int Play(Dictionary<string, string> opts)
    {
        // ---------------- 数据 ----------------
        string dataDir = FindDataDirectory();
        var db = CardDatabase.Load(dataDir);

        string modelPath = opts.GetValueOrDefault("model", "nn-model.bin");
        var model = NnModel.Load(modelPath);

        string vecDir = FindDirWith("card-vectors.json");
        var vecs = StateEncoder.LoadCardVectors(vecDir);

        // ✅ 蓝图 IR **默认加载** —— 因为 NNTrain 的 dump **已经加载了**
        //    （见 tools/NNTrain/Program.cs 的 `KismetLibrary.Initialize`，
        //     以及它开头的启动横幅「蓝图 IR: N 张卡可解释」）。
        //    训练数据是在「手写脚本 + 蓝图 IR」的引擎上生成的，
        //    推理必须跟训练同一个动力学，否则状态分布对不上。
        //
        //    要复现「无效果内核」的旧行为（分布外对照）才加 --no-ir。
        //
        // ⚠️ 这里原先**默认不加载**，注释还写着「因为 NNTrain 的 dump 没加载」——
        //    那段注释在 NNTrain 修好之后就已经反了。后果是默认配置在做分布外推理，
        //    实测差别很大（同一模型、同一对位、seeds 1..10）：
        //        默认加载 IR : 20 胜 0 负，攻击 19.4 次/局
        //        --no-ir     :  4 胜 6 负，攻击 16.9 次/局
        bool loadIr = !opts.ContainsKey("no-ir");
        if (loadIr)
        {
            string irPath = Path.Combine(dataDir, "card-ir.json");
            if (File.Exists(irPath))
            {
                KLink.Bot.Effects.Blueprint.KismetLibrary.Initialize(irPath);
            }
        }

        var lib = KLink.Bot.Effects.Blueprint.KismetLibrary.Default;

        Console.WriteLine($"卡牌数据 : {db.Count} 张（{dataDir}）");
        Console.WriteLine($"模型     : {model.Describe()}");
        Console.WriteLine($"编码规格 : {model.EncoderSpec}");
        Console.WriteLine(lib is null
            ? "蓝图 IR  : ⚠ **未加载**（--no-ir）—— 卡牌效果不执行，与 NNTrain dump **不同分布**"
            : $"蓝图 IR  : 已加载（{lib.CardCount} 张卡）—— 与 NNTrain dump 同分布");
        Console.WriteLine($"手写脚本 : {KLink.Bot.Effects.CardEffectScripts.Count} 张，触发 {KLink.Bot.Effects.CardEventScripts.Count} 条");
        Console.WriteLine();

        // ---------------- 对局参数 ----------------
        ulong seed = (ulong)GetInt(opts, "seed", 12345);
        string nnSideName = opts.GetValueOrDefault("nn-side", "left").ToLowerInvariant();
        Side nnSide = nnSideName == "right" ? Side.Right : Side.Left;
        Side oppSide = nnSide.Opposite();

        string deckA = opts.GetValueOrDefault("deck-a", MetaDecks.All[0].Name);
        string deckB = opts.GetValueOrDefault("deck-b", MetaDecks.All[1].Name);
        string deckNn = opts.GetValueOrDefault("deck-nn", nnSide == Side.Left ? deckA : deckB);
        string deckOpp = opts.GetValueOrDefault("deck-opp", nnSide == Side.Left ? deckB : deckA);

        var leftDeck = LoadDeck(db, nnSide == Side.Left ? deckNn : deckOpp);
        var rightDeck = LoadDeck(db, nnSide == Side.Left ? deckOpp : deckNn);

        // 「移动上前线」默认**开**。原先默认关，但实测这是个会严重低估模型的默认值：
        // 关掉时模型主动过牌率 45~69%、快攻胜率 21%；打开后过牌率降到 9~17%、快胜率 79%。
        // 原因是跨前线射程规则修好之后（半场只有 range>=2 能跨），不推进前线就几乎没有输出，
        // 「攒牌不出」是对规则的理性反应而不是模型缺陷。要复现旧口径用 --no-moves。
        bool allowMoves = !opts.ContainsKey("no-moves");
        bool rollout = opts.ContainsKey("rollout");
        bool verifyAll = opts.ContainsKey("verify-all");
        bool dumpJournal = opts.ContainsKey("dump-journal");
        int top = GetInt(opts, "top", 6);
        int maxSteps = GetInt(opts, "max-steps", 3000);
        string logPath = opts.GetValueOrDefault("log", "");
        bool quiet = opts.ContainsKey("quiet");

        var log = new Log(logPath, quiet);

        log.Write($"对局   : NN（{deckNn}） 作为 {nnSide.ToWire()}  vs  GreedyBot（{deckOpp}） 作为 {oppSide.ToWire()}");
        log.Write($"种子   : {seed}   候选动作里的移动: {(allowMoves ? "开" : "关")}   每个候选都校验重放: {(verifyAll ? "是" : "否")}");
        log.Write($"试算   : {(rollout ? "走一步后用 GreedyBot 走完本回合再估值（贴合训练分布）" : "只走一步就估值（训练数据是回合交界处 ⇒ 分布外）")}");
        log.Write($"模型   : {modelPath}");
        log.Write(new string('=', 100));

        // ---------------- 开局 ----------------
        var engine = new MatchEngine(db, leftDeck, rightDeck, seed);
        var greedy = new GreedyBot("greedy");
        var policy = new NnPolicy(model, vecs, nnSide, allowMoves, rollout);
        var replayer = new Replayer(db, leftDeck, rightDeck, seed);

        var journal = new List<AtomicAction>();
        engine.Start();

        int seenEngineLog = 0;
        int actionNo = 0;
        int decisionNo = 0;
        int replayMismatch = 0;
        var rejectedTotal = 0;
        var rejectedByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var chosenByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        int greedySteps = 0;
        var decisions = new List<Decision>();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // 每回合开始前打印一次局面摘要（看得出牌/场面）
        BoardLine(engine, "开局");
        Drain();

        int steps = 0;
        while (!engine.State.IsFinished && steps++ < maxSteps)
        {
            var st = engine.State;

            if (st.ActiveSide == nnSide)
            {
                // ---------- NN 决策 ----------
                if (verifyAll)
                {
                    // 每个候选都会单独校验；这里不重复
                }
                else
                {
                    var probe = replayer.Build(journal);
                    if (ActionJournal.Fingerprint(probe) != ActionJournal.Fingerprint(engine))
                    {
                        replayMismatch++;
                        log.Write($"❌ 重放与实时引擎不一致（决策 #{decisionNo + 1}）—— 试算不可信，中止");
                        break;
                    }
                }

                Decision d;
                try
                {
                    d = policy.Choose(engine, journal, replayer, ++decisionNo, verifyAll);
                }
                catch (InvalidOperationException ex)
                {
                    log.Write($"❌ {ex.Message}");
                    break;
                }

                decisions.Add(d);
                rejectedTotal += d.Rejected;

                log.Write("");
                log.Write($"🧠 决策 #{d.Index}  [T{d.Turn}/{d.Side.ToWire()}]  "
                          + $"kredit {st.Kredits(nnSide)}/{st.MaxKredits(nnSide)}  "
                          + $"手牌 {st.Hand(nnSide).Count}  场面 {st.Board(nnSide).Count}"
                          + $"  HQ {st.HqDefense(nnSide)}/{st.HqDefense(oppSide)}");
                log.Write($"   当前局面估值 P({nnSide.ToWire()} 胜) = {d.CurrentWinProb:P1}"
                          + $"   （枚举 {d.Enumerated} 个，合法 {d.LegalCount} 个，内核拒绝 {d.Rejected} 个）");

                // 候选表：按胜率排序取前 top 个，外加「结束回合」那一项（必须能看到）
                var ranked = d.Candidates.OrderByDescending(c => c.WinProb).ToList();
                var shown = new List<ScoredAction>();
                foreach (var c in ranked)
                {
                    if (shown.Count >= top) break;
                    shown.Add(c);
                }
                var endTurn = d.Candidates.FirstOrDefault(c => c.Action is EndTurnAction);
                if (endTurn is not null && !shown.Contains(endTurn)) shown.Add(endTurn);

                foreach (var c in shown)
                {
                    bool isChosen = ReferenceEquals(c, d.Chosen);
                    string mark = isChosen ? "✔" : " ";
                    log.Write($"   {mark} {c.WinProb:P1}  {c.Action.Describe(st)}{(c.Note.Length > 0 ? "  " + c.Note : "")}");
                }
                if (ranked.Count > shown.Count)
                {
                    log.Write($"     …还有 {ranked.Count - shown.Count} 个候选没列（--top {top} 可调）");
                }

                log.Write($"   → 选 [{d.Chosen.WinProb:P1}] {d.Chosen.Action.Describe(st)}");

                // ---------- 执行 ----------
                actionNo++;
                chosenByKind[d.Chosen.Action.Kind] = chosenByKind.GetValueOrDefault(d.Chosen.Action.Kind) + 1;
                log.Write($"#{actionNo,4} [T{st.Turn}/{nnSide.ToWire()}] NN   {d.Chosen.Action.Describe(st)}"
                          + (d.Chosen.Note.Length > 0 ? "  " + d.Chosen.Note : ""));
                d.Chosen.Action.Apply(engine);
                journal.Add(d.Chosen.Action);
                Drain();
            }
            else
            {
                // ---------- GreedyBot 一整个回合 ----------
                int before = st.ActionLog.Count;
                int journalBefore = journal.Count;
                greedy.PlayTurn(engine, oppSide);
                var derived = ActionJournal.Derive(engine.State, before);
                foreach (var a in derived)
                {
                    actionNo++;
                    greedySteps++;
                    log.Write($"#{actionNo,4} [T{a.LogTurn}/{a.LogSide.ToWire()}] 对手 {a.DescribeName(engine.State)}");
                    journal.Add(a);
                }
                Drain();

                // ---- 诊断：把「引擎记的原始动作」与「反推出来的原子动作」逐条并列 ----
                //
                // 为什么要这个开关：`Derive` 只认 4 种 ActionType，而且出牌目标是从
                // **紧随其后的子动作**里取的 —— 一旦引擎在中间插了别的记录，目标就会丢。
                // 这类偏差在指纹校验里只表现为「某个字段差 1」，看不出是哪一步造成的。
                if (dumpJournal)
                {
                    DumpJournal(log, engine, before, derived);
                }

                // ---- 诊断：把反推出来的动作逐个应用到「与实时引擎同状态的重放副本」上，
                //      看哪一步被内核拒了（`Replayer.Build` 是**忽略返回值**的，
                //      被拒的动作会静默消失 —— 那正是「重放少花了一点 kredit」的形态）。----
                if (dumpJournal)
                {
                    VerifyDerivedOneByOne(log, replayer, journal, journalBefore, derived);
                }
            }
        }

        // ---------------- 结算 ----------------
        double secs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalSeconds;
        var final = engine.State;

        // 最终整体校验：把整局流水账重放一遍，必须与实时引擎逐字一致
        bool finalOk = false;
        string? finalDiff = null;
        try
        {
            var rebuilt = replayer.Build(journal);
            finalOk = ActionJournal.Fingerprint(rebuilt) == ActionJournal.Fingerprint(engine);
            if (!finalOk)
            {
                finalDiff = FirstDiff(ActionJournal.Fingerprint(rebuilt), ActionJournal.Fingerprint(engine));
            }
        }
        catch (Exception ex)
        {
            finalDiff = ex.Message;
        }

        log.Write("");
        log.Write(new string('=', 100));
        log.Write("结果");
        log.Write(new string('=', 100));
        log.Write($"胜负      : {(final.IsFinished ? $"{final.Winner.ToWire()} 获胜（{final.WinnerReason}）" : "★ 未分胜负（达到步数上限或卡住）")}");
        log.Write($"NN 是      : {nnSide.ToWire()}  ⇒ "
                  + (final.IsFinished
                        ? (final.Winner == nnSide ? "✅ NN 赢" : "❌ NN 输")
                        : "—"));
        log.Write($"回合数    : {final.Turn}    总步数 {steps}    用时 {secs:F1}s");
        log.Write($"HQ        : NN {final.HqDefense(nnSide)}  对手 {final.HqDefense(oppSide)}");
        log.Write($"场面      : NN {final.Board(nnSide).Count} 个单位  对手 {final.Board(oppSide).Count} 个单位");
        log.Write($"NN 决策   : {decisions.Count} 次；平均候选 {(decisions.Count == 0 ? 0 : decisions.Average(d => d.Enumerated)):F1} 个，"
                  + $"平均合法 {(decisions.Count == 0 ? 0 : decisions.Average(d => d.LegalCount)):F1} 个");
        log.Write($"内核拒绝  : {rejectedTotal} 个候选（枚举超集里被 PlayCard/Attack/MoveUnit 拒掉的）");
        log.Write($"NN 动作   : {string.Join("  ", chosenByKind.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}×{kv.Value}"))}");
        log.Write($"对手动作  : {greedySteps} 步");
        log.Write($"重放      : {replayer.Stats()}");
        log.Write($"最终校验  : {(finalOk ? "✅ 整局重放与实时引擎逐字一致（状态副本机制可信）" : "❌ 不一致：" + finalDiff)}");
        if (replayMismatch > 0)
        {
            log.Write($"⚠ 决策期校验失败 {replayMismatch} 次");
        }

        // 模型对自己每一步的平均估值 —— 看它是不是真的在"觉得自己能赢"
        if (decisions.Count > 0)
        {
            log.Write($"模型平均自评: 决策前 {decisions.Average(d => (double)d.CurrentWinProb):P1}，"
                      + $"选中后 {decisions.Average(d => (double)d.Chosen.WinProb):P1}");
            log.Write($"选中「结束回合」{decisions.Count(d => d.Chosen.Action is EndTurnAction)} 次"
                      + $"/{decisions.Count} 次决策");
            int missedWins = decisions.Count(d => d.Candidates.Any(c => c.WinsGame) && !d.Chosen.WinsGame);
            if (missedWins > 0)
            {
                log.Write($"⚠ 有 {missedWins} 次决策存在「一步直接获胜」的候选，但模型没选它");
            }
        }

        if (!final.IsFinished)
        {
            log.Write("⚠ 没打完 —— 别把它当成有效对局结果，先查为什么会卡住");
        }

        log.Flush();
        Console.WriteLine();
        Console.WriteLine($"日志已写入: {(logPath.Length > 0 ? Path.GetFullPath(logPath) : "（未指定 --log，只打到屏幕）")}");
        return 0;

        // ---------------- 局部函数 ----------------
        void Drain()
        {
            while (seenEngineLog < engine.Log.Count)
            {
                log.Write("        │ " + engine.Log[seenEngineLog++]);
            }
        }
    }

    /// <summary>一行局面摘要。</summary>
    private static void BoardLine(MatchEngine engine, string tag)
    {
        var st = engine.State;
        Console.WriteLine($"[{tag}] T{st.Turn} 行动方 {st.ActiveSide.ToWire()}  "
                          + $"L: kredit {st.Kredits(Side.Left)}/{st.MaxKredits(Side.Left)} 手 {st.Hand(Side.Left).Count} 场 {st.Board(Side.Left).Count} HQ {st.HqDefense(Side.Left)}  |  "
                          + $"R: kredit {st.Kredits(Side.Right)}/{st.MaxKredits(Side.Right)} 手 {st.Hand(Side.Right).Count} 场 {st.Board(Side.Right).Count} HQ {st.HqDefense(Side.Right)}");
    }

    private static string FirstDiff(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                int s = Math.Max(0, i - 80);
                return $"第 {i} 字符处：\n      重放 …{a[s..Math.Min(a.Length, i + 80)]}\n      实时 …{b[s..Math.Min(b.Length, i + 80)]}";
            }
        }
        return $"长度不同：重放 {a.Length}，实时 {b.Length}";
    }

    // ==================== 诊断（--dump-journal）====================

    /// <summary>
    /// 把「引擎 <c>State.ActionLog</c> 里的原始记录」与「<see cref="ActionJournal.Derive"/>
    /// 反推出来的原子动作」逐条并列打印。
    ///
    /// 用途：指纹校验只说「某个字段差了 1」，说不出是哪一步造成的。
    /// 这里能一眼看出「某条 <c>XActionAttackCard</c> 的 defenderCardID 反推错了」
    /// 或「某个动作在日志里根本没有对应记录」。
    /// </summary>
    private static void DumpJournal(Log log, MatchEngine engine, int from, List<AtomicAction> derived)
    {
        var st = engine.State;
        log.Write($"        ┌─ 原始 ActionLog[{from}..{st.ActionLog.Count - 1}] ─────────────");
        for (int i = from; i < st.ActionLog.Count; i++)
        {
            var a = st.ActionLog[i];
            string data = string.Join(",", a.ActionData.Select(kv => $"{kv.Key}={kv.Value}"));
            log.Write($"        │ #{i} {a.ActionType} p={a.Player.ToWire()} T={a.TurnNumber}  {{{data}}}");
            foreach (var s in a.SubActions)
            {
                string vals = string.Join(",", s.Values.Select(v => $"{v.Name}={v.Value}"));
                log.Write($"        │      └ {s.Name}  {vals}");
            }
        }
        log.Write($"        ├─ Derive → {derived.Count} 个原子动作 ─────────────");
        foreach (var a in derived)
        {
            string raw = a switch
            {
                PlayCardAction p => $"card={p.CardId}({st.ById(p.CardId)?.Name ?? "?"}) target={p.TargetId}({st.ById(p.TargetId)?.Name ?? "-"})",
                AttackAction at => $"attacker={at.AttackerId}({st.ById(at.AttackerId)?.Name ?? "?"}) defender={at.DefenderId}({st.ById(at.DefenderId)?.Name ?? "?"})",
                MoveAction m => $"unit={m.UnitId}({st.ById(m.UnitId)?.Name ?? "?"}) slot={m.Slot}",
                _ => "",
            };
            log.Write($"        │ T{a.LogTurn}/{a.LogSide.ToWire()} {a.Kind,-6} {raw}");
        }
        log.Write("        └──────────────────────────────────────────────");
    }

    /// <summary>
    /// 把反推出来的动作**逐个**应用到一个「与实时引擎同状态的重放副本」上，
    /// 报告哪个被内核拒绝了（<c>Apply</c> 返回 false）。
    ///
    /// <para>
    /// <b>为什么必须单独查这一条</b>：<see cref="Replayer.Build"/> 里是
    /// <c>a.Apply(e);</c> —— **返回值被丢掉**。被拒的动作不会抛错、不会记数，
    /// 只是**静默不发生**：少扣的费用、少掉的牌、少死的人全部吞掉。
    /// 表现形式恰好就是诊断报告里那句「实时引擎多花了 1 点费用，而重放没能重建这次花费」。
    /// </para>
    /// </summary>
    private static void VerifyDerivedOneByOne(Log log, Replayer replayer,
        List<AtomicAction> journal, int journalBefore, List<AtomicAction> derived)
    {
        var prefix = journal.Take(journalBefore).ToList();
        foreach (var a in derived)
        {
            var e = replayer.Build(prefix);
            bool ok = a.Apply(e);
            var live = e.State;

            // 应用之后再看：这条动作在当前副本上是否"看起来发生了"
            log.Write($"        ⊙ 逐步校验 {a.Kind,-6} {a.DescribeName(live)}  → Apply={ok}"
                      + $"  kredit L{live.Kredits(Side.Left)}/R{live.Kredits(Side.Right)}"
                      + $" T{live.Turn}/{live.ActiveSide.ToWire()}");
            prefix.Add(a);
        }

        // 全部应用完之后，与实时引擎比一次（这就是 Build(journal) 做的事，只是拆开了）
        var full = replayer.Build(prefix);
        string fp = ActionJournal.Fingerprint(full);
        log.Write($"        ⊙ 逐步重放完成后：Turn={full.State.Turn} Lkredit={full.State.Kredits(Side.Left)} "
                  + $"Rkredit={full.State.Kredits(Side.Right)}  指纹长度={fp.Length}");
    }

    // ==================== 辅助 ====================

    private sealed class Log
    {
        private readonly StreamWriter? _w;
        private readonly bool _quiet;

        public Log(string path, bool quiet)
        {
            _quiet = quiet;
            if (!string.IsNullOrEmpty(path))
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _w = new StreamWriter(path, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
            }
        }

        public void Write(string line)
        {
            if (!_quiet) Console.WriteLine(line);
            _w?.WriteLine(line);
        }

        public void Flush() => _w?.Flush();
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
                return full;
            }
        }

        throw new DirectoryNotFoundException("找不到 card-effects.json");
    }

    /// <summary>
    /// 找含指定文件的目录。输出目录里的 <c>Data\</c> 没有 <c>card-vectors.json</c>
    /// （它不在 KLink.Bot.csproj 的拷贝清单里），所以会回退到 <c>klink bot\docs</c>。
    /// </summary>
    private static string FindDirWith(string fileName)
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Data"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "klink bot", "docs"),
        };

        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(Path.Combine(full, fileName)))
            {
                return full;
            }
        }

        throw new DirectoryNotFoundException($"找不到 {fileName}");
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        foreach (string a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) result[pending] = "true";
                pending = a[2..];
            }
            else if (pending is not null)
            {
                result[pending] = a;
                pending = null;
            }
        }
        if (pending is not null) result[pending] = "true";
        return result;
    }

    private static int GetInt(Dictionary<string, string> opts, string key, int fallback)
        => opts.TryGetValue(key, out string? v) && int.TryParse(v, out int i) ? i : fallback;

    private static int Help()
    {
        Console.WriteLine("""
            NNPlay —— 让训练好的神经网络下场真打一局

              NNPlay play [选项]
                  --model PATH      模型文件（默认 nn-model.bin）
                  --nn-side left|right   NN 坐哪边（默认 left = 先手）
                  --seed S          随机种子（默认 12345）
                  --deck-nn NAME    NN 的卡组（默认 德芬车）
                  --deck-opp NAME   对手的卡组（默认 德澳老兵）
                  --log PATH        日志文件（默认不写文件）
                  --no-moves        候选动作里**不含**「移动上前线」
                                    （⚠ 默认是**含**。关掉会严重低估模型：实测主动过牌率
                                     45~69%、快攻胜率 21%；打开后过牌率 9~17%、快攻胜率 79%。
                                     跨前线射程规则修好后，不推进前线几乎没有输出。
                                     只在复现旧口径时才用）
                  --rollout         每个候选试算后，用 GreedyBot 把本方本回合走完再估值
                                    （训练数据是「回合交界」的局面，一步试算属分布外）
                  --verify-all      每个候选的重放都与实时引擎校验一遍（慢）
                  --no-ir           不加载蓝图 IR（⚠ 卡牌效果不执行 ⇒ 与训练数据**不同分布**，
                                    只用于复现旧的无效果内核做对照；默认是**加载**）
                  --dump-journal    打印每个对手回合的「原始 ActionLog ↔ 反推动作」对照，
                                    并把反推动作逐个应用到重放副本上，报告哪一步被内核拒了
                  --top N           候选表里列前 N 个（默认 6）
                  --quiet           不打屏幕，只写文件

            决策 = 枚举所有合法动作 → 逐个试算（重放出一份状态副本）→
                   编码成 StateEncoder.Dim 维（与训练同一份 StateEncoder）→ 模型算胜率 → 取最大。

            ⚠️ 维度不是写死的：模型文件里存了 dim/cardDim/zones/perSide，
               NnModel.Load 会拿它和当前 StateEncoder 的常量逐项对账，
               编码器改过版（例如 v0 740 → v1 925）就用不了旧模型，会**直接报错**而不是静默出错。
            """);
        return 0;
    }
}
