using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

/// <summary>
/// ★★ **防回归守卫**：派发表静态缺口的**冻结基线**。
///
/// 计算在 <see cref="KLink.Bot.Effects.Blueprint.DispatchGap"/>（库里，
/// 因为 `ServerBridgeTest` 的审计 ⑥b 也要用同一份数字）。
/// 这里只放「冻结值 + 比较逻辑」—— 它是**测试资产**，所以留在测试工程里。
///
/// ## 为什么这个 bug 类需要守卫
///
/// 派发表认不出的调用，`KismetVm.ExecuteCall` 会**什么都不写回 out 槽**、
/// 只记一笔 `GameState.UnimplementedCalls`（`KismetVm.cs:606-612`）。于是：
/// <list type="bullet">
/// <item>带副作用的调用（生成 / 弃牌 / 摧毁 / 改数值）**整段效果静默不发生**；</item>
/// <item>带返回值的调用 out 槽保持 null ⇒ 蓝图侧读成**假 / 0**，判据方向直接反掉。</item>
/// </list>
/// 而动作流本身仍然"应用成功" ⇒ **回放对拍测不到**，只是状态悄悄漂开。
/// 2026-10-02 量化：IR 里被调用的函数 **760** 种、派发表 **195** 键；
/// 扣掉 locals 兜底的 63 种之后，真缺口 **565 种**（补完第一批 25 个键之后是 540）。
///
/// ## 守卫怎么"只降不升"
///
/// 断言缺口集合的**指纹**与 <see cref="BaselineFingerprint"/> 逐位相等：
/// <list type="bullet">
/// <item>**多**一个缺口 ⇒ 失败（新 IR 带来的新调用名没实现 / 已修好的键被弄丢）；</item>
/// <item>**少**一个缺口 ⇒ 也失败，要求你把基线一起更新 —— 否则
///       「修一个 + 坏一个」会互相抵消，以后再没人测得出来；</item>
/// <item>集合变了但**总数没变** ⇒ 同样失败（这正是只比总数会漏掉的那种）。</item>
/// </list>
/// 更新方式：`dotnet run --project tools\BotSim -c Release -- dispatch-gap`。
/// </summary>
internal static class DispatchGapBaseline
{
    /// <summary>
    /// 冻结的缺口种类数。
    ///
    /// 历史：
    /// <list type="bullet">
    /// <item>2026-10-02 第一次冻结 = **540 种 / 2752 个真缺口调用点**
    /// （补完第一批 25 个键之后的值；补之前是 565 种）。</item>
    /// <item>2026-10-02 第二次冻结 = **539 种 / 2740 个真缺口调用点**
    /// —— 补上 `MakeCardsFight`（互斗）。这一键的价值不在"少一个名字"，
    /// 而在它**真的会改变棋局**：IR 里 12 个调用点 / 12 张卡，
    /// 缺了它就是"本该战死的单位没死"（玩家报的「虚空单位」根因）。</item>
    /// <item>2026-10-02 第三次冻结 = **539 种 / 2791 个真缺口调用点**
    /// —— 目标合法性门：`card-ir.json` 重生成（`LOCAL_FUNCTIONS` 加
    /// `CanPlayFromHand`，438 张卡）**+ 补 `AddKreditsTax`**。
    /// ⚠️ **种类数没变、指纹变了、调用点数 +51**，三件事各有原因，别混为一谈：
    /// <list type="bullet">
    /// <item>`AddKreditsTax` 补进派发表 ⇒ 少 1 种 / −3 个调用点；</item>
    /// <item>438 张卡的 `CanPlayFromHand` 函数体**现在会被 `DispatchGap.Compute`
    ///   扫到**（它扫 `card.Locals.Values`）⇒ 里面调用的 6 个未实现名字
    ///   （`HasCampaignUpgrade` +30 / `GetSupportLineLocationBySide` +19 /
    ///    `GetCardsInFrontlineBySide` +4 / `CheckHasUnitToSpawn` +4 /
    ///    `IsGotcha` +1 / `Get_X_AndMoreAttackCardsOnBoard` +1）**变成可见缺口**；</item>
    /// <item>那 6 个名字里有 5 个**本来就在缺口集合里**（别处也调），所以**种类数没变**，
    ///   只是调用点数涨了。这不是"新引入的缺口"，而是"以前根本没编译那部分蓝图、
    ///   所以看不见"。⇒ 调用点总数**不再可比**（分母变了），以后看种类数与指纹。</item>
    /// </list></item>
    /// </list>
    /// 以后**只能往小改**（种类数）。
    /// </summary>
    /// <remarks>
    /// 2026-10-02：539 → **538**（调用点 2791 → 2788）。原因是补上了
    /// `SuppressMultipleUnits` 这个派发键 —— 它在缺口集合里原本有 **3 个调用点**
    /// （`card_event_white_death` i=382、`card_unit_38th_independent` i=590/i=2808），
    /// 也就是说那两张卡的「抑制」此前是**静默空转**（玩家报的「抑制不生效」）。
    /// 种类数与调用点**都降了**，符合"只降不升"。
    /// 更新方式：`dotnet run --project tools\BotSim -c Release -- dispatch-gap`。
    /// </remarks>
    /// <remarks>
    /// 2026-10-02：538 → **537**（调用点 2788 → **2778**）。原因是补上了
    /// `GetCardsPlayedFromHandLastTurn` 与 `getCardsPlayedFromHandByTurn` 两个派发键 ——
    /// 前者在缺口集合里原本有 **10 个调用点**，全在**卡内私有函数**
    /// `didPlayBritishInfantryLastTurn` 的体里（**5 张卡**：
    /// `card_event_forward_observers` / `card_unit_baltimore_mk_iii` /
    /// `card_unit_defiant_mk_i` / `card_unit_the_polar_bears` /
    /// `card_unit_valentine_mk_ii`，每张调 2 次）。
    /// 也就是说那 5 张卡的「上回合打过英国步兵」分支此前是**静默空转**
    /// —— 审计里 4 局的 `<local-ran:didPlayBritishInfantryLastTurn> ×1` 就是这条的留痕。
    /// 种类数与调用点**都降了**，符合"只降不升"。
    /// （`getCardsPlayedFromHandByTurn` 本来不在缺口集合里：它只被
    /// `GetCardsPlayedFromHandLastTurn` 调，而后者不是卡内私有函数、不在 IR 里。）
    /// </remarks>
    /// 2026-10-03：537 → **536**（调用点 2778 → **2776**）。原因是补上了
    /// `ChangeFrontlineLimiter`，对应 `card_unit_black_prince` 的前线容量规则。
    /// 2026-10-03：536 → **534**（调用点 2776 → **2750**）。原因是补上了
    /// `AddGameplayRestriction`、`RemoveGameplayRestriction` 和
    /// `IsThereGameplayRestriction`，覆盖全局玩法限制的状态查询与修改。
    /// 2026-10-03: 534 → **533** (2714 calls), implementing `MakeCardRetreat`.
    /// <item>2026-10-03：533 → **525**（调用点 2714 → **2528**）。按蓝图重写 Gotcha（反制卡）
    /// 子系统，补上 **5 个**派发键 —— 它们此前**全部**在缺口里，也就是整族效果静默空转：
    /// <list type="bullet">
    /// <item>`GotchaTriggered` −54（IR 里 52 张 gotcha 卡的触发点）</item>
    /// <item>`ShouldGotchaTrigger` −53</item>
    /// <item>`IsGotcha` −16</item>
    /// <item>`SetCardsSeenByCipher` −2（`card_event_cruiser_scouts` / `card_event_stretch_the_line`）</item>
    /// <item>`AddIntelToCard` −3（`card_unit_lublin_r_xiii`）</item>
    /// </list>
    /// 合计 **−8 种 / −128 个调用点**。
    /// 指纹 7A024C984F6E9873 → **5588ABD560840292**。
    /// <para>
    /// ⚠️ **为什么不是 524 种**：`GetHandLocationBySide` **有实现但故意不注册** ——
    /// 注册它会把 IR 里 5 个既有调用点（5 张卡的"这张牌在我手里吗 / 手牌满了吗"门）
    /// 从「out 槽不写 ⇒ null」变成真实手牌位置，实测对局 `854099` **106/118 → 100/118**
    /// （其余 8 局逐位不变）。这是典型的「两个错抵消」（README §7.3）：
    /// 门恒假掩盖了下游另一个偏差。按「任何一局应用率下降都算失败 ⇒ 回退」的硬性要求
    /// 回退注册，并**如实把它留在缺口里**（−5 个调用点不扣）。
    /// 判据与完整 A/B 见 `CardApiDispatch.cs` 里 `["GetHandLocationBySide"]` 上方那段注释。
    /// </para>
    /// </item>
    /// </remarks>
    /// 2026-10-03：525 → **522**（实际重跑：2462 调用点）。补上
    /// `GetCardsInFrontlineBySide`、`FullyHealCard`、`GetCardsPlayedFromHandThisTurn`。
    public const int BaselineCount = 522;

    /// <summary>冻结的缺口集合指纹（<see cref="KLink.Bot.Effects.Blueprint.DispatchGap.Fingerprint"/>）。</summary>
    public const string BaselineFingerprint = "FFEC7E071E9518C0";

    /// <summary>自测用：返回 null = 通过，否则是失败原因。</summary>
    public static string? Check(CardDatabase db)
    {
        SortedDictionary<string, int> gaps = KLink.Bot.Effects.Blueprint.DispatchGap.Compute(db);
        string fp = KLink.Bot.Effects.Blueprint.DispatchGap.Fingerprint(gaps);
        int calls = gaps.Values.Sum();

        if (gaps.Count == BaselineCount && fp == BaselineFingerprint)
        {
            return null;
        }

        var top = string.Join("\n       ", gaps.OrderByDescending(kv => kv.Value).Take(15)
            .Select(kv => $"{kv.Key} ×{kv.Value}"));

        string verdict = gaps.Count > BaselineCount
            ? "⚠️ **缺口变多了** —— 要么是新 IR 带来的新调用名没实现，要么是已经修好的键被弄丢了。"
            : gaps.Count < BaselineCount
                ? "✅ 缺口变少了（修好了一批）—— 这是好事，但必须把基线常量一起更新，"
                  + "否则「修一个 + 坏一个」会互相抵消、以后再也测不出来。"
                : "⚠️ 缺口**总数没变但集合变了** —— 典型的「修一个 + 坏一个」，"
                  + "正是只比总数会漏掉的那种。";

        return $"{verdict}\n"
             + $"       基线：{BaselineCount} 种 / 指纹 {BaselineFingerprint}\n"
             + $"       现在：{gaps.Count} 种 / {calls} 个真缺口调用点 / 指纹 {fp}\n"
             + $"       缺口最大的 15 个：\n       {top}\n"
             + "       ⇒ 跑 `dotnet run --project tools\\BotSim -c Release -- dispatch-gap` "
             + "拿新的两个常量（**只允许按提示更新，不允许调高**）。";
    }
}
