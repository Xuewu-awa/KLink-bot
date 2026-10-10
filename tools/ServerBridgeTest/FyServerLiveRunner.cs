using System.Text.Json.Nodes;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Server;

namespace KLink.Bot.ServerBridgeTest;

internal static class FyServerLiveRunner
{
    public static async Task<int> RunAsync(string repoRoot, IReadOnlyDictionary<string, string> options)
    {
        string? baseUrl = options.GetValueOrDefault("base-url");
        string? token = options.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("FY_SERVER_JWT");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("需要 --base-url <URL> 和 --token <JWT>（或环境变量 FY_SERVER_JWT）。");
            return 2;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? serverUri)
            || serverUri.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine("--base-url 必须是 http 或 https 的绝对 URL。");
            return 2;
        }

        string sideOption = options.GetValueOrDefault("side", "right");
        if (sideOption is not ("left" or "right"))
        {
            Console.Error.WriteLine("--side 只能是 left 或 right。");
            return 2;
        }
        Side botSide = sideOption == "left" ? Side.Left : Side.Right;
        bool commit = options.ContainsKey("commit-actions");
        int? expectedMatchId = null;
        if (options.TryGetValue("match-id", out string? expectedId))
        {
            if (!int.TryParse(expectedId, out int requestedId))
            {
                Console.Error.WriteLine("--match-id 必须是整数。");
                return 2;
            }
            expectedMatchId = requestedId;
        }

        using var client = new FyServerHttpClient(baseUrl, token);
        FyServerSession session;
        Console.WriteLine(commit
            ? "commit mode：将调用 reconnect（服务端会标记当前用户在线）；通过校验后才提交动作。"
            : "dry-run：动作轮询调用 PUT /actions；fyserver 会先执行 TickBot。本工具不会 POST 预览动作。");
        try
        {
            session = await client.GetCurrentSessionAsync(botSide, includeMulliganState: commit,
                                                          expectedMatchId: expectedMatchId);
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or System.Text.Json.JsonException
                                      or InvalidOperationException or TaskCanceledException)
        {
            Console.Error.WriteLine($"无法读取 fyserver 对局：{ex.Message}");
            return 2;
        }

        ServerMatchSnapshot snapshot = session.Snapshot;
        int botPlayerId = botSide == Side.Left ? snapshot.LeftPlayerId : snapshot.RightPlayerId;
        Console.WriteLine($"对局 {snapshot.MatchId}：回合计数 {snapshot.Turns}，动作 {snapshot.Actions.Count}，" +
                          $"下一动作号 {snapshot.NextActionId}，action side={session.ActionSide.ToWire()}，bot={botSide.ToWire()}");
        if (!session.HasMulliganState)
        {
            Console.WriteLine("注意：只读快照不含换牌弃牌列表；此结果仅供预览，不能据此提交动作。");
        }
        string dataDir = FindDataDirectory(repoRoot);
        var db = CardDatabase.Load(dataDir);
        string irPath = Path.Combine(dataDir, "card-ir.json");
        if (File.Exists(irPath)) KismetLibrary.Initialize(irPath);
        var service = new BotTurnService(db, botSide, botPlayerId);
        service.LoadDeckCodeTable(LoadDeckCodeTable(repoRoot));
        BotTurnService.TurnResult result = service.DecideTurn(snapshot);

        Console.WriteLine("===== 预览动作 =====");
        foreach (ServerAction action in result.Actions)
        {
            string data = string.Join(" ", action.ActionData.Select(pair => $"{pair.Key}={pair.Value}"));
            Console.WriteLine($"#{action.ActionId} {action.ActionType} player={action.PlayerId} turn={action.TurnNumber} {data}");
        }
        foreach (string line in result.Log) Console.WriteLine(line);
        Console.WriteLine($"未应用动作 {result.UnappliedActions} 条；会话 ID {snapshot.ActionSessionId}");

        if (!commit)
        {
            Console.WriteLine("dry-run 完成：未提交任何动作。");
            return 0;
        }

        if (!session.HasMulliganState)
        {
            Console.Error.WriteLine("拒绝提交：缺少双方换牌弃牌状态。");
            return 2;
        }
        if (session.ActionSide != botSide)
        {
            Console.Error.WriteLine("拒绝提交：fyserver 当前行动方不是所选 bot side。");
            return 2;
        }
        if (result.Divergence?.Detected == true || result.UnappliedActions > 0 || result.State?.ActiveSide != botSide)
        {
            Console.Error.WriteLine("拒绝提交：回放存在漂移，或内核认为当前不是 bot 回合。");
            return 2;
        }
        if (snapshot.ActionSessionId <= 0 || result.Actions.Count == 0)
        {
            Console.Error.WriteLine("拒绝提交：动作会话 ID 不可用或动作列表为空。");
            return 2;
        }

        Console.WriteLine("确认开启提交：将按序发送上方全部动作。");
        foreach (ServerAction action in result.Actions)
        {
            await client.SubmitActionAsync(snapshot.MatchId, snapshot.ActionSessionId, action);
            Console.WriteLine($"已提交 #{action.ActionId} {action.ActionType}");
        }

        return 0;
    }

    private static string FindDataDirectory(string root)
    {
        foreach (string relative in new[] { "src/KLink.Bot/bin/Release/net10.0/Data", "src/KLink.Bot/bin/Debug/net10.0/Data" })
        {
            string path = Path.Combine(root, relative);
            if (File.Exists(Path.Combine(path, "cards.json"))) return path;
        }
        return Path.Combine(root, "klink bot", "docs");
    }

    private static Dictionary<string, string> LoadDeckCodeTable(string root)
    {
        foreach (string relative in new[] { "klink bot/docs/deck_code_ids.live.json", "klink bot/docs/deck_code_ids.json" })
        {
            string path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj) continue;
            foreach (var (key, value) in obj)
            {
                string? name = value switch
                {
                    JsonValue scalar when scalar.TryGetValue<string>(out string? text) => text,
                    JsonObject card => card["card"]?.GetValue<string>() ?? card["name"]?.GetValue<string>(),
                    _ => null,
                };
                if (!string.IsNullOrEmpty(name)) result[key] = name;
            }
            if (result.Count > 0) return result;
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
