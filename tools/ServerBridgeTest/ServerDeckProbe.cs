using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.Replay;
using KLink.Bot.Server;

namespace KLink.Bot.ServerBridgeTest;

/// <summary>
/// **模拟服务端**的探针：用 fyserver 里那份**硬编码的 bot 卡组**合成一个开局，
/// 再让 bot 决策。用来复现「bot 只会跳过」这类**只在实际服务端形状下才出现**的问题。
///
/// 为什么不直接用回放：回放的 `starting_data` 是**人工构造**的（`ReplayData.Load`
/// 从 JSON 读），而服务端的是 `MatchManagerService.GetCardsFromDeck` **现算**的 ——
/// 两者在**卡名、cardID、位置字符串、初始局面**上都可能不同。
/// 拿回放验过不代表服务端能work。
///
/// 这个探针**逐步打印**，哪一步断了直接可见：
///   1. 卡组码解析 → 卡名列表
///   2. 这些卡名在内核卡库里找不找得到（找不到会被 ReplayRunner 静默跳过！）
///   3. 合成开局后：牌库/手牌各几张、HQ 在不在
///   4. 重建后的局面：行动方是谁、手牌几张
///   5. 决策产出几条动作
/// </summary>
internal static class ServerDeckProbe
{
    // fyserver `MatchManagerService.cs:500` 里那份硬编码 bot 卡组
    private const string BotDeckCode =
        "%%21|4v32323232sTgv0z0C0C0C0CoBoBoBoB0Y0Y101010hShShShS1902020202030303ououpRpRpRsU;;;~;;;|0N1b";

    private const int LeftHqId = 1;
    private const int RightHqId = 41;

    public static int Run(string repoRoot, string sideName)
    {
        string dataDir = Path.Combine(repoRoot, "tem", "fyserver", "bin", "Release", "net10.0", "BotData");
        if (!Directory.Exists(dataDir))
        {
            dataDir = Path.Combine(repoRoot, "klink bot", "docs");
        }

        Console.WriteLine($"=== 模拟服务端探针 ===");
        Console.WriteLine($"数据目录 {dataDir}");

        var db = CardDatabase.Load(dataDir);
        Console.WriteLine($"卡库 {db.Count} 张");

        string irPath = Path.Combine(dataDir, "card-ir.json");
        if (File.Exists(irPath))
        {
            KismetLibrary.Initialize(irPath);
        }

        Console.WriteLine($"蓝图 IR {KismetLibrary.Default?.CardCount ?? 0} 张" +
                          (KismetLibrary.LoadError is null ? "" : $"（错误：{KismetLibrary.LoadError}）"));
        Console.WriteLine();

        // ---- 1) 卡组码 → 卡名（照抄服端 GetCardsFromDeck 的拆法）----
        var table = LoadDeckCodes(dataDir);
        Console.WriteLine($"卡组码表 {table.Count} 条");

        var names = DecodeDeckCode(BotDeckCode, table, out string hqName, out List<string> unknown);
        Console.WriteLine($"1) 卡组码解析：牌 {names.Count} 张，HQ = {hqName}，未知码 {unknown.Count} 个" +
                          (unknown.Count > 0 ? $"（{string.Join(",", unknown)}）" : ""));

        // ---- 2) 卡名在内核卡库里找不找得到（**这一步最容易断**）----
        var notFound = names.Where(n => db.Find(n) is null).Distinct().ToList();
        Console.WriteLine($"2) 内核卡库解析：{names.Count - names.Count(n => db.Find(n) is null)}/{names.Count} 张命中" +
                          (notFound.Count > 0 ? $"，**找不到 {notFound.Count} 张**：{string.Join(", ", notFound.Take(8))}" : ""));

        if (db.Find(hqName) is null)
        {
            Console.WriteLine($"   ⚠️ HQ 卡名 '{hqName}' 在卡库里找不到！");
        }

        // ---- 3) 合成开局（照抄服端：5 张手牌 + 其余牌库，编号跨区连续）----
        //
        // ⚠️⚠️ cardID 必须按**服端 `GetCardsFromDeck` 的真实分配**来：
        //   HQ 占 `startId`（右 = 41），**其余牌 42 起**。
        //   本探针原来写成 `RightHqId + 1 + i`（也 = 42 起）看着一样，
        //   但下面 `cards.Add` 的顺序把 HQ 放在最前 ⇒ 一旦顺序变了编号就会
        //   和服端错开，而**错开以后症状是"枚举出不存在的 cardID"**，极难定位。
        //   所以这里把编号显式写出来，并断言总数对得上。
        var cards = new List<ServerCard>
        {
            new(LeftHqId, false, "board_hqleft", 0, "card_location_london"),
            new(RightHqId, false, "board_hqright", 0, hqName),
        };

        for (int i = 0; i < names.Count; i++)
        {
            int id = RightHqId + 1 + i;          // 42, 43, …
            bool inHand = i < 5;
            cards.Add(new ServerCard(id, false, inHand ? "hand_right" : "deck_right",
                                     inHand ? i : 5 + (i - 5), names[i]));
        }

        int handCount = cards.Count(c => c.Location == "hand_right");
        int deckCount = cards.Count(c => c.Location == "deck_right");
        Console.WriteLine($"3) 合成开局：右方手牌 {handCount} 张、牌库 {deckCount} 张、总卡 {cards.Count}");
        Console.WriteLine($"   cardID 范围 {cards.Min(c => c.CardId)}..{cards.Max(c => c.CardId)}" +
                          $"（HQ = {RightHqId}）");

        // ---- 4) 走桥 → 重放 → 看局面 ----
        // 动作流：一条 StartOfTurn(left) → EndOfTurn(left)，模拟「人类刚结束回合」
        var actions = new List<ServerAction>
        {
            new(1, "XActionStartOfTurn", 100, new Dictionary<string, string> { ["side"] = "left" }, 1),
            new(2, "XActionEndOfTurn", 100,
                new Dictionary<string, string> { ["side"] = "left", ["reason"] = "endTurnButton" }, 1),
        };

        var snapshot = new ServerMatchSnapshot(9999, 1, 100, 200, cards, actions)
        {
            NextActionId = 3,
        };

        var replay = ServerReplayBridge.ToReplayData(snapshot);
        Console.WriteLine($"4) 桥转换：{replay.Cards.Count} 张卡，{replay.Actions.Count} 条动作");

        var runner = new ReplayRunner(db);
        var report = runner.Run(replay, verbose: false);
        var engine = report.Engine;

        if (engine is null)
        {
            Console.WriteLine("   ❌ 重放没有产出引擎");
            return 1;
        }

        var st = engine.State;
        Console.WriteLine($"   重建后：回合 {st.Turn}，行动方 {st.ActiveSide.ToWire()}，" +
                          $"左库 {st.Deck(Side.Left).Count} 左手 {st.Hand(Side.Left).Count} " +
                          $"右库 {st.Deck(Side.Right).Count} 右手 {st.Hand(Side.Right).Count} " +
                          $"总卡 {st.AllCards.Count}");

        // ★ 把引擎里**真实存在的 cardID** 打出来。
        //   "枚举出不存在的 cardID" 这类问题，只有把实际集合摊开才能定位 ——
        //   光看"不在引擎里"这句话不知道缺的是什么。
        var engineIds = st.AllCards.Select(c => c.CardId).OrderBy(x => x).ToList();
        Console.WriteLine($"   引擎内 cardID（{engineIds.Count} 个）：{string.Join(",", engineIds)}");
        var snapshotIds = cards.Select(c => c.CardId).OrderBy(x => x).ToList();
        var missing = snapshotIds.Except(engineIds).ToList();
        Console.WriteLine($"   快照有但引擎没有：{(missing.Count == 0 ? "无" : string.Join(",", missing))}");
        Console.WriteLine($"   右方手牌：" + string.Join(", ",
            st.Hand(Side.Right).Select(c => $"#{c.CardId} {c.Name}(费{c.KreditCost})")));
        Console.WriteLine($"   HQ：左 {(st.Hq(Side.Left) is null ? "❌无" : st.HqDefense(Side.Left).ToString())} " +
                          $"右 {(st.Hq(Side.Right) is null ? "❌无" : st.HqDefense(Side.Right).ToString())}");

        // ---- 5) 决策 ----
        Side botSide = sideName == "left" ? Side.Left : Side.Right;

        // 尽量带上神经网络（否则只验贪心兜底，看不到候选表）
        KLink.Bot.NN.NnModel? model = null;
        KLink.Bot.NN.StateEncoder.CardVecs? vecs = null;
        string modelPath = Path.Combine(dataDir, "nn-model.bin");
        if (File.Exists(modelPath) && File.Exists(Path.Combine(dataDir, "card-vectors.json")))
        {
            try
            {
                model = KLink.Bot.NN.NnModel.Load(modelPath);
                vecs = KLink.Bot.NN.StateEncoder.LoadCardVectors(dataDir);
                Console.WriteLine($"   模型 {Path.GetFileName(modelPath)} dim={model.Dim} hidden={model.Hidden}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ 模型加载失败（{ex.Message}），改用贪心");
                model = null;
                vecs = null;
            }
        }

        var svc = new BotTurnService(db, botSide, botSide == Side.Left ? 100 : 200, model, vecs);
        svc.LoadDeckCodeTable(table);

        var result = svc.DecideTurn(snapshot);
        Console.WriteLine();
        Console.WriteLine("5) 决策日志：");
        foreach (string l in result.Log)
        {
            Console.WriteLine("     " + l);
        }

        Console.WriteLine($"   产出 {result.Actions.Count} 条动作：");
        foreach (var a in result.Actions)
        {
            Console.WriteLine($"     #{a.ActionId} {a.ActionType} " +
                              string.Join(" ", a.ActionData.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        Console.WriteLine();

        var problems = new List<string>();
        if (names.Count is not (38 or 39))
        {
            problems.Add($"卡组码解析出 {names.Count} 张牌（期望 38~39）");
        }

        if (notFound.Count > 0)
        {
            problems.Add($"{notFound.Count} 个卡名内核卡库找不到 → 会被静默跳过");
        }

        if (st.Hq(Side.Right) is null || st.Hq(Side.Left) is null)
        {
            problems.Add("HQ 没建出来");
        }

        if (st.Hand(botSide).Count == 0)
        {
            problems.Add("bot 手牌是空的 → 打不出任何牌");
        }

        if (result.Actions.Count <= 1)
        {
            problems.Add("只产出了结束回合 → 决策没找到可做的动作");
        }

        if (problems.Count == 0)
        {
            Console.WriteLine("✅ 服务端形状下一切正常");
            return 0;
        }

        Console.WriteLine("❌ 发现问题：");
        foreach (string p in problems)
        {
            Console.WriteLine("   - " + p);
        }

        return 1;
    }

    /// <summary>照抄服端 `GetCardsFromDeck`：HQ = 倒数第 4~3 位；主体按 ';' 分 4 组、第 idx 组重复 idx+1 次。</summary>
    private static List<string> DecodeDeckCode(string code, Dictionary<string, string> table,
                                               out string hqName, out List<string> unknown)
    {
        hqName = table.GetValueOrDefault(code[^4..^2], "card_location_london");
        var names = new List<string>();
        unknown = new List<string>();

        string body = code[5..];
        var groups = body.Split(';');
        for (int idx = 0; idx < Math.Min(4, groups.Length); idx++)
        {
            string g = groups[idx];
            for (int i = 0; i + 1 < g.Length; i += 2)
            {
                string key = g.Substring(i, 2);
                if (!table.TryGetValue(key, out string? name))
                {
                    unknown.Add(key);
                    continue;
                }

                for (int r = 0; r <= idx; r++)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static Dictionary<string, string> LoadDeckCodes(string dataDir)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(dataDir, "deck_code_ids.json");
        if (!File.Exists(path))
        {
            return table;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    table[p.Name] = p.Value.GetString()!;
                }
            }
        }

        return table;
    }
}
