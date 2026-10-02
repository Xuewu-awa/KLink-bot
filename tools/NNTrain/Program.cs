using System.Text;
using System.Text.Json;
using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;

namespace KLink.Bot.NNTrain;

/// <summary>
/// 验证「向量方案对不对」的最小管线。
///
/// 判据（关键）：
///   用一个网络从局面预测胜负。**准确率明显高于 50% = 编码里有信号；
///   ≈50% = 编码是垃圾。** 这个判据直接回答向量方案是否有意义，
///   而且不需要动作编码 —— 最省事。
///
/// 三个命令：
///   NNTrain dump   --games 10000 --out data.bin
///   NNTrain train  --data data.bin --epochs 30 [--out nn-model.bin]
///   NNTrain verify --data data.bin --model nn-model.bin [--split-seed S]
///
/// 状态向量 v1（925 维，见 <see cref="StateEncoder"/> 的类注释）：
///   全局       3 维   Turn/30 / ActiveSide==视角方 / CardsPlayedThisTurn
///   每方全局   6 维   HQ / kredits / 上限 / 手牌数 / 牌库数 / 场上数
///   每方每区域 5 区 × (池化卡向量 90 维 + 计数 1) = 455（v0 是 4 区，缺**半场**）
///   卡向量 = 标量 9 + 关键字 11 + 交互 55 + 效果标签 15
///
/// ⭐ **卡组对位是随机的**（v0 是写死的 <c>decks[g%22] vs decks[(g+7)%22]</c>，
///    于是 10,000 局只有 22 种对位、极端一边倒 ⇒ 模型只要认出「是哪一对卡组」
///    就能白拿 75~79%，那是与打法无关的捷径。见 docs/NN训练诊断.md §3.2 / §5）。
///    现在每局从 22 套里抽 2 套**不同**的、再随机决定谁在左；
///    同样的 <c>--seed</c> + <c>--games</c> 必须产出同样的数据。
///
/// ⚠️ **编码已抽到 <see cref="StateEncoder"/>（src/KLink.Bot/NN/）**，训练与推理共用一份。
///    以前它写死在本文件的局部函数里，NN 玩家要么复制一份（编码漂移 = 静默错误），
///    要么没法用。抽取是逐字搬运，验证方式：
///      NNTrain dump --games 100 --seed 1 --out x.bin   → 与抽取前逐字节相同，
///      且是既有 out/nn-data.bin 的前缀。
///
/// 训练用**手写 MLP**（不依赖 numpy/torch）—— 本机 Python 3.14 装不了 torch，
/// 而这点规模手写绰绰有余。
/// </summary>
internal static class Program
{
    // 编码常量统一从 StateEncoder 取 —— 别再在这里写死一份。
    // ⚠️ PerSide/Dim 是 StateEncoder 静态构造函数里算出来的（v2 起牌库区不带卡向量），
    //    所以这里只能用 static readonly，不能用 const。
    private const int CardDim = StateEncoder.CardDim;
    private const int Zones = StateEncoder.Zones;
    private static readonly int PerSide = StateEncoder.PerSide;
    private static readonly int Dim = StateEncoder.Dim;

    /// <summary>dump 文件头的魔数（v2：12 字节头 + 每条 dim+3 个 float）。</summary>
    private const int DataMagic = 0x324C4B41;      // 'AKL2'
    private const int OldDataMagic = 0x314C4B41;   // 'AKL1'（v1：8 字节头 + 每条 dim+2）

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var opts = ParseOpts(args.Skip(1));

        try
        {
            string dataDir = FindDir("card-effects.json");
            var db = CardDatabase.Load(dataDir);
            var vecs = StateEncoder.LoadCardVectors(FindDir("card-vectors.json"));

            // ⚠️⚠️ 必须显式加载 Kismet IR —— 内核**不会**自己去加载。
            //       漏掉这一步，`KismetLibrary.Default` 就是 null，`Api.RunCardEffect` 对
            //       除手写脚本之外的**所有卡牌效果都不执行**，而且会把每张这样的卡记成
            //       `<card:X>` 未实现 —— 看起来像"这些卡缺实现"，实际是"效果系统没打开"。
            //
            //       这个坑在 BoardCompare 里踩过一次（当时报出去的数字全是在无效果引擎上测的）。
            //       但**没有同步检查 NNTrain** —— 结果前几批训练数据（74.3% / 76.5% / 76.29%）
            //       全是在「白板对拼」上生成的：效果从没发生过，胜负只能由身材和卡组身份决定。
            //       诊断报告见 klink bot/docs/NN训练诊断.md，那里用「胜率与单位占比 +0.64 强相关、
            //       与效果实现覆盖率 −0.17 无关」反向印证了这一点。
            //
            //       而 `CardEffectScripts`（手写脚本）全库只有 1 张卡，所以 IR 不加载 ≈ 全白板。
            string irPath = Path.Combine(dataDir, "card-ir.json");
            if (File.Exists(irPath))
            {
                KLink.Bot.Effects.Blueprint.KismetLibrary.Initialize(irPath);
            }

            var irLib = KLink.Bot.Effects.Blueprint.KismetLibrary.Default;
            Console.WriteLine($"卡库 {db.Count} 张，卡向量 {vecs.Count} 条，维度 {Dim}/局面");
            Console.WriteLine(irLib is not null
                ? $"蓝图 IR: {irLib.CardCount} 张卡可解释，手写脚本 {KLink.Bot.Effects.CardEffectScripts.Count} 张"
                : $"⚠ 蓝图 IR **未加载**（{KLink.Bot.Effects.Blueprint.KismetLibrary.LoadError ?? "文件不存在"}）"
                  + " —— 生成的数据将不含卡牌效果！");
            Console.WriteLine();

            return cmd switch
            {
                "dump" => Dump(db, vecs, opts),
                "train" => Train(opts),
                "verify" => Verify(opts),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ==================== 编码 ====================
    //
    // ⚠️ 编码本体已搬到 src/KLink.Bot/NN/StateEncoder.cs（训练与推理共用一份）。
    //    本文件里原来那份局部实现（CardVecs / LoadCardVectors / Encode）已删除 ——
    //    逐字搬运，没有改任何算式。

    // ==================== dump ====================

    private static int Dump(CardDatabase db, StateEncoder.CardVecs vecs, Dictionary<string, string> opts)
    {
        int games = GetInt(opts, "games", 10000);
        ulong seed = (ulong)GetInt(opts, "seed", 1);
        string outPath = opts.GetValueOrDefault("out", "nn-data.bin");
        int everyTurns = GetInt(opts, "every", 1);

        var decks = MetaDecks.All;
        Console.WriteLine($"自对弈 {games} 局；每局从 {decks.Count} 套元卡组里**随机抽 2 套不同的**"
                          + "（seed 派生的独立随机流）+ 左右随机互换，输出 " + outPath);

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite,
                                      FileShare.Read, 1 << 22);
        using var bw = new BinaryWriter(fs);
        bw.Write(DataMagic);
        bw.Write(Dim);
        bw.Write(decks.Count);   // 对位编号的进制：pair = 左下标 × deckCount + 右下标

        long rows = 0;
        int leftWins = 0, rightWins = 0, unfinished = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ⭐ 对位清单：哪一局是哪一对卡组（**按卡组下标**，不是按名字 ——
        //    基线脚本要按它分组算「背对位」的平凡基线，见 nn-baseline3.py）。
        var pairs = new List<(int G, int L, int R)>(games);

        for (int g = 0; g < games; g++)
        {
            // ⭐ 每局**随机抽两个不同的卡组** + **左右随机互换**。
            //
            // 为什么必须这样：v0 写死 `decks[g%22]` vs `decks[(g+7)%22]`，
            // 10,000 局只有 22 种对位，且很多对位一边倒到 454:0 ——
            // 而卡组身份在编码里完全可见（Deck 区池化向量 + 起手手牌），
            // 于是「认出这是哪一对卡组」就能白拿 75~79%。这条捷径与打法好坏无关。
            //
            // 随机流由 (seed, g) 派生，**与对局本身的洗牌流 `seed+g` 无关**：
            // 同样的命令行 ⇒ 同样的卡组分配 ⇒ 同样的数据（可复现）。
            var pick = new DeterministicRandom(seed ^ (0x9E3779B97F4A7C15UL * ((ulong)g + 1)));
            int di = pick.Next(decks.Count);
            int dj = pick.Next(decks.Count - 1);
            if (dj >= di) dj++;                       // 保证 dj ≠ di
            bool swap = pick.Next(2) == 1;            // 左右随机互换
            int leftIdx = swap ? dj : di;
            int rightIdx = swap ? di : dj;
            pairs.Add((g, leftIdx, rightIdx));

            string da = decks[leftIdx].Name;
            string dbName = decks[rightIdx].Name;
            if (da == dbName) continue;               // 抽签逻辑已保证不等，留作断言（不该触发）

            var left = ExpandDeck(db, da);
            var right = ExpandDeck(db, dbName);
            if (left.Count == 0 || right.Count == 0) continue;

            var engine = new MatchEngine(db, left, right, seed + (ulong)g);
            var botL = new GreedyBot("L");
            var botR = new GreedyBot("R");
            engine.Start();

            var snapshots = new List<(float[] V, int Turn)>();
            int guard = 0;
            while (!engine.State.IsFinished && guard++ < 500)
            {
                var side = engine.State.ActiveSide;
                if (side == Side.Left) botL.PlayTurn(engine, side);
                else botR.PlayTurn(engine, side);

                if (engine.State.Turn % everyTurns == 0)
                {
                    snapshots.Add((StateEncoder.Encode(engine.State, vecs, Side.Left), engine.State.Turn));
                }
            }

            float label = engine.State.IsFinished
                ? (engine.State.Winner == Side.Left ? 1f : 0f)
                : 0.5f;
            if (label == 1f) leftWins++;
            else if (label == 0f) rightWins++;
            else { unfinished++; continue; }   // 未分胜负的局整个丢掉

            // 对位编号（**新格式的核心**）：让「优势回归 / 对位内成对排序」这类
            // 需要在训练时按对位做中心化的目标不用去读外部清单文件。
            // pair = 左卡组下标 × deckCount + 右卡组下标。
            float pairId = leftIdx * decks.Count + rightIdx;

            foreach (var (v, _) in snapshots)
            {
                foreach (float x in v) bw.Write(x);
                bw.Write(label);      // 该视角方（= 左方）最终是否获胜
                bw.Write((float)g);   // ⭐ 对局号 —— 切分必须按对局，不能按样本
                bw.Write(pairId);     // ⭐ 对位编号（同一局内恒定）
                rows++;
            }
        }

        sw.Stop();
        Console.WriteLine($"\n完成 {games} 局（左胜 {leftWins} / 右胜 {rightWins} / 未分 {unfinished}）");
        Console.WriteLine($"样本 {rows:N0} 条，维度 {Dim}，用时 {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"速度 {games / sw.Elapsed.TotalSeconds:F0} 局/秒");
        // 哨兵：只有带候选表缓存的 dll 才会打出非零命中数（见 CardApi.GetChooseSpawnCards）。
        Console.WriteLine($"候选表缓存 命中 {KLink.Bot.Effects.CardApi.GcsCacheHits:N0} "
                          + $"/ 未命中 {KLink.Bot.Effects.CardApi.GcsCacheMisses:N0} "
                          + $"/ 不可缓存 {KLink.Bot.Effects.CardApi.GcsCacheBypassed:N0}");
        Console.WriteLine($"文件 {new FileInfo(outPath).Length / 1024.0 / 1024.0:F0} MB");

        // ---- 对位清单（诊断/基线用；**不参与训练**，也不改 .bin 的格式）----
        var pairCount = pairs.GroupBy(p => (p.L, p.R)).ToDictionary(k => k.Key, v => v.Count());
        Console.WriteLine($"卡组对位：{pairCount.Count} 种有序对位，每对位 "
                          + $"{pairCount.Values.Min()}~{pairCount.Values.Max()} 局"
                          + $"（理论 = {games}/({decks.Count}×{decks.Count - 1}) = "
                          + $"{games / (double)(decks.Count * (decks.Count - 1)):F1}）");

        string manPath = outPath + ".decks.json";
        var sb = new StringBuilder();
        sb.Append("{\"tool\":\"NNTrain dump\",\"seed\":").Append(seed)
          .Append(",\"games\":").Append(games)
          .Append(",\"dim\":").Append(Dim)
          .Append(",\"deckCount\":").Append(decks.Count)
          .Append(",\"decks\":[");
        for (int i = 0; i < decks.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(decks[i].Name));
        }
        sb.Append("],\"pairs\":[");
        for (int i = 0; i < pairs.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(pairs[i].G).Append(',').Append(pairs[i].L)
              .Append(',').Append(pairs[i].R).Append(']');
        }
        sb.Append("]}");
        File.WriteAllText(manPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"对位清单 {manPath}（{new FileInfo(manPath).Length / 1024.0:F0} KB）");
        return 0;
    }

    private static List<string> ExpandDeck(CardDatabase db, string deckName)
    {
        foreach (var (name, code) in MetaDecks.All)
        {
            if (name != deckName) continue;
            try
            {
                var parsed = DeckCodeParser.Parse(code);
                return DeckCodeParser.Expand(parsed, db.DeckCodeIds, out _);
            }
            catch { return new List<string>(); }
        }
        return new List<string>();
    }

    // ==================== train ====================

    private static int Train(Dictionary<string, string> opts)
    {
        string path = opts.GetValueOrDefault("data", "nn-data.bin");
        int epochs = GetInt(opts, "epochs", 30);
        // ⭐ 报告节奏（**纯日志开关，不参与任何训练算式**）。
        //    默认 5 = 历史行为（第五轮之前是 `ep % 5 == 0 || ep == epochs`，写死的）。
        //    第五轮的收敛实验需要**逐 epoch** 的曲线，用 `--eval-every 1` 打开。
        int evalEvery = Math.Max(1, GetInt(opts, "eval-every", 5));
        int hidden = GetInt(opts, "hidden", 64);
        string modelPath = opts.GetValueOrDefault("out", "nn-model.bin");
        float lr = 0.05f;
        string target = opts.GetValueOrDefault("target", "win").ToLowerInvariant();
        if (target is not ("win" or "pair"))
        {
            throw new ArgumentException($"--target 只认 win | pair，收到 '{target}'");
        }
        bool pairwise = target == "pair";

        var (X, y, gids, pair, n, deckCount) = LoadData(path);
        Console.WriteLine($"样本 {n:N0}，维度 {Dim}，对局 {gids.Distinct().Count():N0}，"
                          + $"卡组 {deckCount} 套，对位编号 {pair.Distinct().Count():N0} 种");
        Console.WriteLine($"训练目标: {(pairwise
            ? "pair = 对位内成对排序（同一对位 + 同一回合，赢局局面 vs 输局局面）"
            : "win  = 视角方最终是否获胜（BCE）")}");

        // 分层诊断用的「回合代理量」—— **纯诊断，不参与任何训练算式**。
        //    v1 起编码里直接有全局回合号（字段 0 = `State.Turn` / 30），不再需要
        //    v0 那个 `左maxKredits + 右maxKredits` 的代理量（触顶 12 后会饱和失真）。
        //    必须在标准化**之前**取：下面的标准化会**就地**改写 X。
        var turnBucket = BuildTurnBuckets(X, n);

        // 标准化
        var mean = new float[Dim];
        var std = new float[Dim];
        for (int i = 0; i < n; i++)
            for (int d = 0; d < Dim; d++) mean[d] += X[i * Dim + d];
        for (int d = 0; d < Dim; d++) mean[d] /= n;
        for (int i = 0; i < n; i++)
            for (int d = 0; d < Dim; d++)
            {
                float t = X[i * Dim + d] - mean[d];
                std[d] += t * t;
            }
        for (int d = 0; d < Dim; d++) std[d] = MathF.Sqrt(std[d] / n) + 1e-6f;
        for (int i = 0; i < n; i++)
            for (int d = 0; d < Dim; d++) X[i * Dim + d] = (X[i * Dim + d] - mean[d]) / std[d];

        // ⭐ **按对局切分**，不是按样本 —— 同一局的不同回合高度相关，
        //    按样本切会让验证集混进训练过的对局，准确率虚高。
        //
        // ⚠️ 默认走 `Random.Shared`（= 原来的行为，每次跑切分都不同 ⇒ 数字不可复现）。
        //    给了 `--split-seed N` 就用固定种子，切分可复现 —— `verify` 靠它复现同一份验证集。
        int splitSeed = GetInt(opts, "split-seed", -1);
        var splitRng = splitSeed < 0 ? null : new Random(splitSeed);
        var gameIds = gids.Distinct()
                          .OrderBy(_ => splitRng is null ? Random.Shared.Next() : splitRng.Next())
                          .ToArray();

        int nValGames = Math.Max(1, (int)(gameIds.Length * 0.15));
        var valGames = new HashSet<float>(gameIds.Take(nValGames));
        var idx = Enumerable.Range(0, n)
                            .Where(i => !valGames.Contains(gids[i]))
                            .Concat(Enumerable.Range(0, n).Where(i => valGames.Contains(gids[i])))
                            .ToArray();
        int nTrain = n - idx.Count(i => valGames.Contains(gids[i]));
        Console.WriteLine($"训练 {nTrain:N0} / 验证 {n - nTrain:N0}  （按对局切分，验证集 {nValGames} 局）\n");

        // ==================== 训练 ====================
        //
        // 两种目标（`--target`）：
        //   win  —— BCE(y)。y = 视角方最终是否获胜。**静态先验（这副牌本来强不强）也在这个
        //           目标里**，而卡池 22 套牌差 78 个点 ⇒ 最优解里有一大块与局面无关。
        //   pair —— **对位内成对排序**：只在「同一对位 + 同一回合」这一个单元里，比较
        //           「赢局的局面」与「输局的局面」，让模型给前者更高的分。
        //           对位先验在每一对里是**同一个常数**，相减即抵消 ⇒ 梯度只可能来自局面差异。
        //
        // ⚠️ 为什么**没有**选任务书里的另一个选项「优势回归（label = y − 同对位基线胜率）」：
        //    设 b ∈ (0,1) 是该对位的基线胜率，则 t = y − b 满足 **sign(t) ≡ 2y−1** ——
        //    标签的**正负号与原始胜负完全相同**。也就是说「谁会赢」这个分类任务一个字没变，
        //    A 卡组对位基线在它上面照样是 73%；变的只是损失的权重尺度。
        //    先验要从**任务**里去掉，只有在同一单元内做**比较**（相减）才做得到。
        //    完整论证与实测见 klink bot/docs/NN训练诊断.md 第四轮。

        // MLP: Dim → hidden → 1
        var rng = new Random(42);
        float Scale(int fan) => (float)(Math.Sqrt(2.0 / fan) * (rng.NextDouble() * 2 - 1));
        var W1 = new float[hidden * Dim];
        var B1 = new float[hidden];
        var W2 = new float[hidden];
        for (int i = 0; i < W1.Length; i++) W1[i] = Scale(Dim);
        for (int i = 0; i < W2.Length; i++) W2[i] = Scale(hidden);

        var h = new float[hidden];
        var h2 = new float[hidden];
        var g1 = new float[hidden * Dim];
        var gb1 = new float[hidden];
        var g2 = new float[hidden];
        float bestAcc = 0;
        float lastAcc = 0;

        // ---------- 前向 / 反传（两种目标共用，保证与 NnModel 的算式逐字一致）----------
        float ForwardLogit(int r, float[] hh)
        {
            var x = X.AsSpan(r * Dim, Dim);
            for (int j = 0; j < hidden; j++)
            {
                float s = B1[j];
                var w = W1.AsSpan(j * Dim, Dim);
                for (int d = 0; d < Dim; d++) s += w[d] * x[d];
                hh[j] = s > 0 ? s : 0;                      // ReLU
            }
            float o = 0;
            for (int j = 0; j < hidden; j++) o += W2[j] * hh[j];
            return o;
        }

        void BackwardFrom(int r, float dOut, float[] hh)
        {
            var x = X.AsSpan(r * Dim, Dim);
            for (int j = 0; j < hidden; j++)
            {
                if (hh[j] == 0) continue;
                g2[j] += dOut * hh[j];
                float c = dOut * W2[j];
                var gw = g1.AsSpan(j * Dim, Dim);
                for (int d = 0; d < Dim; d++) gw[d] += c * x[d];
                gb1[j] += c;
            }
        }

        /// <summary>
        /// 把 [from, to) 这段样本按「(对位, 回合)」分单元，单元内赢局样本与输局样本
        /// **按顺序两两配对**（确定性，不用随机数），返回 (排序正确对数, 总对数, 按回合分档)。
        /// </summary>
        (int ok, int tot, SortedDictionary<int, int[]> byTurn) PairRank(int from, int to)
        {
            var byCell = new Dictionary<(int, int), (List<int> W, List<int> L)>();
            for (int k = from; k < to; k++)
            {
                int r = idx[k];
                var key = ((int)pair[r], turnBucket[r]);
                if (!byCell.TryGetValue(key, out var c))
                {
                    c = (new List<int>(), new List<int>());
                    byCell[key] = c;
                }
                (y[r] > 0.5f ? c.W : c.L).Add(r);
            }

            var byTurn2 = new SortedDictionary<int, int[]>();
            int ok = 0, tot = 0;
            foreach (var kv in byCell)
            {
                var c = kv.Value;
                int m = Math.Min(c.W.Count, c.L.Count);
                var bs = NewSlot(byTurn2, kv.Key.Item2);
                for (int i = 0; i < m; i++)
                {
                    bool win = ForwardLogit(c.W[i], h) > ForwardLogit(c.L[i], h2);
                    if (win) ok++;
                    tot++;
                    bs[1]++;
                    if (win) bs[0]++;
                }
            }
            return (ok, tot, byTurn2);
        }

        // pair 目标的训练单元索引：(对位, 回合) → 赢局 / 输局训练样本下标
        Dictionary<(int, int), (List<int> W, List<int> L)>? cells = null;
        var shuffled = new int[nTrain];
        var pairRng = new Random(20260926);
        if (pairwise)
        {
            cells = new Dictionary<(int, int), (List<int> W, List<int> L)>();
            for (int k = 0; k < nTrain; k++)
            {
                int r = idx[k];
                var key = ((int)pair[r], turnBucket[r]);
                if (!cells.TryGetValue(key, out var c))
                {
                    c = (new List<int>(), new List<int>());
                    cells[key] = c;
                }
                (y[r] > 0.5f ? c.W : c.L).Add(r);
            }
            int usable = cells.Values.Count(c => c.W.Count > 0 && c.L.Count > 0);
            Console.WriteLine($"成对排序单元（对位 × 回合）：{cells.Count:N0} 个，"
                              + $"其中两侧都非空、能出对的 {usable:N0} 个\n");
        }

        for (int ep = 1; ep <= epochs; ep++)
        {
            Array.Clear(g1); Array.Clear(gb1); Array.Clear(g2);
            double loss = 0;
            int used = 0;

            if (!pairwise)
            {
                for (int k = 0; k < nTrain; k++)
                {
                    int r = idx[k];
                    float t = y[r];
                    float o = ForwardLogit(r, h);
                    o = 1f / (1f + MathF.Exp(-o));                  // sigmoid
                    float e = o - t;
                    loss += -t * MathF.Log(o + 1e-7f) - (1 - t) * MathF.Log(1 - o + 1e-7f);
                    BackwardFrom(r, e, h);
                    used++;
                }
            }
            else
            {
                // 每轮重新洗牌（确定性种子），再按对取样：
                //   对 i 取一个**同单元、反标签**的伙伴 j，目标 = sigmoid(s_i − s_j) → 1。
                for (int k = 0; k < nTrain; k++) shuffled[k] = k;
                for (int k = nTrain - 1; k > 0; k--)
                {
                    int t = pairRng.Next(k + 1);
                    (shuffled[k], shuffled[t]) = (shuffled[t], shuffled[k]);
                }

                for (int k = 0; k + 1 < nTrain; k += 2)
                {
                    int ri = idx[shuffled[k]];
                    var cell = cells![((int)pair[ri], turnBucket[ri])];
                    var opp = y[ri] > 0.5f ? cell.L : cell.W;
                    if (opp.Count == 0) continue;
                    int rj = opp[pairRng.Next(opp.Count)];

                    float si = ForwardLogit(ri, h);
                    float sj = ForwardLogit(rj, h2);
                    float d = si - sj;
                    // softplus(−d) = log(1+exp(−d))
                    loss += d > 0 ? Math.Log(1 + Math.Exp(-d)) : -d + Math.Log(1 + Math.Exp(d));
                    float dp = 1f / (1f + MathF.Exp(-d));      // sigmoid(d)
                    BackwardFrom(ri, -(1f - dp), h);
                    BackwardFrom(rj, +(1f - dp), h2);
                    used++;
                }
            }

            float sc = lr / Math.Max(1, used);
            for (int i = 0; i < W1.Length; i++) W1[i] -= sc * g1[i];
            for (int j = 0; j < hidden; j++) B1[j] -= sc * gb1[j];
            for (int j = 0; j < hidden; j++) W2[j] -= sc * g2[j];

            if (ep % evalEvery == 0 || ep == epochs)
            {
                double acc;
                double trainAcc;
                string metricName;
                if (!pairwise)
                {
                    int correct = 0;
                    // ⭐ 分档诊断（纯统计，不改训练）：按 turnBucket 累积 [正确, 总数]
                    var byTurn = new SortedDictionary<int, int[]>();
                    for (int k = nTrain; k < n; k++)
                    {
                        int r = idx[k];
                        bool ok = (ForwardLogit(r, h) > 0) == (y[r] > 0.5f);
                        if (ok) correct++;
                        var bs = NewSlot(byTurn, turnBucket[r]);
                        bs[1]++;
                        if (ok) bs[0]++;
                    }
                    acc = (double)correct / (n - nTrain);
                    // ⭐ 训练集准确率（**纯统计**，与前向/反传共用同一个 ForwardLogit）——
                    //    它和留出准确率的差距就是过拟合/欠拟合的直接读数。
                    int trCorrect = 0;
                    for (int k = 0; k < nTrain; k++)
                    {
                        int r = idx[k];
                        if ((ForwardLogit(r, h) > 0) == (y[r] > 0.5f)) trCorrect++;
                    }
                    trainAcc = (double)trCorrect / nTrain;
                    metricName = "验证准确率";
                    if (ep == epochs) PrintBuckets("\n验证集分档准确率（模型）", byTurn);
                }
                else
                {
                    var (ok, tot, byTurn) = PairRank(nTrain, n);
                    acc = tot == 0 ? 0.5 : (double)ok / tot;
                    var (okTr, totTr, _) = PairRank(0, nTrain);
                    trainAcc = totTr == 0 ? 0.5 : (double)okTr / totTr;
                    metricName = $"验证对位内排序准确率（{tot:N0} 对）";
                    if (ep == epochs) PrintBuckets("\n验证集分档【对位内排序】准确率（模型）", byTurn);
                }
                bestAcc = Math.Max(bestAcc, (float)acc);
                lastAcc = (float)acc;
                Console.WriteLine($"  epoch {ep,3}  loss {loss / Math.Max(1, used):F6}  {metricName} {acc:P2}"
                                  + $"  训练准确率 {trainAcc:P2}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(pairwise
            ? $"最佳验证对位内排序准确率 {bestAcc:P2}（随机 = 50%）"
            : $"最佳验证准确率 {bestAcc:P2}");
        Console.WriteLine(bestAcc > 0.55
            ? "✅ 明显高于 50% —— 编码里有信号，向量方案成立"
            : "⚠ 接近 50% —— 编码可能没信号，或者需要更多数据/特征");

        // ==================== 存模型 ====================
        //
        // ⚠️ 存的是**最后一次 epoch** 的权重（本循环不保留历史最优快照）。
        //    所以元数据里 valAccuracy = 最后一轮的数字，bestValAccuracy = 历史最好。
        //    两者在本数据上一样（loss 一直在降），但不能假设总是如此。
        var meta = new
        {
            tool = "NNTrain",
            encoderSpec = StateEncoder.Spec,
            data = Path.GetFileName(path),
            samples = n,
            trainSamples = nTrain,
            valSamples = n - nTrain,
            valGames = nValGames,
            games = gids.Distinct().Count(),
            epochs,
            hidden,
            lr,
            weightInitSeed = 42,
            splitSeed,
            target,
            valAccuracy = lastAcc,
            bestValAccuracy = bestAcc,
            savedEpochWeights = epochs,
            trainedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        string metaJson = JsonSerializer.Serialize(meta,
            new JsonSerializerOptions { WriteIndented = false });

        var model = new NnModel
        {
            Dim = Dim,
            Hidden = hidden,
            CardDim = CardDim,
            Zones = Zones,
            PerSide = PerSide,
            Activation = 0,
            EncoderSpec = StateEncoder.Spec,
            MetaJson = metaJson,
            Mean = mean,
            Std = std,
            W1 = W1,
            B1 = B1,
            W2 = W2,
            B2 = 0f,
        };
        model.Save(modelPath);

        Console.WriteLine();
        Console.WriteLine($"模型已存: {modelPath}  ({new FileInfo(modelPath).Length / 1024.0:F0} KB)");
        Console.WriteLine($"  结构 {Dim}→{hidden}→1，含 mean/std 归一化参数 + 编码规格");        Console.WriteLine($"  复现命令: NNTrain train --data {Path.GetFileName(path)} --epochs {epochs} "
                          + $"--hidden {hidden} --split-seed {splitSeed} --out {Path.GetFileName(modelPath)}");
        return 0;
    }

    // ==================== verify ====================

    /// <summary>
    /// 用**存下来的模型**（走 <see cref="NnModel"/> 的共用前向）重算验证集准确率。
    ///
    /// 这是「训练 == 推理」的验收：
    /// <code>
    ///   NNTrain train  --data d.bin --epochs 5 --split-seed 7 --out m.bin   → 报告 X%
    ///   NNTrain verify --data d.bin --model m.bin --split-seed 7            → 必须也是 X%
    /// </code>
    /// 对不上就说明模型文件/归一化参数/前向算式三处里有一处错了。
    /// </summary>
    private static int Verify(Dictionary<string, string> opts)
    {
        string path = opts.GetValueOrDefault("data", "nn-data.bin");
        string modelPath = opts.GetValueOrDefault("model", "nn-model.bin");
        int splitSeed = GetInt(opts, "split-seed", -1);

        var model = NnModel.Load(modelPath);
        Console.WriteLine($"模型: {model.Describe()}");
        Console.WriteLine($"编码规格: {model.EncoderSpec}");
        Console.WriteLine();

        var (X, y, gids, pair, n, deckCount) = LoadData(path);
        Console.WriteLine($"样本 {n:N0}，维度 {Dim}，卡组 {deckCount} 套");

        var splitRng = splitSeed < 0 ? null : new Random(splitSeed);
        var gameIds = gids.Distinct()
                          .OrderBy(_ => splitRng is null ? Random.Shared.Next() : splitRng.Next())
                          .ToArray();
        int nValGames = Math.Max(1, (int)(gameIds.Length * 0.15));
        var valGames = new HashSet<float>(gameIds.Take(nValGames));

        var idx = Enumerable.Range(0, n)
                            .Where(i => !valGames.Contains(gids[i]))
                            .Concat(Enumerable.Range(0, n).Where(i => valGames.Contains(gids[i])))
                            .ToArray();
        int nTrain = n - idx.Count(i => valGames.Contains(gids[i]));

        if (splitSeed < 0)
        {
            Console.WriteLine("⚠ 没给 --split-seed ⇒ 验证集与训练时不是同一份，数字只能「量级相近」，不能逐位对齐");
        }

        int correct = 0, correctTrain = 0, correctAll = 0;
        // ⭐ 分档诊断（纯统计，不改任何判定逻辑）
        var bucketAll = new SortedDictionary<int, int[]>();
        var bucketVal = new SortedDictionary<int, int[]>();
        var z = new float[Dim];
        for (int k = 0; k < n; k++)
        {
            // ⚠️ 必须走 idx[k] —— idx 是**重排后**的样本顺序（[训练…, 验证…]），
            //    k 是它里面的位置，**不是**原始样本下标。
            //    这里原先直接用了 `X.AsSpan(k * Dim, …)`，于是 `k >= nTrain` 切的是
            //    「原始下标 >= nTrain 的样本」而不是验证集 —— 切错了，报出来的
            //    准确率也就对不上训练时报的数字。verify 就是用来抓这种事的。
            int r = idx[k];

            // ⚠️ 判据用 logit > 0 —— 和训练里 `o > 0` 逐位一致
            //    （sigmoid(o) > 0.5 在 o 极小时会因舍入判反）
            model.NormalizeInto(X.AsSpan(r * Dim, Dim), z);
            bool pred = model.ForwardLogitNormalized(z) > 0;
            bool truth = y[r] > 0.5f;
            // ⭐ 分档只用原始特征里的全局回合号，不参与判定
            int turn = (int)MathF.Round(X[r * Dim + StateEncoder.OffGlobal] * StateEncoder.TurnScale);
            NewSlot(bucketAll, turn)[1]++;
            if (k >= nTrain) NewSlot(bucketVal, turn)[1]++;
            if (pred == truth)
            {
                correctAll++;
                NewSlot(bucketAll, turn)[0]++;
                if (k >= nTrain) { correct++; NewSlot(bucketVal, turn)[0]++; }
                else correctTrain++;
            }
        }

        double valAcc = (double)correct / (n - nTrain);
        Console.WriteLine();
        Console.WriteLine($"【口径 A·原始胜负】用 sign(logit) 猜「视角方（左方）是否获胜」");
        Console.WriteLine($"  验证集准确率 {valAcc:P2}   ({correct:N0}/{n - nTrain:N0}，验证集 {nValGames} 局)");
        Console.WriteLine($"  训练集准确率 {(double)correctTrain / nTrain:P2}   ({correctTrain:N0}/{nTrain:N0})");
        Console.WriteLine($"  全体准确率   {(double)correctAll / n:P2}   ({correctAll:N0}/{n:N0})");

        // ==================== 口径 B：对位内成对排序 ====================
        //
        // **同一对位 + 同一回合**内，赢局的局面 vs 输局的局面，模型给谁的分更高。
        // 两个样本共享同一个对位 ⇒ 静态先验在每一对里是同一个常数、**结构性抵消**，
        // 所以这个口径量的是纯局面判断。随机 = 50%，与卡池平衡程度无关。
        var score = new float[n];
        for (int k = 0; k < n; k++)
        {
            int r = idx[k];
            model.NormalizeInto(X.AsSpan(r * Dim, Dim), z);
            score[k] = model.ForwardLogitNormalized(z);
        }

        (int ok, int tot, SortedDictionary<int, int[]> byTurn) Rank(int from, int to)
        {
            var byCell = new Dictionary<(int, int), (List<int> W, List<int> L)>();
            for (int k = from; k < to; k++)
            {
                int r = idx[k];
                int turn = (int)MathF.Round(X[r * Dim + StateEncoder.OffGlobal] * StateEncoder.TurnScale);
                var key = ((int)pair[r], turn);
                if (!byCell.TryGetValue(key, out var c))
                {
                    c = (new List<int>(), new List<int>());
                    byCell[key] = c;
                }
                (y[r] > 0.5f ? c.W : c.L).Add(k);
            }
            var bt = new SortedDictionary<int, int[]>();
            int ok = 0, tot = 0;
            foreach (var kv in byCell)
            {
                var c = kv.Value;
                int m = Math.Min(c.W.Count, c.L.Count);
                var bs = NewSlot(bt, kv.Key.Item2);
                for (int i = 0; i < m; i++)
                {
                    bool win = score[c.W[i]] > score[c.L[i]];
                    if (win) ok++;
                    tot++;
                    bs[1]++;
                    if (win) bs[0]++;
                }
            }
            return (ok, tot, bt);
        }

        var (okT, totT, _) = Rank(0, nTrain);
        var (okV, totV, bucketPairVal) = Rank(nTrain, n);
        Console.WriteLine();
        Console.WriteLine("【口径 B·对位内成对排序】同一（对位 × 回合）内：赢局局面 vs 输局局面，谁的分高（随机 = 50%）");
        Console.WriteLine($"  验证集 {(totV == 0 ? 0.5 : (double)okV / totV):P2}   "
                          + $"({okV:N0}/{totV:N0} 对，验证集 {nValGames} 局)");
        Console.WriteLine($"  训练集 {(totT == 0 ? 0.5 : (double)okT / totT):P2}   ({okT:N0}/{totT:N0} 对)");
        Console.WriteLine();
        PrintBuckets("\n【全体样本】模型分档准确率（口径 A）", bucketAll);
        PrintBuckets($"\n【验证集 {nValGames} 局】模型分档准确率（口径 A，对上表基线用）", bucketVal);
        PrintBuckets($"\n【验证集 {nValGames} 局】模型分档【对位内排序】准确率（口径 B）", bucketPairVal);
        return 0;
    }

    /// <summary>
    /// 读 dump 文件。v2 格式（<see cref="DataMagic"/>）：
    /// <code>
    ///   int32 magic('AKL2'), int32 dim, int32 deckCount
    ///   每条： float v[dim] ; float outcome(0/1) ; float gid ; float pairId
    /// </code>
    /// </summary>
    private static (float[] X, float[] y, float[] g, float[] pair, int n, int deckCount) LoadData(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        int magic = br.ReadInt32();
        if (magic == OldDataMagic)
        {
            throw new FormatException(
                "这是 v1 的旧数据（magic 'AKL1'，每条 dim+2 个 float、没有对位编号）。"
                + "本轮（第四轮）改了两处：编码 v2（牌库区不再写卡向量，925→745 维）"
                + "与 dump 行格式（多了对数对位编号）。**必须重新 dump**，旧数据不能混用。");
        }
        if (magic != DataMagic) throw new FormatException($"不是本工具产出的数据（magic={magic:X8}）");

        int dim = br.ReadInt32();
        if (dim != Dim) throw new FormatException($"维度不符：文件 {dim}，程序 {Dim}");
        int deckCount = br.ReadInt32();

        const int Extra = 3;   // outcome + gid + pairId
        long total = (fs.Length - 12) / (4L * (Dim + Extra));
        var X = new float[total * Dim];
        var y = new float[total];
        var g = new float[total];
        var pair = new float[total];
        for (long i = 0; i < total; i++)
        {
            for (int d = 0; d < Dim; d++) X[i * Dim + d] = br.ReadSingle();
            y[i] = br.ReadSingle();
            g[i] = br.ReadSingle();
            pair[i] = br.ReadSingle();
        }
        return (X, y, g, pair, (int)total, deckCount);
    }

    // ==================== 分层诊断（纯输出，不参与训练）====================

    /// <summary>
    /// 算「全局回合号」分档键 = <c>v[0] × TurnScale</c>（<c>v[0] = State.Turn / 30</c>）。
    ///
    /// v0 用的是 `左maxKredits + 右maxKredits` 代理量 —— 那个量在双方都触到上限 12
    /// 之后会**饱和**在 24，把 T24 之后的残局全混进同一档（诊断报告 §7.3）。
    /// v1 编码里直接有 `State.Turn`，这个缺陷没有了。
    /// </summary>
    private static int[] BuildTurnBuckets(float[] X, int n)
    {
        var b = new int[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (int)MathF.Round(X[i * Dim + StateEncoder.OffGlobal] * StateEncoder.TurnScale);
        }
        return b;
    }

    /// <summary>打印分档准确率表。</summary>
    private static void PrintBuckets(string title, SortedDictionary<int, int[]> byTurn)
    {
        int tc = 0, tn = 0;
        foreach (var kv in byTurn) { tc += kv.Value[0]; tn += kv.Value[1]; }
        Console.WriteLine(title + "  （分档 = 全局回合号 Turn，编码字段 0）");
        Console.WriteLine($"    {"turn",5} {"样本",9} {"准确率",9} {"占比",7}");
        foreach (var kv in byTurn)
        {
            int c = kv.Value[0], t = kv.Value[1];
            if (t == 0) continue;
            Console.WriteLine($"    {kv.Key,5} {t,9:N0} {100.0 * c / t,8:F2}% {100.0 * t / tn,6:F1}%");
        }
        Console.WriteLine($"    {"合计",5} {tn,9:N0} {(tn == 0 ? 0 : 100.0 * tc / tn),8:F2}%");
    }

    private static int[] NewSlot(SortedDictionary<int, int[]> d, int key)
    {
        if (!d.TryGetValue(key, out var s)) d[key] = s = new int[2];
        return s;
    }

    // ==================== 辅助 ====================

    private static Dictionary<string, string> ParseOpts(IEnumerable<string> args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string key = null;
        foreach (string a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (key is not null) d[key] = "1";
                key = a[2..];
            }
            else if (key is not null) { d[key] = a; key = null; }
        }
        if (key is not null) d[key] = "1";
        return d;
    }

    private static int GetInt(Dictionary<string, string> o, string k, int def)
        => o.TryGetValue(k, out var v) && int.TryParse(v, out int r) ? r : def;

    private static string FindDir(string probeFile)
    {
        string[] c =
        {
            Path.Combine(AppContext.BaseDirectory, "Data"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "klink bot", "docs"),
        };
        foreach (string s in c)
        {
            string full = Path.GetFullPath(s);
            if (File.Exists(Path.Combine(full, probeFile))) return full;
        }
        throw new DirectoryNotFoundException($"找不到 {probeFile}");
    }

    private static int Help()
    {
        Console.WriteLine("""
            NNTrain —— 验证向量方案的最小管线

              NNTrain dump   --games 10000 --out nn-data.bin [--seed 1] [--every 1]
              NNTrain train  --data nn-data.bin [--epochs 30] [--hidden 64]
                             [--out nn-model.bin] [--split-seed N] [--target win|pair]
              NNTrain verify --data nn-data.bin [--model nn-model.bin] [--split-seed N]

            判据：验证准确率明显 > 50% = 编码有信号；≈50% = 编码是垃圾。

            dump 每局随机抽 2 套不同卡组 + 左右随机互换（seed 派生，可复现），
            并把对位清单写到 <out>.decks.json（诊断/基线用，不参与训练）。
            数据文件格式（v2，magic 'AKL2'）：
              头 = int32 magic, int32 dim, int32 deckCount
              每条 = float v[dim], float outcome, float gid, float pairId
                     （pairId = 左卡组下标 × deckCount + 右卡组下标）

            --target win   标签 = 视角方最终是否获胜（BCE）。**含静态先验。**
            --target pair  标签 = 同一（对位 × 回合）内「赢局局面 应高于 输局局面」的成对排序。
                           对位先验在每一对里是同一常数、相减即抵消 ⇒ 梯度只来自局面差异。
                           verify 会同时报两个口径：
                             A 原始胜负（sign(logit) 猜谁赢；跨对位可比历史数字）
                             B 对位内成对排序（随机 = 50%，与卡池平衡程度无关）

            ⚠️ 不给 --split-seed 时按对局切分用的是 Random.Shared（每次跑都不同）⇒
               数字不可复现。要复现就显式给 --split-seed；verify 靠它复现同一份验证集。

            状态编码在 src/KLink.Bot/NN/StateEncoder.cs —— 训练与推理共用一份。
            """);
        return 0;
    }
}
