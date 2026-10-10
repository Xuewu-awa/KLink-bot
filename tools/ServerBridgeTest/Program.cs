using System.Text.Json;
using System.Text.Json.Nodes;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Server;

namespace KLink.Bot.ServerBridgeTest;

/// <summary>
/// **接线验证器**：不装真服务器，把一份真实回放转成 <see cref="ServerMatchSnapshot"/>，
/// 走一遍「重建局面 → 决策 → 产出服务端动作」的完整链路。
///
/// 为什么必须有它：真对局跑一次成本高（要开客户端、登录、打完），
/// 而这条链路的**大部分逻辑与"数据从哪来"无关** —— 只与
/// 「快照形状对不对称」有关。所以先在这里把形状问题全打掉。
///
/// 用法：
/// <code>
///   ServerBridgeTest --replay 989040 [--up-to 30] [--side right]
///   ServerBridgeTest --wrap-fyserver raw-match.json --output out/replay-123
///   ServerBridgeTest --wrap-fyserver replay-123.json --actions replay-123.actions.json --output out/replay-123
///   ServerBridgeTest --fyserver-turn --base-url http://127.0.0.1:5000 --token <JWT> [--side right]
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var opts = ParseArgs(args);

        // `--verify-load`：只验服务端数据目录能否加载（不跑对局）
        if (opts.TryGetValue("verify-load", out string? dataDirArg))
        {
            string dir = dataDirArg == "1"
                ? Path.Combine(FindRepoRoot(), "tem", "fyserver", "bin", "Release", "net10.0", "BotData")
                : dataDirArg;
            return LoadVerifier.Run(dir);
        }

        // 将 fyserver 的 /matches/v2/{id} 原始响应包装成 ReplayData.Load 的两个文件。
        if (opts.TryGetValue("wrap-fyserver", out string? rawReplay))
        {
            string outputPrefix = opts.GetValueOrDefault("output", "replay");
            return FyServerReplayWrapper.Run(rawReplay, opts.GetValueOrDefault("actions"), outputPrefix);
        }

        if (opts.ContainsKey("fyserver-turn"))
        {
            return await FyServerLiveRunner.RunAsync(FindRepoRoot(), opts);
        }

        // `--audit-replay <路径前缀>`：逐条动作审计（找"移动死亡单位"的根因）
        //
        // 可加两个**归因实验**开关（见 `ReplayRunner.IdentityCorrection`）：
        //   `--no-identity-fix`        关掉身份校正 ⇒ 得到「修复前」的基线
        // 默认开启身份校正：动作流携带的卡组码是客户端对随机生成卡的权威身份声明。
        // `--dup-start-kredit` 用于正式回放口径；`--no-dup-start-kredit` 可复现旧模型。
        //   `--identity-only <卡名>`   只对某一张卡校正 ⇒ 把一次改动的因果钉死
        // `--board-trace` 打印每条动作后的半场/前线构成，定位容量与复制链问题。
        if (opts.TryGetValue("audit-replay", out string? auditBase))
        {
            return ReplayAudit.Run(FindRepoRoot(), auditBase, 0,
                identityCorrection: !opts.ContainsKey("no-identity-fix"),
                identityOnly: opts.GetValueOrDefault("identity-only"),
                rngTrace: opts.ContainsKey("rng-trace"),
                dumpLog: opts.ContainsKey("dump-log"),
                dupStartKredit: !opts.ContainsKey("no-dup-start-kredit"),
                boardTrace: opts.ContainsKey("board-trace"));
        }

        // `--kredit-table <目录>`：把目录下所有回放的**实际支付**导成 CSV，
        // 用来判「kredit 自然增长」的模型（见 KreditTable 的类注释）。
        if (opts.TryGetValue("kredit-table", out string? ktDir))
        {
            return KreditTable.Run(FindRepoRoot(), ktDir);
        }

        // `--kredit-trace <路径前缀>`：逐条动作打印双方 kredit 轨迹
        if (opts.TryGetValue("kredit-trace", out string? ktTrace))
        {
            return KreditTable.Trace(FindRepoRoot(), ktTrace);
        }

        // `--server-deck`：**完全模拟服务端**（FyServerStub 那份硬编码 bot 卡组 + 合成开局）
        // 用来复现「bot 只会跳过」这类只在实际服务端形状下才出现的问题。
        if (opts.ContainsKey("server-deck"))
        {
            return ServerDeckProbe.Run(FindRepoRoot(), opts.GetValueOrDefault("side", "right"));
        }

        string replayId = opts.GetValueOrDefault("replay", "989040");
        int upTo = int.Parse(opts.GetValueOrDefault("up-to", "100000"));
        string sideName = opts.GetValueOrDefault("side", "right");
        Side botSide = sideName == "left" ? Side.Left : Side.Right;

        string repoRoot = FindRepoRoot();
        string repDir = Path.Combine(repoRoot, "klink bot", "docs", "fresh-replays");
        string snapshotPath = Path.Combine(repDir, $"replay-{replayId}.json");
        string actionsPath = Path.Combine(repDir, $"replay-{replayId}.actions.json");

        if (!File.Exists(snapshotPath) || !File.Exists(actionsPath))
        {
            Console.Error.WriteLine($"找不到 replay-{replayId}");
            return 2;
        }

        string dataDir = FindDataDirectory(repoRoot);
        var db = CardDatabase.Load(dataDir);
        Console.WriteLine($"卡库 {db.Count} 张    数据目录 {dataDir}");

        // ReplayRunner uses the card-local GetChooseSpawnCards programs to
        // distinguish Develop choices from cards selected from a deck.  The
        // shadow bridge must load the same IR as BotSim and the audit tools;
        // otherwise every CS answer is incorrectly treated as a deck pick.
        string irPath = Path.Combine(dataDir, "card-ir.json");
        if (File.Exists(irPath))
        {
            KismetLibrary.Initialize(irPath);
        }

        Console.WriteLine($"蓝图 IR {KismetLibrary.Default?.CardCount ?? 0} 张" +
                          (KismetLibrary.LoadError is null ? "" : $"（错误：{KismetLibrary.LoadError}）"));

        // ---- 1) 真实回放 → ServerMatchSnapshot（模拟服务端会喂给桥的东西）----
        var snapshot = BuildSnapshotFromReplay(snapshotPath, actionsPath, upTo);
        Console.WriteLine($"快照：回合 {snapshot.Turns}，卡 {snapshot.Cards.Count} 张，" +
                          $"动作 {snapshot.Actions.Count} 条（截到 actionId<={upTo}）");
        Console.WriteLine($"      玩家 左{snapshot.LeftPlayerId} / 右{snapshot.RightPlayerId}，" +
                          $"bot 坐 {botSide.ToWire()}，NextActionId={snapshot.NextActionId}");

        // ---- 2) 跑决策 ----
        var svc = new BotTurnService(db, botSide, BotPlayerId(snapshot, botSide));
        svc.LoadDeckCodeTable(LoadDeckCodeTable(repoRoot));
        Console.WriteLine($"卡组码反向索引 {svc.DeckCodeCount} 条");
        var result = svc.DecideTurn(snapshot);

        Console.WriteLine();
        Console.WriteLine("===== 重建与决策日志 =====");
        foreach (string line in result.Log)
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine();
        Console.WriteLine("===== 产出的服务端动作 =====");
        foreach (var a in result.Actions)
        {
            string data = string.Join(" ", a.ActionData.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"  #{a.ActionId,-4} {a.ActionType,-22} player={a.PlayerId,-8} turn={a.TurnNumber}  {data}");
        }

        Console.WriteLine();
        Console.WriteLine($"未应用动作 {result.UnappliedActions} 条");

        // ---- 3) 自检 ----
        var problems = new List<string>();
        if (result.Actions.Count == 0)
        {
            problems.Add("没有产出任何动作");
        }
        else if (result.Actions[^1].ActionType != "XActionEndOfTurn")
        {
            problems.Add("最后一条不是 XActionEndOfTurn");
        }

        // ⚠️ 关键检查：**产出的槽位必须与人类自己发的动作同形**。
        //    客户端对槽位是挑剔的（`isValidatingActionSent` / `syncErrorCheckCards`），
        //    槽填错了**不会报错、只会静默不同步**。所以拿真实回放里人类发过的
        //    同类型动作当模板，逐槽对账。
        var humanShapes = HumanSlotShapes(replayId);

        foreach (var a in result.Actions)
        {
            if (a.PlayerId != BotPlayerId(snapshot, botSide))
            {
                problems.Add($"#{a.ActionId} 的 player_id={a.PlayerId} 不是 bot 的 " +
                             $"{BotPlayerId(snapshot, botSide)}");
            }

            if (humanShapes.TryGetValue(a.ActionType, out var want))
            {
                var got = a.ActionData.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                var expect = want.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                if (!got.SequenceEqual(expect))
                {
                    problems.Add($"#{a.ActionId}（{a.ActionType}）槽位不符："
                                 + $"产出 [{string.Join(",", got)}] vs 人类 [{string.Join(",", expect)}]");
                }
            }
            else if (a.ActionType is "PC" or "AC" or "ML")
            {
                problems.Add($"#{a.ActionId}（{a.ActionType}）人类从没发过这个类型，无模板可对");
            }
        }

        // 动作号必须严格递增
        for (int i = 1; i < result.Actions.Count; i++)
        {
            if (result.Actions[i].ActionId != result.Actions[i - 1].ActionId + 1)
            {
                problems.Add($"动作号不连续：#{result.Actions[i - 1].ActionId} → #{result.Actions[i].ActionId}");
                break;
            }
        }

        // turn_number 必须 == 内核重建出来的回合
        if (result.State is { } st)
        {
            foreach (var a in result.Actions)
            {
                if (a.TurnNumber != st.Turn)
                {
                    problems.Add($"#{a.ActionId} 的 turn_number={a.TurnNumber}，"
                                 + $"内核重建出来是 {st.Turn}");
                    break;
                }
            }
        }

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("✅ 链路自检通过（含槽位与人类动作同形）");
            return 0;
        }

        Console.WriteLine("❌ 自检发现问题：");
        foreach (string p in problems)
        {
            Console.WriteLine("   - " + p);
        }

        return 1;
    }

    /// <summary>
    /// 载入 `<c>deck_code_ids.live.json</c>`（码 → 卡名）。
    /// 动作的槽 `4` 要填这张牌的卡组码，所以需要它建反向索引。
    /// </summary>
    private static Dictionary<string, string> LoadDeckCodeTable(string repoRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string rel in new[]
                 {
                     "klink bot/docs/deck_code_ids.live.json",
                     "klink bot/docs/deck_code_ids.json",
                 })
        {
            string p = Path.Combine(repoRoot, rel);
            if (!File.Exists(p))
            {
                continue;
            }

            var node = JsonNode.Parse(File.ReadAllText(p));
            if (node is not JsonObject o)
            {
                continue;
            }

            foreach (var (k, v) in o)
            {
                string? name = v switch
                {
                    JsonValue jv => jv.TryGetValue<string>(out string? s) ? s : null,
                    JsonObject jo => jo["card"]?.GetValue<string>() ?? jo["name"]?.GetValue<string>(),
                    _ => null,
                };
                if (!string.IsNullOrEmpty(name))
                {
                    result[k] = name;
                }
            }

            if (result.Count > 0)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>人类在真实回放里发过的动作类型 → 它的槽位集合（权威模板）。</summary>
    private static Dictionary<string, string[]> HumanSlotShapes(string replayId)
    {
        string repoRoot = FindRepoRoot();
        string path = Path.Combine(repoRoot, "klink bot", "docs", "fresh-replays",
                                   $"replay-{replayId}.actions.json");
        var shapes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return shapes;
        }

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var n in doc["actions"]?.AsArray() ?? new JsonArray())
        {
            if (n is not JsonObject a)
            {
                continue;
            }

            string type = a["action_type"]?.GetValue<string>() ?? "";
            if (type.Length == 0 || shapes.ContainsKey(type))
            {
                continue;
            }

            if (a["action_data"] is JsonObject ad)
            {
                shapes[type] = ad.Select(kv => kv.Key).ToArray();
            }
        }

        return shapes;
    }

    private static int BotPlayerId(ServerMatchSnapshot s, Side side)
        => side == Side.Left ? s.LeftPlayerId : s.RightPlayerId;

    /// <summary>
    /// 把回放 JSON 转成「服务端视角的快照」。
    ///
    /// ⚠️ 这一步**只是为了让验证器能跑**，真实服务端是从 `MatchInfo` 直接映射的。
    /// 关键的是**形状要对**：卡的位置字符串、动作的 `action_data` 字典、
    /// `nextActionId` —— 这三样与服务端一致，链路才是真被验过。
    /// </summary>
    private static ServerMatchSnapshot BuildSnapshotFromReplay(string snapshotPath, string actionsPath, int upTo)
    {
        var snap = JsonNode.Parse(File.ReadAllText(snapshotPath))!.AsObject();
        var summary = snap["summary"]!.AsObject();
        var sd = snap["starting_info"]!["match_and_starting_data"]!["starting_data"]!.AsObject();

        var cards = new List<ServerCard>();

        void AddCard(JsonNode? node, string loc)
        {
            if (node is not JsonObject c)
            {
                return;
            }

            cards.Add(new ServerCard(
                c["card_id"]?.GetValue<int>() ?? 0,
                c["is_gold"]?.GetValue<bool>() ?? false,
                loc,
                c["location_number"]?.GetValue<int>() ?? 0,
                c["name"]?.GetValue<string>() ?? ""));
        }

        AddCard(sd["location_card_left"], "board_hqleft");
        AddCard(sd["location_card_right"], "board_hqright");
        foreach (var c in sd["starting_hand_left"]?.AsArray() ?? new JsonArray()) AddCard(c, "hand_left");
        foreach (var c in sd["starting_hand_right"]?.AsArray() ?? new JsonArray()) AddCard(c, "hand_right");
        foreach (var c in sd["deck_left"]?.AsArray() ?? new JsonArray()) AddCard(c, "deck_left");
        foreach (var c in sd["deck_right"]?.AsArray() ?? new JsonArray()) AddCard(c, "deck_right");

        var acts = new List<ServerAction>();
        var doc = JsonNode.Parse(File.ReadAllText(actionsPath))!.AsObject();
        int maxId = 0;
        foreach (var n in doc["actions"]?.AsArray() ?? new JsonArray())
        {
            if (n is not JsonObject a)
            {
                continue;
            }

            int id = a["action_id"]?.GetValue<int>() ?? 0;
            if (id > upTo)
            {
                continue;
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            if (a["action_data"] is JsonObject ad)
            {
                foreach (var (k, v) in ad)
                {
                    data[k] = v?.ToString() ?? "";
                }
            }

            acts.Add(new ServerAction(
                id,
                a["action_type"]?.GetValue<string>() ?? "",
                a["player_id"]?.GetValue<int>() ?? 0,
                data,
                a["turn_number"]?.GetValue<int>() ?? 0));

            maxId = Math.Max(maxId, id);
        }

        return new ServerMatchSnapshot(
            summary["match_id"]?.GetValue<int>() ?? 0,
            summary["turns"]?.GetValue<int>() ?? 0,
            summary["left_player_id"]?.GetValue<int>() ?? 0,
            summary["right_player_id"]?.GetValue<int>() ?? 0,
            cards,
            acts)
        {
            NextActionId = maxId + 1,
            SendActionId = maxId,
        };
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                string key = args[i][2..];
                string val = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i] : "1";
                d[key] = val;
            }
        }

        return d;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KLink.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string FindDataDirectory(string repoRoot)
    {
        foreach (string rel in new[] { "src/KLink.Bot/bin/Release/net10.0/Data", "src/KLink.Bot/bin/Debug/net10.0/Data" })
        {
            string p = Path.Combine(repoRoot, rel);
            if (File.Exists(Path.Combine(p, "cards.json")))
            {
                return p;
            }
        }

        return Path.Combine(repoRoot, "klink bot", "docs");
    }
}
