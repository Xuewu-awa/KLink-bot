using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLink.Bot.Engine;

/// <summary>
/// 线路上的**紧凑动作**格式 —— 由真实客户端发出的原样形态。
///
/// 实测样本（来自 fyserver 控制台日志，见 docs/live-actions.json）：
/// <code>
/// {"action_type":"PC","player_id":892257,"action_id":49,"local_subactions":1,
///  "action_data":{"0":"64","1":"3","2":"0","3":"0","4":"jJ","84":"11"}}
///
/// {"action_type":"AC","player_id":654612,"action_id":39,"local_subactions":1,
///  "action_data":{"0":"7","1":"59","2":"4H","3":"d3","84":"3"}}
/// </code>
///
/// 两个和离线推断不同的要点：
/// 1. <c>action_type</c> 走**紧凑名**（`PC`/`ML`/`AC`），但回合边界动作保持全名
///    （`XActionStartOfTurn`/`XActionEndOfTurn`）—— 对应 `XActionNamesFullToCompact`
/// 2. <c>action_data</c> 是**数字下标 → 字符串值**的映射，不是带名字的 ActionValue2 数组
///
/// <c>local_subactions = 1</c> + 服务端侧 <c>sub_actions</c> 为空 = **效果本地结算**，
/// 这就是确定性锁步的确证。
/// </summary>
public sealed class WireAction
{
    public required string ActionType { get; init; }
    public required int PlayerId { get; init; }
    public int ActionId { get; init; }
    public bool LocalSubactions { get; init; }

    /// <summary>
    /// 服务端记录里的 <c>turn_number</c>。
    ///
    /// ⚠️ 这是**回放接口独有的字段**（真实客户端发的紧凑动作里没有），
    /// 所以回放驱动必须用它、而不要自己数回合 —— 自己数会因为
    /// 「同一回合里的多次动作」和「跳过的空回合」而错位。
    /// </summary>
    public int TurnNumber { get; init; }

    /// <summary>服务端侧的子动作列表长度（锁步下恒为 0，非 0 就说明服务端参与了结算）。</summary>
    public int SubActionCount { get; init; }

    public IReadOnlyDictionary<string, string> ActionData { get; init; }
        = new Dictionary<string, string>();

    /// <summary>展开成蓝图里的全名（无法展开时原样返回）。</summary>
    public string FullActionType => CompactToFull.GetValueOrDefault(ActionType, ActionType);

    /// <summary>
    /// 紧凑名 → 全名。
    ///
    /// ⚠️ `PC`/`ML`/`AC` 是**实测**确证的；`HT`/`CS` 由 BP_OnlineMatch 的
    ///    `XAction*` 名字全集反推（见下面注释），其余仍未实测。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CompactToFull = new Dictionary<string, string>
    {
        // ---- 实测确证（docs/live-actions.json）----
        ["PC"] = "XActionPlayCardFromHand",
        ["ML"] = "XActionMoveCardToLine",
        ["AC"] = "XActionAttackCard",

        // ---- `CS` / `HT`：由「XAction 名字全集 + 分派开关链」确证 ----
        //
        // 依据（BP_OnlineMatch，`out/bp-onlinematch.json`）：
        //  1. 类里的 X 动作名字**全集只有 11 个**：XActionPlayCardFromHand / AttackCard /
        //     MoveCardToLine / Cheat / Mulligan / StartOfGame / StartOfTurn / EndOfTurn /
        //     Finished / HandTargetSelected / CardToDrawSelected。
        //  2. `ExecuteUbergraph_BP_OnlineMatch` 里有一串开关
        //     （`NotEqual_StriStri(XActionNameCompactToFull(action_type), "<全名>")` +
        //     `JumpIfNot`），逐个分派到 `ReceiveAction*`。偏移 4023 那一档比较的字符串
        //     就是 `XActionCardToDrawSelected`，处理函数是
        //     `Receive Action XAction Card to Draw Selected`。
        //  3. 紧凑名按「两个关键词首字母」取（PlayCardFromHand→PC、AttackCard→AC、
        //     HandTargetSelected→HT）；C 开头 S 相关的**只有** CardToDrawSelected。
        //  4. 实测回放里 `CS` 的 `0` 号槽全部落在 `GetChooseSpawnCards` 的实现卡上
        //     （pams / bpf / the_rock_of_gibraltar / hampshire_regiment / 2nd_west_africa），
        //     语义与 CardToDrawSelected 自洽。
        ["CS"] = "XActionCardToDrawSelected",
        ["HT"] = "XActionHandTargetSelected",

        // ---- 以下为推测，待实测确认 ----
        ["MG"] = "XActionMulligan",
        ["SG"] = "XActionStartOfGame",
        ["CH"] = "XActionCheat",
        ["EM"] = "XActionEndMatch",
    };

    /// <summary>
    /// <c>action_data</c> 的键是**下标**，而且**含义随动作类型而变** ——
    /// 每种动作有自己的 ActionValue2 参数表，压缩后的下标自然不同。
    ///
    /// 依据：5 局真实回放的全部动作（docs/live-replays/）交叉验证。
    ///
    /// | 动作 | `0` | `1` | `2` | `3` | `4` | `84` |
    /// |---|---|---|---|---|---|---|
    /// | `PC` 出牌 | 卡牌 ID | 部署槽位 | 目标卡牌 ID(0=无) | **三选一下标** | **卡组码** | 对手 HQ |
    /// | `AC` 攻击 | 攻击者 ID | 防御者 ID | **攻击者码** | **防御者码** | — | 对手 HQ |
    /// | `ML` 移动 | 卡牌 ID | 目标槽位 | **卡组码** | — | — | 对手 HQ |
    /// | `CS` 选牌答复 | 挑牌的卡 ID | **候选下标(0..2)** | **选中卡的卡组码** | — | — | — |
    /// | `HT` 手牌目标 | 目标卡 ID | 第二张目标 ID | — | — | — | — |
    /// | `XActionStartOfTurn` | — | — | — | — | — | 对手 HQ |
    /// | `XActionEndOfTurn` | — | — | — | — | — | 对手 HQ |
    ///
    /// ⚠️ `CS` 那一行是 2026-02 补的，依据见 <see cref="CompactToFull"/>：
    /// `CS` = `XActionCardToDrawSelected`，是 `ZActionSelectCardToDrawPending`
    /// （「从候选里挑一张」）的**答复**。同一张卡可以连续答复多次（实测 206428 的
    /// `#57`/`#58` 都是 `cardID=4`），所以驱动侧要按队列而不是单个值来存。
    ///
    /// ⚠️ 坑 1：`0` 的取值（如 64 / 60 / 32）**有些恰好是合法的 2 字符卡组码**，
    /// 拿它去查 deckCodeIDsTable2 会得到**看起来合理但完全无关**的卡名（假阳性）。
    /// 真正引用卡的是那些值形如 `jJ`/`d4`/`4H` 的键。
    ///
    /// ⚠️ 坑 2（**曾判错，已改正**）：`84` 是**行动方的对手**的 HQ 当前防御力，
    /// **不是行动方自己的**。判据是 310284 那局：把 `84` 按「对手 HQ」解释，
    /// 整条 21 回合的轨迹与 `ActionEndMatch{reason:Victory_DestroyHQ,winner_side:left}`
    /// 完全闭合（最后一次行动把右方 HQ 从 3 打到 0）；
    /// 按「自己 HQ」解释则在终局时右方还剩 11 点，与胜利原因矛盾。
    /// 另外它是**动作结算前**的采样值。
    /// </summary>
    public static class KeyIndex
    {
        public const string CardId = "0";
        public const string SecondId = "1";
        public const string CodeSlotA = "2";
        public const string CodeSlotB = "3";
        public const string CodeSlotC = "4";

        /// <summary>
        /// `PC` 的 `3` 号槽 = 三选一（Choose One）选的分支下标。
        ///
        /// ⚠️ 文档以前写「`3` 恒为 0」，那是**采样里没出现过三选一卡**造成的。
        /// 实测 6 局 295 条 `PC`：294 条是 0，唯一非 0 的是 634651 `#122`
        /// （`card_event_planned_attack`，卡面「Choose One - deal 2 damage and add a PLAN
        /// to hand OR draw a card and add a BLAS…」，蓝图用 `WhichChooseOne` 走 0/1 分支）。
        /// 对应 `ZActionPlayCardFromHand` 参数表里的 `Int:chooseOneIndex`。
        /// </summary>
        public const string ChooseOneIndex = CodeSlotB;

        public const string OpponentHq = "84";

        /// <summary>旧名，保留以免调用点散落；语义就是 <see cref="OpponentHq"/>。</summary>
        public const string SideMarker = OpponentHq;
    }

    /// <summary>该动作引用的卡组码（按动作类型取对应槽位）。</summary>
    public IReadOnlyList<string> CardCodes => ActionType switch
    {
        "PC" => NonZero(Get(KeyIndex.CodeSlotC)),
        "AC" => NonZero(Get(KeyIndex.CodeSlotA), Get(KeyIndex.CodeSlotB)),
        "ML" => NonZero(Get(KeyIndex.CodeSlotA)),

        // `CS` 的 `1` 号槽是**候选下标**（0..2），不是卡组码 —— 直接套默认分支
        // 会把它当卡组码去查表，得到一个"看起来合理但完全无关"的卡名（§8.2.2 那个假阳性坑）。
        "CS" => NonZero(Get(KeyIndex.CodeSlotA)),

        _ => NonZero(Get(KeyIndex.CodeSlotA), Get(KeyIndex.CodeSlotB), Get(KeyIndex.CodeSlotC)),
    };

    /// <summary>
    /// 取有效的卡组码，滤掉「空」的各种写法。
    ///
    /// ⚠️ 必须包含字面量 <c>"None"</c>：动作数据里没填的槽位序列化出来就是字符串
    /// <c>"None"</c>（不是 null、也不是空串）。漏掉它会让身份校验把**本来合法的动作**
    /// 判成失败 —— 实测这一条误杀了 29~40 条 PC 动作（"卡组码 None 不在
    /// deckCodeIDsTable2 里"），进而让这些牌永远卡在牌库/手牌里，
    /// 直接污染「区域」这项对拍指标。
    /// </summary>
    private static string[] NonZero(params string?[] values)
        => values.Where(v => !string.IsNullOrEmpty(v)
                             && v != "0"
                             && !v.Equals("None", StringComparison.OrdinalIgnoreCase))
                 .Select(v => v!).ToArray();

    public int CardId => ParseInt(Get(KeyIndex.CardId));
    public int SecondId => ParseInt(Get(KeyIndex.SecondId));
    public string? SideMarker => Get(KeyIndex.SideMarker);

    /// <summary>非动作参数的「额外状态字段」的键（纯数字，且不是 0..4）。</summary>
    public IEnumerable<string> ExtraStateKeys
        => ActionData.Keys.Where(k => k.Length > 0 && k.All(char.IsAsciiDigit)
                                      && !ActionParamKeys.Contains(k));

    private static readonly HashSet<string> ActionParamKeys = new(StringComparer.Ordinal)
    {
        "0", "1", "2", "3", "4",
    };

    /// <summary>动作结算**前**，行动方对手的 HQ 防御力（缺失或非数字时为 -1）。</summary>
    /// <param name="hqKey">
    /// HQ 字段在 <c>action_data</c> 里的下标。
    ///
    /// ⚠️ **这个下标每局都不一样**，不能写死。实测 5 局分别是
    /// 310284→`84`、165924→`24`、955337→`37`、130691→`91`、563868→`68`。
    /// 原因：客户端发的是「下标 → 值」，下标来自一张**动态登记**的字段名表，
    /// 每局登记顺序不同。用 <see cref="ReplayData.InferHqKey"/> 推出来。
    /// </param>
    public int OpponentHqOf(string? hqKey)
        => hqKey is null ? -1 : ParseInt(Get(hqKey)) is var v && v > 0 ? v : -1;

    /// <summary>`PC` 的 `2` 号键 = 目标卡牌 ID（0 表示无目标）。</summary>
    public int TargetId => ParseInt(Get(KeyIndex.CodeSlotA));

    public string? Get(string key) => ActionData.GetValueOrDefault(key);

    /// <summary>`action_data` 里的值转 int（非数字/缺失 → 0）。</summary>
    public static int ParseInt(string? s) => int.TryParse(s, out int v) ? v : 0;
    public static WireAction Parse(JsonNode node)
    {
        var obj = node as JsonObject ?? throw new FormatException("动作不是 JSON 对象");

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (obj["action_data"] is JsonObject ad)
        {
            foreach (var (k, v) in ad)
            {
                data[k] = v?.ToString() ?? "";
            }
        }

        return new WireAction
        {
            ActionType = obj["action_type"]?.GetValue<string>() ?? "",
            PlayerId = obj["player_id"]?.GetValue<int>() ?? 0,
            ActionId = obj["action_id"]?.GetValue<int>() ?? 0,
            LocalSubactions = (obj["local_subactions"]?.GetValue<int>() ?? 0) != 0,
            TurnNumber = obj["turn_number"]?.GetValue<int>() ?? 0,
            SubActionCount = (obj["sub_actions"] as JsonArray)?.Count ?? 0,
            ActionData = data,
        };
    }

    public static WireAction Parse(string json)
        => Parse(JsonNode.Parse(json) ?? throw new FormatException("空 JSON"));

    public override string ToString()
        => $"[{ActionId}] {ActionType,-20} p{PlayerId}  " +
           string.Join(" ", ActionData.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key)
               .Select(kv => $"{kv.Key}={kv.Value}"));
}

/// <summary>
/// 一局对局的线路动作序列（从日志/回放里抽出来的原始数据）。
/// </summary>
public sealed class WireReplay
{
    public required string Source { get; init; }
    public required IReadOnlyList<WireAction> Actions { get; init; }

    public static WireReplay Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var actions = new List<WireAction>();

        if (doc.RootElement.TryGetProperty("actions", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                actions.Add(WireAction.Parse(JsonNode.Parse(el.GetRawText())!));
            }
        }

        string source = doc.RootElement.TryGetProperty("source_log", out var s)
            ? s.GetString() ?? path
            : path;

        return new WireReplay { Source = source, Actions = actions };
    }

    /// <summary>统计摘要 —— 用来快速看清一份回放里有什么。</summary>
    public string Summarize()
    {
        var byType = Actions.GroupBy(a => a.ActionType)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}×{g.Count()}");

        var players = Actions.Select(a => a.PlayerId).Distinct().OrderBy(x => x);
        var codes = Actions.SelectMany(a => a.CardCodes)
            .Where(c => c is not null).Distinct().OrderBy(c => c, StringComparer.Ordinal);

        return $"动作 {Actions.Count} 条（{string.Join(", ", byType)}）\n" +
               $"玩家: {string.Join(", ", players)}\n" +
               $"出现的卡组码: {string.Join(" ", codes)}\n" +
               $"全部 local_subactions=1: {Actions.All(a => a.LocalSubactions)}";
    }
}
