using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Server;

namespace KLink.Bot.ServerBridgeTest;

/// <summary>
/// **服务端加载路径的验证器** —— 走一遍 `fyserver/Services/ServerBotService.Load()`
/// 的**同一串调用**，确认服务端那个数据目录真的能加载成功。
///
/// 为什么单独验：`ServerBotService` 是**懒加载**的（第一次要用 bot 才加载），
/// 所以「服务器能启动」**不等于**「内核能加载」——
/// 加载失败只会在真对局的第一回合炸出来，那时候排查成本高得多。
///
/// 用法：
/// <code>
///   ServerBridgeTest --verify-load "tem/fyserver/bin/Release/net10.0/BotData"
/// </code>
/// </summary>
internal static class LoadVerifier
{
    public static int Run(string dataDir)
    {
        Console.WriteLine($"=== 验证服务端加载路径 ===");
        Console.WriteLine($"数据目录 {dataDir}");
        Console.WriteLine();

        if (!Directory.Exists(dataDir))
        {
            Console.Error.WriteLine("❌ 目录不存在");
            return 2;
        }

        int problems = 0;

        // ---- 1) 卡库 ----
        CardDatabase db;
        try
        {
            db = CardDatabase.Load(dataDir);
            Console.WriteLine($"✅ CardDatabase.Load  → {db.Count} 张卡");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ CardDatabase.Load 失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // ---- 2) 蓝图 IR ----
        string irPath = Path.Combine(dataDir, "card-ir.json");
        if (!File.Exists(irPath))
        {
            Console.Error.WriteLine($"❌ 缺 card-ir.json —— 卡牌效果不会执行，与训练分布不符");
            problems++;
        }
        else
        {
            KismetLibrary.Initialize(irPath);
            if (KismetLibrary.Default is null)
            {
                Console.Error.WriteLine($"❌ KismetLibrary 加载失败：{KismetLibrary.LoadError}");
                problems++;
            }
            else
            {
                Console.WriteLine($"✅ KismetLibrary      → {KismetLibrary.Default.CardCount} 张卡可解释");
            }
        }

        // ---- 3) 卡组码表 ----
        var codes = LoadDeckCodes(dataDir);
        if (codes.Count == 0)
        {
            Console.Error.WriteLine("❌ 卡组码表为空 —— 动作的槽 4 会填 0");
            problems++;
        }
        else
        {
            Console.WriteLine($"✅ 卡组码表           → {codes.Count} 条");
        }

        // ---- 4) 神经网络（可选，但缺了会退化成贪心）----
        string modelPath = Path.Combine(dataDir, "nn-model.bin");
        string vecPath = Path.Combine(dataDir, "card-vectors.json");

        NnModel? model = null;
        StateEncoder.CardVecs? vecs = null;

        if (!File.Exists(modelPath))
        {
            Console.WriteLine("⚠ 没有 nn-model.bin —— 会用贪心兜底（能打，但不是 AI）");
        }
        else if (!File.Exists(vecPath))
        {
            Console.WriteLine("⚠ 有模型但缺 card-vectors.json —— 用不了，退化成贪心");
        }
        else
        {
            try
            {
                model = NnModel.Load(modelPath);
                vecs = StateEncoder.LoadCardVectors(dataDir);
                Console.WriteLine($"✅ NnModel.Load       → dim={model.Dim} hidden={model.Hidden}");
                Console.WriteLine($"✅ CardVecs           → {vecs.ByName.Count} 张卡有向量");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ 神经网络加载失败：{ex.GetType().Name}: {ex.Message}");
                model = null;
                vecs = null;
                problems++;
            }
        }

        // ---- 5) 真的构造一次服务，确认策略就绪 ----
        try
        {
            var svc = new BotTurnService(db, Side.Right, botPlayerId: 2, model, vecs);
            svc.LoadDeckCodeTable(codes);
            Console.WriteLine($"✅ BotTurnService     → 策略={(svc.HasNeuralPolicy ? "神经网络" : "贪心兜底")}，" +
                              $"卡组码反向索引 {svc.DeckCodeCount} 条");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ BotTurnService 构造失败：{ex.GetType().Name}: {ex.Message}");
            problems++;
        }

        Console.WriteLine();
        if (problems == 0)
        {
            Console.WriteLine("✅ 服务端加载路径全部通过");
            return 0;
        }

        Console.WriteLine($"❌ 有 {problems} 项问题");
        return 1;
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
