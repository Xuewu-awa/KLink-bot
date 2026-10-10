using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLink.Bot.ServerBridgeTest;

/// <summary>
/// Converts the raw JSON returned by fyserver's match endpoint into the two
/// files consumed by <see cref="KLink.Bot.Replay.ReplayData.Load"/>.
/// </summary>
internal static class FyServerReplayWrapper
{
    private static readonly IReadOnlyDictionary<string, string> FullToCompact =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XActionPlayCardFromHand"] = "PC",
            ["XActionMoveCardToLine"] = "ML",
            ["XActionAttackCard"] = "AC",
            ["XActionCardToDrawSelected"] = "CS",
            ["XActionHandTargetSelected"] = "HT",
        };

    public static int Run(string inputPath, string? actionsPathArg, string outputPrefix)
    {
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"找不到 fyserver 原始回放：{inputPath}");
            return 2;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(inputPath))?.AsObject()
                   ?? throw new FormatException("根节点不是 JSON 对象");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            Console.Error.WriteLine($"无法读取 fyserver 原始回放：{ex.Message}");
            return 2;
        }

        JsonObject head;
        JsonArray actions;
        if (root["starting_data"] is JsonObject startingData
            && root["match"] is JsonObject match
            && root["actions"] is JsonArray rawActions)
        {
            head = BuildHead(root, startingData, match);
            actions = rawActions;
        }
        else if (root["summary"] is not null && root["starting_info"] is not null)
        {
            head = root;
            string rawActionsPath = actionsPathArg
                ?? Path.ChangeExtension(inputPath, ".actions.json");
            if (!File.Exists(rawActionsPath))
            {
                Console.Error.WriteLine($"包装快照缺少动作文件：{rawActionsPath}（可用 --actions 指定）");
                return 2;
            }

            try
            {
                JsonNode actionRoot = JsonNode.Parse(File.ReadAllText(rawActionsPath))
                    ?? throw new FormatException("动作文件为空");
                actions = actionRoot["actions"]?.AsArray()
                    ?? throw new FormatException("动作文件缺少 actions 数组");
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                Console.Error.WriteLine($"无法读取动作文件：{ex.Message}");
                return 2;
            }
        }
        else
        {
            Console.Error.WriteLine("原始回放必须包含 starting_data、match、actions，或 summary、starting_info");
            return 2;
        }

        static JsonObject BuildHead(JsonObject source, JsonObject startingData, JsonObject match)
        {
            var summary = new JsonObject
            {
                ["match_id"] = match["match_id"]?.DeepClone(),
                ["match_type"] = match["match_type"]?.DeepClone(),
                ["status"] = match["status"]?.DeepClone(),
                ["left_player_id"] = match["player_id_left"]?.DeepClone(),
                ["right_player_id"] = match["player_id_right"]?.DeepClone(),
                ["turns"] = match["current_turn"]?.DeepClone(),
                ["action_count"] = match["current_action_id"]?.DeepClone(),
                ["winner_side"] = match["winner_side"]?.DeepClone(),
                ["local_subactions"] = source["local_subactions"]?.DeepClone(),
            };

            var matchAndStartingData = new JsonObject
            {
                ["starting_data"] = startingData.DeepClone(),
                ["match"] = match.DeepClone(),
                ["mulligan_left"] = source["mulligan_left"]?.DeepClone(),
                ["mulligan_right"] = source["mulligan_right"]?.DeepClone(),
            };
            return new JsonObject
            {
                ["summary"] = summary,
                ["starting_info"] = new JsonObject
                {
                    ["local_subactions"] = source["local_subactions"]?.DeepClone(),
                    ["match_and_starting_data"] = matchAndStartingData,
                },
            };
        }

        int converted = 0;
        foreach (JsonNode? node in actions)
        {
            if (node is not JsonObject action
                || action["action_type"] is not JsonValue typeValue
                || !typeValue.TryGetValue<string>(out string? fullName)
                || !FullToCompact.TryGetValue(fullName, out string? compactName))
            {
                continue;
            }

            action["action_type"] = compactName;
            converted++;
        }

        string headPath = outputPrefix + ".json";
        string actionsPath = outputPrefix + ".actions.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(headPath))!);
        File.WriteAllText(headPath, head.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(actionsPath, new JsonObject { ["actions"] = actions.DeepClone() }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

        Console.WriteLine($"写出 {headPath}");
        Console.WriteLine($"写出 {actionsPath}（{actions.Count} 条动作，{converted} 条全名→紧凑名）");
        return 0;
    }
}
