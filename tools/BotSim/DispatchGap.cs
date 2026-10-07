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
    /// 2026-10-05：522 → 521 → **520**（实际重跑：2429 调用点）。补上
    /// `GetAllCardsInFrontline`，返回双方当前前线卡列表。
    /// 2026-10-05：519 → 518 → **517**（实际重跑：2388 调用点）。补上
    /// `getKreditTempBuffAmount`，按来源读取目标卡的改费 buff 偏移。
    /// `RemovePin`，复用钉住关键字的解除、时长清零和事件广播。
    /// 2026-10-05：517 → **516**（实际重跑：2373 调用点）。补上
    /// `SetCountdown`，按蓝图包装写入目标卡的 `countdown_timer` 私有字段并持久化。
    /// 2026-10-05：516 → **514**（实际重跑：2361 调用点）。补上
    /// `GetLeftMostCardInHand` 与 `GetRightMostCardInHand`，返回稳定的双出参手牌边界查询。
    /// 2026-10-05：514 → 513 → **512**（实际重跑：2328 调用点）。补上
    /// `GetIsGoldCard`，按隐式 self 返回卡牌的 `IsGold` 标记；随后补上
    /// `MoveMultipleCardsToTopOfOwnersDeck`，按蓝图数组顺序逐张置顶。
    /// 2026-10-05：512 → **511**（实际重跑：2313 调用点）。补上
    /// `PlayCardDirectlyFromHand`，复用完整出牌触发链并支持指定前线/槽位。
    /// 2026-10-05：510 → **509**（实际重跑：2280 调用点）。补上
    /// `ConvertCard`，覆盖就地换身份、临时状态清理和转换事件广播。
    /// 2026-10-06：509 → **508**（实际重跑：2271 调用点）。补上
    /// `getAttackTempBuffAmount`，按来源读取目标卡的攻击 buff 偏移。
    /// 2026-10-06：508 → **507**（实际重跑：2262 调用点）。补上了
    /// `SetCardSeen`，按蓝图第一个 cardID 标记目标卡的 `cardSeen`。
    /// 2026-10-06：507 → **506**（实际重跑：2255 调用点）。补上了
    /// `IsTopDeckNavy`，按显式 side 检查牌库顶牌的 `subtype.navy` 标签。
    /// 2026-10-06：506 → **505**（实际重跑：2254 调用点）。补上了
    /// `ChangedPinnedTurns`，按蓝图将有效在场单位的 pinnedTurns 增量夹到 0..5。
    /// 2026-10-06：505 → **504**（实际重跑：2250 调用点）。补上了
    /// `WasRightMostCardWhenPlayedFromHand`，读取正版 JSON 位置标记。
    /// 2026-10-06：504 → **503**（实际重跑：2242 调用点）。补上
    /// `ForceCardChangeLocation`，按 cardID 移动并回写 moved/旧位置出参。
    /// 2026-10-06：503 → **502**（实际重跑：2231 调用点）。补上
    /// `SpawnMultipleCardsOnBattlefield`，按数组顺序生成并回写卡 ID。
    /// 2026-10-06：502 → **501**（实际重跑：2224 调用点）。补上
    /// `Get_X_AndMoreAttackCardsOnBoard`，按蓝图过滤本方在场单位并返回卡 ID。
    /// 2026-10-06：501 → **500**（实际重跑：2218 调用点）。补上
    /// `WasLeftMostCardWhenPlayedFromHand`，读取正版 JSON 的左侧手牌位置标记。
    /// 2026-10-06：500 → **499**（实际重跑：2211 调用点）。补上
    /// `JSON_RemoveFromIntArray`，按值移除首个数组元素并回写 found。
    /// 2026-10-06：499 → **497**（实际重跑：2201 调用点）。补上
    /// `AddCustomGameplayTag` 与 `RemoveCustomGameplayTag`，按目标 cardID
    /// 维护动态 GameplayTag，查询与卡面静态标签合并。
    /// 2026-10-06：497 → **496**（实际重跑：2195 调用点）。补上
    /// `TriggerDestruction`，接通主动摧毁效果触发与事件 24 派发。
    /// 2026-10-07：496 → **494**（实际重跑：2188 调用点）。补上
    /// `AddToTriggerQueue` 与 `ResolveTriggerQueue`，接通 Develop 延迟触发链。
    /// 2026-10-07：493 → **491**（实际重跑：2176 调用点）。补上
    /// `TakeControlOfEnemyUnit` 与 `ReleaseControlOfEnemyUnit`，接通控制权转移链。
    /// 2026-10-07：491 → **490**（实际重跑：2173 调用点）。补上
    /// `SpawnNextToCard`，复用带相邻槽位插入、金卡继承和老兵/攻击复制的生成链。
    /// <para>
    /// 2026-10-07：488 → <b>485</b>（2166 → 2146 个调用点）。补上 Blueprint
    /// Set 原语族：Set_Add / Set_Clear / Set_ToArray（并一并实现
    /// Set_Contains / Set_Length / Set_Remove / Set_RemoveItems 的表达式路径）。
    /// 表现层/战役相关 Set 调用仍按缺口保留，不纳入实现。
    /// </para>
    /// 2026-10-07：485 → **483**（2146 → 2140 个调用点）。补上
    /// `GetHQ_DamagedAmountThisTurnBySide` 与
    /// `GetOperationKreditsSpentThisTurn`，接通本回合 HQ 伤害和行动费
    /// 统计查询；两个计数器按蓝图在每个开始回合重置。
    /// 2026-10-07：483 → **482**（2140 → 2138 个调用点）。补上
    /// `getFrontlineLimit`，按蓝图返回 `IsFrontlineLimited` 布尔标志。
    /// 2026-10-07：481 → **480**（2134 → 2132 个调用点）。补上
    /// `AddDefenseToMultipleCards` 的批量防御结算。
    /// 2026-10-07：480 → **479**（2132 → 2130 个调用点）。补上
    /// `RemoveAlpine`，按来源移除动态 Alpine，并保留卡面自带 Alpine。
    /// 2026-10-07：479 → **478**（2130 → 2129 个调用点）。补上
    /// `RemoveSalvage`，清除目标卡的收缴关键字；随后 478 → **477**
    ///（2129 → 2128 个调用点），补上 `RemoveBond` 的羁绊移除覆盖标记；
    /// 477 → **476**（2128 → 2126 个调用点），补上 `StealCardFromBoardToDeck`。
    /// 2026-10-07：476 → **475**（2126 → 2124 个调用点）。补上
    /// `AdjustCardPositionInDeck`，按蓝图移除/按顶部位置插回牌库，并派发
    /// `OnAfterDeckChanged`。
    /// 2026-10-07：475 → **473**（2124 → 2120 个调用点）。补上
    /// `ApplyGameplaySideEffect` 与 `RemoveGameplaySideEffect`，按阵营、标签和
    /// 来源卡维护 GameplayEffect，并让 `sideeffect.blockgotcha` 接入 Gotcha 判定。
    /// 2026-10-07：473 → **472**（2120 → 2118 个调用点）。补上
    /// `GetReducedDamage`，按 Seagull 蓝图将本方 HQ 本回合减伤额度限制为 4 点。
    /// 2026-10-07：472 → **471**（2118 → 2116 个调用点）。补上
    /// `GiveCredits` 并在移动/攻击支付行动费时派发两个蓝图事件。
    /// 2026-10-07：465 → **464**（2106 → 2104 个调用点）。补上
    /// `SalvageMultipleUnits`，接通两张日本事件卡的打捞复制链。
    /// 2026-10-07：464 → **463**（2104 → 2103 个调用点）。补上
    /// `ExecuteOnDeploymentTriggered` 派发适配并覆盖 triggerMultiple 出参。
    /// 2026-10-07：463 → **462**（2103 → 2102 个调用点）。补上
    /// `CountFriendlyGuardUnits`，接通 SDF 的 Guard 数量光环。
    /// 2026-10-07：462 → **461**（2102 → 2101 个调用点）。补上
    /// `GetDefenseBuffFromAdjacentUnits` 与事件 38 的最终伤害修正链，接通
    /// 48th Armored Infantry 的相邻单位减伤。
    /// 2026-10-07：461 → **459**（2101 → 2098 个调用点）。按卡库里显式的
    /// `_vet` 卡定义实现 `getHasVeteranUpgrade` / `getStaticVeteranUpgrade`，
    /// 接通 Battle Valor、266th Guards Rifles 与 37mm M1 AA Gun 的老兵升级查询。
    /// 2026-10-08：459 → **458**（2098 → 2097 个调用点）。补上
    /// `MoveCardInHandToLeftMost`，按蓝图将同方手牌目标移动到位置 0 并重排索引。
    /// 2026-10-08：458 → **457**（2097 → 2096 个调用点）。补上
    /// `Array_Resize`，按 UE 原地裁剪/扩容蓝图数组。
    /// 2026-10-08：457 → **456**（2096 → 2094 个调用点）。接通
    /// `card_unit_sturmovik_pol` 带空格的 `Apply The Buff` 私有函数。
    /// </remarks>
    public const int BaselineCount = 456;

    /// <summary>冻结的缺口集合指纹（<see cref="KLink.Bot.Effects.Blueprint.DispatchGap.Fingerprint"/>）。</summary>
    public const string BaselineFingerprint = "F0085A6DE214B8DA";

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
