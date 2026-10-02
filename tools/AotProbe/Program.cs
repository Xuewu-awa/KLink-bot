using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Replay;
using KLink.Bot.Server;

namespace KLink.Bot.AotProbe;

/// <summary>
/// **在「反射式序列化被关掉」的宿主里**走一遍服务端要走的全部路径。
///
/// ## 为什么必须有这个工程
///
/// `fyserver.csproj` 里写着 `<JsonSerializerIsReflectionEnabledByDefault>false</...>`
/// （为了 AOT 兼容），那是**进程级**开关。任何用了反射式 JSON 却没显式配
/// `TypeInfoResolver` 的代码，在这个宿主里抛异常、在别的宿主里正常。
///
/// **实测代价**（`rel/data/fyserver/bot-log/bot-20261001.log`）：一整局 66 行日志全是
/// <c>⚠ 神经网络决策失败（Reflection-based serialization has been disabled…），本回合改用贪心</c>
/// —— 对局照常跑完，**看起来像「AI 在打但很笨」，实际是 AI 根本没上场**。
///
/// `ServerBridgeTest` / `NNPlay` 都测不出它：它们自己**没关**反射，
/// 所以内核在那两个宿主里是好的，一进 fyserver 就废。
///
/// 本工程就是为这一条存在的。**改完内核跑它一次**，比打一局真对局便宜得多。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string repoRoot = FindRepoRoot();
        string dataDir = args.Length > 0
            ? args[0]
            : Path.Combine(repoRoot, "tem", "fyserver", "bin", "Release", "net10.0", "BotData");

        Console.WriteLine("=== 反射被关掉的宿主里跑内核全路径 ===");
        Console.WriteLine($"数据目录 {dataDir}");
        Console.WriteLine($"反射开关 JsonSerializerIsReflectionEnabledByDefault = false");
        Console.WriteLine();

        int failed = 0;

        // ① 卡库（`CardDatabase.Load` 用 JsonSerializer.Deserialize<T>）
        CardDatabase db;
        try
        {
            db = CardDatabase.Load(dataDir);
            Console.WriteLine($"✅ ① CardDatabase.Load      → {db.Count} 张");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ① CardDatabase.Load      → {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // ② 蓝图 IR（JsonDocument / JsonNode，不依赖反射 —— 应该过）
        try
        {
            string ir = Path.Combine(dataDir, "card-ir.json");
            if (File.Exists(ir)) KismetLibrary.Initialize(ir);
            Console.WriteLine($"✅ ② KismetLibrary          → {KismetLibrary.Default?.CardCount ?? 0} 张可解释");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ② KismetLibrary          → {ex.GetType().Name}: {ex.Message}");
            failed++;
        }

        // ③ 模型 + 卡向量
        NnModel? model = null;
        StateEncoder.CardVecs? vecs = null;
        try
        {
            string mp = Path.Combine(dataDir, "nn-model.bin");
            if (File.Exists(mp)) model = NnModel.Load(mp);
            Console.WriteLine($"✅ ③ NnModel.Load           → dim={model?.Dim} hidden={model?.Hidden}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ③ NnModel.Load           → {ex.GetType().Name}: {ex.Message}");
            failed++;
        }

        try
        {
            vecs = StateEncoder.LoadCardVectors(dataDir);
            Console.WriteLine($"✅ ④ LoadCardVectors        → {vecs.ByName.Count} 张有向量");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ④ LoadCardVectors        → {ex.GetType().Name}: {ex.Message}");
            failed++;
        }

        // ⑤ ★★ 状态指纹 —— **就是这一处曾经挂掉，导致整局 AI 静默退回贪心**
        try
        {
            var probe = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 1);
            string fp = probe.State.SnapshotJson();
            Console.WriteLine($"✅ ⑤ SnapshotJson（状态指纹）→ {fp.Length} 字符  ← `Replayer` 靠它做试算");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ⑤ SnapshotJson（状态指纹）→ {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("      ↑ 它一挂，神经网络决策每回合都会抛异常并**静默退回贪心**");
            failed++;
        }

        // ⑥ ★★ 完整决策（含 Replayer 试算）—— 端到端
        if (model is not null && vecs is not null)
        {
            try
            {
                var (ok, detail) = RunDecision(db, model, vecs, repoRoot);
                Console.WriteLine($"{(ok ? "✅" : "❌")} ⑥ 完整 NN 决策          → {detail}");
                if (!ok) failed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ⑥ 完整 NN 决策          → {ex.GetType().Name}: {ex.Message}");
                failed++;
            }
        }
        else
        {
            Console.WriteLine("⚠ ⑥ 跳过（缺模型或向量）");
        }

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("✅ 反射被关掉时内核全路径正常");
            return 0;
        }

        Console.WriteLine($"❌ {failed} 项失败 —— 这些在普通宿主里都是好的，只有 fyserver 会中招");
        return 1;
    }

    /// <summary>用真实回放跑一次完整决策，确认 `Replayer` 试算没挂。</summary>
    private static (bool Ok, string Detail) RunDecision(CardDatabase db, NnModel model,
                                                        StateEncoder.CardVecs vecs, string repoRoot)
    {
        string repDir = Path.Combine(repoRoot, "klink bot", "docs", "fresh-replays");
        string snapPath = Path.Combine(repDir, "replay-989040.json");
        string actsPath = Path.Combine(repDir, "replay-989040.actions.json");
        if (!File.Exists(snapPath) || !File.Exists(actsPath))
        {
            return (true, "跳过（没有回放样本）");
        }

        var replay = ReplayData.Load(snapPath, actsPath);
        var runner = new ReplayRunner(db);
        var report = runner.Run(replay, verbose: false);
        var engine = report.Engine;
        if (engine is null)
        {
            return (false, "重放没产出引擎");
        }

        var svc = new BotTurnService(db, Side.Right, 2, model, vecs);
        var snapshot = ReplayToSnapshot(replay);
        var result = svc.DecideTurn(snapshot);

        bool fellBack = result.Log.Any(l => l.Contains("神经网络决策失败", StringComparison.Ordinal));
        bool hasCandidateTable = result.Log.Any(l => l.Contains("NN 决策 #", StringComparison.Ordinal));

        if (fellBack)
        {
            string why = result.Log.First(l => l.Contains("神经网络决策失败", StringComparison.Ordinal));
            return (false, $"**退回了贪心**：{why.Trim()}");
        }

        return (true, hasCandidateTable
            ? "走了神经网络（候选表已产出）"
            : $"走了神经网络但本回合没候选（{result.Actions.Count} 条动作）");
    }

    /// <summary>把回放包成服务端快照形状（只为让决策跑起来）。</summary>
    private static ServerMatchSnapshot ReplayToSnapshot(ReplayData r)
    {
        var cards = new List<ServerCard>();
        foreach (var c in r.Cards)
        {
            string loc = c.Location switch
            {
                CardLocation.BoardHqLeft => "board_hqleft",
                CardLocation.BoardHqRight => "board_hqright",
                CardLocation.HandLeft => "hand_left",
                CardLocation.HandRight => "hand_right",
                CardLocation.DeckLeft => "deck_left",
                CardLocation.DeckRight => "deck_right",
                _ => "",
            };
            if (loc.Length == 0) continue;
            cards.Add(new ServerCard(c.CardId, c.IsGold, loc, c.LocationNumber, c.Name));
        }

        var acts = r.Actions.Select(a => new ServerAction(
            a.ActionId, a.ActionType, a.PlayerId, a.ActionData, a.TurnNumber)).ToList();

        return new ServerMatchSnapshot(r.MatchId, r.Turns, r.LeftPlayerId, r.RightPlayerId, cards, acts)
        {
            NextActionId = acts.Count + 1,
        };
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
}
