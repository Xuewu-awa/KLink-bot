using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;

/// <summary>
/// 效果内核的回归自测 —— **直接用真实卡定义 + 真实蓝图 IR 跑，断言可观测的状态变化**。
///
/// 为什么需要它：回放对拍只能告诉你「哪一步对不上」，但常常分不清是
/// 「效果没触发」「原语接错参数」还是「前置条件不满足」。
/// 这里对单个效果做最小可复现的断言，坏掉时能立刻定位。
/// </summary>
internal static class SelfTest
{
    private sealed record Case(string Name, Func<CardDatabase, string?> Run);

    private static readonly List<Case> Cases = new()
    {
        new("10.5cm lefh 战吼：对敌方 HQ 造成 2 点伤害", LehfDeployment),
        new("GetOppositeSide 的零入参语义", OppositeSideSemantics),
        new("GetOpponentSide 按当前对局方返回对手阵营", OpponentSideSemantics),
        new("GetLocationCardBySide 能取到指定阵营的 HQ", LocationCardLookup),
        new("DamageCard 能打掉 HQ 的防御", DamageHqDirectly),
        new("本回合 HQ 伤害与行动费计数按蓝图查询并在回合开始清零", TurnScopedGameplayCounters),
        new("Seagull：GetReducedDamage 按本方 HQ 本回合剩余减伤额度截断", GetReducedDamage),
        new("Landwehr：操作扣费事件按蓝图返还 Veteran 的实际行动费", LandwehrOperationCredits),
        new("Landwehr：第三次操作后由 IncOpCountAndCheckVeteran 晋升 Veteran",
            LandwehrOperationCountPromotion),
        new("2nd Michigan：操作费达到 3 时返还 2 kredit", SecondMichiganGiveTwoKredits),

        // ---- GetPlayFromHandDamage（2026-09-27）----
        // 它**不是**引擎的通用函数，而是每张卡蓝图各自实现的普通函数
        // （编译成独立 export，不在 ubergraph 里）。英联邦的判据是
        // `SelectInt(20, 0, 己方HQ防御 >= 30)` —— 见 out/gpfd/commonwealth.bpasm `.export 4`。
        new("英联邦：己方 HQ 防御 ≥30 时对目标 HQ 造成 20 点，<30 时 0 点", CommonwealthHqDamage),
        // ⚠️ 这条守的是「HQ 数值漂开」这一类里**唯一能单卡复现**的那条（2026-10-02）。
        //    对局 773639 的 #89/#98 两笔 HQ 差（客户端 −2 / −4，我们各 −1）就是它。
        new("流星：攻击后移除自己，牌库副本攻/防必须翻倍（1/1 → 2/2 → 4/4）", MeteorDoublesDeckCopy),
        // ★ 这条才是 773639「HQ 追踪漂开」的**根因断言**（修复前必红）。
        new("回放发号：连续同侧 StartOfTurn（中间无 EndOfTurn）只能算一个客户端回合",
            ReplayClientTurnDedup),
        new("回放动态卡别名：未知动作 ID 复用唯一已生成卡且后续攻击仍命中",
            ReplayGeneratedCardAlias),
        // ★ 发号**口径**本身（2026-10-02）：蓝图 `GenerateNextCardID` 没有 side、
        //   计数器全局、每回合归零。历史实现按 side 分号段 ⇒ 客户端认不出我们发的号。
        new("发号口径：效果生成卡 = 回合号×1000 + 本回合第几张，计数器全局且双方共用（无 side）",
            GeneratedCardIdRule),
        // ★ 唯一一处与蓝图不同的"避让跳号"必须可观测（它是兜底占位卡的放大器）。
        new("发号避让：目标号已占用时跳号，并且必须写进 UnimplementedCalls",
            CardIdCollisionSkipIsObservable),

        // ---- getTotalAttack / getTotalDefense 的接收者（2026-09-27）----
        // 这两个是 `UBaseCardObject` 的原生成员函数（`BaseCardObject.h:982/985`），
        // 语义是「**接收者那张卡自己的**总防御 / 总攻击」。旧实现把接收者丢了、
        // 返回 `State.Board(side).Sum(...)`（而且还排除 HQ）。
        new("红牛：回合开始攻击力翻倍（读的是**自己**的攻击力，不是己方场面之和）", RedBullDoublesOwnAttack),
        new("爱国热忱：翻倍的是**目标那张卡**的攻防（不是己方场面之和）", PatrioticZealDoublesTarget),

        // ---- Develop 族：GetChooseSpawnCards + 生成（2026-09-27）----
        new("PAMS：候选表 = 英国 + 指令 + 总费<5（读的是卡自己的 GetChooseSpawnCards）", PamsDevelopCandidates),
        new("GetMainNationForSide：从两侧 HQ 读取主阵营并过滤 Pilot Escape 空军候选", MainNationForSide),
        new("PAMS：选中一张后被生成成新卡并塞进牌库（走完 CS 答复的整条链）", PamsDevelopEndToEnd),
        new("Develop：OnHandTargetSelected 之后广播 OnOtherCardDeveloped，并传递 instigatorID", OtherCardDevelopedAfterHandTargetSelected),
        new("触发队列：AddToTriggerQueue 按 FIFO 延迟执行并保留 CurrentTarget", TriggerQueueFifoAndTarget),
        new("回合结束队列：普通→endofturn1→endofturn2、抑制门、临时 buff 清理", EndOfTurnQueueSemantics),
        new("ForceEndTurn：效果完成后只结束一次，并记录来源卡 ID", ForceEndTurnDeferred),
        new("控制权：夺取/释放维护当前归属、原始归属、位置索引与协议子动作",
            UnitOwnershipTransfer),
        // ---- GetDeckByside 的出参形状（2026-10-02，对局 542091 t7 的根因）----
        // 蓝图出参是 `TArray<int> deckCardIDs`（卡 **ID**），内核曾实现成卡**实例**。
        // 这两条直接断言中间状态与最终状态，不依赖随机抽样。
        new("牌库查询：`GetDeckByside` 的元素必须是卡 ID（蓝图 TArray<int> deckCardIDs）",
            GetDeckBySideReturnsCardIds),
        new("IsTopDeckNavy：按显式 side 检查牌库顶牌 subtype.navy，空牌库为 false",
            IsTopDeckNavy),
        new("PAMS：开发出来的那张牌费用必须被设成 0（IR i=348 的 ChangeKreditCost 真的执行）",
            PamsDevelopedCardCostZero),
        new("批量回牌库：按输入顺序逐张置顶，且隔离拥有者牌库",
            MoveMultipleCardsToTopOfOwnersDeck),
        new("调整牌库位置：移除后按 positionFromTop 插回，并派发 OnAfterDeckChanged",
            AdjustCardPositionInDeck),
        new("MoveCardInHandToLeftMost：重排目标手牌并拒绝无效/重复移动",
            MoveCardInHandToLeftMost),
        new("手牌重排：MoveCardInHandToNewIndex 后广播 OnOtherCardMovedToLocationInHand，并覆盖手牌接收者",
            OtherCardMovedToLocationInHand),
        new("PlayCardDirectlyFromHand：免费出牌、指定前线/槽位、跨行动方且回写 qqq",
            PlayCardDirectlyFromHand),
        new("MoveUnitFromSupportToFrontLine：免费效果位移、忽略普通移动限制并正确拒绝非法目标",
            MoveUnitFromSupportToFrontLine),
        new("ForceCardChangeLocation：按 cardID 移动并回写 moved/旧位置，cantMove 时拒绝",
            ForceCardChangeLocation),
        new("SpawnMultipleCardsOnBattlefield：按数组顺序生成、继承金卡/关键字并受半场容量限制",
            SpawnMultipleCardsOnBattlefield),
        new("SpawnNextToCard：沿生成者战区插入到相邻位置，并支持金卡、老兵和攻击力复制",
            SpawnNextToCard),
        new("ResetUnitOperations：只恢复棋盘单位的移动/攻击额度，Fury 恢复两次攻击",
            ResetUnitOperations),
        new("GiveRandomCombatKeyword：双出参、已有关键字排除与 Smokescreen 特殊过滤",
            GiveRandomCombatKeyword),
        new("Blueprint Set 原语：Add 去重、Contains/Length、ToArray 与 Clear",
            BlueprintSetPrimitives),
        new("Blueprint Map 原语：Big Three 按友军阵营数量加攻防",
            BigThreeCountsFriendlyFactions),
        new("Blueprint Array_Resize：原地裁剪/扩容并钳制负长度",
            BlueprintArrayResize),
        new("Blueprint 数学边界：短名 Max/Min/Clamp 按整数节点求值",
            BlueprintNumericBounds),
        new("Sturmovik Polish：带空格 Apply The Buff 叠加幸存单位攻防",
            SturmovikPolishBuff),
        new("L4 Grasshopper：带空格 Remove the Buff 撤销 Sherman 光环",
            L4GrasshopperRemoveBuff),
        new("Get_X_AndMoreAttackCardsOnBoard：按阵营、单位、存活、防御和攻击阈值返回卡 ID",
            GetXAndMoreAttackCardsOnBoard),
        new("Precision Bombing：GetHighestBomberAttack 取本方轰炸机实时最高攻击",
            GetHighestBomberAttack),
        new("LoseKreditSlot：只降当前槽位，下一回合按当前槽位自然增长",
            LoseKreditSlotRefillsFromCurrentSlot),
        new("ConvertCard：保留卡位与 ID、替换身份并清理临时状态/派发转换事件",
            ConvertCard),

        // ---- 候选池口径（2026-10-02）----
        // `GetAllActiveStaticCards` 原来**直接返回整个卡库**（2021 张），
        // 少了「卡集」与「预备卡」两层过滤 ⇒ 随机候选表偏大 ⇒
        // `GetRandomCard` 的下标（`RandomIntegerInRangeFromStream(0, Count-1)`，
        // 见 `CardApi.cs:2255`）与客户端算不到同一张卡上。
        // 下面四条直接断言**池子的张数与成员**，以及"同一流下标 → 同一张卡"。
        new("候选池口径：Special/OnlySpawnable/Placeholder/Expansion1/Wildcards 不得进池",
            StaticPoolCardSetFilter),
        new("候选池口径：预备卡默认不进池，includeReserved=true 时必须在池里",
            StaticPoolReservedFilter),
        new("CheckHasUnitToSpawn：四张苏联随机生成事件与其候选池条件一致",
            CheckHasUnitToSpawn),
        new("候选池口径：atlantic_convoy 的『美国费≤3单位』候选池 = 53 张，成员与顺序正确",
            AtlanticConvoyCandidatePool),
        new("★ 同一流下标抽到同一张卡：match_id=508065 复刻到 t9，两次抽签 = 507th_pir / fifth_ohio",
            PoolDrawSameStreamIndex),

        // ---- ① 三个原语 + 4 张光环（2026-09-27）----
        // 这四条守的是「光环类效果」整条链：触发点接上了吗 / 原语幂等吗 / 撤销还原得回去吗。
        // BoardCompare 那 6 局里这 4 张卡都没被打出过，所以回放对拍**测不到**它们，
        // 必须有这组最小断言兜底。
        new("85 先驱连：手牌指令 -1 费、第一张指令打出后还原、重复施加不叠加", PioneerCompanyAura),
        new("第二张指令判定：按传入阵营精确计数，并让 Wave After Wave 排除天气牌",
            SecondOrderThisTurn),
        new("SDF：CountFriendlyGuardUnits 只计本方在场 Guard，并据此更新友军攻击力", SdfCountsFriendlyGuards),
        new("通用费用下限：普通减费可将单位、指令和反制卡降到 0", KreditCostFloorIsZero),
        new("大红一师：手牌全部变 4 费、抽牌补 buff、离场还原", BigRedOneAura),
        new("第 214 阿穆尔：己方 T-34 +1 重甲 / 行动费 -1、离场还原", AmurTankAura),
        new("敢死队（committed crew）：Spitfire 变 0 费、部署 +3+3", CommittedCrewAura),

        // ---- `ChangeAttack` / `ChangeDefense` 的 `changeType = 4`（2026-10-02）----
        // 蓝图 `ChangeAttack` :6591 `localChangeType == 4 → L_096F` 是**撤销**该来源的攻 buff：
        // :6789「amountRemoved == 0 ⇒ 什么都不做」、:6805「实参 amount 被 amountRemoved 覆盖」。
        // 内核旧实现只处理 ct=2，ct=4 落进默认分支 ⇒「撤销」被当成「再加一次」。
        // 卡池里有 6 个 amount≠0 的 ct=4 调用点（su_100 / ki_42_ii_ko / type_97 ×2 /
        // kyushu_j7w3 / type_92_105mm），另有 42 个 amount=0 的（旧实现下全是空转）。
        // ⚠️ 这 5 张卡**没有**出现在 BoardCompare 那 6 局回放里，所以回放对拍给不出信号 ——
        //    这条自测是唯一能守住它的东西。
        new("★ ChangeAttack 的 changeType=4 是**撤销该来源的攻击 buff**（给→撤必须回到原值）",
            ChangeAttackTempBuffRemove),
        // `ChangeDefense` 的 ct=4 在蓝图里是 :7906 `L_0E96` 的**非法值分支**（只 log + return），
        // 与 `ChangeAttack` 的 L_096F **语义不一致** ⇒ 补的是 no-op，不是撤销。
        // 当前卡池 ct=4 有 0 个调用点，属行为中性的预防性对齐。
        new("★ ChangeDefense 的 changeType=4 是蓝图 :7906 的非法值分支（防御一点不动）",
            ChangeDefenseChangeType4IsRejected),
        new("ChangeDefense：旁观卡可增加防御或中止 HQ 获得防御",
            ChangeDefenseBeforeOtherCardGainDefense),
        new("Type 89：HQ 获得防御时改为承受等量效果伤害",
            Type89ConvertsHqDefenseGainToDamage),

        // ---- 三个规则 bug 的回归断言（2026-09-27）----
        new("3 掷弹兵：只有**德国**单位操作才 +1+1（别的阵营不算）", PanzergrenadierFactionGate),
        new("Attack 必须拒绝已经进弃牌堆的目标", AttackRejectsDeadTarget),
        new("T30 OnOtherCardAttackSwitchTarget：Cold Trap 把攻击改到 SISSI", OtherCardAttackSwitchesTarget),
        new("T31 OnOtherCardAttacks：Merchant Navy 能中止敌方攻击并撤回攻击者", OtherCardAttacksStopsAttack),
        new("攻击被中止后仍派发攻击者 OnAttackStopped（Meteor 清理并生成副本）",
            AttackStoppedTriggersSelfEvent),

        // ---- §①.9 三条基本规则（蓝图定案，2026-09-27）----
        // 这四条守的是「棋盘模型」本身。回放对拍只有 6 局、而且 `MoveUnit` 几乎跑不到
        // （真实 ML 大多因为别的原因被拒），所以必须有最小断言兜底。
        new("部署落点是**自己半场**；半场 5 格（HQ 占 1）⇒ 最多 4 个单位", HalfBoardDeploymentAndCapacity),
        new("召唤失调：部署当回合不能攻击/移动，**Blitz 例外**", DeploymentSickness),

        // ---- 奋战（Fury）的第二次攻击（2026-10-02，对局 389594 ⑤b 的根因）----
        // 规则表 `KARDS基础规则参考.md:107`：「**奋战**：一回合中可以**攻击两次**。」
        // 蓝图把额度放在 `attackLeft`（回合开始 = `getHasFury() ? 2 : 1`，每次攻击 -1），
        // `CanAttack` 的门是 `HasAttackLeft()`。内核旧实现用 `!HasAttackedThisTurn`
        // 这个布尔当门 ⇒ 奋战的第二次攻击恒被拒。
        // 实测：`389594` `#85/#86 t19 AC` 人类 `card_unit_queens_own`（Fury）
        // 同一回合打了两次右 HQ（动作流 19→12→5），内核只应用了第一条。
        new("奋战（Fury）：一回合能攻击**两次**，第三次被拒；无奋战的第二次被拒",
            FuryAllowsSecondAttack),

        // ---- `SpawnCardInDeckBySide` 的出参必须是数组（2026-10-02，对局 389594 ④ 的根因）----
        // `card_unit_meteor` 的 `OnAfterAttack` 是「移除自己 → 生成一张同名副本 →
        // **循环**把副本攻/防设成原值×2」。循环靠 `Array_Length/Array_Get(spawnedCardIDs)`
        // 驱动，而出参旧实现是单张卡 ⇒ 长度 0 ⇒ 循环被跳过 ⇒ 副本一直是 1/1。
        new("METEOR：攻击后生成的副本攻/防必须是原值×2（`SpawnCardInDeckBySide` 出参是数组）",
            MeteorCopyDoublesStats),
        new("前线互斥：对面占着推不进去；我方满了也推不进去", FrontlineMutualExclusion),
        new("卡面自带关键字已加载（Blitz / Guard）", InnateKeywordsLoaded),

        // ---- 前线归属残留（2026-10-01，用户实测 replay-214436「AI 空过率极高」）----
        // 根因：`FrontlineOwner` 原先**只在 `MoveUnit` 里**被重算；`Destroy` 把前线最后
        // 一个单位搬进弃牌堆时不重算 ⇒ 归属永久残留在死者那一方。而前线是互斥的：
        // 推进被 `MoveUnit` 的 `<frontline-blocked-by-opponent>` 拒、前线已空没有目标可打、
        // 半场步兵 `Range=1` 又够不着对面半场 ⇒ 三个方向全堵死，只能一路空过。
        // 修法是把重算收口到**换区钩子**（`FireLocationMoved`，出处蓝图
        // `CardLocationMoved` si=1190 `UpdateFrontlineIfNeeded`）。这三条守的就是那个钩子。
        new("前线归属：前线最后一个单位死亡后 `FrontlineOwner` 必须回到 NotAvailable",
            FrontlineOwnerResetOnLastUnitDeath),
        new("前线归属：残留会让**推进被互斥门误拒** —— 死者对面必须能重新推进",
            FrontlineRetakeableAfterOwnerDied),
        new("前线归属：归属真的变化时 `OnFrontlineOwnershipChange` 派发到订阅卡",
            FrontlineOwnershipTriggerDispatched),
        new("前线归属：手牌中的 Pz. BEFEHLSWAGEN 35t 响应夺取前线并自动上前线",
            FrontlineOwnershipTriggerReachesHand),

        // ---- 跨前线射程（蓝图定案，cardsCheckFunctions::CanAttack i=90-99）----
        // 判据只有一条：`attacker.location != 7 && defender.location != 7 && attacker.range < 2`
        // ⇒ 拒绝，failReason="not_enough_range"。所以**半场的步兵（range=1）
        // 不能打对方 HQ / 对方半场单位**，而炮兵/战斗机/轰炸机（range=2）能。
        // 这五条同时守「该拒的拒」和「该放的放」，避免把检查放宽或收窄。
        new("跨前线：半场步兵(range=1) 不能打对方 HQ，也不能打对方半场单位", CrossFrontlineInfantryBlocked),
        new("跨前线：半场炮兵(range=2) 能跨前线打对方 HQ / 对方半场单位", CrossFrontlineArtilleryAllowed),
        new("跨前线：半场战斗机/轰炸机(range=2) 能跨前线打", CrossFrontlineFighterBomberAllowed),
        new("跨前线：前线单位（任一方在前线）任何射程都够得着", FrontlineAlwaysInRange),
        new("跨前线：CanReachAcrossFrontline 的真值表（距离 2 需要 range≥2）", CrossFrontlineTruthTable),
        new("攻击候选：敌方前线存在时仍按射程保留后方合法目标", FrontlineDoesNotBlockRangedTargets),

        // ---- P0 第 1 族：事件层派发（2026-09-27）----
        // 这四条守的是「事件真的派发到订阅它的卡上了吗」—— 光有 FireTrigger 字面量
        // 不算数：`CardApi.FireTrigger` 的快照/广播/兜底三段逻辑任何一段写错，
        // 事件就会**静默**丢掉（订阅卡的程序永远不跑，回放里看不出来）。
        // 每张探针卡都是 IR 里**真有这个程序**的卡（清单 out/audit/p0-event-cards.json）。
        new("事件层：'自己'那一族能派发到主体（抽牌/生成/重置/压制/老兵/换区/离场/修复/回合开始）",
            EventLayerSelfEvents),
        new("事件层：'别的卡'那一族能广播到旁观的订阅者", EventLayerOtherEvents),
        new("事件层：OnOtherCardReset 传递 cardReset/resetCardID（真实 Coastwatchers 订阅）",
            ResetEventCarriesPayload),
        new("事件层：OnOtherCardDealDamage 传递来源/目标/伤害载荷（真实 15th RECCE 订阅）",
            DealDamageEventCarriesPayload),
        new("卡内伤害自定义事件入口：220th / Kyushu / SU-100 真实执行",
            CustomDamageEventEntrypoints),
        new("事件 38：48th Armored Infantry 按相邻单位数减少最终伤害",
            DealDamageAfterCalcAdjacentDefense),
        new("Forecast：天气卡判定与三类天气候选池按蓝图标签筛选",
            ForecastCardPrimitives),
        new("天气牌：每个己方回合最多打出一张，回合开始重置且不影响普通指令",
            WeatherCardPerTurn),
        new("Forecast：同步广播 OnOtherCardForecasted，并触发 H8K 抽牌与 2nd Pioneers 增益",
            ForecastBroadcastGameplay),
        new("511th Regiment：老兵步兵 1 防御时获得 +2 攻击，条件失效后撤回",
            FiveEleventhRegimentVeteranBuff),
        new("Veteran：按显式 `_vet` 卡库变体查询升级并返回静态老兵模板",
            VeteranUpgradeQueries),
        new("事件层：OnOtherCardCreatedAlterCard 传递 cardPlayed/method（真实 67th BARANOVICHI 订阅）",
            CreatedAlterEventCarriesPayload),
        new("事件层：OnOtherCardLocationMoved 传递敌方推进主体并触发 35th Infantry Regiment 光环",
            LocationMovedEventCarriesPayload),
        new("事件层：OnOtherCardDiscarded 传递弃牌主体并触发 NAKAJIMA B5N 伤害",
            DiscardedEventCarriesPayload),
        new("弃牌事件：手牌的成功弃牌效果生效，压制只跳过弃牌卡自身效果",
            SuccessfulHandDiscardEffects),
        new("事件层：OnOtherCardLoseSmokescreen 传递目标并触发 HIROSAKI REGIMENT 光环",
            LoseSmokescreenEventCarriesPayload),
        new("事件层：OnDeckShuffled 传递牌库阵营并触发 110e REGIMENT 光环",
            DeckShuffledEventCarriesPayload),
        new("事件层：OnOtherCardBlitzChanged 传递目标并触发 PANZER III L 光环",
            BlitzChangedEventCarriesPayload),
        new("原语：getCardsBuffedByThisCard 返回来源卡实际增益目标 ID",
            CardsBuffedByThisCardReturnsIds),
        new("原语：FullyHealCard 返回实际治疗量、满血幂等且拒绝死亡目标",
            FullyHealCardPrimitive),
        new("Okayama Regiment：阻止单位完全修复，但不阻止 HQ 修复",
            OkayamaBlocksUnitFullRepair),
        new("原语：GetAllCardsInFrontline 返回双方前线卡且排除半场/HQ",
            GetAllCardsInFrontlinePrimitive),
        new("事件层：钉住/解除钉住广播真实目标参数，并触发订阅卡效果", PinnedEventLayer),
        new("事件层：MakeCardRetreat 广播真实撤退目标参数", RetreatEventLayer),
        new("事件层：推进广播 cardMoved/ForceMove/moveCost（真实 Blue Legion 订阅）",
            MoveToFrontlineEventPayload),
        new("事件层：受伤广播 fromCard/toCard/fromAttack/damage（真实 Panzer III 订阅）",
            ReceiveDamageEventPayload),
        new("事件层：战斗存活事件（OnSurvivedCombat / OnOtherCardSurvivedCombat），打 HQ 不发", EventLayerSurvivedCombat),
        new("事件层：压制会挡住广播（OnOtherCardBecomingVeteran / OnOtherCardDestroyed），但不挡自己那一路",
            EventLayerSuppressionGate),

        // ---- P0 第 2 族：同形「接收者/参数位」bug（2026-09-27）----
        // 这一族的共同形状：**handler 只认 recv，不认隐式 self / 只读半个签名**。
        // 每条都直接在派发表上按蓝图实参形状调用（`CardApi.InvokeByName`），
        // 不复现整张卡的蓝图 —— 因为要守的正是"派发表这一层"。
        new("同形bug：HasCustomAbility/FromCard 读能力名 + 接收者（旧写法 a[0] 是字符串 ⇒ 恒 false）",
            CustomAbilityArgs),
        new("同形bug：IsVeteran/IsDamaged/getTotalHeavyArmor/getHasGameplayTag 认隐式 self",
            ImplicitSelfReceivers),
        new("同形bug：GiveKreditsBySide 的数额在 a[1]（旧写法把 side 当数额）", GiveKreditsAmountArg),
        new("同形bug：GetCardsOnBoardBySide 的 unitsOnly / GetAllCardsOnBoard 含 HQ", BoardQueryOptionalArgs),
        new("同形bug：SpawnCardOnBattlefield 的 Frontline 决定落点（半场 vs 前线）", SpawnFrontlineArg),
        new("同形bug：IsSameSideUnit 的形状是 `Context{卡}.IsSameSideUnit(side)`（旧写法按 `(卡,卡)` 读 ⇒ 19/19 恒 false）",
            SameSideUnitShape),
        new("同形bug：MakeVeteran 的目标在 a[0]（旧写法只认接收者 ⇒ 45 处里 3 处把施法者自己变成老兵）",
            MakeVeteranTargetArg),
        new("Sniped：触发后只禁用同阵营手牌中的其他 Sniped",
            DeactivateOtherSniped),
        new("National Fire Service：触发后只禁用同阵营手牌中的其他 NFS",
            DeactivateOtherNfs),

        // ---- P0 第 4 族：三条「实现了但语义错」（2026-09-27）----
        new("掩护：邻卡有 Guard ⇒ 该卡不可打；掩护卡自己可打；孤立单位可打；HQ 只在被邻卡掩护时不可打",
            GuardIsNeighbourCover),
        new("掩护：轰炸机 / 炮兵跳过掩护判定（CanAttack si=2492-2626）", GuardSkippedByBomberArtillery),
        new("抑制：被抑制的单位**仍然能攻击、能移动**（中文「抑制」= `Suppress` ≠ 「压制」= `Pin`；" +
            "蓝图正面证据：CanAttack/CanCardDoAnything 里 isSuppressed 出现 0 次）", SuppressedCanStillAct),
        new("抑制：**永不解除**（玩家确认 + 蓝图 `isSuppressed` 全库无写 False 处）；" +
            "反向断言压制（Pinned）**仍然**到期", SuppressionNeverExpires),
        new("抑制：`SuppressMultipleUnits` 必须逐张抑制（派发键原先**缺失** ⇒ `white_death` 静默空转）",
            SuppressMultipleUnitsDispatch),
        new("抑制：**失去所有关键词与所有增益**（Guard/Blitz/HeavyArmor/Salvage/… + 攻防回落 + 老兵变回普通），" +
            "且**永不自动还原**（还原只能手工直调 `RestoreAfterSuppression`）；Pinned 不摘",
            SuppressStripsEverythingAndRestores),
        new("抑制：三个触发点的先后必须是 **自己 OnSuppressed → T58 OnOtherCardSuppressed → T11 OnAfterOtherCardSuppressed**" +
            "（蓝图 L_0314→L_174B→L_0322→L_15A5；旧实现三个全错位）", SuppressTriggerOrderMatchesBlueprint),
        // ---- 行动限制（2026-10-02，服务器实测暴露）----
        new("行动限制：**非坦克**移动后不能再攻击（规则表只有坦克能移动+攻击）", NonTankCannotMoveThenAttack),
        new("行动限制：**坦克**可以移动后攻击（规则表明确写的例外）", TankCanMoveThenAttack),
        new("行动限制：非坦克攻击后不能再移动（与上面对称）", NonTankCannotAttackThenMove),
        new("Alpine 同回合行动能力：CustomName1 授予后非坦克可移动并攻击，撤销后恢复限制",
            AlpineMoveAndAttackAbility),
        new("行动限制：**死亡单位不能移动也不能攻击**（实测 AI 移动了死单位）", DeadUnitCannotMoveOrAttack),
        // ---- 钉住（Pinned）：移动侧缺门（2026-10-02，玩家实测 + 日志 637706）----
        //    中文客户端把 `Pin` 译作「压制」，所以玩家说的"压制"是 `Pinned`、不是 `Suppressed`。
        new("钉住：被钉住的单位**不能移动**（`MoveUnit` 原先缺这道门 —— 日志 637706 t12/t14）",
            PinnedCannotMove),
        new("钉住：被钉住的单位不能攻击（**对照** —— 修复前就应该是绿的）", PinnedCannotAttack),
        new("钉住：带 `canOperateWhilePinned` 的被钉住单位**可以**移动和攻击（蓝图两侧都有这条例外）",
            PinnedWithOperateAbilityCanAct),
        new("14th Panzergrenadier：`_qualifiesForUnpinning` 绑定 InputPin 并在解除钉住时撤销能力",
            PanzergrenadierPinnedOverrideLocalPredicate),
        new("钉住到期：**于单位所有者下个回合结束时**解除（`pinnedTurns` 3/2 递减；不是永久）",
            PinnedExpiresAtOwnerNextTurnEnd),
        new("钉住：`cantBePinned`、非在场卡和 HQ/非单位必须被蓝图守卫拒绝",
            PinUnitHonorsBlueprintGuards),
        new("开发选牌：内核会**留痕**（触发卡 / 候选下标 / 卡码）—— 供发出 `CS` 给客户端", DevelopPickIsRecorded),
        new("开发选牌：**真打开发牌**能走到选牌那一步（诊断哪些卡可达）", DevelopCardsReachable),
        new("AOE 伤害：`forward_observers`（对敌方所有单位 2 点）**必须真的扣血**", AoeDamageApplies),
        new("关键字目标：`monty`（钉住目标**及其相邻**）必须钉**敌方**，不能钉到自己", PinTargetsCorrectUnits),
        new("落点：`desert_dust` 的两个 GARRISON 必须进**支援线**，不能进牌库", DesertDustGarrisonToSupportLine),
        new("落点：`atlantic_convoy` 生成的「支援线那一张」必须在场上、另一张在手牌，两张都不在牌库（508065 #44 的根因）",
            AtlanticConvoyBoardAndHand),
        new("★★ 卡号：生成卡一律走客户端的 `回合号×1000+序号`，**双方一致**、计数器全局（虚空部署的根因）",
            ClientCardIdAllocator),
        new("手牌目标：`gordon_highlanders` 的「选手牌里的指令」必须**真的落实**（0 费 + 回牌库顶）", HandTargetSelectWorks),
        new("CanCardBeBuffed：门对所有位置放行（si=41 极性修正）+ 未揭示隐蔽卡的位置表逐条核对",
            CanCardBeBuffedTruthTable),
        new("RevealCard：清除 Covert、持久化揭示状态并按蓝图派发揭示/入场触发",
            RevealCardSemantics),

        // ---- P0 第 3 族：卡内私有函数（locals 管道，2026-09-27）----
        new("私有函数：IR 里带了卡自己的函数体（ApplyBuff / didPlayBritishInfantryLastTurn …）",
            LocalFunctionBodiesPresent),
        new("私有函数：派发表认不出来时会**执行卡自己的函数体**（不再记 Unimplemented）",
            LocalFunctionActuallyRuns),

        // ---- P1：关键字基础设施（2026-09-30）----
        // 审计 §6 的 P1#27f / #27g：同一个判据在 IR 里有两种形状 ——
        // 成员读（`card.hasDeployment`）和函数调用（`getHasDeployment`）。
        // 两处以前都不全：派发表里**一个 `getHas*` 键都没有**（113 个调用点静默取假），
        // 成员表只覆盖 7 个（漏掉 hasCovert/hasDestruction/hasAlpine/hasMobilize/hasDeployment）。
        new("关键字：getHas* 一族进派发表（旧实现一个键都没有 ⇒ 静默取假）", GetHasDispatch),
        new("关键字：成员读 hasXxx 覆盖 15 个（旧成员表只有 7 个）", KeywordMemberReads),
        new("成员读 cardSeen：已被情报揭示的手牌必须读为 true", CardSeenMemberRead),
        new("Blueprint 运行时状态成员读：攻击/受击/抑制/反制序号", RuntimeStateMemberReads),
        new("Blueprint attackBuff 成员读：返回来源账本中的攻击修正总和", AttackBuffMemberRead),
        new("isSalvaged 成员读与抑制：打捞复制品必须保留 1/1 静态基准", SalvagedMemberAndSuppressionBaseline),
        new("Blueprint 卡字段成员读：type / rarity / salvageFaction", CardDefinitionAndSalvageMemberReads),

        // ---- P1：部署 Deployment（2026-09-30）----
        // 蓝图 CardPlayedFromHand si=3640..6434 —— **一条统一机制**：
        // hasDeployment 门 → 事件14 取消钩子 → 事件23 取 triggerMultiple
        // → 跑 (1 + triggerMultiple) 次卡自己的 OnPlayedFromHand。
        new("部署：hasDeployment 门只影响取消/翻倍，指令与单位都照常跑一次 OnPlayedFromHand",
            DeploymentGate),
        new("部署：事件14 取消钩子（PE-2FT「Deployment effects do not trigger.」）", DeploymentCancelHook),
        new("部署：事件23 翻倍数（B-26「Your non-targeting deployment effects trigger twice.」）",
            DeploymentTriggerMultiple),
        new("部署：ExecuteOnDeploymentTriggered 写回 triggerMultiple 出参",
            ExecuteOnDeploymentTriggeredDispatch),
        new("部署：TriggerDeployment 按目标卡触发非目标部署效果并忽略无 Deployment 目标",
            TriggerDeploymentDispatch),

        // ---- P1：摧毁 Destruction（2026-09-30）----
        // TriggerDestruction si=905 门 → si=1237 OnDestroyed → si=1286 事件24
        // → si=1346 若 TriggerMultiple>0 再整轮派发那么多次。
        // 4 张订阅者的卡面全是「when a Destruction effect triggers」。
        new("摧毁：事件24 派发给订阅者（松本连「when a Destruction effect triggers」），且被 hasDestruction 门挡住",
            DestructionEffectTriggered),
        new("摧毁：事件24 的翻倍数（114 步兵连「friendly Destruction effect … triggers twice」）",
            DestructionTriggerMultiple),
        new("摧毁：TriggerDestruction 只触发效果、不移动目标，并支持 RemoveDestruction",
            TriggerDestructionDispatch),
        new("摧毁状态：按回合记录卡 ID，并维护本回合单位摧毁标记",
            DestroyedCardTurnState),

        // ---- P1：摧毁事件必须带 killer 载荷（2026-10-03）----
        // 派发方 `ExecuteOnCardDestroyedFunction` stmt 50 的第二个出参就是 killer，
        // 而 `MatchEngine.Destroy` 原先**一个载荷都没传** ⇒ 订阅方读到自己。
        new("摧毁：`OnOtherCardDestroyed` 必须把 **killer**（击杀者）传给订阅卡（142 步兵连「摧毁敌方单位时 HQ +2 防」）",
            DestroyEventCarriesKiller),
        new("摧毁：事件 24 与收缴处理完成后，再派发主体 `OnAfterDestroyed`",
            AfterDestroyedDispatch),

        // ---- P1：重甲 HeavyArmor（2026-09-30；2026-10-02 修正为**只管战斗伤害**）----
        // CalculateDamageDealt g.cs:5207-5225：
        //   damage = Max(damage − (ignoreHeavyArmor ? 0 : getTotalHeavyArmor()) − …, 0)
        // 但那个函数**只被攻击链调用**（g.cs:4657/4667 + BP_Logic.g.cs:2538/2558），
        // 效果伤害走裸减的 ApplyDamageToCard（g.cs:1053-1057）⇒ 效果伤害不扣重甲。
        new("★ 重甲：**只对战斗伤害**减伤（效果伤害/指令伤害一点不扣）；战斗伤害下限 0",
            HeavyArmorReduction),

        // ---- P1：烟幕 Smokescreen（2026-09-30）----
        // CanAttack si=3637/3793（不能被打）+ AttackCard si=3511（自己攻击后消失）
        // + CardLocationMoved si=643/735（移到前线消失）
        new("烟幕：不能被攻击 / 自己攻击后消失（被压制也会移除）/ 移到前线消失", SmokescreenRules),
        new("战斗伤害：Shock 取消反击并在攻击后消耗，Ambush 首次被攻击先反击", AmbushAndShockCombat),
        new("战斗伤害：lethal 只把正值战斗伤害变成致命，效果伤害不触发", LethalCombatDamage),

        // ---- P1：伤害修正链（2026-09-30）----
        // `BP_CardFunctions::ExecuteOnDealDamageAddDamage`（46 条语句）——
        // 每一次伤害结算**之前**的唯一修正入口。33 张卡有
        // `OnCardDealDamage_ModifyDamageDealt`、29 张有 `OnOtherCardDealDamageAddDamage`；
        // 这些函数体是独立 export 的函数图，P1 §1 才刚编进 IR，
        // 在此之前内核**没有任何地方按这些名字派发**。
        new("伤害修正：OnCardDealDamage_ModifyDamageDealt 派发（M18 打坦克双倍 / 打非坦克原值 / 被压制不修正 / isRedirected 跳过）",
            DamageModifyDealt),
        new("伤害修正：OnOtherCardDealDamageAddDamage 派发（游骑兵：友方单位的非攻击伤害 +1，攻击伤害/敌方来源不加）",
            DamageAddDamageObservers),

        // ---- P1：山地 Alpine（2026-09-30）----
        // `GiveAlpineBonus`（39 条语句）：进场时 +N/+N，N = 场上其它同阵营 Alpine 数。
        // `changeType=1` 是加法 ⇒ 一次进场只能加一次（数值恰好等于 +N 就是这条断言）。
        new("山地：新部署的 Alpine 单位得到 +N/+N（手牌打出 + 效果生成两条路），且一次部署只加一次",
            AlpineBonusOnEntry),

        // ---- ★ 2026-10-02：派发表缺口第一批（`out/audit/missing-keys-classify2.py`）----
        //
        // 背景：IR 里被调用的函数 **760** 种，派发表只有 **195** 键。
        // 其中 63 种由 `KismetVm` 的 locals 兜底执行（卡自己的函数体），
        // 剩下 **545 种 / 3241 个调用点**是真缺口。
        //
        // 这一组自测守的是本次补的那一批。**每一条都直接断言中间状态**：
        // 先看 `InvokeByName(..., out handled)` 的 `handled`（修复前是 false），
        // 再看它造成的**可观测状态变化**（落点 / 攻防 / 关键字 / 位置）。
        // 不写成"连打 N 局看结果" —— 种子固定时随机抽样可能恰好都过。
        new("★ SpawnCardInFrontline：落点必须是**前线**（不是半场），giveBlitz / makeVeteran 生效",
            SpawnCardInFrontlineLandsOnFrontline),
        new("DiscardCardFromHand：卡对象 / 整数 cardID **两种形状**都要真的弃掉",
            DiscardCardFromHandBothShapes),
        new("getAndDecryptAttack / getAndDecryptDefense：必须返回**真实攻防**，不能恒 0",
            DecryptAttackDefenseRealValues),
        new("IsBomber / IsFighter：轰炸机 / 战斗机判据必须为真（旧实现 out 槽恒 null ⇒ 恒假）",
            BomberFighterPredicates),
        new("GetIsGoldCard：隐式 self 正确返回金卡标记", GoldCardPredicate),
        new("IsPinned / HasBond：关键字授予之后判据必须为真", PinnedBondPredicates),
        new("CustomName1/2 三件套：Add → HasAttribute → Remove 往返（关掉 CardApi.cs:631 的 TODO）",
            CustomNameSuffixRoundTrip),
        new("GetCardsInSupportLineBySide：只回本方半场、unitsOnly 过滤、不含前线",
            SupportLineQuery),
        new("IsLocationFull：半场 5 格（**含 HQ**）判满，前线另算", LocationFullCapacity),
        new("★ BP_CardFunctions::ChangeFrontlineLimiter：Black Prince 将前线容量限制为 2，离场后恢复", ChangeFrontlineLimiter),
        new("★ GameplayRestriction：禁抽牌/加槽/指令/部署/地面攻击/手牌弃牌，并按来源与回合解除", GameplayRestrictions),
        new("★ GameplaySideEffect：按阵营/标签/来源维护 blockgotcha，并真实阻止反制触发", GameplaySideEffects),
        new("撤回：AA Barrage 半场回手、前线退半场；M16 无目标不撤自己", RetreatEndToEnd),
        new("508065：Fifth Ohio 无目标部署不得摧毁自身；显式目标仍执行摧毁", FifthOhioNullableTarget),
        new("快照：累计扣槽、限制来源/时长、伏击标记与钉住时长必须可区分", SnapshotTracksRuleState),
        new("随机追踪：超过 64 项不截断，开关不改变随机结果与消费", RandomTraceIsObservational),
        new("蓝图基础函数：支援线位置不回退阵营；AddUnique 去重并返回原下标", BlueprintArrayAndLocationQueries),
        new("GetLeft/RightMostCardInHand：按手牌位置号返回双出参，空手返回 found=false", HandEdgeQueries),
        new("SetCountdown：按目标 cardID 写入 countdown_timer 并持久化，不误写施动卡", SetCountdown),
        new("DecrementCountdown：只递减存在的目标倒计时，正确回写双出参并持久化", DecrementCountdown),
        new("Pincer：建立 receiver/givers 关系、派发收到事件并完整解除", PincerRelationship),
        new("SetCardSeen：按目标 cardID 标记情报已见，不误写施动卡", SetCardSeen),
        new("ChangedPinnedTurns：有效在场单位的 pinnedTurns 按蓝图增量并夹到 0..5",
            ChangedPinnedTurns),
        new("WasRightMostCardWhenPlayedFromHand：读取正版 JSON 标记并支持显式 cardPlayed",
            WasRightMostCardWhenPlayedFromHand),
        new("WasLeftMostCardWhenPlayedFromHand：读取正版 JSON 标记并支持显式 cardPlayed",
            WasLeftMostCardWhenPlayedFromHand),
        new("GetDeckLocationBySide：按正版枚举映射返回 DeckLeft/DeckRight",
            GetDeckLocationBySide),
        new("GetHandLocationBySide：按正版枚举映射返回 HandLeft/HandRight",
            GetHandLocationBySide),
        new("653657：LoseKreditSlot 降槽而不扣当前费用，238 团恢复双倍伤害", LostSlotEnables238thDamage),
        new("DestroyMultipleCards：数组里卡对象 / 整数 cardID 两种元素形状都要被摧毁",
            DestroyMultipleCardsBothShapes),
        new("AddDefenseToMultipleCards：按 ID 顺序改防御、同步 MaxDefense，降至 0 的单位被摧毁",
            AddDefenseToMultipleCards),
        new("RemoveAlpine：按来源移除动态山地，保留其他来源和卡面自带山地",
            RemoveAlpine),
        new("Mobilize：按来源去重并触发 Gain/Lose 联动（48e Guard、43e 全军修复）",
            MobilizeEvents),
        new("Mobilize：回合开始只给当前方在场单位永久 +1/+1",
            MobilizeStartTurnBonus),
        new("Mobilize：受到实际伤害后移除，零伤害时保留",
            MobilizeLostOnDamage),
        new("RemoveSalvage：按目标卡移除收缴关键字并返回 0", RemoveSalvage),
        new("SalvageMultipleUnits：复制到施动方手牌并保留金卡/来源状态，满手跳过",
            SalvageMultipleUnits),
        new("RemoveBond：按目标 cardID 写入 bond_removed、清掉动态 Bond 且屏蔽静态 Bond", RemoveBond),
        new("StealCardFromBoardToDeck：原卡走离场、复制金卡到指定阵营牌库并洗牌", StealCardFromBoardToDeck),
        new("DiscardCardFromDeck：只对**牌库里的卡**生效，弃完进弃牌堆", DiscardFromDeck),
        new("DiscardRandomCardFromHand：空手不消费随机流，单牌必弃，多牌按引擎随机并广播事件",
            DiscardRandomCardFromHand),
        new("★ CustomName1 接上事件24 的门：`StopDestructionEffect` 必须能压掉摧毁效果（关掉 CardApi.cs:631 的 TODO）",
            StopDestructionEffectGate),

        // ---- ★★ 2026-10-02：`card_event_fog_of_war` 把单位移出战场 ----
        // 对局 `773639` 的 `#45 t9 ML`（审计 ⑤b 首个**人类**失败点）根因用例：
        // 人类在 t7 用雾战把 bot 前线的 1st_airborne 移出战场，而内核没移
        // ⇒ 前线归属仍留在 Right ⇒ 人类 t9 推前线被互斥门拒。
        new("★ 雾战（fog_of_war）：把目标单位移出战场 + 前线归属必须跟着释放（773639 #45 的根因）",
            FogOfWarRemovesTargetFromBattlefield),
        new("SpawnCardInDeckBySide：按 bottom 布尔参数落顶/底，不受 side=2 干扰",
            SpawnCardInDeckHonorsBottom),
        new("SpawnCardInDeckBySide：RandomWithoutShuffle 按随机索引插入并逐张消费随机流",
            SpawnCardInDeckRandomInsertion),
        new("SpawnCardInDeckBySide：shuffle=true 在生成后洗牌并继续消费随机流",
            SpawnCardInDeckShuffle),

        // ---- ★★ 2026-10-02：kredit 槽位增长模型**已定案**，那条用例**不恢复** ----
        // 曾有一条 `KreditSlotsGrowEveryTurn`（断言"每回合双方各 +1"）—— **它的模型是错的**，
        // 已永久撤下，**不要恢复**。定案结论是「槽位 = **自己第几个回合**（+ 卡牌效果的额外槽）」，
        // 也就是 `MatchEngine.StartTurn` 里现成的那段（只给行动方 +1）。
        // 证据（真人玩家的规则描述 + 重算的实际支付花费表 + "花费反推不可靠"的教训）
        // 全在 `MatchEngine.StartTurn` 的那段「已定案」注释里。

        // ---- ★★ 2026-10-02：直接生成到前线也要更新归属 ----
        // `SpawnCardInFrontline`（108 调用点 / 29 张卡）走的是
        // `Create(…, BoardFrontline)` + `Move` 到**同一区** ⇒ 换区钩子不发。
        // 蓝图在 `SpawnCardToBoard` 里是**显式**补 `UpdateFrontlineIfNeeded` 的。
        new("★ 前线归属：**直接生成到前线**（不是推进）也必须更新 `FrontlineOwner`，否则互斥门失效",
            SpawnToFrontlineUpdatesOwner),

        // ---- ★ 2026-10-03：`JSON_Clear` 只删**指定的那一个键**，且要回报它是否存在 ----
        // 蓝图 `BP_CardFunctions.g.cs:24383-24395`：`existed = JsonHasField(card.customJson, variableName)`
        // → **仅当 existed** 才 `JsonRemoveField(…, variableName)` → `found = existed`。
        // 旧实现是 `card.CustomJson.Clear()`（**清空整张表**、忽略键名），
        // 且派发表那条 lambda 返回 null ⇒ `found` 从不写入
        //（VM 只在 `result is not null` 时写 out 槽，见 `KismetVm.cs:669`）。
        // 有 **46 张卡**读 `CallFunc_JSON_Clear_found`。
        new("★ `JSON_Clear` 只删指定键、并回报键是否存在（旧实现清空整表且不写 found）",
            JsonClearRemovesOnlyNamedKey),
        new("★ `JSON_RemoveFromIntArray` 只移除首个匹配值并正确回报 found",
            JsonRemoveFromIntArray),
        new("★ 动态 GameplayTag 按目标 cardID 添加/删除、幂等且不污染 CustomAbility",
            CustomGameplayTagDispatch),

        // ---- ★★ 防回归守卫：派发表静态缺口（2026-10-02）----
        //
        // 这个 bug 类的根源是「IR 调了、表里没有、而且静默失败」——
        // 回放对拍**测不到**它（动作仍然"应用成功"，只是效果没发生、状态悄悄漂开）。
        // 守卫的判据与「为什么用指纹而不是只比总数」见 `DispatchGap` 的类注释。
        // 更新方式：`dotnet run --project tools\BotSim -c Release -- dispatch-gap`。
        new("★★ 派发表静态缺口守卫：缺口集合的指纹必须与冻结基线一致（只降不升）",
            DispatchGapGuard),

        // ---- ★★ 2026-10-02：RNG 换成客户端 `cardsRandomStream` 的逐位复刻 ----
        // 方向依据：`Kards_RNG_report` 的 `Weather.md` §4.2（IDA 反编译）
        // 与 §4.2.1（可复算的测试向量）。旧实现是内核自己的 splitmix64，
        // 与客户端毫无关系 ⇒ 随机/复制类效果抽到的卡必然不同。
        new("★★ UE FRandomStream 复刻：必须逐位命中报告 §4.2.1 的测试向量（LCG 常数 + 高 23 位 + 闭区间）",
            UeRandomStreamMatchesReportVector),
        new("★ UE FRandomStream：`Array_ShuffleFromStream` 是**前向** Fisher-Yates、消耗 n 次（不是 n-1）",
            UeShuffleConsumesNDraws),
        new("★ `RandomIntFromRangeWithStream(0,2)` 必须能出 2（闭区间；旧实现是半开区间 ⇒ 只出 0/1）",
            RandomIntFromRangeIsInclusive),

        // ---- ★★ 2026-10-02：安全网（虚空部署）----
        new("★★ 安全网：内核自己生成、身份未经动作流确认的卡**不许打出去**（虚空部署的防线）",
            UntrustedGeneratedCardIsNotPlayed),

        // ---- ★★ 2026-10-02：`MakeCardsFight`（互斗）—— 玩家报的「虚空单位」根因 ----
        // 背景：派发表里**没有** `"MakeCardsFight"` 这个键 ⇒ `KismetVm.ExecuteCall`
        // 静默什么都不做 ⇒「让两个单位互斗」整段效果不发生 ⇒ 本该战死的单位没死
        // ⇒ 玩家看到「对面出现虚空单位」。
        // 语义出处：`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:25907-26040`。
        new("★★ 互斗 MakeCardsFight：双向同时结算（都活 / 一方死 / **双方都死** / 重甲 / 免疫）",
            MakeCardsFightMutualStrike),
        new("★ 互斗 MakeCardsFight **不是攻击**：不扣油费 / 不置已攻击 / 召唤失调也能斗 / 不发战斗存活事件",
            MakeCardsFightIsNotAnAttack),
        new("★ 互斗 MakeCardsFight 的伤害带 `fromFight=True`（M3 Stuart 打步兵 +4 的判据）",
            MakeCardsFightPassesFromFightFlag),

        // ---- ★★ 目标合法性门（2026-10-02，玩家报告「人机可以随意指定」）----
        //
        // 客户端选目标要过**两道门**（枚举主循环 `_deps/BP_Logic.g.cs:1235-1355`）：
        //   ① 卡自己的 `CanPlayFromHand`（「只能指定空军 / 老兵 / 敌方 / 友方」在这里）
        //   ② 规则库的 `CanSelectAsTarget`（隐蔽 / 敌方指令 / 费用 / 被指方自身）
        // 下面每条都**直接断言中间状态**（逐个目标的判定 + 候选枚举的结果），
        // 不是"连打 N 局看结果" —— 种子固定时那种断言可能恰好都通过。
        new("目标门：只能指定**空军**的牌（aa_barrage）—— 地面被拒、轰炸机/战斗机被接受",
            TargetGateAirOnly),
        new("目标门：只能指定**敌方**空军/步兵的牌（m16_halftrack）—— 友方空军被拒、敌方坦克被拒",
            TargetGateEnemyAirOrInfantry),
        new("目标门：只能指定**老兵**的牌（breakout）—— 非老兵被拒、老兵被接受",
            TargetGateVeteranOnly),
        new("目标门：只能指定**友方单位**的牌（air_corps_ferrying）—— 敌方被拒",
            TargetGateFriendlyOnly),
        new("目标门：只能指定 **HQ** 的牌（the_commonwealth）—— 单位被拒、HQ 被接受"
            + "（`IsLocatedOnBoard` 必须含 HQ）", TargetGateHqOnly),
        new("★★ 目标门：**候选枚举**（LegalPlayTargets）里不含非法目标（直接断言中间状态）",
            TargetGateCandidateEnumeration),
        new("★ 目标门：IR 里必须带卡自己的 `CanPlayFromHand`（`card-ir.json` 重生成守卫）",
            TargetGateIrHasCanPlayFromHand),
        new("目标门：规则库门 `CanSelectAsTarget` —— 不在场上 / kredit 不足 / 不在场上的卡",
            TargetGateLibraryChecks),
        new("PayCardCost：敌方目标税计入普通出牌费用，同阵营不收税，余额不足不改变状态",
            PlayCardEnemyTargetTax),

        // ---- ★★ 攻击路径的目标合法性门（2026-10-02 第三轮）----
        //
        // 蓝图 `CanAttack` 末尾直接调规则库（`cardsCheckFunctions.g.cs:902`），
        // 而内核的攻击路径（`LegalTargets` / `Attack`）上一轮**完全没接**这道门。
        // 真正生效的只有两条：⑥ 额外税（3 张税卡）、⑧ 触发点 2 的否决位（commando）。
        new("★ 攻击门：额外税（3 张税卡）—— 余下 kredit 不够付税时目标被拒，够了放行，同阵营不付税",
            TargetGateAttackTax),
        new("★ 攻击门：触发点 2 的否决位（commando「4 攻以上不能攻击」）—— 攻击路径否决，原因 `unit_cant_attack`",
            TargetGateCommandoVeto),
        new("★★ 攻击门**反向**：总攻 < 4 / 订阅者不在场 / 出牌路径 / 真的打一次 —— 都必须**放行**（防恒拒）",
            TargetGateAttackPathAllows),
        new("★ 攻击门：攻击路径与出牌路径**共用同一道门**（拒绝原因同源 + 被拒无副作用 + 枚举一致）",
            TargetGateSharedByBothPaths),
        new("攻击限制：cantAttack:* 与 cantBeAttackedBy:* 同时约束 HQ/空军/地面目标，且候选枚举一致",
            AttackRestrictionAbilities),

        // ---- 全卡池烟雾测试台（`BotSim smoke-all-cards`，2026-10-02）----
        //
        // 这三条守的是**测试台本身**，不是规则。为什么值得守：
        // D 类（零状态变化）的判读完全依赖「局面非退化」这个前提 ——
        // 局面一塌，所有卡都会「零变化」，报告会看起来"卡全是坏的"。
        // 而确定性那一条守的是「同种子跑两次逐位相同」这个锁步前提。
        new("★ 全卡池烟雾测试台：局面形状符合设计（双方 HQ + 5 兵种 + 手牌 + 牌库 + 满 kredit）",
            SmokeBoardShape),
        new("★ 全卡池烟雾测试台：同种子跑两次逐位相同（3 张样本卡 × 各自入口）",
            SmokeDeterminism),
        new("★ 全卡池烟雾测试台：前 40 张卡不抛异常、不撞步数上限", SmokeNoCrash),

        // ---- ★★ 2026-10-03：`PersistCustomFields` 持久化的是**第 0 参那张卡**，不是接收者 ----
        // 权威签名 `0: cardID`、`1: refreshEffectBar`（`BP_CardFunctions.g.cs:27828-27834`），
        // 蓝图体把这个 cardID 喂给 `GetCardFromID` 再取它的 `customJson`。
        // 而 `recv` 在 IR 里 **489/489 恒为 `cardFunction`**（= `ctx.Self`，见
        // `KismetVm.Frame` 的 `_locals["cardFunction"] = ctx.Self`）⇒
        // 旧实现 `AsCard(r)` **恒持久化施法者自己**。
        // 全卡池扫描：489 个调用点里 **30 个**的 `a[0]` 是**别人卡的 cardID**
        // （`{"var":"cardID","ctx":{"var":"K2Node_Event_targetCard"}}` 这类形状）。
        new("★ `PersistCustomFields` 持久化的是 `a[0]` 那张卡（旧实现恒持久化施法者）",
            PersistCustomFieldsTargetsArgCard),

        // ---- ★★ 2026-10-03：`DrawCardsFromDeckBySide` 的**出参** `cardsIDs` 必须写入 ----
        // 权威签名（`BP_CardFunctions.g.cs:12298-12310`）：
        //   `0: instigatorID`、`1: side`、`2: numCards`、`3: cardSeen`、`4: OpponentDraw`、
        //   `5: cardsIDs*`（**出参**）、`6: drawDelay`。
        // 蓝图体循环里把 `DrawTopCardFromDeck` 的 `drawnCard` 逐个 `Array_Add` 进
        // `drawnCards`，循环后 `cardsIDs = drawnCards`（`g.cs:12386`）并
        // `__out_cardsIDs?.Invoke(…)`（`g.cs:12400`）。
        // 派发表旧实现 `DrawCards(...); return null;` ⇒ VM 不写出参（`KismetVm.cs:669`）
        // ⇒ **9 张真的读它的卡**整段恒空。
        new("★ `DrawCardsFromDeckBySide` 的出参 `cardsIDs` 必须写入抽到的卡 ID（旧实现 `return null`）",
            DrawCardsFromDeckBySideWritesCardsIDs),

        new("★ `DrawSpecificCardFromDeckBySide` 先置顶再走完整抽牌路径（含满手烧牌）",
            DrawSpecificCardUsesDrawPath),

        new("★ `ChangeKreditCost/ChangeOperationCost` 的 changeType=0 进入临时 buff 槽并可撤销",
            TemporaryResourceCostBuffsExpire),
        new("Light Anti-Air Battery：行动费临时 buff 清理后由 reset 事件重新施加，离场后撤销",
            LightAntiAirBatteryOperationCostReset),
        new("行动费变更事件：目标排除、零值不触发、抑制接收者跳过",
            OperationCostChangeEvents),

        // ---- ★★ 2026-10-03：`DamageCard` 的第 3 参是**整数 `damagerCardID`**，不是卡对象 ----
        // 权威签名（`BP_CardFunctions.g.cs:11606-11618`）：`2: damagerCardID`，
        // 蓝图体 `GetCardFromID(damagerCardID)`（`g.cs:11624`）。
        // 派发表旧实现 `AsCard(a[2]) ?? c.Self` —— 整数形状恒 null ⇒ **伤害来源记成施法者**。
        // 全卡池 271 个调用点里 **9 个**的 `a[2]` 不是自己（7 个别人卡的 `cardID` +
        // 2 个 `spawnedCardID`），`ZActionDamageCard.attackerCardID` 因此填错。
        new("★ `DamageCard` 的第 3 参（`damagerCardID`，int）必须解析成来源卡（旧实现退回施法者）",
            DamageCardResolvesIntDamagerCardId),

        // ---- ★★ 2026-10-03：`PlayCard` 里 T51 与 T43 的**逐卡次序** ----
        // 蓝图 `CardPlayedFromHand` 把 T51(`OnOtherCardPlayedFromHand`) 与 T43(`OnOtherCardEnterPlay`)
        // 取到后 Append 进**同一个** `otherCards`（`:6060→:6066`），循环里对**同一张卡**
        // **先** T51（`:6462`）**后** T43（`:6464`）。内核旧实现先 T43 后 T51。
        new("★ `PlayCard`：同一张旁观卡必须**先** `OnOtherCardPlayedFromHand`(T51) **后** `OnOtherCardEnterPlay`(T43)" +
            "（蓝图 :6462 → :6464；旧实现反了）", PlayCardOtherTriggersOrder),
        new("★ `PlayCard`：战吼生成的新卡不得收到本次 T51 广播（早快照）",
            PlayCardBroadcastUsesEarlySnapshot),
        new("★ `PlayCard`：未揭示 Covert 牌先派发专用主体/旁观触发，普通牌不派发",
            PlayCardCovertTriggers),

        // ---- ★★ 2026-10-03：攻击前触发点的**接收者**与**先后** ----
        // 蓝图 `AttackCard`：`OnBeforeAttack` 的接收者是 `_attackerCard`（`:4633`，**不是防御方**），
        // 且被压制时跳过；T13(`OnBeforeOtherCardAttacks`) 广播排除攻击者本人（`:4562`），
        // 排在 `OnBeforeAttack` **之后**（`:4633 → :4635 Jump 3002 → :4543/:4587`）。
        new("★ 攻击前触发点：`OnBeforeAttack` **只给攻击方**（且被压制时不发），T13 广播在它**之后**" +
            "（蓝图 :4633 / :4587；旧实现给防御方也发、且两者混在一次派发里）",
            AttackBeforeTriggersRecipientsAndOrder),

        // ---- ★★ 2026-10-03：Gotcha（反制卡）子系统按蓝图重写 ----
        // 权威：`BP_CardFunctions.g.cs:23719-23912`（`GotchaTriggered`）等五处。
        // 这一族此前**完全没有**（`GotchaTriggered` 54 个调用点 / `ShouldGotchaTrigger` 53 /
        // `IsGotcha` 16 / `GetHandLocationBySide` 5 / `SetCardsSeenByCipher` 2 全在派发缺口里）。
        new("★ Gotcha：`IsGotcha` 判的是**卡定义类型**（52 张 `type==gotcha`），普通卡为假",
            GotchaIsGotchaByCardType),
        new("★★ Gotcha：`ShouldGotchaTrigger` 判的是 **self**（不是实参 a[0]）" +
            "（蓝图调用点 `card_event_interception.g.cs:53` 实参是触发卡、self 隐式）",
            GotchaShouldTriggerJudgesSelf),
        new("★★ Gotcha：`OnCounterMeasureTriggered` 的实参必须是 **gotcha 卡**" +
            "（`card_unit_the_tigers` 读 `countermeasureTriggering.originalSide/cardID/side`；" +
            "传触发卡则 +1+1 不发生）",
            GotchaCounterMeasureArgIsGotchaCard),
        new("★ Gotcha：触发后 gotcha 卡 **location = Discard(8)**、`enterPlayOnTurn` 被设、手牌被压紧",
            GotchaTriggeredMovesAndRearranges),
        new("★★ Gotcha：`gotchaActivated` 是**从 1 起的激活序号**（不是 bool）——" +
            "`GotchaTriggered` 置 0、装填递增、`GetActiveGotchasOrdered` 取 >0 且按键升序",
            GotchaActivatedIsSequence),
        new("★ Gotcha：`stopFurtherCardActions` ⇒ `SetStopFurtherActions(true)` + " +
            "`instigator.enterPlayOnTurn=0`；`IsOrder && skipDiscardingOrder` ⇒ `customJson.cancelOrderRemove`",
            GotchaStopFurtherActions),
        new("★ Intel：`SetCardsSeenByCipher` 只翻**对手**手牌里 min(n, 未见面数) 张，并消耗一次洗牌",
            SetCardsSeenByCipherRevealsOpponentUnseen),
    };

    public static int Run(CardDatabase db)
    {
        int failed = 0;
        foreach (var c in Cases)
        {
            string? err;
            try
            {
                err = c.Run(db);
            }
            catch (Exception ex)
            {
                err = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (err is null)
            {
                Console.WriteLine($"  ✅ {c.Name}");
            }
            else
            {
                Console.WriteLine($"  ❌ {c.Name}");
                Console.WriteLine($"       {err}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"全部 {Cases.Count} 项通过"
            : $"{failed}/{Cases.Count} 项失败");
        return failed == 0 ? 0 : 1;
    }

    // ==================== 全卡池烟雾测试台（2026-10-02） ====================
    //
    // 这三条直接把 `SmokeAllCards` 的自测钩子接进来。判据写在那边（见类注释），
    // 这里只负责注册。为什么不让它们跑全卡池：selftest 要秒级返回，
    // 而全卡池要 ~90 秒（4000+ 用例 × 3 次执行）—— 那是 `BotSim smoke-all-cards` 的活。

    private static string? SmokeBoardShape(CardDatabase db) => SmokeAllCards.SelfTestBoardShape(db);

    private static string? SmokeDeterminism(CardDatabase db) => SmokeAllCards.SelfTestDeterminism(db);

    private static string? SmokeNoCrash(CardDatabase db) => SmokeAllCards.SelfTestNoCrash(db);

    /// <summary>建一个只有 HQ 的空局面，双方各 20 点。</summary>
    private static (MatchEngine Engine, GameState State) EmptyBoard(CardDatabase db)    {
        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 1);
        var state = engine.State;
        state.CreateWithId("card_location_berlin", Side.Left, 1, CardLocation.BoardHqLeft, 0);
        state.CreateWithId("card_location_berlin", Side.Right, 41, CardLocation.BoardHqRight, 0);
        state.SetHqDefense(Side.Left, MatchEngine.InitialHqDefense);
        state.SetHqDefense(Side.Right, MatchEngine.InitialHqDefense);
        return (engine, state);
    }

    private static string? UnitOwnershipTransfer(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var unit = state.CreateWithId("card_unit_1st_infantry_regiment_us", Side.Right, 42,
            CardLocation.BoardHqRight, 1);
        var source = state.CreateWithId("card_event_confusion", Side.Left, 43,
            CardLocation.Discard, 0);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        engine.Api.InvokeByName("TakeControlOfEnemyUnit", source,
            new object?[] { unit, source.CardId }, ctx, out bool takeHandled);
        if (!takeHandled)
        {
            return "TakeControlOfEnemyUnit 未接入派发表";
        }

        if (unit.Owner != Side.Left || unit.OriginalOwner != Side.Right
            || !unit.UnderEnemyControl || unit.Location != CardLocation.BoardHqLeft
            || state.Cards(Side.Right, CardLocation.BoardHqRight).Contains(unit)
            || !state.Cards(Side.Left, CardLocation.BoardHqLeft).Contains(unit))
        {
            return $"夺取后状态错误：owner={unit.Owner}, original={unit.OriginalOwner}, "
                 + $"under={unit.UnderEnemyControl}, location={unit.Location}";
        }

        engine.Api.InvokeByName("ReleaseControlOfEnemyUnit", source,
            new object?[] { unit, source.CardId, (int)CardLocation.BoardHqRight, 0 },
            ctx, out bool releaseHandled);
        if (!releaseHandled)
        {
            return "ReleaseControlOfEnemyUnit 未接入派发表";
        }

        if (unit.Owner != Side.Right || unit.UnderEnemyControl
            || unit.Location != CardLocation.BoardHqRight
            || !state.Cards(Side.Right, CardLocation.BoardHqRight).Contains(unit))
        {
            return $"释放后状态错误：owner={unit.Owner}, under={unit.UnderEnemyControl}, "
                 + $"location={unit.Location}";
        }

        var action = state.ActionLog.LastOrDefault(x => x.SubActions.Any(s =>
            s.Name == "ZActionChangeUnitOwnership"));
        return action is null ? "缺少 ZActionChangeUnitOwnership 子动作" : null;
    }

    private static string? ChangeFrontlineLimiter(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var blackPrince = db.Find("card_unit_black_prince");
        if (blackPrince is null)
        {
            return "找不到 card_unit_black_prince";
        }

        var limiter = state.CreateWithId(blackPrince.Name, Side.Left, 2,
            CardLocation.BoardHqLeft, 0);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = limiter,
            Controller = Side.Left,
            Calls = new List<string> { "ChangeFrontlineLimiter" },
        };

        if (state.IsFrontlineLimited)
        {
            return "前置不成立：初始前线不应受限";
        }

        object? initialLimit = engine.Api.InvokeByName("getFrontlineLimit", limiter,
            new object?[] { null }, ctx, out bool initialHandled);
        if (!initialHandled || initialLimit is not bool initialLimited || initialLimited)
        {
            return $"getFrontlineLimit 初始值错误：handled={initialHandled}, value={initialLimit ?? "null"}";
        }

        engine.Api.InvokeByName("ChangeFrontlineLimiter", limiter,
            new object?[] { limiter.CardId, false }, ctx, out bool handledAdd);
        if (!handledAdd || !state.IsFrontlineLimited
            || state.FrontlineCapacity != GameState.LimitedFrontlineCapacity)
        {
            return $"加入 FrontlineLimiter 失败：handled={handledAdd}, limited={state.IsFrontlineLimited}, "
                 + $"capacity={state.FrontlineCapacity}";
        }

        object? limitedFlag = engine.Api.InvokeByName("getFrontlineLimit", limiter,
            new object?[] { null }, ctx, out bool limitedHandled);
        if (!limitedHandled || limitedFlag is not bool limited || !limited)
        {
            return $"getFrontlineLimit 受限值错误：handled={limitedHandled}, value={limitedFlag ?? "null"}";
        }

        if (!state.FrontlineLimiters.Contains(limiter.CardId))
        {
            return "加入 FrontlineLimiter 后集合中没有限制者 cardID";
        }

        engine.Api.InvokeByName("ChangeFrontlineLimiter", limiter,
            new object?[] { limiter.CardId, true }, ctx, out bool handledRemove);
        if (!handledRemove || state.IsFrontlineLimited
            || state.FrontlineCapacity != GameState.DefaultFrontlineCapacity)
        {
            return $"移除 FrontlineLimiter 失败：handled={handledRemove}, limited={state.IsFrontlineLimited}, "
                 + $"capacity={state.FrontlineCapacity}";
        }

        if (state.FrontlineLimiters.Contains(limiter.CardId))
        {
            return "移除 FrontlineLimiter 后集合仍保留限制者 cardID";
        }

        return null;
    }

    private static string? GameplayRestrictions(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        int source = 900;
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Controller = Side.Left,
        };

        void Add(GameplayRestrictionType type, int turns = 2)
        {
            engine.Api.InvokeByName("AddGameplayRestriction", null,
                new object?[] { (int)Side.Left, (int)type, source, turns }, ctx, out _);
        }

        Add(GameplayRestrictionType.CannotDrawCardAtTurnStart);
        Add(GameplayRestrictionType.CannotKreditSlotAtTurnStart);
        Add(GameplayRestrictionType.CannotPlayOrders);
        Add(GameplayRestrictionType.CannotDeployUnits);
        Add(GameplayRestrictionType.CannotAttackWithGroundUnits);
        Add(GameplayRestrictionType.CannotDiscardAnyCardFromHand);

        foreach (var type in Enum.GetValues<GameplayRestrictionType>().Where(x => x != GameplayRestrictionType.NotAvailable))
        {
            if (!state.HasGameplayRestriction(Side.Left, type))
            {
                return $"限制 {type} 未加入";
            }
        }

        var secondSource = 901;
        engine.Api.InvokeByName("AddGameplayRestriction", null,
            new object?[] { (int)Side.Left, (int)GameplayRestrictionType.CannotPlayOrders, secondSource, 2 },
            ctx, out _);
        engine.Api.InvokeByName("RemoveGameplayRestriction", null,
            new object?[] { (int)Side.Left, (int)GameplayRestrictionType.CannotPlayOrders, source, false },
            ctx, out bool handled);
        if (!handled || !state.HasGameplayRestriction(Side.Left, GameplayRestrictionType.CannotPlayOrders))
        {
            return "按来源移除时错误地清空了同类型的另一条限制";
        }

        engine.Api.InvokeByName("RemoveGameplayRestriction", null,
            new object?[] { (int)Side.Left, (int)GameplayRestrictionType.CannotPlayOrders, secondSource, true },
            ctx, out _);
        if (state.HasGameplayRestriction(Side.Left, GameplayRestrictionType.CannotPlayOrders))
        {
            return "RemoveAll 没有清空同类型限制";
        }

        state.DecrementGameplayRestrictions();
        if (!state.HasGameplayRestriction(Side.Left, GameplayRestrictionType.CannotDrawCardAtTurnStart))
        {
            return "剩余回合数 2 的限制过早解除";
        }

        state.DecrementGameplayRestrictions();
        if (state.HasGameplayRestriction(Side.Left, GameplayRestrictionType.CannotDrawCardAtTurnStart))
        {
            return "剩余回合数到期后限制仍存在";
        }

        return null;
    }

    private static string? RetreatEndToEnd(CardDatabase db)
    {
        foreach (bool frontline in new[] { false, true })
        {
            var (engine, state) = DeploymentBoard(db);
            var target = state.CreateWithId("card_unit_j2m_raiden", Side.Right, 42,
                frontline ? CardLocation.BoardFrontline : CardLocation.BoardHqRight, 1);
            var order = state.CreateWithId("card_event_aa_barrage", Side.Left, 2, CardLocation.HandLeft, 0);
            int before = state.HqDefense(Side.Left);
            if (!engine.PlayCard(order, target)) return "AA Barrage 出牌失败";
            var expected = frontline ? CardLocation.BoardHqRight : CardLocation.HandRight;
            if (target.Location != expected) return $"撤回落点 {target.Location}，应为 {expected}";
            if (state.HqDefense(Side.Left) != before + 2) return "AA Barrage 未给 HQ +2";
        }

        var (emptyEngine, emptyState) = DeploymentBoard(db);
        var m16 = emptyState.CreateWithId("card_unit_m16_halftrack", Side.Left, 2, CardLocation.HandLeft, 0);
        if (!emptyEngine.PlayCard(m16)) return "无目标 M16 出牌失败";
        return m16.Location == CardLocation.BoardHqLeft ? null : "无目标 M16 错误撤回自身";
    }

    private static string? LostSlotEnables238thDamage(CardDatabase db)
    {
        var (engine, state) = DeploymentBoard(db);
        state.SetMaxKredits(Side.Left, 4);
        state.SetKredits(Side.Left, 2);
        var unit = state.CreateWithId("card_unit_238th_regiment", Side.Left, 30, CardLocation.BoardFrontline, 0);
        unit.EnteredPlayOnTurn = -1;
        var ctx = new EffectContext { Engine = engine, State = state, Self = unit, Controller = Side.Left };
        if (engine.Api.ExecuteOnDealDamageAddDamage(unit, state.Hq(Side.Right), 2, true, false, false) != 2)
            return "4 槽时不应双倍";

        // 238th 的判据是 MaxKredits <= 3；当前余额不是判据。
        // 这里把 4/3 两个边界直接隔离出来，避免回放里的攻击/事件链掩盖槽位判断。
        state.SetMaxKredits(Side.Left, 3);
        if (engine.Api.ExecuteOnDealDamageAddDamage(unit, state.Hq(Side.Right), 2, true, false, false) != 4)
            return "3 槽时 238 团应直接双倍（MaxKredits 边界）";
        state.SetMaxKredits(Side.Left, 4);

        engine.Api.InvokeByName("LoseKreditSlot", unit, new object?[] { (int)Side.Left }, ctx, out bool handled);
        if (!handled || state.MaxKredits(Side.Left) != 3 || state.Kredits(Side.Left) != 2)
            return "扣槽必须只改变上限 4→3，当前费用仍为 2";
        int hp = state.HqDefense(Side.Right);
        if (!engine.Attack(unit, state.Hq(Side.Right), out string reason)) return reason;
        if (state.HqDefense(Side.Right) != hp - 4) return "3 槽时 238 团应造成 4 点伤害";
        state.SetMaxKredits(Side.Left, 0);
        engine.Api.InvokeByName("LoseKreditSlot", unit, new object?[] { (int)Side.Left }, ctx, out _);
        if (state.MaxKredits(Side.Left) != 0) return "槽位不能降为负数";
        var loss = engine.Api.InvokeByName("GetTotalKreditsLostThisBattle", unit,
            new object?[] { (int)Side.Left, null }, ctx, out bool queried);
        if (!queried || loss is not int count || count != 2)
            return "累计损失应为 2（包含零槽再次扣槽），查询必须写出整数";
        if (state.KreditSlotsLost(Side.Right) != 0) return "累计损失串到对方";
        int beforeGainKredits = state.Kredits(Side.Left);
        int beforeGainSlots = state.MaxKredits(Side.Left);
        var gainWatcher = state.CreateWithId("card_unit_144th_infantry_regiment", Side.Left, 33,
            CardLocation.BoardHqLeft, 1);
        var gainTrace = new List<string>();
        engine.Api.TriggerTrace = gainTrace;
        engine.Api.GainKreditSlot(Side.Left, 1, unit);
        if (state.MaxKredits(Side.Left) != beforeGainSlots + 1
            || state.Kredits(Side.Left) != beforeGainKredits)
        {
            return $"GainKreditSlot 应只增加槽位、不增加当前 kredit："
                 + $"之前 {beforeGainKredits}/{beforeGainSlots}，"
                 + $"之后 {state.Kredits(Side.Left)}/{state.MaxKredits(Side.Left)}";
        }
        if (!gainTrace.Any(x => x.Contains("OnAfterExtraKreditSlotGain", StringComparison.Ordinal)
                             && x.Contains("eventCard=card_unit_238th_regiment#30", StringComparison.Ordinal)))
        {
            return "GainKreditSlot 的 OnAfterExtraKreditSlotGain 未携带发起卡："
                 + string.Join(" | ", gainTrace.Take(6));
        }
        state.SetMaxKredits(Side.Left, MatchEngine.MaxKreditCap);
        state.SetKredits(Side.Left, MatchEngine.MaxKreditCap);
        engine.Api.GainKreditSlot(Side.Left, 1, unit);
        if (state.MaxKredits(Side.Left) != MatchEngine.MaxKreditCap
            || state.Kredits(Side.Left) != MatchEngine.MaxKreditCap)
        {
            return "GainKreditSlot 不得突破线上 getMaxPossibleKredits 上限 24";
        }
        if (state.KreditSlotsLost(Side.Left) != 2) return "获得槽位不应抵消历史损失";
        var infantry = state.CreateWithId("card_unit_144th_infantry_regiment", Side.Left, 32,
            CardLocation.BoardHqLeft, 1);
        int beforeDestruction = state.HqDefense(Side.Right);
        engine.Destroy(infantry);
        return state.HqDefense(Side.Right) == beforeDestruction - 2
            ? null : "144 步兵团摧毁效果应按累计损失造成 2 点 HQ 伤害";
    }

    private static string? FifthOhioNullableTarget(CardDatabase db)
    {
        foreach (bool withTarget in new[] { false, true })
        {
            var (engine, state) = DeploymentBoard(db);
            var ohio = state.CreateWithId("card_unit_fifth_ohio", Side.Left, 2, CardLocation.HandLeft, 0);
            var target = state.CreateWithId("card_unit_arado_ar_196", Side.Right, 42, CardLocation.BoardHqRight, 1);
            if (!engine.PlayCard(ohio, withTarget ? target : null)) return "Fifth Ohio 无法部署";
            if (!ohio.AliveOnBoard) return "空目标被替换为施法者，导致 Fifth Ohio 自毁";
            if (withTarget && target.Location != CardLocation.Discard) return "显式目标没有被摧毁";
            if (!withTarget && !target.AliveOnBoard) return "无目标部署不应摧毁旁观单位";
        }
        return null;
    }

    private static string? SnapshotTracksRuleState(CardDatabase db)
    {
        var (_, state) = DeploymentBoard(db);
        var unit = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 2, CardLocation.BoardHqLeft, 1);
        string previous = state.SnapshotJson();
        var changes = new Action[]
        {
            () => state.RecordKreditSlotLoss(Side.Left),
            () => state.RecordKreditSlotLoss(Side.Right),
            () => state.AddGameplayRestriction(Side.Left, GameplayRestrictionType.CannotPlayOrders, 2, 3),
            () => state.DecrementGameplayRestrictions(),
            () => state.AddGameplayRestriction(Side.Left, GameplayRestrictionType.CannotPlayOrders, 3, 2),
            () => state.ApplyGameplaySideEffect(Side.Left, "sideeffect.blockgotcha", 77, 3, 0),
            () => state.FrontlineLimiters.Add(2),
            () => state.FrontlineLimiters.Add(3),
            () => unit.HasBeenAttackedThisTurn = true,
            () => unit.PinnedTurns = 2,
            () => unit.PinnedTurns = 1,
        };
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i]();
            string next = state.SnapshotJson();
            if (next == previous) return $"状态变化 {i} 未进入快照";
            previous = next;
        }
        var saved = state.Snapshot();
        state.DecrementGameplayRestrictions();
        if (saved.Restrictions[0].TurnsRemaining != 2) return "快照共享可变限制对象";
        state.RemoveGameplaySideEffect(Side.Left, "sideeffect.blockgotcha", 77);
        if (saved.SideEffects.Length != 1 || saved.SideEffects[0].SourceCardId != 77)
            return "快照共享可变 side-effect 状态";
        return null;
    }

    private static string? GameplaySideEffects(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_unit_c6n_saiun", Side.Left, 1500,
            CardLocation.BoardHqLeft, 0);
        var secondSource = state.CreateWithId("card_unit_c6n_saiun", Side.Left, 1501,
            CardLocation.BoardHqLeft, 1);
        var gotcha = MakeGotcha(state, "card_event_interception", Side.Left, 1502,
            CardLocation.HandLeft, 0);
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        object? applied = engine.Api.InvokeByName("ApplyGameplaySideEffect", source,
            new object?[] { (int)Side.Left, "sideeffect.blockgotcha", source.CardId, 3, 0, null },
            ctx, out bool handledApply);
        if (!handledApply || applied is not int)
            return "ApplyGameplaySideEffect 未派发或没有写入 int 出参";
        engine.Api.InvokeByName("ApplyGameplaySideEffect", source,
            new object?[] { (int)Side.Left, "sideeffect.blockgotcha", source.CardId, 3, 0, null },
            ctx, out _);
        engine.Api.InvokeByName("ApplyGameplaySideEffect", secondSource,
            new object?[] { (int)Side.Left, "sideeffect.blockgotcha", secondSource.CardId, 3, 0, null },
            ctx, out _);
        engine.Api.InvokeByName("ApplyGameplaySideEffect", source,
            new object?[] { (int)Side.Right, "sideeffect.blockgotcha", source.CardId, 3, 0, null },
            ctx, out _);

        if (state.GameplaySideEffects.Count != 3)
            return $"相同来源应用应更新、不同来源/阵营应并存，实际记录 {state.GameplaySideEffects.Count} 条";
        if (engine.Api.ShouldGotchaTrigger(gotcha))
            return "己方 blockgotcha 生效时，Gotcha 仍允许触发";

        engine.Api.InvokeByName("RemoveGameplaySideEffect", source,
            new object?[] { (int)Side.Left, "sideeffect.blockgotcha", source.CardId, null },
            ctx, out bool handledRemove);
        if (!handledRemove || engine.Api.ShouldGotchaTrigger(gotcha))
            return "移除一个来源后错误清除了另一来源，或 RemoveGameplaySideEffect 未派发";

        engine.Api.InvokeByName("RemoveGameplaySideEffect", secondSource,
            new object?[] { (int)Side.Left, "sideeffect.blockgotcha", secondSource.CardId, null },
            ctx, out _);
        if (!engine.Api.ShouldGotchaTrigger(gotcha))
            return "所有己方 blockgotcha 移除后，Gotcha 未恢复触发";
        if (!state.HasGameplaySideEffect(Side.Right, "sideeffect.blockgotcha"))
            return "移除己方来源时错误清除了敌方 side-effect";

        engine.Api.InvokeByName("RemoveGameplaySideEffect", source,
            new object?[] { (int)Side.Right, "sideeffect.blockgotcha", source.CardId, null },
            ctx, out _);
        if (state.GameplaySideEffects.Count != 0)
            return "移除双方来源后仍保留 GameplaySideEffect";
        return null;
    }

    private static string? RandomTraceIsObservational(CardDatabase db)
    {
        var (plain, state) = DeploymentBoard(db);
        var (traced, tracedState) = DeploymentBoard(db);
        var pool = Enumerable.Range(0, 65).Select(i => state.CreateWithId(
            "card_unit_arado_ar_196", Side.Left, 100 + i, CardLocation.DeckLeft, i)).ToList();
        var tracePool = Enumerable.Range(0, 65).Select(i => tracedState.CreateWithId(
            "card_unit_arado_ar_196", Side.Left, 100 + i, CardLocation.DeckLeft, i)).ToList();
        tracedState.CollectRandomTrace = true;
        var a = plain.Api.GetRandomCard(pool);
        var b = traced.Api.GetRandomCard(tracePool);
        if (a?.CardId != b?.CardId || state.Random.Seed != tracedState.Random.Seed
            || state.Random.ConsumedCount != tracedState.Random.ConsumedCount)
            return "日志开关改变了随机行为";
        string line = tracedState.RandomTrace.Single();
        if (!line.Contains("cursor=0->1") || !line.Contains("seed=")) return "随机状态信息缺失";
        if (line.Split("card_unit_arado_ar_196").Length - 1 != 66)
            return "完整池应有 65 项，加选中项共出现 66 次";
        return state.RandomTrace.Count == 0 ? null : "关闭追踪时不应生成日志";
    }

    private static string? BlueprintArrayAndLocationQueries(CardDatabase db)
    {
        var (engine, state) = DeploymentBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Right };
        foreach (var (side, expected) in new[] { (0, 0), (1, 5), (2, 6), (99, 0) })
        {
            object? result = engine.Api.InvokeByName("GetSupportLineLocationBySide", null,
                new object?[] { side, null }, ctx, out bool handled);
            if (!handled || result is not int value || value != expected)
                return $"支援线查询 side={side} 应为 {expected}，实际 {result}";
        }
        var card = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 2, CardLocation.HandLeft, 0);
        var ids = new List<int>();
        object? Add(object array, object item) => engine.Api.InvokeByName("Array_AddUnique", null,
            new object?[] { array, item }, ctx, out _);
        if (!Equals(Add(ids, card.CardId), 0) || !Equals(Add(ids, card), 0) || ids.Count != 1)
            return "整数数组 AddUnique 未按 ID 去重";
        var cards = new List<CardInstance>();
        if (!Equals(Add(cards, card), 0) || !Equals(Add(cards, card.CardId), 0) || cards.Count != 1)
            return "卡对象数组 AddUnique 未按 ID 去重";
        return null;
    }

    private static string? RemoveSalvage(CardDatabase db)
    {
        const string targetName = "card_unit_raf_mitchell";
        const string sourceName = "card_unit_10_5_cm_lefh";
        if (db.Find(targetName) is null || db.Find(sourceName) is null)
        {
            return $"卡库里缺 {targetName} / {sourceName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, sourceName, Side.Left, 20, 1);
        var target = PutOnBoard(state, targetName, Side.Left, 21, 2);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        engine.Api.InvokeByName("GiveSalvage", source,
            new object?[] { target.CardId, null, source.CardId }, ctx, out bool giveHandled);
        if (!giveHandled || !target.Keywords.Contains(Keyword.Salvage))
        {
            return "GiveSalvage 后目标必须拥有 Salvage";
        }

        object? result = engine.Api.InvokeByName("RemoveSalvage", source,
            new object?[] { target.CardId, source.CardId, null, false }, ctx,
            out bool removeHandled);
        if (!removeHandled)
        {
            return "RemoveSalvage 未进入派发表";
        }

        if (result is not int value || value != 0)
        {
            return $"RemoveSalvage 的 qqq 应返回 0，实际 {result ?? "null"}";
        }

        return target.Keywords.Contains(Keyword.Salvage)
            ? "RemoveSalvage 后目标仍保留 Salvage"
            : null;
    }

    private static string? SalvageMultipleUnits(CardDatabase db)
    {
        const string sourceName = "card_unit_raf_mitchell";
        const string instigatorName = "card_unit_10_5_cm_lefh";
        if (db.Find(sourceName) is null || db.Find(instigatorName) is null)
        {
            return $"卡库里缺 {sourceName} / {instigatorName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var instigator = PutOnBoard(state, instigatorName, Side.Left, 20, 1);
        var original = state.Create(sourceName, Side.Right, CardLocation.Discard, 0, isGold: true);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = instigator, Controller = Side.Left,
        };

        var result = engine.Api.InvokeByName("SalvageMultipleUnits", instigator,
            new object?[] { new[] { original.CardId }, instigator.CardId, null }, ctx,
            out bool handled);
        if (!handled || result is not IReadOnlyList<int> ids || ids.Count != 1)
        {
            return $"应返回 1 个新卡 ID，handled={handled} result={result ?? "null"}";
        }

        var copy = state.ById(ids[0]);
        if (copy is null || copy.Location != CardLocation.HandLeft || copy.Owner != Side.Left)
        {
            return "打捞副本必须进入施动方左手牌";
        }

        if (!copy.IsGold || !copy.IsSalvaged || copy.SalvagedCardId != original.CardId ||
            copy.SalvageFaction != original.Definition.Faction)
        {
            return "打捞副本未保留金卡、isSalvaged、来源 cardID 或原卡阵营";
        }

        for (int i = 0; i < GameState.HandCapacity - 1; i++)
        {
            state.Create(instigatorName, Side.Left, CardLocation.HandLeft,
                state.NextLocationNumber(Side.Left, CardLocation.HandLeft));
        }

        var fullResult = engine.Api.InvokeByName("SalvageMultipleUnits", instigator,
            new object?[] { new[] { original.CardId }, instigator.CardId, null }, ctx,
            out bool fullHandled);
        return fullHandled && fullResult is IReadOnlyList<int> fullIds && fullIds.Count == 0
            ? null
            : "满手时 SalvageMultipleUnits 必须跳过生成";
    }

    private static string? RemoveBond(CardDatabase db)
    {
        const string sourceName = "card_unit_10_5_cm_lefh";
        const string staticBondName = "card_unit_18_infantry_regiment";
        if (db.Find(sourceName) is null || db.Find(staticBondName) is null)
        {
            return $"卡库里缺 {sourceName} / {staticBondName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, sourceName, Side.Left, 30, 1);
        var target = PutOnBoard(state, sourceName, Side.Left, 31, 2);
        var targetCtx = new EffectContext
        {
            Engine = engine, State = state, Self = target, Controller = Side.Left,
        };

        engine.Api.InvokeByName("GiveBond", source,
            new object?[] { target.CardId, null, source.CardId }, targetCtx, out bool giveHandled);
        object? hasBefore = engine.Api.InvokeByName("HasBond", target,
            new object?[] { null }, targetCtx, out bool hasBeforeHandled);
        if (!giveHandled || !hasBeforeHandled || hasBefore is not true)
        {
            return "GiveBond 后目标的 HasBond 应为真";
        }

        var invokeCtx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Target = target, Controller = Side.Left,
        };
        object? result = engine.Api.InvokeByName("RemoveBond", source,
            new object?[] { target.CardId, source.CardId, null }, invokeCtx, out bool handled);
        object? hasAfter = engine.Api.InvokeByName("HasBond", target,
            new object?[] { null }, targetCtx, out bool hasAfterHandled);
        if (!handled || result is not int value || value != 0
            || !hasAfterHandled || hasAfter is not false
            || target.CustomAbility != "bond_removed")
        {
            return $"RemoveBond 未按目标 ID 写入覆盖标记并屏蔽 Bond（handled={handled}, result={result}, "
                 + $"hasAfter={hasAfter}, ability={target.CustomAbility})";
        }

        engine.Api.InvokeByName("GiveBond", source,
            new object?[] { target.CardId, null, source.CardId }, targetCtx, out bool reGiveHandled);
        object? hasRegained = engine.Api.InvokeByName("HasBond", target,
            new object?[] { null }, targetCtx, out bool hasRegainedHandled);
        if (!reGiveHandled || !hasRegainedHandled || hasRegained is not true)
        {
            return "GiveBond 重新授予时应清除 bond_removed 覆盖并恢复 HasBond";
        }

        var staticCard = PutOnBoard(state, staticBondName, Side.Left, 32, 3);
        var staticCtx = new EffectContext
        {
            Engine = engine, State = state, Self = staticCard, Controller = Side.Left,
        };
        object? staticBefore = engine.Api.InvokeByName("HasBond", staticCard,
            new object?[] { null }, staticCtx, out bool staticBeforeHandled);
        if (!staticBeforeHandled || staticBefore is not true)
        {
            return "卡面静态 ability.bond 应被 HasBond 识别";
        }

        engine.Api.InvokeByName("RemoveBond", source,
            new object?[] { staticCard.CardId, source.CardId, null }, invokeCtx, out bool staticRemoveHandled);
        object? staticAfter = engine.Api.InvokeByName("HasBond", staticCard,
            new object?[] { null }, staticCtx, out bool staticAfterHandled);
        return !staticRemoveHandled || !staticAfterHandled || staticAfter is not false
            ? "RemoveBond 应通过 bond_removed 覆盖卡面静态 Bond"
            : null;
    }

    private static string? StealCardFromBoardToDeck(CardDatabase db)
    {
        const string sourceName = "card_event_cobelligerents";
        const string targetName = "card_unit_18_infantry_regiment";
        if (db.Find(sourceName) is null || db.Find(targetName) is null)
        {
            return $"卡库里缺 {sourceName} / {targetName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, sourceName, Side.Left, 900, 1);
        var target = PutOnBoard(state, targetName, Side.Right, 901, 2);
        target.IsGold = true;
        int targetId = target.CardId;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Target = target, Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("StealCardFromBoardToDeck", source,
            new object?[] { targetId, source.CardId, (int)Side.Left, null }, ctx,
            out bool handled);
        var copies = state.Deck(Side.Left).Where(x => x.Name == targetName).ToList();
        var stealSubAction = state.ActionLog
            .SelectMany(x => x.SubActions)
            .LastOrDefault(x => x.Name == "ZActionStealCardFromBoardToDeck");
        var payload = stealSubAction?.Values.ToDictionary(x => x.Name, StringComparer.Ordinal);
        if (!handled || result is not int value || value != 0
            || target.Location != CardLocation.Discard
            || copies.Count != 1 || copies[0].CardId == targetId || !copies[0].IsGold
            || copies[0].Owner != Side.Left || copies[0].Location != CardLocation.DeckLeft
            || payload is null
            || stealSubAction is null
            || !stealSubAction.Values.Select(x => x.Name).SequenceEqual(
                new[] { "oldLocation", "deckSide", "instigatorID" }, StringComparer.Ordinal)
            || !payload.TryGetValue("oldLocation", out var oldLocation)
            || oldLocation.Value != (int)CardLocation.BoardHqRight
            || !payload.TryGetValue("deckSide", out var deckSide)
            || deckSide.Text != Side.Left.ToWire()
            || !payload.TryGetValue("instigatorID", out var instigator)
            || instigator.Value != source.CardId)
        {
            return $"应将原卡移入弃牌堆，并把新的金卡副本放入左方牌库 "
                 + $"(handled={handled}, result={result}, old={target.Location}, "
                 + $"copies={copies.Count}, gold={copies.FirstOrDefault()?.IsGold}, "
                 + $"payload={string.Join(",", stealSubAction?.Values.Select(x => $"{x.Name}={x.Value}/{x.Text}")
                     ?? Array.Empty<string>())})";
        }

        if (state.Deck(Side.Right).Any(x => x.Name == targetName))
        {
            return "复制卡不应进入原卡拥有方（右方）的牌库";
        }

        return null;
    }

    private static string? AdjustCardPositionInDeck(CardDatabase db)
    {
        const string moverName = "card_event_fog_of_war";
        const string watcherName = "card_event_betasom";
        if (db.Find(moverName) is null || db.Find(watcherName) is null)
        {
            return $"卡库里缺 {moverName} / {watcherName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.Turn = 1;
        var watcher = state.CreateWithId(watcherName, Side.Left, 9300,
            CardLocation.BoardHqLeft, 1);
        watcher.EnteredPlayOnTurn = 1;

        var leftFirst = state.CreateWithId(moverName, Side.Left, 9301,
            CardLocation.DeckLeft, 0);
        var leftTarget = state.CreateWithId(moverName, Side.Left, 9302,
            CardLocation.DeckLeft, 1);
        var leftLast = state.CreateWithId(moverName, Side.Left, 9303,
            CardLocation.DeckLeft, 2);
        var rightOnly = state.CreateWithId(moverName, Side.Right, 9304,
            CardLocation.DeckRight, 0);

        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = watcher, Controller = Side.Left,
        };
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        object? result = engine.Api.InvokeByName("AdjustCardPositionInDeck", watcher,
            new object?[] { (int)Side.Left, leftTarget.CardId, 0, null }, ctx,
            out bool handled);
        var leftOrder = state.Deck(Side.Left).Select(x => x.CardId).ToArray();
        if (!handled || result is not null
            || !leftOrder.SequenceEqual(new[] { leftTarget.CardId, leftFirst.CardId, leftLast.CardId })
            || leftTarget.LocationNumber != 0
            || !trace.Any(x => x.StartsWith("OnAfterDeckChanged → card_event_betasom#9300",
                StringComparison.Ordinal))
            || !engine.Api.HasCustomGameplayTag(leftTarget, "subtype.navy"))
        {
            return "左方牌库调整应将目标插到顶端并触发 Betasom 的 OnAfterDeckChanged；"
                 + $"handled={handled}, result={result?.GetType().Name ?? "null"}, "
                 + $"order=[{string.Join(",", leftOrder)}], targetLoc={leftTarget.LocationNumber}, "
                 + $"trace={string.Join(" | ", trace.Take(4))}";
        }

        // Event payload must carry the changed deck side.  A left-side watcher
        // must ignore a right-side adjustment, and the other deck must remain
        // isolated from the left reorder.
        trace.Clear();
        engine.Api.InvokeByName("AdjustCardPositionInDeck", watcher,
            new object?[] { (int)Side.Right, rightOnly.CardId, 0, null }, ctx,
            out bool rightHandled);
        if (!rightHandled || state.Deck(Side.Right).Select(x => x.CardId).SequenceEqual(new[] { rightOnly.CardId }) == false
            || state.Deck(Side.Left).Select(x => x.CardId).FirstOrDefault() != leftTarget.CardId)
        {
            return "右方牌库调整不应污染左方牌库或跳过合法处理";
        }

        // An invalid card must be a no-op, including no deck-change trigger.
        trace.Clear();
        engine.Api.InvokeByName("AdjustCardPositionInDeck", watcher,
            new object?[] { (int)Side.Left, watcher.CardId, 0, null }, ctx,
            out bool invalidHandled);
        return invalidHandled && state.Deck(Side.Left).FirstOrDefault()?.CardId == leftTarget.CardId
            && !trace.Any(x => x.StartsWith("OnAfterDeckChanged", StringComparison.Ordinal))
            ? null
            : "不在牌库中的卡调用 AdjustCardPositionInDeck 必须保持无变化且不派发事件";
    }

    private static string? MoveCardInHandToLeftMost(CardDatabase db)
    {
        const string sourceName = "card_event_hmas_warramunga";
        const string cardName = "card_unit_10_5_cm_lefh";
        if (db.Find(sourceName) is null || db.Find(cardName) is null)
        {
            return $"卡库里缺 {sourceName} / {cardName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 3000, CardLocation.Discard, 0);
        var first = state.CreateWithId(cardName, Side.Left, 3001, CardLocation.HandLeft, 0);
        var target = state.CreateWithId(cardName, Side.Left, 3002, CardLocation.HandLeft, 1);
        var last = state.CreateWithId(cardName, Side.Left, 3003, CardLocation.HandLeft, 2);
        var otherSide = state.CreateWithId(cardName, Side.Right, 3004, CardLocation.HandRight, 0);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("MoveCardInHandToLeftMost", source,
            new object?[] { target, null }, ctx, out bool handled);
        var leftOrder = state.Hand(Side.Left).Select(x => x.CardId).ToArray();
        var rightOrder = state.Hand(Side.Right).Select(x => x.CardId).ToArray();
        if (!handled || result is not bool moved || !moved
            || !leftOrder.SequenceEqual(new[] { target.CardId, first.CardId, last.CardId })
            || target.LocationNumber != 0 || first.LocationNumber != 1 || last.LocationNumber != 2
            || !rightOrder.SequenceEqual(new[] { otherSide.CardId }))
        {
            return $"目标应移到左侧手牌第 0 格：handled={handled}, result={result}, "
                 + $"left=[{string.Join(",", leftOrder)}], right=[{string.Join(",", rightOrder)}]";
        }

        result = engine.Api.InvokeByName("MoveCardInHandToLeftMost", source,
            new object?[] { target, null }, ctx, out bool repeatedHandled);
        if (!repeatedHandled || result is not bool repeatedMoved || repeatedMoved
            || !state.Hand(Side.Left).Select(x => x.CardId)
                .SequenceEqual(new[] { target.CardId, first.CardId, last.CardId }))
        {
            return "已经位于最左侧的手牌重复调用必须是成功处理但不移动";
        }

        result = engine.Api.InvokeByName("MoveCardInHandToLeftMost", source,
            new object?[] { source, null }, ctx, out bool invalidHandled);
        return invalidHandled && result is bool invalidMoved && !invalidMoved
            && state.Hand(Side.Left).Select(x => x.CardId)
                .SequenceEqual(new[] { target.CardId, first.CardId, last.CardId })
            ? null
            : "非手牌目标必须拒绝且不得改变手牌顺序";
    }

    private static string? OtherCardMovedToLocationInHand(CardDatabase db)
    {
        const string auraName = "card_event_guerilla_warfare_school";
        const string moverName = "card_event_aans";
        if (db.Find(auraName) is null || db.Find(moverName) is null)
        {
            return $"卡库里缺 {auraName} / {moverName}";
        }

        var (engine, state) = EmptyBoard(db);
        var aura = state.CreateWithId(auraName, Side.Left, 3010, CardLocation.HandLeft, 0);
        var mover = state.CreateWithId(moverName, Side.Left, 3011, CardLocation.HandLeft, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = mover,
            Controller = Side.Left,
        };
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        int initialCost = aura.KreditCost;
        object? result = engine.Api.InvokeByName("MoveCardInHandToLeftMost", mover,
            new object?[] { mover, null }, ctx, out bool handled);

        bool broadcastSeen = trace.Any(x => x.StartsWith(
            $"OnOtherCardMovedToLocationInHand → {auraName}#{aura.CardId}",
            StringComparison.Ordinal));
        if (!handled || result is not bool moved || !moved
            || aura.LocationNumber != 1
            || aura.KreditCost != Math.Max(0, initialCost - 1)
            || !broadcastSeen)
        {
            return $"手牌重排后应广播到手牌中的游击战学校并按最右位置减费："
                 + $"handled={handled}, result={result}, location={aura.LocationNumber}, "
                 + $"cost={aura.KreditCost}/{initialCost}, trace={string.Join(" | ", trace)}";
        }

        return null;
    }

    private static string? SecondOrderThisTurn(CardDatabase db)
    {
        const string lureName = "card_event_lure";
        const string waveName = "card_event_wave_after_wave";
        const string cameroniansName = "card_unit_cameronians";
        const string ordinaryOrderName = "card_event_aans";
        const string weatherName = "card_event_rain2_deluge";
        foreach (string name in new[]
        {
            lureName, waveName, cameroniansName, ordinaryOrderName, weatherName,
        })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        var lure = state.CreateWithId(lureName, Side.Left, 200,
            CardLocation.HandLeft, 0);
        state.CardsPlayedThisTurn.Add(state.CreateWithId(ordinaryOrderName, Side.Left, 201,
            CardLocation.Discard, 0));
        state.CardsPlayedThisTurn.Add(state.CreateWithId(ordinaryOrderName, Side.Left, 203,
            CardLocation.Discard, 0));
        state.CardsPlayedThisTurn.Add(state.CreateWithId(ordinaryOrderName, Side.Right, 202,
            CardLocation.Discard, 0));
        var ctx = new EffectContext { Engine = engine, State = state, Self = lure, Controller = Side.Left };

        object? result = engine.Api.InvokeByName("isSecondOrderThisTurn", null,
            new object?[] { (int)Side.Left, null }, ctx, out bool handled);
        if (!handled || result is not true)
        {
            return $"恰好两张己方指令（含一张敌方指令）应判真：handled={handled}, result={result ?? "null"}";
        }

        state.CardsPlayedThisTurn.Add(state.CreateWithId(ordinaryOrderName, Side.Left, 204,
            CardLocation.Discard, 0));
        result = engine.Api.InvokeByName("isSecondOrderThisTurn", null,
            new object?[] { (int)Side.Left, null }, ctx, out _);
        if (result is not false)
        {
            return "第三张己方指令后应不再判为第二张指令";
        }

        var (waveEngine, waveState) = EmptyBoard(db);
        var wave = waveState.CreateWithId(waveName, Side.Left, 210,
            CardLocation.HandLeft, 0);
        waveState.CardsPlayedThisTurn.Add(waveState.CreateWithId(weatherName, Side.Left, 211,
            CardLocation.Discard, 0));
        waveState.CardsPlayedThisTurn.Add(waveState.CreateWithId(ordinaryOrderName, Side.Left, 212,
            CardLocation.Discard, 0));
        var waveCtx = new EffectContext
        {
            Engine = waveEngine, State = waveState, Self = wave, Controller = Side.Left,
        };
        result = waveEngine.Api.InvokeByName("isSecondOrderThisTurn", null,
            new object?[] { (int)Side.Left, null }, waveCtx, out bool waveHandled);
        if (!waveHandled || result is not false)
        {
            return $"Wave After Wave 的天气牌不应计入第二张指令：handled={waveHandled}, result={result ?? "null"}";
        }

        waveState.CardsPlayedThisTurn.Add(waveState.CreateWithId(ordinaryOrderName, Side.Left, 213,
            CardLocation.Discard, 0));
        result = waveEngine.Api.InvokeByName("isSecondOrderThisTurn", null,
            new object?[] { (int)Side.Left, null }, waveCtx, out _);
        if (result is not true)
        {
            return "Wave After Wave 在两张非天气己方指令后应判真";
        }

        var (cameroniansEngine, cameroniansState) = EmptyBoard(db);
        var cameronians = cameroniansState.CreateWithId(cameroniansName, Side.Left, 220,
            CardLocation.BoardHqLeft, 1);
        cameroniansState.CardsPlayedThisTurn.Add(
            cameroniansState.CreateWithId(ordinaryOrderName, Side.Left, 221,
                CardLocation.Discard, 0));
        var cameroniansCtx = new EffectContext
        {
            Engine = cameroniansEngine, State = cameroniansState,
            Self = cameronians, Controller = Side.Left,
        };
        result = cameroniansEngine.Api.InvokeByName("isSecondOrderThisTurn", null,
            new object?[] { (int)Side.Left, null }, cameroniansCtx, out _);
        return result is false ? null : "Cameronians 在只有一张己方指令时应判假";
    }

    /// <summary>
    /// card_unit_10_5_cm_lefh 的蓝图逻辑是
    /// <c>GetOppositeSide() → GetLocationCardBySide(那个 side) → DamageCard(它, 2, …)</c>，
    /// 也就是「对敌方 HQ 造成 2 点」。
    ///
    /// 这条同时守两件事：
    /// 1. <c>OnPlayedFromHand</c> 程序确实会被执行
    /// 2. <c>GetOppositeSide</c> 能拿到正确的「敌方」——
    ///    它**没有入参**（实测 756 次调用参数表里只有 out 槽），
    ///    「我方」必须取自卡自己。旧实现从 args 读，会拿到未赋值的 out 槽 →
    ///    <c>Side.NotAvailable</c> → 整条链静默失效。
    /// </summary>
    private static string? LehfDeployment(CardDatabase db)
    {
        const string card = "card_unit_10_5_cm_lefh";
        if (db.Find(card) is null)
        {
            return $"卡库里没有 {card}";
        }

        if (KismetLibrary.Default?.FindProgram(card, "OnPlayedFromHand") is null)
        {
            return $"IR 里没有 {card} 的 OnPlayedFromHand（card-ir.json 是否加载？）";
        }

        var (engine, state) = EmptyBoard(db);
        var unit = state.CreateWithId(card, Side.Left, 2, CardLocation.HandLeft, 0);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        int before = state.HqDefense(Side.Right);
        if (!engine.PlayCard(unit))
        {
            return "打不出这张牌";
        }

        int after = state.HqDefense(Side.Right);
        if (after == before - 2)
        {
            return null;
        }

        // 失败时把现场打出来，省得再猜
        Console.WriteLine($"       诊断：左HQ={state.HqDefense(Side.Left)} 右HQ={after}");
        Console.WriteLine($"       场上: {string.Join("  ", state.Board(Side.Left).Concat(state.Board(Side.Right))
            .Select(x => $"{x.Name}#{x.CardId}({x.Location} {x.Attack}/{x.Defense})"))}");
        Console.WriteLine($"       lefh 自身: 位置={unit.Location} {unit.Attack}/{unit.Defense}");
        Console.WriteLine($"       未实现: {(state.UnimplementedCalls.Count == 0 ? "无" :
            string.Join(", ", state.UnimplementedCalls.Select(kv => $"{kv.Key}×{kv.Value}")))}");
        return $"敌方 HQ 期望 {before - 2}，实际 {after}（战吼没生效）";
    }

    /// <summary>
    /// 不依赖具体卡：直接断言 <c>GetOppositeSide</c> 派发出来的是
    /// 「卡主的对手」而不是 <c>NotAvailable</c>。
    /// </summary>
    private static string? OppositeSideSemantics(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var self = state.CreateWithId("card_unit_10_5_cm_lefh", Side.Right, 42, CardLocation.HandRight, 0);

        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = self,
            Controller = Side.Right,
        };

        // 传入空 args —— 字节码里这个调用本来就没有入参
        object? result = engine.Api.InvokeByName("GetOppositeSide", null, Array.Empty<object?>(), ctx, out bool handled);
        if (!handled)
        {
            return "GetOppositeSide 没有注册到派发表里";
        }

        if (result is not int raw)
        {
            return $"返回类型不是 int，而是 {result?.GetType().Name ?? "null"}";
        }

        var side = (Side)raw;
        if (side != Side.Left)
        {
            return $"右方卡片的对手应为 left(1)，实际 {side}({raw})";
        }

        return null;
    }

    /// <summary>
    /// <c>GetOpponentSide()</c> 是对局状态的零入参查询；验证它在两侧及
    /// <c>NotAvailable</c> 下都遵循统一的 <c>Side.Opposite()</c> 映射。
    /// </summary>
    private static string? OpponentSideSemantics(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        foreach (var (controller, expected) in new[]
        {
            (Side.Left, Side.Right),
            (Side.Right, Side.Left),
            (Side.NotAvailable, Side.NotAvailable),
        })
        {
            var ctx = new EffectContext
            {
                Engine = engine,
                State = state,
                Controller = controller,
            };

            object? result = engine.Api.InvokeByName(
                "GetOpponentSide", null, Array.Empty<object?>(), ctx, out bool handled);
            if (!handled)
            {
                return "GetOpponentSide 没有注册到派发表里";
            }

            if (result is not int raw)
            {
                return $"{controller} 返回类型不是 int，而是 {result?.GetType().Name ?? "null"}";
            }

            var actual = (Side)raw;
            if (actual != expected)
            {
                return $"{controller} 的对手应为 {expected}({(int)expected})，实际 {actual}({raw})";
            }
        }

        return null;
    }

    /// <summary>
    /// `GetLocationCardBySide(side)` → 该阵营的 HQ 卡。
    /// 入参在 index 2（前两个是 out 槽 `card` 与 `locationCardID`）。
    /// </summary>
    private static string? LocationCardLookup(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = state.ById(1),
            Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName(
            "GetLocationCardBySide", null, new object?[] { null, null, (int)Side.Right }, ctx, out bool handled);
        if (!handled)
        {
            return "没有注册到派发表里";
        }

        if (result is not object?[] outputs || outputs.Length < 2
            || outputs[0] is not CardInstance card)
        {
            return $"期望返回 [HQ 卡, HQ 卡 ID]，实际 {result?.GetType().Name ?? "null"}";
        }

        if (!card.IsHq || card.Owner != Side.Right)
        {
            return $"期望右方 HQ，实际 {card.Name} owner={card.Owner} isHq={card.IsHq}";
        }

        if (outputs[1] is not int cardId || cardId != card.CardId)
        {
            return $"期望第二个出参为 HQ 卡 ID {card.CardId}，实际 {outputs[1] ?? "null"}";
        }

        return null;
    }

    /// <summary>`DamageCard(hq, 2, ...)` 应当从 HQ 防御里扣 2。</summary>
    private static string? DamageHqDirectly(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var hq = state.Hq(Side.Right);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = state.ById(1),
            Controller = Side.Left,
        };

        int before = state.HqDefense(Side.Right);
        engine.Api.InvokeByName("DamageCard", null, new object?[] { hq, 2 }, ctx, out bool handled);
        if (!handled)
        {
            return "没有注册到派发表里";
        }

        int after = state.HqDefense(Side.Right);
        return after == before - 2 ? null : $"期望 {before - 2}，实际 {after}";
    }

    private static string? TurnScopedGameplayCounters(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = state.ById(1),
            Controller = Side.Left,
        };

        state.UpdateHQDamagedAmountThisTurn(Side.Right, 3);
        state.AddOperationKreditsSpentThisTurn(2);

        object? hqDamage = engine.Api.InvokeByName(
            "GetHQ_DamagedAmountThisTurnBySide", null,
            new object?[] { (int)Side.Right, null }, ctx, out bool hqHandled);
        object? operationSpend = engine.Api.InvokeByName(
            "GetOperationKreditsSpentThisTurn", null,
            new object?[] { null }, ctx, out bool spendHandled);
        if (!hqHandled || !spendHandled)
        {
            return "本回合计数查询没有注册到派发表里";
        }

        if (Convert.ToInt32(hqDamage) != 3 || Convert.ToInt32(operationSpend) != 2)
        {
            return $"计数查询错误：HQ={hqDamage ?? "null"}，行动费={operationSpend ?? "null"}";
        }

        engine.StartTurn(Side.Left, draw: false);
        return state.GetHQDamagedAmountThisTurn(Side.Right) == 0
               && state.OperationKreditsSpentThisTurn == 0
            ? null
            : $"开始回合未清零：HQ={state.GetHQDamagedAmountThisTurn(Side.Right)}，"
              + $"行动费={state.OperationKreditsSpentThisTurn}";
    }

    private static string? GetReducedDamage(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var seagull = state.CreateWithId("card_unit_seagull", Side.Left, 1500,
            CardLocation.BoardHqLeft, 0);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = seagull,
            Controller = Side.Left,
        };

        object? first = engine.Api.InvokeByName("GetReducedDamage", seagull,
            new object?[] { 7, null }, ctx, out bool handled);
        if (!handled || first is not int firstReduction || firstReduction != 4)
            return $"HQ 未受伤时应减免 4 点，实际 {first ?? "null"}";

        state.UpdateHQDamagedAmountThisTurn(Side.Left, 3);
        object? second = engine.Api.InvokeByName("GetReducedDamage", seagull,
            new object?[] { 7, null }, ctx, out _);
        if (second is not int secondReduction || secondReduction != 1)
            return $"HQ 已受伤 3 点时应只减免 1 点，实际 {second ?? "null"}";

        state.UpdateHQDamagedAmountThisTurn(Side.Left, 2);
        object? third = engine.Api.InvokeByName("GetReducedDamage", seagull,
            new object?[] { 7, null }, ctx, out _);
        if (third is not int thirdReduction || thirdReduction != 0)
            return $"HQ 已达到 4 点上限后应减免 0 点，实际 {third ?? "null"}";

        state.ResetTurnGameplayCounters();
        object? clamped = engine.Api.InvokeByName("GetReducedDamage", seagull,
            new object?[] { 2, null }, ctx, out _);
        return clamped is int clampedReduction && clampedReduction == 2
            ? null : $"输入伤害 2 时减免不能超过输入，实际 {clamped ?? "null"}";
    }

    /// <summary>
    /// `card_event_the_commonwealth`（英联邦，12 费英国指令）——
    /// 卡面：<c>Deal 20 damage to target HQ if your HQ has 30 or more defense. Draw 4 cards.</c>
    ///
    /// 伤害值来自 <c>GetPlayFromHandDamage</c>。它不是引擎函数，而是**卡自己的**
    /// 蓝图函数（独立 export），字节码就是：
    /// <code>
    /// GetLocationCardBySide(side)                       ← 己方 HQ
    /// getTotalDefense(那个 HQ)                          ← 读 HQ 防御
    /// GreaterEqual_IntInt(那个值, 30)
    /// SelectInt(20, 0, 上面那个 bool)  → damage
    /// </code>
    /// 见 <c>out/gpfd/commonwealth.bpasm</c> 的 <c>.export 4 "GetPlayFromHandDamage"</c>。
    ///
    /// 断言两段（正反都要，否则「恒返回 20」也能骗过正向）：
    /// 1. 己方 HQ 防御 = 30 → 目标 HQ 掉 20
    /// 2. 己方 HQ 防御 = 29 → 目标 HQ 一点不掉
    /// </summary>
    private static string? CommonwealthHqDamage(CardDatabase db)
    {
        const string card = "card_event_the_commonwealth";
        if (db.Find(card) is null)
        {
            return $"卡库里没有 {card}";
        }

        if (KismetLibrary.Default?.FindLocalProgram(card, "GetPlayFromHandDamage") is null)
        {
            return $"IR 里没有 {card} 的 GetPlayFromHandDamage 局部函数（card-ir.json 是否重新生成过？）";
        }

        // ⚠️ 第三列 `targetHq` 是**故意加进来**的：判据必须能区分「读的是**施法方**的 HQ」
        //    与「读的是**被指方**的 HQ」—— 这两者只要有一个能过，(30,20) 那条就都会绿。
        //    第 3 条把**被指方** HQ 抬到 35（≥30）、**施法方**压到 29 ⇒ 必须 0 点。
        //    若实现读错对象，这一条会掉 20 点（而不是 0），立刻暴露。
        //
        //    出处：`card-ir.json` → `card_event_the_commonwealth` 的
        //    `OnPlayedFromHand` 入口在 i=525，先 `jump i=90`，i=90 是
        //    `GetLocationCardBySide(…, side)`，那个 `side` 是**事件自己的 side**
        //    （= 施法方）；i=194 才 `GreaterEqual_IntInt(getTotalDefense(那张卡), 30)`。
        foreach ((int hqDefense, int targetHq, int expected) in
                 new[] { (30, 20, 20), (29, 20, 0), (29, 35, 0), (35, 10, 20) })
        {
            var (engine, state) = EmptyBoard(db);
            state.SetHqDefense(Side.Left, hqDefense);
            state.SetHqDefense(Side.Right, targetHq);
            state.SetKredits(Side.Left, 12);
            state.SetMaxKredits(Side.Left, 12);
            state.ActiveSide = Side.Left;

            var cw = state.CreateWithId(card, Side.Left, 2, CardLocation.HandLeft, 0);
            int before = state.HqDefense(Side.Right);
            if (!engine.PlayCard(cw, state.Hq(Side.Right)))
            {
                return $"施法方 HQ 防御 {hqDefense} 时打不出这张牌";
            }

            int after = state.HqDefense(Side.Right);
            if (after != before - expected)
            {
                return $"施法方 HQ {hqDefense} / 被指方 HQ {targetHq} 时，目标 HQ 应掉 {expected} 点"
                     + $"（{before} → {before - expected}），实际 {before} → {after}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        return null;
    }

    private static string? LandwehrOperationCredits(CardDatabase db)
    {
        const string landwehrName = "card_unit_182_landwehr";
        const string moverName = "card_unit_arado_ar_196";
        if (db.Find(landwehrName) is null || db.Find(moverName) is null)
        {
            return "卡库缺少 Landwehr 或移动测试单位";
        }

        // `GiveCredits` 没有事件载荷时必须是严格 no-op。
        {
            var (engine, state) = EmptyBoard(db);
            var landwehr = state.CreateWithId(landwehrName, Side.Left, 1500,
                CardLocation.BoardHqLeft, 1);
            var ctx = new EffectContext
            {
                Engine = engine,
                State = state,
                Self = landwehr,
                Controller = Side.Left,
            };
            state.SetKredits(Side.Left, 7);
            engine.Api.InvokeByName("GiveCredits", null, Array.Empty<object?>(), ctx, out bool handled);
            if (!handled || state.Kredits(Side.Left) != 7)
            {
                return "GiveCredits 缺少 kreditsSpent 事件载荷时不应返还 kredit";
            }
        }

        // Veteran Landwehr operating itself: pay once, then refund the same
        // effective operation cost once.
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 9);
            var landwehr = state.CreateWithId(landwehrName, Side.Left, 1501,
                CardLocation.BoardHqLeft, 1);
            landwehr.EnteredPlayOnTurn = state.Turn - 1;
            engine.Api.MakeVeteran(landwehr);
            int cost = landwehr.OperationCost;
            if (!engine.MoveUnit(landwehr, 0, out string reason))
            {
                return $"Veteran Landwehr 移动失败：{reason}";
            }

            if (state.Kredits(Side.Left) != 9)
            {
                return $"Veteran Landwehr 自己操作后应净不变，实际 kredit={state.Kredits(Side.Left)}（行动费={cost}）";
            }

            if (state.OperationKreditsSpentThisTurn != cost)
            {
                return $"行动费计数应只累计实际支出 {cost}，实际 {state.OperationKreditsSpentThisTurn}";
            }
        }

        // A friendly unit operating nearby must trigger Landwehr's
        // OnOtherCardOperationKreditsSpent path and refund that unit's cost.
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 9);
            var landwehr = state.CreateWithId(landwehrName, Side.Left, 1502,
                CardLocation.BoardHqLeft, 1);
            landwehr.EnteredPlayOnTurn = state.Turn - 1;
            engine.Api.MakeVeteran(landwehr);
            var mover = state.CreateWithId(moverName, Side.Left, 1503,
                CardLocation.BoardHqLeft, 2);
            mover.EnteredPlayOnTurn = state.Turn - 1;
            int cost = mover.OperationCost;
            if (!engine.MoveUnit(mover, 0, out string reason))
            {
                return $"友方单位移动失败：{reason}";
            }

            if (state.Kredits(Side.Left) != 9)
            {
                return $"Veteran Landwehr 未返还友方单位行动费 {cost}，实际 kredit={state.Kredits(Side.Left)}";
            }
        }

        // The attack path uses the same event pair as ordinary movement.
        {
            var (engine, state, landwehr) = GuardBoard(db, landwehrName, (moverName, 1));
            state.SetKredits(Side.Left, 9);
            engine.Api.MakeVeteran(landwehr);
            var defender = state.Cards(Side.Right, CardLocation.BoardHqRight)
                .First(card => card.Name == moverName);
            int cost = landwehr.OperationCost;
            if (!engine.Attack(landwehr, defender, out string reason))
            {
                return $"Veteran Landwehr 攻击失败：{reason}";
            }

            if (state.Kredits(Side.Left) != 9
                || state.OperationKreditsSpentThisTurn != cost)
            {
                return $"Veteran Landwehr 攻击返还/计数错误：kredit={state.Kredits(Side.Left)}，"
                     + $"行动费计数={state.OperationKreditsSpentThisTurn}，实际费用={cost}";
            }
        }

        // A non-veteran Landwehr counts the operation but must not refund it.
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 9);
            var landwehr = state.CreateWithId(landwehrName, Side.Left, 1504,
                CardLocation.BoardHqLeft, 1);
            landwehr.EnteredPlayOnTurn = state.Turn - 1;
            int cost = landwehr.OperationCost;
            if (!engine.MoveUnit(landwehr, 0, out string reason))
            {
                return $"普通 Landwehr 移动失败：{reason}";
            }

            if (state.Kredits(Side.Left) != 9 - cost)
            {
                return $"普通 Landwehr 不应返还行动费，实际 kredit={state.Kredits(Side.Left)}，期望 {9 - cost}";
            }

            if (state.OperationKreditsSpentThisTurn != cost)
            {
                return $"普通 Landwehr 行动费计数错误：实际 {state.OperationKreditsSpentThisTurn}，期望 {cost}";
            }
        }

        return null;
    }

    private static string? LandwehrOperationCountPromotion(CardDatabase db)
    {
        const string name = "card_unit_182_landwehr";
        if (db.Find(name) is null)
        {
            return $"卡库里缺 {name}";
        }

        var (engine, state) = EmptyBoard(db);
        var landwehr = PutOnBoard(state, name, Side.Left, 1510, 1);
        var context = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = landwehr,
            Controller = Side.Left,
        };

        for (int i = 1; i <= 2; i++)
        {
            engine.Api.InvokeByName("IncOpCountAndCheckVeteran", null,
                Array.Empty<object?>(), context, out bool handled);
            if (!handled || engine.Api.JsonGetInt(landwehr, "opCount") != i
                || landwehr.Keywords.Contains(Keyword.Veteran))
            {
                int actual = engine.Api.JsonGetInt(landwehr, "opCount");
                return $"第 {i} 次操作计数错误：opCount={actual}";
            }

            if (LastPersistedCardId(state) != landwehr.CardId)
            {
                return $"第 {i} 次操作应持久化 Landwehr 的 opCount";
            }
        }

        engine.Api.InvokeByName("IncOpCountAndCheckVeteran", null,
            Array.Empty<object?>(), context, out bool thirdHandled);
        if (!thirdHandled || engine.Api.JsonGetInt(landwehr, "opCount") != 0
            || !landwehr.Keywords.Contains(Keyword.Veteran))
        {
            return $"第三次操作后 Landwehr 应晋升 Veteran 并由 OnBecomingVeteran 清理 opCount，实际 opCount={engine.Api.JsonGetInt(landwehr, "opCount")}";
        }

        if (LastPersistedCardId(state) != landwehr.CardId)
        {
            return "晋升 Veteran 后清理 opCount 应持久化 Landwehr";
        }

        return null;
    }

    private static string? SecondMichiganGiveTwoKredits(CardDatabase db)
    {
        const string name = "card_unit_2nd_michigan";
        if (db.Find(name) is null)
        {
            return $"卡库里缺 {name}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 9);
        var michigan = state.CreateWithId(name, Side.Left, 1520,
            CardLocation.BoardHqLeft, 1);
        michigan.EnteredPlayOnTurn = state.Turn - 1;

        if (!engine.MoveUnit(michigan, 0, out string reason))
        {
            return $"2nd Michigan 操作失败：{reason}";
        }

        int expected = 9 - michigan.OperationCost + 2;
        if (state.Kredits(Side.Left) != expected)
        {
            return $"操作费达到 {michigan.OperationCost} 后应返还 2 kredit，"
                 + $"实际={state.Kredits(Side.Left)}，期望={expected}";
        }

        return null;
    }

    /// <summary>
    /// **回放发号：客户端回合号的复刻**（对局 `773639` 的 HQ 漂开根因）。
    ///
    /// ## 症状
    /// `773639` 的 `#74 t17 left` 与 `#75 t18 left` 是**紧挨着的两条同侧
    /// `XActionStartOfTurn`，中间没有任何 `XActionEndOfTurn`**。旧规则"数
    /// `StartOfTurn` 条数"从 #75 起把客户端回合号多算 **1** ⇒ 效果生成的卡号
    /// **整体偏 1000**：
    /// <code>
    ///   我们的 CREATE 流水（--rng-trace）：#18001 #20001 #22001（流星副本）
    ///                                      #26001..#26003 #28001/#28002
    ///   动作流里人类真正引用的：            #17001 #19001
    ///                                      #25001..#25003 #27001/#27002
    /// </code>
    /// ⇒ 人类 `#88 PC {"0":17001}` 引用的那张卡我们**根本没生成过**，只能走
    /// `ReplayRunner.ResolveCard` 的"按码现建"兜底 —— 建出来是一张**裸的 1/1 流星**，
    /// 而客户端那张是 `card_unit_meteor.OnAfterAttack` 翻倍出来的 **2/2**
    /// （下一张 `#19001` 是 4/4）。
    /// ⇒ `#89` 打右 HQ：客户端 19→**17**（2 点）、我们 19→**18**（1 点）；
    ///   `#98`：客户端 14→**10**（4 点）、我们 14→**13**（1 点）
    ///   ⇒ 从那一步起 HQ 校验和全程差，`773639` 的人类侧 ④ 由 **33 条 → 0 条**。
    ///
    /// ## 断言设计（必须能把两种规则分开）
    /// 合成两条同侧 `StartOfTurn`（不依赖任何真实回放文件，纯逻辑）：
    /// <list type="bullet">
    /// <item>正确（连续同侧去重）→ `ClientTurn == 1`</item>
    /// <item>旧实现（数条数）→ `ClientTurn == 2`</item>
    /// </list>
    /// 再加一条**反向**断言防"去重过头"：中间隔了一次 `EndOfTurn` 的同侧
    /// `StartOfTurn` 必须照算（→ 2）。
    /// </summary>
    private static string? ReplayClientTurnDedup(CardDatabase db)
    {
        KLink.Bot.Replay.ReplayData Make(params WireAction[] acts) => new()
        {
            MatchId = 1,
            Turns = 2,
            LeftPlayerId = 1,
            RightPlayerId = -9178,
            WinnerSide = "",
            ClientSide = Side.Left,
            Cards = new List<KLink.Bot.Replay.ReplayData.SnapshotCard>
            {
                new(1, "card_location_berlin", CardLocation.BoardHqLeft, 0, Side.Left, false, null),
                new(41, "card_location_berlin", CardLocation.BoardHqRight, 0, Side.Right, false, null),
            },
            Actions = acts,
        };

        WireAction Mark(int id, int turn, string type, string side) => new()
        {
            ActionType = type,
            PlayerId = side == "left" ? 1 : -9178,
            ActionId = id,
            TurnNumber = turn,
            ActionData = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["side"] = side,
                ["36"] = "20",
            },
        };

        // ---- ① 连续同侧 StartOfTurn（中间没有 EndOfTurn）= 重复标记 ----
        var r1 = new KLink.Bot.Replay.ReplayRunner(db)
            .Run(Make(Mark(1, 1, "XActionStartOfTurn", "left"),
                      Mark(2, 2, "XActionStartOfTurn", "left")), verbose: false);
        if (r1.ClientTurn != 1)
        {
            return $"连续同侧 StartOfTurn（中间无 EndOfTurn）只应算 **1** 个客户端回合，"
                 + $"实际 ClientTurn={r1.ClientTurn}"
                 + "（旧实现数条数得 2 ⇒ 生成卡号偏 1000，实测 773639 #74/#75）";
        }

        // ---- ② 中间隔了一次 EndOfTurn ⇒ 是真正的新回合，必须照算 ----
        var r2 = new KLink.Bot.Replay.ReplayRunner(db)
            .Run(Make(Mark(1, 1, "XActionStartOfTurn", "left"),
                      Mark(2, 1, "XActionEndOfTurn", "left"),
                      Mark(3, 3, "XActionStartOfTurn", "left")), verbose: false);
        if (r2.ClientTurn != 2)
        {
            return $"中间隔了一次 EndOfTurn 的同侧 StartOfTurn 必须照算（应为 2），"
                 + $"实际 ClientTurn={r2.ClientTurn} —— 去重过头了";
        }

        return null;
    }

    /// <summary>
    /// 508065 的 PAMS 生成卡在创建时是 #14001，但后续动作引用为 #83。
    /// 回放器应把 #83 解析为同一张已生成卡，而不是造一张裸占位卡；后续
    /// #107/#119 的攻击也必须继续命中这张卡。
    /// </summary>
    private static string? ReplayGeneratedCardAlias(CardDatabase db)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "out", "_server-replays"));
        string snapshot = Path.Combine(root, "replay-508065.json");
        string actions = Path.Combine(root, "replay-508065.actions.json");
        if (!File.Exists(snapshot) || !File.Exists(actions))
        {
            return $"缺少动态卡别名回归语料：{snapshot} / {actions}";
        }

        var report = new KLink.Bot.Replay.ReplayRunner(db)
        {
            IdentityCorrection = true,
            KreditSlotOnDuplicateStart = true,
        }.Run(KLink.Bot.Replay.ReplayData.Load(snapshot, actions), verbose: false);

        // GiveMobilizeBonus changes this historical line before #107/#127, so
        // those two stale client attacks are now correctly rejected; #119 still
        // proves that the generated-card alias remains usable.
        if (report.Steps.Count(s => s.Applied) != 138)
        {
            return $"508065 应应用 138 条动作，实际 {report.Steps.Count(s => s.Applied)}";
        }

        if (report.Unimplemented.Any(x => x.Key.StartsWith("<unresolved-cardid:", StringComparison.Ordinal)))
        {
            return "动态卡别名仍产生 unresolved-cardid 占位记录："
                + string.Join(", ", report.Unimplemented
                    .Where(x => x.Key.StartsWith("<unresolved-cardid:", StringComparison.Ordinal))
                    .Select(x => $"{x.Key}×{x.Value}"));
        }

        var commando = report.Steps.FirstOrDefault(s => s.ActionId == 119);
        if (commando is null || !commando.Applied)
        {
            return "#119 对动态生成的 No.43 Commando 攻击未成功应用";
        }

        return null;
    }

    /// <summary>
    /// **效果生成卡的发号口径** —— 蓝图 `BP_GameState_Battle::GenerateNextCardID(turnNumber, out id)`
    /// （`ref/kards-sim/KardsSim/Generated/_deps/BP_GameState_Battle.g.cs:1545`）的签名里
    /// **只有 `turnNumber` 与一个 out 槽，没有 side**：
    /// <code>
    /// IncrementCardsCreatedThisTurn()                     // cardsCreatedCountThisTurn++
    /// id = cardsCreatedCountThisTurn + turnNumber * 1000  // GetCurrentCardID(:76160)
    /// ResetCardsCreatedThisTurn()                         // 每回合归零(:135020)
    /// </code>
    /// 断言的是**中间状态**（每张卡实际拿到的号），而不是"连打 N 局看结果"：
    /// <list type="bullet">
    /// <item>同一回合内**双方共用**序号（Left 取 5001、Right 紧接着取 5002）——
    ///   历史实现按 side 分号段（人类走 `1000×回合`、bot 走顺序号 81/82/…），
    ///   客户端在本地执行我们的效果时用的是**它自己**的分配器 ⇒ 认不出我们发的号
    ///   ⇒ **虚空部署**（真人实测对局 458321 的 `card_unit_1st_airborne#81/#82`）。</item>
    /// <item>换回合必须归零（⇒ 6001，而不是接着 5004）。</item>
    /// <item>走 <see cref="GameState.Create"/>（效果生成的唯一入口，默认
    ///   <c>sequentialId: false</c>）时同样是这条规则，而不是顺序号段。</item>
    /// </list>
    /// </summary>
    private static string? GeneratedCardIdRule(CardDatabase db)
    {
        var (_, state) = EmptyBoard(db);

        // 回放路径把「客户端发号用的回合号」塞进 ClientIdTurnOverride（默认取 State.Turn）。
        state.ClientIdTurnOverride = 5;

        int a = state.NextCardId(Side.Left);
        int b = state.NextCardId(Side.Right);
        int c = state.NextCardId(Side.Left);
        if (a != 5001 || b != 5002 || c != 5003)
        {
            return $"回合 5 的生成号应为 5001/5002/5003（**全局**一个计数器、无 side），实际 {a}/{b}/{c}";
        }

        // 换回合 ⇒ ResetCardsCreatedThisTurn ⇒ 序号从 1 重新开始
        state.ClientIdTurnOverride = 6;
        int d = state.NextCardId(Side.Left);
        if (d != 6001)
        {
            return $"换到回合 6 后序号必须归零（应 6001），实际 {d} —— 计数器没按回合清零";
        }

        // 效果生成的入口 `Create(...)` 也必须走这条规则：
        // 用顺序号会发成左 2.. / 右 42..，与客户端完全是两套号。
        state.ClientIdTurnOverride = 9;
        var spawned = state.Create("card_unit_10_5_cm_lefh", Side.Right, CardLocation.HandRight,
                                   state.NextLocationNumber(Side.Right, CardLocation.HandRight));
        if (spawned.CardId != 9001)
        {
            return $"效果生成卡走 `Create(...)` 应拿 9001，实际 {spawned.CardId}"
                 + "（顺序号段 = 客户端不认，会变成虚空部署）";
        }

        return null;
    }

    /// <summary>
    /// **发号的「避让跳号」必须留痕**。
    ///
    /// 客户端的分配器就是 `cardsCreatedCountThisTurn + turnNumber*mult`，**撞了也不避让**；
    /// 我们多一个避让循环（否则回放路径上回合 0 发出来的号 `id = n` 会撞上快照的 1..80，
    /// `CreateWithId` 直接抛「cardID 已被占用」）。这个偏差本身无害，但它是
    /// 「我们内存里多了一张本该没有的卡」的**放大器** ——
    /// `ReplayRunner` 的兜底占位卡（`<unresolved-cardid:...>`）就是头号来源。
    /// 以前跳号是静默的：号段悄悄偏开，审计里查不到任何痕迹。这条把它钉住。
    /// </summary>
    private static string? CardIdCollisionSkipIsObservable(CardDatabase db)
    {
        var (_, state) = EmptyBoard(db);
        state.ClientIdTurnOverride = 7;

        // 先占掉 7001（模拟"我们这边凭空多了一张 7001"）
        state.CreateWithId("card_unit_10_5_cm_lefh", Side.Right, 7001, CardLocation.DeckRight, 0);

        int id = state.NextCardId(Side.Left);
        if (id != 7002)
        {
            return $"7001 已被占用时下一个生成号应为 7002，实际 {id}";
        }

        string? key = state.UnimplementedCalls.Keys
            .FirstOrDefault(k => k.StartsWith("<cardid-collision-skip:", StringComparison.Ordinal));
        if (key is null)
        {
            return "发生了避让跳号，但 UnimplementedCalls 里没有 <cardid-collision-skip:...> 信号"
                 + " —— 号段偏移会静默发生，审计看不见";
        }

        return null;
    }

    /// <summary>
    /// `card_unit_meteor` —— 卡面「**Remove after this unit attacks. Add a copy to your
    /// deck with double attack and defense.**」
    ///
    /// 为什么这条值得单独守（**它是 HQ 校验和漂开的真实来源**）：
    /// 对局 `773639` 里人类连打三张流星打右 HQ：
    /// <code>
    ///   #77 t18 用 #14（1/1）打  → 客户端右 HQ 20→19（1 点）
    ///   #89 t20 用 #17001 打    → 客户端 19→**17**（2 点）   ← 副本是 2/2
    ///   #98 t22 用 #19001 打    → 客户端 14→**10**（4 点）   ← 副本是 4/4
    /// </code>
    /// 判据：`ExpectedHq`（动作流里客户端写的「行动方对手 HQ」）在 #89 掉 2、#98 掉 4；
    /// 而我们的内核在 `out/_hq773639.log` 里三次都打 **1** 点（副本一直是 1/1）。
    /// 对局 `389594` 同一条链是对的（`#7001` 2/2、`#15001` 4/4）—— 所以这不是
    /// 「没实现」，而是**在某条路径上翻倍没生效**，必须用最小局面钉死。
    ///
    /// 蓝图出处：`card-ir.json` → `card_unit_meteor` 的 `OnAfterAttack`（入口 i=745）：
    /// <code>
    /// i=745  IsLocatedOnBoard → popFlowIfNot          ; 不在场上就什么都不做
    /// i=774  getTotalAttack()  → attTotal             ; ★ 翻倍的基数取自**这张卡自己**
    /// i=820  getTotalDefense() → defTotal
    /// i=866  RemoveCardFromBoard(self)
    /// i=929  SpawnCardInDeckBySide(side, "card_unit_meteor", …, out spawnedCardIDs)
    /// i=1017 jump i=717  →  loopCounter=0 → i=577 Array_Length(spawnedCardIDs)
    /// i=674  popFlowIfNot(loopCounter &lt; len)
    /// i=47   Multiply_IntInt(attTotal, 2) → ChangeAttack(副本, 那个值, changeType=2)
    /// i=277  Multiply_IntInt(defTotal, 2) → ChangeDefense(副本, 那个值, changeType=2)
    /// </code>
    /// 断言两段（链式，才分得清「没生成副本」与「生成了但没翻倍」）：
    /// 1. 1/1 流星打完 → 自己离场、牌库出现一张副本且必须是 **2/2**
    /// 2. 那张 2/2 再打一次 → 牌库再出现一张 **4/4**
    /// </summary>
    private static string? MeteorDoublesDeckCopy(CardDatabase db)
    {
        const string meteor = "card_unit_meteor";   // 1/1
        if (db.Find(meteor) is null)
        {
            return $"卡库里没有 {meteor}";
        }

        if (KismetLibrary.Default?.FindProgram(meteor, "OnAfterAttack") is null)
        {
            return $"IR 里没有 {meteor} 的 OnAfterAttack（card-ir.json 是否加载？）";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);

        // ---- 第 1 段：1/1 打 1 点，牌库副本必须 2/2 ----
        var m = state.CreateWithId(meteor, Side.Left, 14, CardLocation.BoardHqLeft, 1);
        m.EnteredPlayOnTurn = state.Turn - 1;
        int hq0 = state.HqDefense(Side.Right);
        if (!engine.Attack(m, state.Hq(Side.Right), out string why0))
        {
            return $"1/1 的流星攻击敌方 HQ 应该成功，实际被拒：{why0}";
        }

        if (state.HqDefense(Side.Right) != hq0 - 1)
        {
            return $"1/1 的流星应打 1 点（{hq0} → {hq0 - 1}），实际 → {state.HqDefense(Side.Right)}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (m.Location.IsBoard())
        {
            return "流星攻击后应离场（卡面「Remove after this unit attacks」）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        var copy = state.Deck(Side.Left).FirstOrDefault(x => x.Definition.Name == meteor);
        if (copy is null)
        {
            return "流星攻击后牌库里应该出现一张副本（蓝图 i=929 SpawnCardInDeckBySide）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (copy.Attack != 2 || copy.Defense != 2)
        {
            return $"牌库副本的攻/防应是 2/2（蓝图 i=47/i=277 的 attTotal*2 / defTotal*2），"
                 + $"实际 {copy.Attack}/{copy.Defense}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        // ---- 第 2 段：2/2 再打一次，第二张副本必须 4/4 ----
        state.Move(copy, CardLocation.BoardHqLeft);
        copy.EnteredPlayOnTurn = state.Turn - 1;
        copy.HasAttackedThisTurn = false;
        int hq1 = state.HqDefense(Side.Right);
        if (!engine.Attack(copy, state.Hq(Side.Right), out string why1))
        {
            return $"2/2 的副本攻击敌方 HQ 应该成功，实际被拒：{why1}";
        }

        if (state.HqDefense(Side.Right) != hq1 - 2)
        {
            return $"2/2 的副本应打 2 点（{hq1} → {hq1 - 2}），实际 → {state.HqDefense(Side.Right)}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        var copy2 = state.Deck(Side.Left).FirstOrDefault(x => x.Definition.Name == meteor);
        if (copy2 is null)
        {
            return "2/2 副本攻击后牌库里应该再出现一张副本"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (copy2.Attack != 4 || copy2.Defense != 4)
        {
            return $"第二张副本的攻/防应是 4/4，实际 {copy2.Attack}/{copy2.Defense}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        return null;
    }

    // ==================================================================
    //  getTotalAttack / getTotalDefense 的**接收者**（2026-09-27）
    // ==================================================================

    /// <summary>
    /// `card_unit_red_bull` —— 卡面 "double the attack of this unit."
    ///
    /// 它的 IR（`OnStartOfTurn`，i=10→128）逐条是：
    /// <code>
    /// IsSideActive(side)                                  ← side 是卡的实例变量
    /// IsLocatedOnBoard()                                  ← 隐式 self（IR 里**没有** recv）
    /// BooleanAND(上两个)
    /// jumpIfNot → return
    /// getTotalAttack()                                    ← 隐式 self
    /// ChangeAttack(self, cardID, 上面那个值, 1, false)     ← changeType=1 = 加算 ⇒ 翻倍
    /// </code>
    ///
    /// 这条守的是 <c>getTotalAttack</c> 的**接收者**：它是 `UBaseCardObject` 的原生成员函数
    /// （`BaseCardObject.h:985` `void getTotalAttack(int32& totalAttack);`，
    /// 与 `getTotalDefense`(:982) 同组声明、同为 `BlueprintPure` 成员函数、同样零形式参数），
    /// 语义是「**接收者那张卡自己的**总攻击」。
    ///
    /// 断言设计成**能把两种实现分开**：场上放红牛（1 攻）+ 一支 5 攻步兵。
    /// <list type="bullet">
    /// <item>正确（读接收者自己）→ 红牛 1 → 2</item>
    /// <item>旧实现（己方场面之和）→ 红牛 1 → 1 + (1+5) = 7</item>
    /// <item>返回常量的桩 → 任何值都不会是 2（0 会得到 1）</item>
    /// </list>
    /// 另外断言那支步兵自己没被改动 —— 防止效果打到别人身上。
    /// </summary>
    private static string? RedBullDoublesOwnAttack(CardDatabase db)
    {
        const string bull = "card_unit_red_bull";          // 1/6
        const string other = "card_unit_1005th_rifles";    // 5/3，且没有 OnStartOfTurn
        if (db.Find(bull) is null || db.Find(other) is null)
        {
            return $"卡库里缺 {bull} 或 {other}";
        }

        if (KismetLibrary.Default?.FindProgram(bull, "OnStartOfTurn") is null)
        {
            return $"IR 里没有 {bull} 的 OnStartOfTurn";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;

        var bullCard = state.CreateWithId(bull, Side.Left, 2, CardLocation.BoardFrontline, 0);
        var otherCard = state.CreateWithId(other, Side.Left, 3, CardLocation.BoardFrontline, 1);
        int own = bullCard.Attack;
        int otherAtk = otherCard.Attack;

        if (own <= 0 || otherAtk <= 0 || own == otherAtk)
        {
            return $"前置不成立：红牛 {own} 攻 / 另一支 {otherAtk} 攻"
                 + "（必须都 >0 且不相等，否则「读自己」和「读场面之和」给出同一个数）";
        }

        engine.StartTurn(Side.Left, draw: false);

        int wantBull = own * 2;                       // 「翻倍」= 加上自己的攻击力
        if (bullCard.Attack != wantBull)
        {
            return $"回合开始后红牛攻击力应为 {own} → {wantBull}（翻倍自己），"
                 + $"实际 {own} → {bullCard.Attack}"
                 + $"（己方场面之和是 {own + otherAtk}；若得到 {own + own + otherAtk} "
                 + "说明 getTotalAttack 读的是场面总和，丢掉了接收者）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (otherCard.Attack != otherAtk)
        {
            return $"{other} 不该被改动：{otherAtk} → {otherCard.Attack}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        return null;
    }

    /// <summary>
    /// `card_event_patriotic_zeal` —— 卡面 "Double the attack and defense of a friendly unit."
    ///
    /// 它的 IR（`OnPlayedFromHand`，i=10→304）逐条是：
    /// <code>
    /// tempCard = K2Node_Event_targetCard
    /// if (IsValid(tempCard)):
    ///     getTotalAttack(tempCard)   ← **显式接收者**（IR 的 recv = tempCard）
    ///     ChangeAttack(tempCard, cardID, 那个值, 1, false)
    ///     getTotalDefense(tempCard)  ← 同样显式接收者
    ///     ChangeDefense(tempCard, cardID, 那个值, 1, false)
    /// </code>
    ///
    /// 这条守的是**显式接收者**那一路（`SelfArg` 的第 1 条分支），
    /// 和 <see cref="RedBullDoublesOwnAttack"/> 的隐式 self 互为补充 ——
    /// 两条路径在 `SelfArg` 里走的是不同的分支，一条过不代表另一条过。
    ///
    /// 断言：目标 5/3，己方场上另有红牛 1/6（场面之和 = 6）。
    /// <list type="bullet">
    /// <item>正确 → 目标 10/6</item>
    /// <item>旧实现 → 目标 5+(5+1)=11 / 3+(5+1)=9</item>
    /// </list>
    /// </summary>
    private static string? PatrioticZealDoublesTarget(CardDatabase db)
    {
        const string order = "card_event_patriotic_zeal";  // 7 费美国指令
        const string target = "card_unit_1005th_rifles";   // 5/3
        const string bystander = "card_unit_red_bull";     // 1/6（只有 OnStartOfTurn，不受打牌影响）
        if (db.Find(order) is null || db.Find(target) is null || db.Find(bystander) is null)
        {
            return $"卡库里缺 {order} / {target} / {bystander}";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var victim = state.CreateWithId(target, Side.Left, 2, CardLocation.BoardFrontline, 0);
        var other = state.CreateWithId(bystander, Side.Left, 3, CardLocation.BoardFrontline, 1);
        var orderCard = state.CreateWithId(order, Side.Left, 4, CardLocation.HandLeft, 0);

        int atk = victim.Attack, def = victim.Defense;
        int boardSum = atk + other.Attack;

        if (!engine.PlayCard(orderCard, victim))
        {
            return $"{order} 打不出来（kredits={state.Kredits(Side.Left)}）";
        }

        if (victim.Attack != atk * 2 || victim.Defense != def * 2)
        {
            return $"目标应翻倍成 {atk * 2}/{def * 2}，实际 {victim.Attack}/{victim.Defense}"
                 + $"（旧实现会得到 {atk + boardSum}/{def + boardSum}，"
                 + "即把「己方场面之和」当成了目标自己的攻防）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (other.Attack != 1 || other.Defense != 6)
        {
            return $"旁观的红牛不该被改动：应为 1/6，实际 {other.Attack}/{other.Defense}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        return null;
    }

    // ==================================================================
    //  Develop 族：GetChooseSpawnCards + 生成（2026-09-27）
    // ==================================================================

    /// <summary>
    /// `card_event_pams` —— 卡面 "Develop a British order costing 4 or less.
    /// Add it to your deck with a cost of 0."
    ///
    /// 它的 `GetChooseSpawnCards` 是**卡的局部函数**（独立 export，不在 ubergraph 里），
    /// 字节码原文见 `out/gcs/pams.bpasm` 第 256–457 行，逐条是：
    /// <code>
    /// PossibleCards.Clear()
    /// isReserved = self.IsCardReserved()
    /// all = self.GetAllActiveStaticCards(false, isReserved)
    /// for i in 0..all.Length-1:
    ///     card = all[i]
    ///     if (card.getTotalKreditCost() &lt; 5) &amp;&amp; card.IsOrder() &amp;&amp; (card.faction == 2):
    ///         PossibleCards.Add(card)
    /// cards = PossibleCards ; markAsSeen = false ; keepOrder = false
    /// </code>
    /// （`byteconst 2` = `EFactionEnum::Britain`，见 `CardDefinition.FactionId` 的注释。）
    ///
    /// 这条同时守三件事，每一件单独坏掉都会让候选表变成空的、而**不会报错**：
    /// 1. `card-ir.json` 里真的有这个 `locals`（生成器的 `LOCAL_FUNCTIONS`）；
    /// 2. `GetAllActiveStaticCards` 返回的是**能被判据读的卡对象**
    ///    （`faction` / `IsOrder` / `getTotalKreditCost` 都要求实参是 `CardInstance`）；
    /// 3. 累加循环真的攒起来了 —— `Array_Add(成员数组, x)` 必须是**原地**追加，
    ///    返回拷贝的话 `PossibleCards` 永远是空的（旧实现就是这样）。
    /// </summary>
    private static string? PamsDevelopCandidates(CardDatabase db)
    {
        const string pams = "card_event_pams";
        if (db.Find(pams) is null)
        {
            return $"卡库里没有 {pams}";
        }

        if (KismetLibrary.Default?.FindLocalProgram(pams, "GetChooseSpawnCards") is null)
        {
            return $"IR 里没有 {pams} 的 GetChooseSpawnCards 局部函数（card-ir.json 是否重新生成过？）";
        }

        var (engine, state) = EmptyBoard(db);
        var card = state.CreateWithId(pams, Side.Left, 2, CardLocation.HandLeft, 0);

        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Controller = Side.Left,
            Self = card,
        };

        var list = engine.Api.GetChooseSpawnCards(ctx, card, out bool markAsSeen, out bool keepOrder);

        if (list.Count == 0)
        {
            return "候选表是空的（判据一个都没过，或累加循环没攒起来）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (markAsSeen || keepOrder)
        {
            return $"markAsSeen / keepOrder 都应为 false，实际 {markAsSeen} / {keepOrder}";
        }

        foreach (var t in list)
        {
            if (t.Definition.FactionId != 2)
            {
                return $"候选 {t.Name} 的阵营是 {t.Definition.FactionId}，pams 的判据要求 2（Britain）";
            }

            if (!t.Definition.IsOrder)
            {
                return $"候选 {t.Name} 不是指令（type={t.Definition.Type}）";
            }

            if (t.KreditCost >= 5)
            {
                return $"候选 {t.Name} 的总费是 {t.KreditCost}，判据要求 < 5";
            }
        }

        // ---- 反向断言：过滤必须真的在起作用 ----
        // 没有这一段，"候选表 = 整个卡库" 也能通过上面所有检查。
        var britOrders = db.All.Where(d => d.FactionId == 2 && d.IsOrder).ToList();
        var excluded = britOrders.Where(d => d.Kredits >= 5).ToList();
        if (excluded.Count == 0)
        {
            return "前置不成立：英国指令里没有 ≥5 费的，测不出 '<5' 这条过滤";
        }

        foreach (var d in excluded)
        {
            if (list.Any(t => t.Name == d.Name))
            {
                return $"{d.Name}（{d.Kredits} 费英国指令）不该在候选表里（判据是 <5）";
            }
        }

        // 非英国指令也必须在表外（否则阵营那一条恒真）
        var foreignOrder = db.All.FirstOrDefault(d => d.FactionId != 2 && d.IsOrder && d.Kredits < 5);
        if (foreignOrder is null)
        {
            return "前置不成立：卡库里没有 <5 费的非英国指令，测不出阵营过滤";
        }

        if (list.Any(t => t.Name == foreignOrder.Name))
        {
            return $"{foreignOrder.Name}（阵营 {foreignOrder.FactionId}）不该在候选表里";
        }

        // 英国单位也必须在表外（否则 IsOrder 那一条恒真）
        var britUnit = db.All.FirstOrDefault(d => d.FactionId == 2 && d.IsUnit && d.Kredits < 5);
        if (britUnit is not null && list.Any(t => t.Name == britUnit.Name))
        {
            return $"{britUnit.Name} 是单位，不该在候选表里";
        }

        return null;
    }

    private static string? MainNationForSide(CardDatabase db)
    {
        const string selectingName = "card_event_pilot_escape";
        if (db.Find(selectingName) is null || db.Find("card_location_london") is null
            || db.Find("card_location_berlin") is null)
        {
            return "卡库缺少 Pilot Escape 或测试用 HQ 定义";
        }

        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 1);
        var state = engine.State;
        state.CreateWithId("card_location_london", Side.Left, 1, CardLocation.BoardHqLeft, 0);
        state.CreateWithId("card_location_berlin", Side.Right, 41, CardLocation.BoardHqRight, 0);
        state.SetHqDefense(Side.Left, MatchEngine.InitialHqDefense);
        state.SetHqDefense(Side.Right, MatchEngine.InitialHqDefense);

        var selecting = state.CreateWithId(selectingName, Side.Left, 2, CardLocation.HandLeft, 0);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Controller = Side.Left,
            Self = selecting,
        };

        object? left = engine.Api.InvokeByName("GetMainNationForSide", selecting,
            new object?[] { (int)Side.Left, null }, ctx, out bool leftHandled);
        object? right = engine.Api.InvokeByName("GetMainNationForSide", selecting,
            new object?[] { (int)Side.Right, null }, ctx, out bool rightHandled);
        object? invalid = engine.Api.InvokeByName("GetMainNationForSide", selecting,
            new object?[] { (int)Side.NotAvailable, null }, ctx, out bool invalidHandled);
        if (!leftHandled || !rightHandled || !invalidHandled
            || left is not int leftFaction || right is not int rightFaction
            || leftFaction != 2 || rightFaction != 1 || !Equals(invalid, 0))
        {
            return $"主阵营读取错误：left={left ?? "null"}, right={right ?? "null"}, "
                 + $"invalid={invalid ?? "null"}, handled={leftHandled}/{rightHandled}/{invalidHandled}";
        }

        if (KismetLibrary.Default?.FindLocalProgram(selectingName, "GetChooseSpawnCards") is null)
        {
            return $"IR 里没有 {selectingName} 的 GetChooseSpawnCards 局部函数";
        }

        var candidates = engine.Api.GetChooseSpawnCards(ctx, selecting,
            out bool markAsSeen, out bool keepOrder);
        if (candidates.Count == 0)
        {
            return "Pilot Escape 候选为空，主阵营过滤链没有产生空军候选";
        }

        if (candidates.Any(c => c.Definition.FactionId != leftFaction
            || c.Definition.Type is not ("fighter" or "bomber")))
        {
            return "Pilot Escape 候选包含非左方主阵营或非空军卡";
        }

        if (candidates.Any(c => c.Definition.FactionId == rightFaction
            && (c.Definition.Type is "fighter" or "bomber")))
        {
            return "Pilot Escape 候选错误包含右方主阵营空军";
        }

        return markAsSeen || keepOrder
            ? $"Pilot Escape 的 markAsSeen/keepOrder 应为 false，实际 {markAsSeen}/{keepOrder}"
            : null;
    }

    /// <summary>
    /// 走完 <c>CS</c> 答复的**整条链**（这是回放里 45 条 <c>CS</c> 落不了地的那一步）。
    ///
    /// 蓝图出处：`BP_OnlineMatch.OpponentActionsCardToDrawSelected`
    /// （`klink bot/decompiled/BP_OnlineMatch.live.all-functions.json`，bytecode 26–53）：
    /// <code>
    /// tmpSourceCard   = GetCardFromID(cardTriggeringDraw)
    /// tmpTargetCardID = CreateCard(tmpSourceCard.side, 卡名, 手牌位置, …)
    /// tmpSourceCard.OnHandTargetSelected(tmpTargetCardID, cardTriggeringDraw)
    /// </code>
    /// 之后由那张卡自己的 <c>OnHandTargetSelected</c> 决定最终去向 —— pams 是
    /// 「塞回牌库随机位置 + 把费用设成 0」（卡面 "Add it to your deck with a cost of 0."）。
    ///
    /// 断言两件事：
    /// 1. 选中的模板卡**真的变成了对局里的一张新卡**（`CardId != 0`）；
    /// 2. 它落在**牌库**里 —— 说明 pams 的 `OnHandTargetSelected` 真的跑到了
    ///    `MoveCardToTopOfOwnersDeck`（这一条同时守住了 `K2Node_Event_instigatorID`
    ///    的解析：读错的话那句 `if (instigatorID == cardID)` 恒假，
    ///    整段会被 `popFlowIfNot` 弹栈跳过，卡就留在手牌里）。
    ///
    /// ✅ 后半段「把费用设成 0」**已于 2026-10-02 修好并单独断言** ——
    /// 缺口在 `GetDeckByside`：蓝图出参是 <c>TArray&lt;int&gt; deckCardIDs</c>（卡 **ID**），
    /// 内核原先实现成卡**实例**，于是 `EqualEqual_IntInt(deck[i], cardDeveloped)`
    /// 变成 `ToInt(卡实例)=0 == 卡的 ID` ⇒ 恒假 ⇒ 那个
    /// `ChangeKreditCost(卡, 来源, 0, changeType=2)` 一次都不执行。
    /// 现在由两条独立用例守着：
    /// <see cref="GetDeckBySideReturnsCardIds"/>（出参形状 + `Array_Contains`）
    /// 与 <see cref="PamsDevelopedCardCostZero"/>（端到端 0 费）。
    /// </summary>
    private static string? PamsDevelopEndToEnd(CardDatabase db)
    {
        const string pams = "card_event_pams";
        const string chosen = "card_event_fog_of_war";   // 英国指令，费用 <5
        if (db.Find(pams) is null || db.Find(chosen) is null)
        {
            return $"卡库里缺 {pams} 或 {chosen}";
        }

        if (KismetLibrary.Default?.FindLocalProgram(pams, "GetChooseSpawnCards") is null)
        {
            return $"IR 里没有 {pams} 的 GetChooseSpawnCards 局部函数";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var card = state.CreateWithId(pams, Side.Left, 2, CardLocation.HandLeft, 0);
        int deckBefore = state.Deck(Side.Left).Count;

        // 模拟"玩家选了 card_event_fog_of_war"：回放里这一答复来自 `CS` 动作的卡组码，
        // 这里直接给一个带正确卡名的**卡池模板**（和回放路径交给内核的东西同形）。
        engine.PickCardToDraw = (selecting, fromTopOfDeck, _) =>
            fromTopOfDeck ? null : KLink.Bot.Effects.CardApi.TemplateInstance(db.Require(chosen), Side.Left);

        if (!engine.PlayCard(card))
        {
            return $"pams 打不出来（kredits={state.Kredits(Side.Left)}）";
        }

        var developed = state.Deck(Side.Left).FirstOrDefault(x => x.Name == chosen);
        if (developed is null)
        {
            return $"develop 出来的 {chosen} 不在牌库里"
                 + $"（牌库 {deckBefore} → {state.Deck(Side.Left).Count}，"
                 + $"手牌 {state.Hand(Side.Left).Count}）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (developed.CardId == 0)
        {
            return "生成出来的卡 cardId 是 0 —— 说明它还是卡池模板，没真的进对局";
        }

        if (state.Hand(Side.Left).Any(x => ReferenceEquals(x, developed)))
        {
            return $"{chosen} 还在手牌里 —— pams 的 OnHandTargetSelected 没跑完";
        }

        return null;
    }

    /// <summary>
    /// `ExecuteEndOfTurnEvents` 的批次顺序、抑制门、事件回合号和临时 buff
    /// 都必须在同一次队列结算中保持蓝图语义。
    /// </summary>
    private static string? EndOfTurnQueueSemantics(CardDatabase db)
    {
        const string cardName = "card_event_national_fire_service";
        if (db.Find(cardName) is null)
        {
            return $"卡库里没有 {cardName}";
        }

        var (engine, state) = EmptyBoard(db);
        var ordinary = state.CreateWithId(cardName, Side.Left, 101, CardLocation.BoardHqLeft, 1);
        var end1 = state.CreateWithId(cardName, Side.Left, 102, CardLocation.BoardHqLeft, 2);
        var end2 = state.CreateWithId(cardName, Side.Left, 103, CardLocation.BoardHqLeft, 3);
        var suppressed = state.CreateWithId(cardName, Side.Left, 104, CardLocation.BoardHqLeft, 4);
        end1.CustomJson["customName1"] = "endofturn1";
        end2.CustomJson["customName1"] = "endofturn2";
        suppressed.CustomJson["customName1"] = "endofturn1";
        suppressed.Keywords.Add(Keyword.Suppressed);

        int baseAttack = ordinary.Attack;
        engine.Api.AddAttackUntilEndOfTurn(ordinary, ordinary, 3);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        engine.Api.ExecuteEndOfTurnEvents();

        int[] observed = trace
            .Where(x => x.StartsWith("OnEndOfTurn → ", StringComparison.Ordinal))
            .Select(x =>
            {
                int marker = x.IndexOf('#');
                int end = x.IndexOf('（', marker);
                return marker >= 0 && end > marker
                    && int.TryParse(x[(marker + 1)..end], out int id) ? id : -1;
            })
            .ToArray();
        if (!observed.SequenceEqual(new[] { ordinary.CardId, end1.CardId, end2.CardId }))
        {
            return $"回合结束队列顺序错误：{string.Join(",", observed)}";
        }

        if (trace.Any(x => x.Contains($"#{suppressed.CardId}（", StringComparison.Ordinal))
            || !trace.All(x => x.Contains("turn=1", StringComparison.Ordinal)))
        {
            return "抑制门未生效，或 OnEndOfTurn 没有使用递增前的回合号";
        }

        if (ordinary.Attack != baseAttack)
        {
            return $"所有回合结束批次完成后才清理临时 buff：{ordinary.Attack} != {baseAttack}";
        }

        return null;
    }

    private static string? ForceEndTurnDeferred(CardDatabase db)
    {
        const string cardName = "card_event_calm_before_the_storm";
        if (db.Find(cardName) is null)
        {
            return $"卡库里没有 {cardName}";
        }

        var (engine, state) = EmptyBoard(db);
        var card = state.CreateWithId(cardName, Side.Left, 501, CardLocation.HandLeft, 0);
        int startingTurn = state.Turn;
        if (!engine.PlayCardDirectlyFromHand(card, toFrontline: false,
                instigatorID: card.CardId, locationNumber: -1))
        {
            return "Calm Before the Storm 直接出牌失败";
        }

        var hits = state.ActionLog
            .SelectMany(x => x.SubActions)
            .Where(x => x.Name == "ZActionForceEndTurn")
            .ToList();
        if (hits.Count != 1)
        {
            return $"ForceEndTurn 应只记录一次，实际 {hits.Count} 次";
        }

        var instigator = hits[0].Values.FirstOrDefault(x => x.Name == "instigatorID");
        if (instigator is null || instigator.Value != card.CardId)
        {
            return $"ForceEndTurn instigatorID 错误：{instigator?.Value.ToString() ?? "缺失"}";
        }

        if (state.Turn != startingTurn + 1 || state.ActiveSide != Side.Right)
        {
            return $"ForceEndTurn 未准确结束当前回合：turn={state.Turn}, active={state.ActiveSide}";
        }

        return null;
    }

    /// <summary>
    /// `AddToTriggerQueue` 使用蓝图的 FIFO Array_Add 语义，且延迟执行时
    /// 把原始卡对象的 `currentTarget` 传给 OnPlayedFromHand。
    /// </summary>
    private static string? TriggerQueueFifoAndTarget(CardDatabase db)
    {
        const string orderName = "card_event_air_corps_ferrying";
        if (db.Find(orderName) is null)
        {
            return $"卡库里没有 {orderName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 1);
        state.SetMaxKredits(Side.Left, 1);
        var targetA = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var targetB = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        var first = state.CreateWithId(orderName, Side.Left, 30, CardLocation.HandLeft, 0);
        var second = state.CreateWithId(orderName, Side.Left, 31, CardLocation.HandLeft, 1);
        first.CurrentTarget = targetA;
        second.CurrentTarget = targetB;

        var dispatchContext = new EffectContext
        {
            Engine = engine, State = state, Self = first, Controller = Side.Left,
        };
        engine.Api.InvokeByName("AddToTriggerQueue", first,
            new object?[] { second }, dispatchContext, out bool dispatchHandled);
        if (!dispatchHandled || state.TriggerQueue.Count != 1
            || !ReferenceEquals(state.TriggerQueue[0], second))
        {
            return "AddToTriggerQueue 应优先使用显式目标实参，不能把 receiver 自身入队";
        }
        state.TriggerQueue.Clear();

        engine.Api.AddToTriggerQueue(first);
        engine.Api.AddToTriggerQueue(second);
        if (state.TriggerQueue.Count != 2
            || !ReferenceEquals(state.TriggerQueue[0], first)
            || !ReferenceEquals(state.TriggerQueue[1], second))
        {
            return "队列没有按 Array_Add 追加，或 FIFO 顺序不正确";
        }

        int firstAttack = targetA.Attack;
        int firstDefense = targetA.Defense;
        int secondAttack = targetB.Attack;
        int secondDefense = targetB.Defense;
        engine.Api.ResolveTriggerQueue();

        if (state.TriggerQueue.Count != 0)
        {
            return $"ResolveTriggerQueue 后仍有 {state.TriggerQueue.Count} 张卡未处理";
        }

        if (targetA.Attack != firstAttack + 1 || targetA.Defense != firstDefense + 1
            || targetB.Attack != secondAttack + 1 || targetB.Defense != secondDefense + 1
            || first.CurrentTarget is not { CardId: 20 }
            || second.CurrentTarget is not { CardId: 21 })
        {
            return $"延迟目标没有正确传递：A={targetA.Attack}/{targetA.Defense} "
                 + $"B={targetB.Attack}/{targetB.Defense}";
        }

        return null;
    }

    /// <summary>
    /// `OpponentActionsCardToDrawSelected` 的 Develop 路径在新卡完成
    /// `OnHandTargetSelected` 后，还要调用 `DevelopAndForecastCheck`。
    /// 这里用唯一订阅者 `card_event_yank_the_army_weekly` 做端到端探针：
    /// 它会读取 `cardDeveloped` 与 `instigatorID`，并把 `hasDeveloped` 写到自己身上。
    /// </summary>
    private static string? OtherCardDevelopedAfterHandTargetSelected(CardDatabase db)
    {
        const string watcherName = "card_event_yank_the_army_weekly";
        const string selectingName = "card_event_pams";
        const string developedName = "card_event_fog_of_war";
        if (db.Find(watcherName) is null || db.Find(selectingName) is null || db.Find(developedName) is null)
        {
            return $"卡库里缺 {watcherName} / {selectingName} / {developedName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        var watcher = state.CreateWithId(watcherName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var selecting = state.CreateWithId(selectingName, Side.Left, 21, CardLocation.HandLeft, 0);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        var developed = engine.Api.DevelopChosenCard(selecting, developedName);
        if (developed is null)
        {
            return "DevelopChosenCard 没有生成卡";
        }

        if (!trace.Any(t => t.StartsWith($"OnOtherCardDeveloped → {watcherName}#20", StringComparison.Ordinal)))
        {
            return "OnOtherCardDeveloped 没有广播到订阅者"
                 + $"；实际派发记录：{string.Join(" | ", trace.Take(12))}";
        }

        int handTargetIndex = trace.FindIndex(t => t.StartsWith("OnHandTargetSelected →", StringComparison.Ordinal));
        int developedIndex = trace.FindIndex(t => t.StartsWith($"OnOtherCardDeveloped → {watcherName}#20", StringComparison.Ordinal));
        if (handTargetIndex < 0 || developedIndex < 0 || handTargetIndex >= developedIndex)
        {
            return "OnOtherCardDeveloped 的派发顺序不在 OnHandTargetSelected 之后"
                 + $"；实际派发记录：{string.Join(" | ", trace.Take(12))}";
        }

        return null;
    }

    /// <summary>
    /// `GetDeckByside` 的出参**形状**必须是「卡 ID 列表」，不是「卡实例列表」。
    ///
    /// ## 蓝图侧证据（三条，互相独立）
    /// 1. 包装层 `BP_CardFunctions.GetDeckByside` 只是转手调用
    ///    `BP_GameState_Battle.GetDeckBySide`，后者返回
    ///    `GameState.DeckCardIDs_Left` / `DeckCardIDs_Right`
    ///    （反编译原文 `ref/kards-sim/KardsSim/Generated/_deps/BP_GameState_Battle.g.cs:1837/1841`）
    ///    —— 成员名就写着 **IDs**。
    /// 2. 全卡池 **46 张**调用它的卡里，`Array_Get` 出来的元素只有三种用法，**全部是整数语义**：
    ///    · `GetCardFromID(deck[i])`（29 张，例 `card_event_blockade` / `card_event_colossus`）
    ///    · `Greater_IntInt(deck[i], 0)` / `JSON_SetInt(..., deck[i])` /
    ///      `DiscardCardFromDeck(deck[i], …)`（例 `card_unit_lovat_scouts` i=180、
    ///      `card_event_alpenfestung` 的成员 `topID`）
    ///    · `DrawSpecificCardFromDeckBySide(cardID, deck[i], side, false)`
    ///      （例 `card_event_defend_the_empire` i=869）
    ///    **没有一个调用点把元素当卡实例用**（扫描脚本见报告）。
    /// 3. `DrawSpecificCardFromDeckBySide` 的内联体第一步就是
    ///    `Array_Contains(deckCardIDs, cardID)`（`out/Generated-gap/_deps/BP_CardFunctions.g.cs:12460`），
    ///    拿牌库数组和一个**整数** cardID 比。
    ///
    /// ## 内核侧症状（本用例要守住的）
    /// 内核曾实现成 `IEnumerable&lt;CardInstance&gt;` ⇒ 元素是卡实例 ⇒
    /// `AsInt(卡实例) = 0`（`CardApiDispatch.cs:2976` 的 `_ =&gt; 0`）⇒
    /// `Array_Contains` 恒假、`GetCardFromID` 恒 null、pams 的
    /// `EqualEqual_IntInt` 恒假（对局 542091 t7：`convoy_175` 没被设成 0 费）。
    /// </summary>
    private static string? GetDeckBySideReturnsCardIds(CardDatabase db)
    {
        const string chosen = "card_event_fog_of_war";
        if (db.Find(chosen) is null)
        {
            return $"卡库里缺 {chosen}";
        }

        var (engine, state) = EmptyBoard(db);
        const int deckCardId = 5002;   // 对局 542091 里 convoy_175 的卡号
        state.CreateWithId(chosen, Side.Left, deckCardId, CardLocation.DeckLeft, 0);

        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        object? raw = engine.Api.InvokeByName("GetDeckByside", null,
            new object?[] { (int)Side.Left, null }, ctx, out bool handled);

        if (!handled)
        {
            return "GetDeckByside 没进派发表";
        }

        if (raw is not System.Collections.IList list)
        {
            return $"GetDeckByside 返回 {raw?.GetType().Name ?? "null"}，"
                 + "蓝图出参是 TArray<int> deckCardIDs（必须是数组）";
        }

        if (list.Count != 1)
        {
            return $"左方牌库应有 1 张，实际 {list.Count} 张";
        }

        object? first = list[0];
        if (first is not int id)
        {
            return $"GetDeckByside 的元素是 {first?.GetType().Name ?? "null"}（{first}）—— "
                 + "蓝图出参是 TArray<int> deckCardIDs ⇒ 元素必须是卡 ID；"
                 + "元素是卡实例时 `AsInt(实例)=0`，`Array_Contains` / `GetCardFromID` / "
                 + "pams 的 `EqualEqual_IntInt` 全部恒假";
        }

        if (id != deckCardId)
        {
            return $"元素是 {id}，应当是 {deckCardId}";
        }

        // 第二条独立证据：`DrawSpecificCardFromDeckBySide` 内联体的
        // `Array_Contains(deckCardIDs, cardID)` 必须为真，否则"从牌库里抽指定卡"
        // 永远走不到 RemoveCardFromDeckBySide / AddCardToDeckBySide 那一段
        // （受影响：`card_event_defend_the_empire`、`card_event_free_french_navy`）。
        object? contains = engine.Api.InvokeByName("Array_Contains",
            "/Script/Engine.Default__KismetArrayLibrary",
            new object?[] { raw, deckCardId }, ctx, out _);
        if (contains is not true)
        {
            return $"Array_Contains(deckCardIDs, {deckCardId}) 返回 {contains ?? "null"} —— "
                 + "DrawSpecificCardFromDeckBySide 会一直走「牌库里没有这张卡」那一支";
        }

        return null;
    }

    /// <summary>
    /// pams 的**后半段**：`OnHandTargetSelected` 里那条循环必须真的把开发出来的牌设成 0 费。
    ///
    /// 蓝图（`card-ir.json` 的 `card_event_pams`，ubergraph）：
    /// <code>
    /// i=20   GetDeckByside(side, out deckCardIDs)          ← 出参 TArray&lt;int&gt;
    /// i=74   Array_Get(deckCardIDs, i, out item)
    /// i=133  EqualEqual_IntInt(item, cardDeveloped)        ← 元素必须是 ID 才可能相等
    /// i=171  popFlowIfNot(那个比较)
    /// i=294  GetCardFromID(item, out card)
    /// i=348  ChangeKreditCost(card, cardID, 0, changeType=2, false)   ← 卡面 "with a cost of 0."
    /// </code>
    /// `changeType=2` 是 `EChangeType::SetValue` 的真实枚举值
    /// （`<kards-src>\Source\kards\Public\EChangeType.h:6-17`），
    /// `DoChangeKreditCost` 对它的实现是**对的**（`CardApiDispatch.cs:1893-1904`），
    /// 不要动 —— 缺的是"这条语句一次都没执行"。
    ///
    /// 用固定的答复源（`PickCardToDraw` 恒返回 fog_of_war），所以**没有随机抽样**，
    /// 不依赖"连打 N 局看费用"。
    /// </summary>
    private static string? PamsDevelopedCardCostZero(CardDatabase db)
    {
        const string chosen = "card_event_fog_of_war";
        string? setup = RunPamsDevelop(db, chosen, out CardInstance? developed);
        if (setup is not null)
        {
            return setup;
        }

        if (developed!.KreditCost == 0)
        {
            return null;
        }

        return $"开发出来的 {chosen} 费用是 {developed.KreditCost}，应当是 0 —— "
             + "pams 的 `ChangeKreditCost(卡, 卡ID, 0, changeType=2)`（IR i=348）没执行到，"
             + "它的前置 `EqualEqual_IntInt(Array_Get(GetDeckByside(side), i), cardDeveloped)`（IR i=133）恒假";
    }

    // ==================================================================
    //  候选池口径（2026-10-02）
    //
    //  `BP_CardFunctions.GetAllActiveStaticCards` 的三层过滤见
    //  `Effects/CardApiDispatch.cs` 的 `StaticCardPool`，数值映射见
    //  `Cards/CardPoolTable.cs`。这里断言的是**池子本身**（张数 + 成员 + 顺序），
    //  不是"连打 N 局"—— 回放只有 5 局、其中只有 `508065` 真的一次静态池抽签，
    //  覆盖面太窄，必须由这些最小断言兜底。
    // ==================================================================

    /// <summary>
    /// 直接走派发表取候选池 —— 与蓝图调用点同形：
    /// `GetAllActiveStaticCards(includeNotAttainable, includeReserved, out cards)`。
    /// </summary>
    private static string? TryStaticPool(
        MatchEngine engine, GameState state, bool includeNotAttainable, bool includeReserved,
        out List<CardInstance> pool)
    {
        pool = new List<CardInstance>();
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName(
            "GetAllActiveStaticCards",
            null,
            new object?[] { includeNotAttainable, includeReserved },
            ctx,
            out bool handled);

        if (!handled)
        {
            return "派发表里没有 GetAllActiveStaticCards";
        }

        if (result is not List<CardInstance> list || list.Count == 0)
        {
            return $"GetAllActiveStaticCards 返回了 {result?.GetType().Name ?? "null"}（应当是非空 List<CardInstance>）";
        }

        pool = list;
        return null;
    }

    /// <summary>
    /// 第一层判据：卡集。`includeNotAttainable=false` 时，数值 2..7 的卡集
    /// （Special / OnlySpawnable / Candidate / Placeholder / Expansion1 / Expansion2）
    /// 与 11（Wildcards）/14（Core）/0 一律不进池。
    ///
    /// 修复前这条**必红**：旧实现返回整个卡库 2021 张，Special 的 319 张全在里面。
    /// </summary>
    private static string? StaticPoolCardSetFilter(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        string? err = TryStaticPool(engine, state, includeNotAttainable: false, includeReserved: true, out var pool);
        if (err is not null)
        {
            return err;
        }

        string[] excluded = { "Special", "OnlySpawnable", "Placeholder", "Expansion1", "Wildcards" };
        foreach (var card in pool)
        {
            string? set = card.Definition.CardSet;
            if (set is not null && Array.IndexOf(excluded, set) >= 0)
            {
                return $"候选池里有 cardSet={set} 的卡 {card.Name} —— "
                     + "`GetAllActiveStaticCards` 的 SwitchEnum 链对 2..7 走 includeNotAttainable 门、"
                     + "对 11/14 无条件跳过（BP_CardFunctions.g.cs:19238-19288）";
            }
        }

        if (pool.Count >= db.Count)
        {
            return $"候选池 {pool.Count} 张 = 整个卡库（{db.Count} 张）—— 过滤根本没生效";
        }

        // 正面断言：无条件保留的那几个卡集必须还在（别把池子滤空）。
        foreach (string must in new[] { "Basic", "Breakthrough", "Legions", "OceaniaStorm", "CurrentExpansion" })
        {
            if (!pool.Any(c => c.Definition.CardSet == must))
            {
                return $"候选池里一张 cardSet={must} 的卡都没有 —— 过滤过宽";
            }
        }

        return null;
    }

    /// <summary>
    /// 第二层判据：预备卡（`NotifyCheckCardReserved` → `UtilityFunctions.isCardReserved`）。
    ///
    /// `includeReserved` 的实参在调用点是 `IsCardReserved(这张卡的名字)`，
    /// 例 `card_event_atlantic_convoy.g.cs:182-184`（那张卡自己不是预备卡 ⇒ false）。
    /// 修复前 `IsCardReserved` 恒 false 且池子不过滤 ⇒ 预备卡永远在池里，这条必红。
    /// </summary>
    private static string? StaticPoolReservedFilter(CardDatabase db)
    {
        // 三张 CDO `isReserved=true` 的卡，分属三个不同卡集，避免只测到一个集合。
        string[] reserved =
        {
            "card_event_aa_barrage",              // Basic
            "card_event_baker_street_irregulars", // CurrentExpansion
            "card_unit_16th_infantry_brigade_vet",// Special（同时被卡集过滤掉）
        };

        var (engine, state) = EmptyBoard(db);

        string? err = TryStaticPool(engine, state, includeNotAttainable: false, includeReserved: false, out var filtered);
        if (err is not null)
        {
            return err;
        }

        err = TryStaticPool(engine, state, includeNotAttainable: false, includeReserved: true, out var withReserved);
        if (err is not null)
        {
            return err;
        }

        foreach (string name in reserved)
        {
            if (db.Find(name) is null)
            {
                return $"卡库里没有 {name}（这条断言的素材卡）";
            }

            if (filtered.Any(c => c.Name == name))
            {
                return $"includeReserved=false 时 {name} 不该在池里（它是 CDO isReserved=true 的预备卡）";
            }

            // `card_unit_16th_infantry_brigade_vet` 属于 Special ⇒ 即使 includeReserved=true
            // 也被卡集过滤掉，所以只对前两张要求"必须在"。
            if (name != "card_unit_16th_infantry_brigade_vet" && !withReserved.Any(c => c.Name == name))
            {
                return $"includeReserved=true 时 {name} 必须在池里（否则 IsCardReserved 没驱动 includeReserved）";
            }
        }

        if (withReserved.Count <= filtered.Count)
        {
            return $"includeReserved=true 的池子 {withReserved.Count} 张没有比 false 的 {filtered.Count} 张大";
        }

        return null;
    }

    /// <summary>
    /// The four card-local helpers must answer the same question as their
    /// random-spawn functions: whether at least one eligible static template
    /// exists.  This covers each cost/type variant and a no-match boundary.
    /// </summary>
    private static string? CheckHasUnitToSpawn(CardDatabase db)
    {
        string[] required =
        {
            "card_event_red_banner",
            "card_event_ural_factories",
            "card_event_refit",
            "card_event_stand_together_brothers",
            "card_unit_t_34",
            "card_unit_bt_7",
        };
        foreach (string name in required)
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺少 CheckHasUnitToSpawn 回归素材 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        int nextId = 100;
        var cases = new[]
        {
            ("card_event_red_banner", "card_unit_t_34", true),       // 5 -> 7 Soviet unit
            ("card_event_ural_factories", "card_unit_t_34", true),   // 5 -> 6 Soviet unit
            ("card_event_refit", "card_unit_t_34", true),            // 5 -> 8 Soviet tank
            ("card_event_stand_together_brothers", "card_unit_bt_7", true), // Soviet cost 2 tank
        };

        foreach (var (eventName, targetName, expected) in cases)
        {
            var source = state.CreateWithId(eventName, Side.Left, nextId++, CardLocation.Discard, 0);
            var target = state.CreateWithId(targetName, Side.Left, nextId++, CardLocation.BoardFrontline, 0);
            var ctx = new EffectContext
            {
                Engine = engine,
                State = state,
                Self = source,
                Controller = Side.Left,
            };

            object? result = engine.Api.InvokeByName(
                "CheckHasUnitToSpawn", null, new object?[] { target }, ctx, out bool handled);
            if (!handled || result is not bool actual || actual != expected)
            {
                return $"{eventName} 正例应返回 true，handled={handled}, result={result ?? "null"}";
            }

            target.KreditCost = 99;
            result = engine.Api.InvokeByName(
                "CheckHasUnitToSpawn", null, new object?[] { target }, ctx, out handled);
            if (!handled || result is not bool noMatch || noMatch)
            {
                return $"{eventName} 无候选费用边界应返回 false，handled={handled}, result={result ?? "null"}";
            }
        }

        return null;
    }

    /// <summary>
    /// 已知池的张数与成员：`card_event_atlantic_convoy` 的候选池是
    /// 「美国 + 单位 + 费≤3」（IR `card_event_atlantic_convoy` 的
    /// `EnumCompareFaction(faction, 5)` / `IsUnit` / `getAndDecryptKredit <= 3`，
    /// i=79/928/1010）。
    ///
    /// 修复前：102 张（含 21 张非生效卡集 + 28 张预备卡）；
    /// 修复后：**53 张**。
    /// </summary>
    private static string? AtlanticConvoyCandidatePool(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        string? err = TryStaticPool(engine, state, includeNotAttainable: false, includeReserved: false, out var pool);
        if (err is not null)
        {
            return err;
        }

        var cand = pool
            .Where(c => c.Definition.FactionId == 5          // EFactionEnum::USA
                        && c.Definition.IsUnit
                        && c.Definition.Kredits <= 3)
            .ToList();

        if (cand.Count != 53)
        {
            return $"atlantic_convoy 的候选池是 {cand.Count} 张，应当是 53 张"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        // 顺序：`GetAllStaticCardsSortedByName` ⇒ 按卡名 ordinal 排。
        if (cand[0].Name != "card_unit_109th_combat_engineers"
            || cand[19].Name != "card_unit_507th_pir"
            || cand[27].Name != "card_unit_fifth_ohio")
        {
            return $"候选池顺序不对：[0]={cand[0].Name} [19]={cand[19].Name} [27]={cand[27].Name}"
                 + "（期望 109th_combat_engineers / 507th_pir / fifth_ohio）";
        }

        // 成员：被卡集过滤的（Special 的 `..._vet`）与被预备过滤的（aa_barrage）都必须不在。
        foreach (string gone in new[] { "card_event_aa_barrage", "card_unit_164th_infantry_regiment_cam1" })
        {
            if (cand.Any(c => c.Name == gone))
            {
                return $"{gone} 不该在候选池里（前者 isReserved，后者 cardSet=Special）";
            }
        }

        return null;
    }

    /// <summary>
    /// ★ 本任务专属判据：**同一个流下标必须抽到同一张卡**。
    ///
    /// 复刻 `out/_server-replays/replay-508065` 里 t9 那一步：
    /// 客户端的 `card_event_atlantic_convoy` 连抽两次（IR i=455 / i=664 两次
    /// `GetRandomCard(possibleCards)`），客户端拿到 `card_unit_fifth_ohio` /
    /// `card_unit_p40_warhawk`（见动作流卡组码 DB / dd）。
    ///
    /// 游标前缀按 `--rng-trace` 的流水账逐条复刻（共 41 次消费）：
    /// <code>
    ///   #1..#7   GetRandomCard×3 + SpawnCardInDeckBySide×3（card_event_colossus，t7）
    ///   #8..#40  ShuffleDeckBySide(Left, n=33) —— **前向 Fisher-Yates 跑满 n 次**
    ///   #41      RandomIntFromRangeWithStream(0,28)（pams 回插牌库）
    /// </code>
    /// 修复前池子 102 张 ⇒ 下标 37/52 ⇒ 506th_airborne / f4f_wildcat（错）；
    /// 修复后池子 53 张 ⇒ 下标 19/27 ⇒ 507th_pir / fifth_ohio。
    ///
    /// ⚠️ **修复后仍然与客户端不一致**（客户端是 fifth_ohio / p40_warhawk）——
    /// 这条断言锁的是"池子口径 + 流下标"这个**中间状态**，
    /// 不是"已经和客户端对上了"。差异的成因见任务报告。
    /// </summary>
    private static string? PoolDrawSameStreamIndex(CardDatabase db)
    {
        const int matchId = 508065;

        // 自己造一台种子 = match_id 的引擎（`EmptyBoard` 用的是 seed:1，不能复用）。
        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: matchId);
        var state = engine.State;

        string? err = TryStaticPool(engine, state, includeNotAttainable: false, includeReserved: false, out var pool);
        if (err is not null)
        {
            return err;
        }

        var cand = pool
            .Where(c => c.Definition.FactionId == 5 && c.Definition.IsUnit && c.Definition.Kredits <= 3)
            .ToList();

        // ---- 游标前缀（41 次）----
        var rng = state.Random;
        rng.RandRange(0, 5);   // #1 GetRandomCard n=6（t5 PC left，colossus 的池外一次）
        rng.RandRange(0, 10);  // #2 GetRandomCard n=11
        rng.RandRange(0, 30);  // #3 SpawnCardInDeckBySide deckLen=31
        rng.RandRange(0, 10);  // #4
        rng.RandRange(0, 31);  // #5 deckLen=32
        rng.RandRange(0, 10);  // #6
        rng.RandRange(0, 32);  // #7 deckLen=33
        for (int i = 0; i <= 32; i++)
        {
            rng.RandRange(i, 32);   // #8..#40 ShuffleDeckBySide(Left, 33)
        }

        rng.RandRange(0, 28);  // #41 pams 回插

        if (rng.ConsumedCount != 41)
        {
            return $"游标前缀消费了 {rng.ConsumedCount} 次，应当是 41 次";
        }

        CardInstance? first = engine.Api.GetRandomCard(cand);
        CardInstance? second = engine.Api.GetRandomCard(cand);

        if (first is null || second is null)
        {
            return "两次抽签返回了 null（候选池是空的？）";
        }

        if (first.Name != "card_unit_507th_pir" || second.Name != "card_unit_fifth_ohio")
        {
            return $"同一个流下标抽到了 {first.Name} / {second.Name}，"
                 + "应当是 card_unit_507th_pir / card_unit_fifth_ohio"
                 + "（池子张数或顺序变了；对照 508065 的 --rng-trace `#42/#43`）";
        }

        return null;
    }

    /// <summary>
    /// 布景：让 pams 走完 develop 链（答复源固定选中 <paramref name="chosen"/>），
    /// 生成出来的那张牌从 <paramref name="developed"/> 取。
    /// 返回 null 表示布景成功；否则是失败原因。
    ///
    /// 和 <see cref="PamsDevelopEndToEnd"/> 同一套布景，抽出来是为了让
    /// 「塞进牌库」和「费用归零」两条断言共用，避免两处漂开。
    /// </summary>
    private static string? RunPamsDevelop(CardDatabase db, string chosen, out CardInstance? developed)
    {
        developed = null;
        const string pams = "card_event_pams";
        if (db.Find(pams) is null || db.Find(chosen) is null)
        {
            return $"卡库里缺 {pams} 或 {chosen}";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var card = state.CreateWithId(pams, Side.Left, 2, CardLocation.HandLeft, 0);

        // 模拟"玩家选了 chosen"：回放里这一答复来自 `CS` 动作的卡组码，
        // 这里直接给一个带正确卡名的**卡池模板**（和回放路径交给内核的东西同形）。
        engine.PickCardToDraw = (selecting, fromTopOfDeck, _) =>
            fromTopOfDeck ? null : KLink.Bot.Effects.CardApi.TemplateInstance(db.Require(chosen), Side.Left);

        if (!engine.PlayCard(card))
        {
            return $"pams 打不出来（kredits={state.Kredits(Side.Left)}）";
        }

        developed = state.Deck(Side.Left).FirstOrDefault(x => x.Name == chosen);
        if (developed is null)
        {
            return $"develop 出来的 {chosen} 不在牌库里"
                 + $"（牌库 {state.Deck(Side.Left).Count} 张，手牌 {state.Hand(Side.Left).Count} 张）";
        }

        return null;
    }

    // ==================================================================
    //  ① 三个原语 + 4 张光环
    // ==================================================================

    /// <summary>
    /// `card_unit_sdf` 的 ApplyAndCorrectBuff：友军单位获得「本方场上 Guard 数量」的临时攻击力。
    /// </summary>
    private static string? SdfCountsFriendlyGuards(CardDatabase db)
    {
        const string sdfName = "card_unit_sdf";
        const string guardName = "card_unit_coldstream_guards";
        const string plainName = "card_unit_arado_ar_196";
        foreach (string name in new[] { sdfName, guardName, plainName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = DeploymentBoard(db);
        var sdf = PutOnBoard(state, sdfName, Side.Left, 2100, 1);
        var firstGuard = PutOnBoard(state, guardName, Side.Left, 2101, 2);
        var secondGuard = PutOnBoard(state, guardName, Side.Left, 2102, 3);
        var friendly = PutOnBoard(state, plainName, Side.Left, 2103, 4);
        var enemyGuard = PutOnBoard(state, guardName, Side.Right, 2104, 1);
        int sdfAttack = sdf.Attack;
        int friendlyAttack = friendly.Attack;
        int firstGuardAttack = firstGuard.Attack;
        int secondGuardAttack = secondGuard.Attack;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = sdf, Controller = Side.Left,
        };

        object? count = engine.Api.InvokeByName("CountFriendlyGuardUnits", null,
            new object?[] { null }, ctx, out bool handled);
        if (!handled || count is not int guardCount || guardCount != 2)
        {
            return $"CountFriendlyGuardUnits 应只计 2 个本方 Guard，handled={handled}, result={count}";
        }

        engine.Api.FireTrigger("OnEnterPlay", sdf, Side.Left,
            eventArgs: new object?[] { sdf, 0 });

        if (friendly.Attack != friendlyAttack + 2
            || firstGuard.Attack != firstGuardAttack + 2
            || secondGuard.Attack != secondGuardAttack + 2)
        {
            return "SDF 进场后所有其它友方单位都应获得 +2 攻击"
                 + Dump(state, ("普通友军", friendly.Attack.ToString()),
                     ("友方 Guard", firstGuard.Attack.ToString()));
        }

        if (sdf.Attack != sdfAttack || enemyGuard.Attack != enemyGuard.Definition.Attack)
        {
            return "SDF 不应给自己加攻击，敌方 Guard 也不应受本方光环影响";
        }

        return null;
    }

    /// <summary>
    /// `card_unit_85_pioneer_company` —— "The first order you play each turn costs 1 less."
    ///
    /// 语义（从 `ref/kards-sim/.../card_unit_85_pioneer_company.g.cs` 的直译产物读出）：
    /// - `OnEnterPlay`：若本回合还没打过指令（`anyOrderPlayedThisTurn` 为假），
    ///   就 `ApplyTheBuff()` 给**手牌里所有同阵营指令** `ChangeKreditCost(-1, changeType=0)`
    /// - `OnOtherCardPlayedFromHand`：若打的是**指令**且 `buffActive`，`RemoveTheBuff()` 还原
    /// - `OnEndOfTurn`：`buffActive` 为假时重新 `ApplyTheBuff()`
    /// - `OnLeaveBoardOrOwner`：`RemoveTheBuff()` 还原
    ///
    /// 本轮断言四件事：
    /// 1. 进场后手牌指令 -1
    /// 2. **重复触发不叠加**（抽牌/生成卡都会触发同一条路径）
    /// 3. 打出第一张指令后**还原成原价**
    /// 4. 打出指令**之后**抽进来的新指令**不再**享受减免（这就是「每回合第一张」）
    /// </summary>
    private static string? PioneerCompanyAura(CardDatabase db)
    {
        const string aura = "card_unit_85_pioneer_company";

        // 用一张费用 ≥ 2 的指令验证光环的普通减费路径；0 费下限另有专项断言。
        const string order = "card_event_mi_5";          // 3 费英国指令
        if (db.Find(aura) is null || db.Find(order) is null)
        {
            return $"卡库里缺 {aura} 或 {order}";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        // 手牌：两张指令（同一张卡建两次，cardID 不同）
        var orderA = state.CreateWithId(order, Side.Left, 2, CardLocation.HandLeft, 0);
        var orderB = state.CreateWithId(order, Side.Left, 3, CardLocation.HandLeft, 1);
        int baseCost = orderA.KreditCost;
        if (baseCost < 2)
        {
            return $"前置不成立：{order} 的费用是 {baseCost}，减 1 会被下限夹住、测不出效果";
        }

        // 光环进场（走真实路径：PlayCard → OnEnterPlay）
        var buffCard = state.CreateWithId(aura, Side.Left, 4, CardLocation.HandLeft, 2);
        if (!engine.PlayCard(buffCard))
        {
            return "光环打不出来";
        }

        if (orderA.KreditCost != baseCost - 1)
        {
            return $"光环进场后指令费用应为 {baseCost - 1}，实际 {orderA.KreditCost}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        // 幂等：再触发一次「别的卡被抽到手」（光环订阅了它）
        int afterFirst = orderA.KreditCost;
        engine.Api.FireTrigger("OnOtherCardDrawnFromDeck", orderB, Side.Left);
        if (orderA.KreditCost != afterFirst)
        {
            return $"重复施加叠加了：{afterFirst} → {orderA.KreditCost}";
        }

        // 打出第一张指令 → 还原。
        //
        // ⚠️ 还原目标用**卡面费用**（`Definition.Kredits`）而不是上面那个 `baseCost`
        //    变量 —— 后者在光环进场后已经被 -1 了，拿它当"原价"会永远自我满足。
        if (!engine.PlayCard(orderA))
        {
            return "指令打不出来（费用被减后应该更便宜）";
        }

        int faceCost = orderB.Definition.Kredits;
        if (orderB.KreditCost != faceCost)
        {
            return $"打出第一张指令后，第二张指令应还原为卡面价 {faceCost}，实际 {orderB.KreditCost}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        // 还原之后再抽进来的指令不该被减费
        var orderC = state.CreateWithId(order, Side.Left, 5, CardLocation.HandLeft, 3);
        engine.Api.FireTrigger("OnOtherCardDrawnFromDeck", orderC, Side.Left,
                               eventArgs: new object?[] { orderC, false, (int)Side.Left });
        if (orderC.KreditCost != faceCost)
        {
            return $"第一张指令已打出，之后入手的指令不该再减费（期望 {faceCost}，实际 {orderC.KreditCost}）";
        }

        return null;
    }

    /// <summary>
    /// 普通 `ChangeKreditCost` 的通用下限是 0，而不是 1。
    /// 覆盖单位、指令和反制卡三种类型，避免只验证显式设费（changeType=1）。
    /// </summary>
    private static string? KreditCostFloorIsZero(CardDatabase db)
    {
        string[] names =
        {
            "card_unit_panzer_ii_c",
            "card_event_pams",
            "card_event_interception",
        };

        var (engine, state) = EmptyBoard(db);
        int id = 700;
        foreach (string name in names)
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }

            var card = state.CreateWithId(name, Side.Left, id++, CardLocation.HandLeft, id - 700);
            int before = card.Definition.Kredits;
            if (before <= 0)
            {
                return $"测试卡 {name} 的卡面费用不是正数：{before}";
            }

            engine.Api.ChangeKreditCost(card, -before - 1);
            if (card.KreditCost != 0)
            {
                return $"{name} 普通减费后应为 0，实际 {card.KreditCost}";
            }
        }

        return null;
    }

    /// <summary>
    /// `card_unit_big_red_one` —— "Cards in your hand cost 4 kredits."
    ///
    /// 语义：`ApplyTheBuff(卡)` 把该卡设成 4 费（已经 4 费就跳过）；
    /// 进场时遍历**所有卡**施加一次，之后靠
    /// `OnOtherCardDrawnFromDeck` / `OnOtherCardSpawnedInHand` / `OnOtherCardReset`
    /// 给新入手的牌补；`OnLeaveBoardOrOwner` 遍历所有卡 `RemoveTheBuff(卡)`。
    /// </summary>
    private static string? BigRedOneAura(CardDatabase db)
    {
        const string aura = "card_unit_big_red_one";
        const string card = "card_event_pams";           // 卡面 1 费 → 应被抬到 4
        const string expensive = "card_event_war_bonds"; // 5 费 → 应被压到 4
        if (db.Find(aura) is null || db.Find(card) is null || db.Find(expensive) is null)
        {
            return "卡库里缺 big_red_one / pams / war_bonds";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var cheap = state.CreateWithId(card, Side.Left, 2, CardLocation.HandLeft, 0);
        var pricey = state.CreateWithId(expensive, Side.Left, 3, CardLocation.HandLeft, 1);
        int cheapBase = cheap.KreditCost;
        int priceyBase = pricey.KreditCost;

        var auraCard = state.CreateWithId(aura, Side.Left, 4, CardLocation.HandLeft, 2);
        engine.PlayCard(auraCard);

        if (cheap.KreditCost != 4)
        {
            return $"1 费牌应被抬到 4，实际 {cheap.KreditCost}" + Dump(state, ("未实现", Unimpl(state)));
        }

        var tempAmount = engine.Api.InvokeByName("getKreditTempBuffAmount", cheap,
            new object?[] { auraCard.CardId, null },
            new EffectContext { Engine = engine, State = state, Self = auraCard, Controller = Side.Left },
            out bool tempHandled);
        if (!tempHandled || tempAmount is not int amount || amount != 3)
        {
            return $"getKreditTempBuffAmount 应返回来源 {auraCard.CardId} 的 +3 改费偏移，实际 {tempAmount}";
        }

        if (pricey.KreditCost != 4)
        {
            return $"{priceyBase} 费牌应被压到 4，实际 {pricey.KreditCost}";
        }

        // 重复施加不叠加
        engine.Api.FireTrigger("OnOtherCardDrawnFromDeck", cheap, Side.Left);
        if (cheap.KreditCost != 4)
        {
            return $"重复施加后应仍是 4，实际 {cheap.KreditCost}";
        }

        // 光环离场 → 全部还原。
        //
        // ⚠️ 必须把它**移到别的位置**来触发离场，不能用 `Destroy`：
        //    `card_unit_big_red_one` 的还原挂在 `OnLeaveBoardOrOwner` /
        //    `OnAfterOtherCardLeaveBoardOrOwner` 上，这两个事件的**第一个入参是
        //    `goingToLocation`**；真正走 discard 时客户端根本不发这个事件
        //    （`card_unit_214th_amur` 的 IR 里明确判 `goingToLocation ∈ [5,6,7]`，
        //    8=弃牌堆 不在其中）。`Destroy` 走的是 `OnDestroyed`，那是另一条链。
        //    这里改成挪到手牌（location 3/4 属于「离场」，且卡还在）来看还原。
        engine.FireLeaveTrigger(auraCard, CardLocation.HandLeft);
        if (cheap.KreditCost != cheapBase || pricey.KreditCost != priceyBase)
        {
            return $"光环离场后应还原为 {cheapBase}/{priceyBase}，实际 {cheap.KreditCost}/{pricey.KreditCost}";
        }

        return null;
    }

    /// <summary>
    /// `card_unit_214th_amur` —— "Your T-34 units have +1 Heavy Armor and operate for 1 less."
    ///
    /// 语义：`ApplyTheBuff()` 遍历**场上**的卡，对同阵营 + `subtype.t34` 的
    /// `ChangeHeavyArmor(+1, 0)` + `ChangeOperationCost(-1, 0)`；
    /// `RemoveTheBuff()` 用 `changeType=4` 撤回。
    /// </summary>
    private static string? AmurTankAura(CardDatabase db)
    {
        const string aura = "card_unit_214th_amur";
        const string t34 = "card_unit_t_34";
        const string other = "card_unit_10_5_cm_lefh";   // 不是 T-34，不该受影响
        if (db.Find(aura) is null || db.Find(t34) is null || db.Find(other) is null)
        {
            return "卡库里缺 214th_amur / t_34 / lefh";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var tank = state.CreateWithId(t34, Side.Left, 2, CardLocation.BoardFrontline, 0);
        var bystander = state.CreateWithId(other, Side.Left, 3, CardLocation.BoardFrontline, 1);
        int tankArmor = tank.HeavyArmor;
        int tankOp = tank.OperationCost;
        int bystanderArmor = bystander.HeavyArmor;
        int bystanderOp = bystander.OperationCost;

        var auraCard = state.CreateWithId(aura, Side.Left, 4, CardLocation.HandLeft, 0);
        engine.PlayCard(auraCard);

        if (tank.HeavyArmor != tankArmor + 1)
        {
            return $"T-34 重甲应为 {tankArmor + 1}，实际 {tank.HeavyArmor}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (tank.OperationCost != tankOp - 1)
        {
            return $"T-34 行动费应为 {tankOp - 1}，实际 {tank.OperationCost}";
        }

        if (bystander.HeavyArmor != bystanderArmor || bystander.OperationCost != bystanderOp)
        {
            return $"非 T-34 不该受影响：重甲 {bystanderArmor}→{bystander.HeavyArmor}，"
                 + $"行动费 {bystanderOp}→{bystander.OperationCost}";
        }

        // 重复施加不叠加（移动战线会重发 OnOtherCardEnterPlay）
        engine.Api.FireTrigger("OnOtherCardEnterPlay", bystander, Side.Left);
        if (tank.HeavyArmor != tankArmor + 1 || tank.OperationCost != tankOp - 1)
        {
            return $"重复施加叠加了：重甲 {tank.HeavyArmor}，行动费 {tank.OperationCost}";
        }

        engine.FireLeaveTrigger(auraCard, CardLocation.HandLeft);
        if (tank.HeavyArmor != tankArmor || tank.OperationCost != tankOp)
        {
            return $"光环离场后应还原为 {tankArmor} 重甲 / {tankOp} 行动费，"
                 + $"实际 {tank.HeavyArmor} / {tank.OperationCost}";
        }

        return null;
    }
    /// <summary>
    /// `ChangeAttack` 的 `changeType = 4`（`EChangeType::tempBuffRemove`）= **撤销**，
    /// 不是"再加一次"。这条断言守的就是那个分支。
    ///
    /// 蓝图出处（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`）：
    /// <code>
    /// ChangeAttack（:6491）里 changeType 的分派：
    ///   :6591 localChangeType == 4 → L_096F
    ///   :6789 L_096F: if (amountRemoved == 0) { valueChanged = False; 直接结束 }   ← 没撤到东西 ⇒ 什么都不做
    ///   :6805 L_09AB: localInputAmount = amountRemoved                             ← ★ 实参 amount 被覆盖，不参与运算
    ///   :6808 L_09EF: decryptedAttackBuff = getAndDecryptAttackBuff(card)
    ///   :6811 L_0A18: newBuff = decryptedAttackBuff + amountRemoved
    ///   :6813 L_0A46: setAndEncryptAttackBuff(card, newBuff, …)
    /// </code>
    /// `amountRemoved` 是 `ChangeBuffsFromCards` 的出参（:6561 调用 / :6848 出参槽）：
    /// <code>
    ///   :6885 入口分派 changeType == 4 → :6887 goto L_0831
    ///   :7080 L_0831 用 EChangeType::tempBuffGive(=0) 与 buffType 拼出键名，:7090 goto L_0369
    ///   :6936 L_0369 Map_Find(cardToChange.buffsFromCards, instigatorID, …) 的通用删除路径
    ///   :7474 L_16E6 localAmountRemoved = 已存的量 × -1（:7481/:7483），:7485 Map_Remove 掉那个键
    /// </code>
    /// ⇒ **撤销量 = 当初存进去的量**，与本次实参 `amount` 无关。
    /// 对照：ct=0/1 走 :6918 `L_0250`，那里才有 `if (amount == 0) { amountRemoved = 0; return; }` 的短路。
    ///
    /// 修复前内核只处理了 ct=2，ct=4 落进默认分支 `ChangeAttack(target, +delta)` ——
    /// 「+4 之后再撤 4」变成 +8，攻击力永远回不到原值。
    /// </summary>
    private static string? ChangeAttackTempBuffRemove(CardDatabase db)
    {
        const string unit = "card_unit_t_34";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;

        var target = state.CreateWithId(unit, Side.Left, 2, CardLocation.BoardFrontline, 0);
        var giver = state.CreateWithId(unit, Side.Left, 3, CardLocation.BoardFrontline, 1);
        var other = state.CreateWithId(unit, Side.Left, 4, CardLocation.BoardFrontline, 2);

        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = giver,
            Controller = Side.Left,
        };

        // 蓝图实参形状：`ChangeAttack(卡, instigatorID, 数值, changeType, skipAction, out)`
        // （卡池实测，例：`card_unit_su_100` IR i=376 给 +4/ct=0、i=206 撤 4/ct=4）
        void ChangeAttack(CardInstance source, int amount, int changeType)
        {
            ctx.Self = source;
            engine.Api.InvokeByName("ChangeAttack", null,
                new object?[] { target, source.CardId, amount, changeType, false, null }, ctx, out _);
        }

        int baseAttack = target.Attack;

        // ① 给→撤 往返：+4/ct=0 之后 4/ct=4，攻击力必须回到原值
        ChangeAttack(giver, 4, 0);
        if (target.Attack != baseAttack + 4)
        {
            return $"施加 ct=0 之后攻击力应为 {baseAttack + 4}，实际 {target.Attack}";
        }

        ChangeAttack(giver, 4, 4);
        if (target.Attack != baseAttack)
        {
            return $"撤销 ct=4 之后攻击力应回到 {baseAttack}，实际 {target.Attack}"
                 + $"（多出 {target.Attack - baseAttack}）—— ct=4 被当成「再加一次」了";
        }

        // ② 撤销一个**不存在**的来源 buff：不得崩、不得改数值（蓝图 :6789 的短路）
        ChangeAttack(other, 4, 4);
        if (target.Attack != baseAttack)
        {
            return $"撤销一个不存在的来源之后攻击力不该变（应 {baseAttack}，实际 {target.Attack}）";
        }

        // ③ 别的来源的 buff 不受影响 —— 撤销只动「该来源」那一个槽（`RemoveCostBuff` 的语义）
        ChangeAttack(other, 2, 0);
        ChangeAttack(giver, 5, 0);
        ChangeAttack(giver, 5, 4);
        if (target.Attack != baseAttack + 2)
        {
            return $"撤销 giver 的 buff 不该动 other 的：应 {baseAttack + 2}，实际 {target.Attack}";
        }

        // ④ 蓝图 :6805 的「实参 amount 被覆盖」：撤销量由存量决定，与这次传的数字无关
        ChangeAttack(other, 99, 4);
        if (target.Attack != baseAttack)
        {
            return $"ct=4 的实参应被忽略（撤销量 = 当初存的量）：应 {baseAttack}，实际 {target.Attack}";
        }

        // ⑤ 对已经撤干净的来源再撤一次不产生负向漂移
        ChangeAttack(other, 2, 4);
        if (target.Attack != baseAttack)
        {
            return $"对已撤销的来源再撤一次不该改数值：应 {baseAttack}，实际 {target.Attack}";
        }

        return null;
    }

    /// <summary>
    /// `ChangeDefense` 的 `changeType = 4` —— **不是**撤销：蓝图把它当非法值直接拒绝。
    ///
    /// 出处 `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs` 的 `ChangeDefense`（:7543）：
    /// <code>
    ///   :7630 localChangeType == 0 → L_0E96
    ///   :7634 localChangeType == 1 → L_07CB    （permBuff，加）
    ///   :7638 localChangeType == 2 → L_03D8    （SetValue，设成绝对值）
    ///   :7646 localChangeType == 4 → L_0E96    ← ★ 与 ChangeAttack 的 L_096F 完全不是一回事
    ///   :7906 L_0E96: DirectClientLogger("change type incorrect for \"Change Defense\"")
    ///   :7909         qqq = False
    ///   :7911         return                   ← 什么都不改
    /// </code>
    /// 对照 `ChangeAttack`（:6591）：那里的 ct=4 有专门的 `L_096F` 撤销分支。
    /// ⇒ 两个函数的 ct=4 **语义不一致**，所以这里对齐的是「no-op」，不是「撤销」。
    ///
    /// 当前卡池 `ChangeDefense` 的 changeType 分布是 `{1:333, 2:10}`，**ct=4 有 0 个调用点**
    /// ⇒ 这条属**行为中性**的预防性对齐（改了也不会动 A/B 的任何一格）。
    /// </summary>
    private static string? ChangeDefenseChangeType4IsRejected(CardDatabase db)
    {
        const string unit = "card_unit_t_34";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;

        var target = state.CreateWithId(unit, Side.Left, 2, CardLocation.BoardFrontline, 0);
        var giver = state.CreateWithId(unit, Side.Left, 3, CardLocation.BoardFrontline, 1);

        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = giver,
            Controller = Side.Left,
        };

        void ChangeDefense(int amount, int changeType)
        {
            engine.Api.InvokeByName("ChangeDefense", null,
                new object?[] { target, giver.CardId, amount, changeType, false, null }, ctx, out _);
        }

        int baseDefense = target.Defense;

        // ct=1（permBuff）是「加」—— 顺便钉住"没顺手改 ct=1 语义"这件事
        ChangeDefense(3, 1);
        if (target.Defense != baseDefense + 3)
        {
            return $"ct=1 之后防御应为 {baseDefense + 3}，实际 {target.Defense}";
        }

        // 蓝图 ct=4 是 :7906 的非法值分支 ⇒ 防御一点不动（旧实现按「加」算，会变成 +6）
        ChangeDefense(3, 4);
        if (target.Defense != baseDefense + 3)
        {
            return $"蓝图 `ChangeDefense` 的 ct=4 是 L_0E96 的非法值分支（只 log + return），"
                 + $"防御应停在 {baseDefense + 3}，实际 {target.Defense}";
        }

        return null;
    }

    private static string? ChangeDefenseBeforeOtherCardGainDefense(CardDatabase db)
    {
        const string sourceName = "card_unit_t_34";
        const string bonusName = "card_unit_111th_indian_brigade";
        const string vetoName = "card_unit_kagoshima_regiment";
        foreach (string name in new[] { sourceName, bonusName, vetoName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        // 111th Indian Brigade changes the running amount by +1 for its own HQ.
        {
            var (engine, state) = EmptyBoard(db);
            var hq = state.Hq(Side.Left);
            var source = state.CreateWithId(sourceName, Side.Left, 71, CardLocation.BoardFrontline, 0);
            state.CreateWithId(bonusName, Side.Left, 72, CardLocation.BoardFrontline, 1);
            var trace = new List<string>();
            engine.Api.TriggerTrace = trace;
            int before = hq.Defense;

            engine.Api.ChangeDefense(hq, 2, source);

            if (hq.Defense != before + 3)
            {
                return $"111th Indian Brigade 应把 HQ 的 +2 改为 +3，实际变化 {hq.Defense - before}"
                     + $"\n       派发记录：{string.Join(" | ", trace)}";
            }
        }

        // Kagoshima Regiment sets the amount to zero and stops the change for HQs.
        {
            var (engine, state) = EmptyBoard(db);
            var hq = state.Hq(Side.Left);
            var source = state.CreateWithId(sourceName, Side.Left, 74, CardLocation.BoardFrontline, 0);
            state.CreateWithId(vetoName, Side.Right, 75, CardLocation.BoardFrontline, 0);
            int before = hq.Defense;

            engine.Api.ChangeDefense(hq, 2, source);

            if (hq.Defense != before)
            {
                return $"Kagoshima Regiment 应中止 HQ 防御增益，实际变化 {hq.Defense - before}";
            }
        }

        return null;
    }

    private static string? Type89ConvertsHqDefenseGainToDamage(CardDatabase db)
    {
        const string sourceName = "card_unit_t_34";
        const string observerName = "card_unit_type_89";
        foreach (string name in new[] { sourceName, observerName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        var hq = state.Hq(Side.Left);
        var source = state.CreateWithId(sourceName, Side.Left, 76, CardLocation.BoardFrontline, 0);
        var observer = state.CreateWithId(observerName, Side.Right, 77, CardLocation.BoardFrontline, 0);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        int before = hq.Defense;

        engine.Api.ChangeDefense(hq, 2, source);

        if (hq.Defense != before - 2)
        {
            return $"Type 89 应对 HQ 造成 2 点伤害并中止增防，实际变化 {hq.Defense - before}"
                 + $"\n       派发记录：{string.Join(" | ", trace)}";
        }

        if (observer.Location != CardLocation.BoardFrontline)
        {
            return "Type 89 应留在场上完成响应";
        }

        return null;
    }

    /// <summary>
    /// `card_event_committed_crew` —— "Until end of turn, Spitfires cost 0 to deploy
    /// and get +3+3 when deployed."
    ///
    /// 断言两段（都是逐行读 `ref/kards-sim/.../card_event_committed_crew.g.cs` 得到的行为）：
    /// 1. 打出这张指令（`OnPlayedFromHand` → 遍历所有卡 `ApplyTheBuff()`）之后，
    ///    手牌里的 Spitfire 费用变 0（changeType=1，**允许到 0**，不受 1 费下限约束）
    /// 2. Spitfire 部署时它的 `OnOtherCardPlayedFromHand` 给它 +3+3
    ///
    /// ⚠️ **不做**"回合结束后 +3+3 消失"这条断言 —— 因为卡自己的实现里
    ///    `ChangeAttack(卡, cardID, 3, 1, false, out)` 的 changeType 是 **1（普通加法）**，
    ///    不是 `AddAttackUntilEndOfTurn` 那条"到回合末失效"的路径。
    ///    也就是说"这份实现里 +3+3 是永久的"；要断言它消失，得先实现客户端的
    ///    **临时 buff 回合末重置**机制（`OnAfterOtherCardOperactionCostBuffsReset`
    ///    那一族），目前没有实现 —— 见 klink bot/docs/内核补全队列.md 的已知缺口。
    ///    宁可不写这条断言，也不要写一条"猜"的。
    /// </summary>
    private static string? CommittedCrewAura(CardDatabase db)
    {
        const string crew = "card_event_committed_crew";
        const string spitfire = "card_unit_spitfire";
        if (db.Find(crew) is null || db.Find(spitfire) is null)
        {
            return $"卡库里缺 {crew} 或 {spitfire}";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var plane = state.CreateWithId(spitfire, Side.Left, 2, CardLocation.HandLeft, 0);
        int planeBaseCost = plane.KreditCost;
        int planeBaseAttack = plane.Attack;
        int planeBaseDefense = plane.Defense;
        if (planeBaseCost <= 0)
        {
            return $"前置不成立：{spitfire} 的卡面费用是 {planeBaseCost}，测不出「归零」";
        }

        // 1) 打出敢死队
        var crewCard = state.CreateWithId(crew, Side.Left, 3, CardLocation.HandLeft, 1);
        if (!engine.PlayCard(crewCard))
        {
            return "敢死队打不出来";
        }

        if (plane.KreditCost != 0)
        {
            return $"Spitfire 费用应为 0，实际 {plane.KreditCost}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        // 2) 部署 Spitfire —— 0 费应该打得出来，并且吃到 +3+3
        if (!engine.PlayCard(plane))
        {
            return $"0 费的 Spitfire 应该能打出（费用={plane.KreditCost}，"
                 + $"kredits={state.Kredits(Side.Left)}）";
        }

        if (plane.Attack != planeBaseAttack + 3 || plane.Defense != planeBaseDefense + 3)
        {
            return $"Spitfire 部署后应为 {planeBaseAttack + 3}/{planeBaseDefense + 3}，"
                 + $"实际 {plane.Attack}/{plane.Defense}"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        return null;
    }

    /// <summary>
    /// `card_unit_3_panzergrenadier` —— "Gets +1+1 after you operate a German unit."
    ///
    /// 它的 IR 判据是（`OnAfterOtherCardAttacks`）：
    /// <code>
    /// IsLocatedOnBoard()                        ← 隐式 self（自己还在场）
    /// EqualEqual_ByteByte(attackerCard.side, side)
    /// EnumCompareFaction(attackerCard.faction, 1, out Branches)   ← 1 = EFactionEnum::Germany
    /// CmpSuccess = NotEqual_ByteByte(Branches, 0)
    /// if (!CmpSuccess) ChangeAttack(+1); ChangeDefense(+1)
    /// </code>
    ///
    /// 这里守两件事，它们**各自都能单独让效果失效**：
    /// 1. 带 `ctx` 的成员访问（`attackerCard.faction` / `attackerCard.side`）——
    ///    丢掉 ctx 会让 faction 读成 null
    /// 2. `CallMath` 的输出槽（`CallFunc_EnumCompareFaction_Branches`）——
    ///    生成器不给 math 步算 outs 的话它永远是 null，
    ///    `NotEqual_ByteByte(null, 0)` = false → `JumpIfNot` 恒成立 → **无条件 +1+1**
    /// </summary>
    private static string? PanzergrenadierFactionGate(CardDatabase db)
    {
        const string gren = "card_unit_3_panzergrenadier";
        const string german = "card_unit_arado_ar_196";     // Germany
        const string american = "card_unit_m2a4";           // USA
        // ⚠️ 肉盾 `card_unit_85_pioneer_company` **自带 `Smokescreen`**（CardInnateTable）。
        //    P1 把烟幕的「不能被攻击」（CanAttack si=3637/3793）接上之后，
        //    这条用例的第一枪就被新规则正确地拒了 —— 不是回归，是新规则生效。
        //    这里显式摘掉烟幕，让这条用例只管它自己要守的阵营判定；
        //    烟幕本身另有专门用例（`SmokescreenRules`）。
        const string victim = "card_unit_85_pioneer_company";
        if (db.Find(gren) is null || db.Find(german) is null || db.Find(american) is null)
        {
            return $"卡库里缺 {gren} / {german} / {american}";
        }

        if (db.Find(german)!.FactionId != 1 || db.Find(american)!.FactionId != 5)
        {
            return $"阵营枚举对不上：{german}.FactionId={db.Find(german)!.FactionId}（应 1=Germany），"
                 + $"{american}.FactionId={db.Find(american)!.FactionId}（应 5=USA）";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var grenadier = state.CreateWithId(gren, Side.Left, 2, CardLocation.BoardFrontline, 0);
        var germanUnit = state.CreateWithId(german, Side.Left, 3, CardLocation.BoardFrontline, 1);
        var usaUnit = state.CreateWithId(american, Side.Left, 4, CardLocation.BoardFrontline, 2);
        int baseAttack = grenadier.Attack;
        int baseDefense = grenadier.Defense;

        // 打三枪，每次换一个攻击者（都是自己这边的单位，打对面那个肉盾）。
        // 每一步都要把「本回合已攻击」清掉，否则第二枪起会被拒。
        int expected = 0;
        foreach (var attacker in new[] { grenadier, germanUnit, usaUnit })
        {
            attacker.HasAttackedThisTurn = false;
            state.SetKredits(Side.Left, 12);
            var foe = state.Create($"{victim}", Side.Right, CardLocation.BoardFrontline,
                                   state.NextLocationNumber(Side.Right, CardLocation.BoardFrontline));
            foe.Defense = 99;
            engine.Api.RemoveKeyword(foe, Keyword.Smokescreen);   // 见上面关于烟幕的注释

            // 「自己攻击」那一路**无条件** +1+1（它自己也是德国单位）；
            // 「别人攻击」那一路才带阵营判定 —— 所以只有德国单位出手才该再涨。
            bool counts = attacker.Definition.FactionId == 1;
            int before = grenadier.Attack;
            if (!engine.Attack(attacker, foe))
            {
                return $"{attacker.Name} 打不出（kredits={state.Kredits(Side.Left)}，"
                     + $"行动费={attacker.OperationCost}）";
            }

            expected += counts ? 1 : 0;
            if (grenadier.Attack != baseAttack + expected)
            {
                return $"{attacker.Name}（faction={attacker.Definition.FactionId}）出手后，"
                     + $"掷弹兵攻击力应为 {baseAttack + expected}，实际 {grenadier.Attack}"
                     + $"（本次{'应' : '不'}计入）"
                     + Dump(state, ("未实现", Unimpl(state)));
            }

            if (grenadier.Attack - before != (counts ? 1 : 0))
            {
                return $"{attacker.Name} 这一次让掷弹兵涨了 {grenadier.Attack - before}，"
                     + $"应为 {(counts ? 1 : 0)}";
            }
        }

        if (grenadier.Defense != baseDefense + expected)
        {
            return $"攻防涨幅不一致：攻 +{grenadier.Attack - baseAttack}，"
                 + $"防 +{grenadier.Defense - baseDefense}（应都是 +{expected}）";
        }

        return null;
    }

    /// <summary>
    /// 「攻击一个已经进弃牌堆的目标」必须被拒。
    ///
    /// `Attack` 原先对防御者一个字都不校验，完全靠调用方给对目标。
    /// 这条断言把它变成内核自己的责任 —— 否则一个过期引用就能凭空多打一次
    /// （扣行动费、加攻防 buff、触发 OnAfterAttack 全都会发生）。
    /// </summary>
    private static string? AttackRejectsDeadTarget(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var attacker = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 2, CardLocation.BoardFrontline, 0);
        var victim = state.CreateWithId("card_unit_85_pioneer_company", Side.Right, 42, CardLocation.BoardFrontline, 0);

        // 直接把它送进弃牌堆（模拟"决策与结算之间夹了别的效果"）
        state.Move(victim, CardLocation.Discard);
        int hqBefore = state.HqDefense(Side.Right);
        int kreditsBefore = state.Kredits(Side.Left);

        if (engine.Attack(attacker, victim))
        {
            return "攻击一个已在弃牌堆的目标竟然成功了";
        }

        if (state.Kredits(Side.Left) != kreditsBefore)
        {
            return $"被拒的攻击不该扣行动费：{kreditsBefore} → {state.Kredits(Side.Left)}";
        }

        if (state.HqDefense(Side.Right) != hqBefore)
        {
            return "被拒的攻击不该改 HQ";
        }

        return null;
    }

    /// <summary>
    /// T31 没有 entrypoint，20 张卡的实现都在 locals。Merchant Navy 是最小的
    /// 可观测探针：收到敌方单位攻击后返回 <c>AttackedAndStopped=true</c>，
    /// 触发 gotcha 并把攻击者撤回，攻击伤害本身不应结算。
    /// </summary>
    private static string? OtherCardAttacksStopsAttack(CardDatabase db)
    {
        const string attackerName = "card_unit_arado_ar_196";
        const string defenderName = "card_unit_85_pioneer_company";
        const string merchantName = "card_event_merchant_navy";
        foreach (var name in new[] { attackerName, defenderName, merchantName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Right;
        state.SetKredits(Side.Right, 12);
        state.SetMaxKredits(Side.Right, 12);

        var attacker = state.CreateWithId(attackerName, Side.Right, 200, CardLocation.BoardFrontline, 0);
        var defender = state.CreateWithId(defenderName, Side.Left, 201, CardLocation.BoardFrontline, 0);
        var merchant = state.CreateWithId(merchantName, Side.Left, 202, CardLocation.BoardHqLeft, 1);
        attacker.EnteredPlayOnTurn = -1;
        defender.EnteredPlayOnTurn = -1;
        merchant.EnteredPlayOnTurn = -1;
        engine.Api.RemoveKeyword(defender, Keyword.Smokescreen);

        int defenderBefore = defender.Defense;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        if (!engine.Attack(attacker, defender, out string reason))
        {
            return $"带 Merchant Navy 的攻击没有进入攻击结算：{reason}";
        }

        if (!trace.Any(x => x.StartsWith("OnOtherCardAttacks", StringComparison.Ordinal)
                         && x.Contains(merchantName, StringComparison.Ordinal)))
        {
            return "T31 locals 没有派发到 Merchant Navy"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (defender.Defense != defenderBefore)
        {
            return $"Merchant Navy 已中止攻击，但目标仍受到伤害：{defenderBefore} → {defender.Defense}";
        }

        if (attacker.Location != CardLocation.BoardHqRight)
        {
            return $"Merchant Navy 已中止攻击，但攻击者没有撤回到支援线：位置={attacker.Location}";
        }

        if (merchant.Location != CardLocation.Discard)
        {
            return $"触发后的 Merchant Navy 应进入弃牌堆，实际位置={merchant.Location}";
        }

        return null;
    }

    /// <summary>
    /// `ExecuteStoppedAttack` 必须在 T31 中止攻击后仍执行攻击者自己的
    /// `OnAttackStopped`。Meteor 的卡面清理挂在这个事件上；若漏派，
    /// 被 Merchant Navy 中止的攻击会错误地让 Meteor 留在场上。
    /// </summary>
    private static string? AttackStoppedTriggersSelfEvent(CardDatabase db)
    {
        const string attackerName = "card_unit_meteor";
        const string defenderName = "card_unit_85_pioneer_company";
        const string merchantName = "card_event_merchant_navy";
        foreach (var name in new[] { attackerName, defenderName, merchantName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Right;
        state.SetKredits(Side.Right, 12);
        state.SetMaxKredits(Side.Right, 12);

        var attacker = state.CreateWithId(attackerName, Side.Right, 200,
            CardLocation.BoardFrontline, 0);
        var defender = state.CreateWithId(defenderName, Side.Left, 201,
            CardLocation.BoardFrontline, 0);
        var merchant = state.CreateWithId(merchantName, Side.Left, 202,
            CardLocation.BoardHqLeft, 1);
        attacker.EnteredPlayOnTurn = state.Turn - 1;
        defender.EnteredPlayOnTurn = state.Turn - 1;
        merchant.EnteredPlayOnTurn = state.Turn - 1;
        engine.Api.RemoveKeyword(defender, Keyword.Smokescreen);

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        if (!engine.Attack(attacker, defender, out string reason))
        {
            return $"Merchant Navy 中止 Meteor 攻击失败：{reason}";
        }

        if (!trace.Any(x => x.StartsWith($"OnAttackStopped → {attacker.Name}#{attacker.CardId}",
                                           StringComparison.Ordinal)))
        {
            return "攻击被中止后没有派发攻击者 OnAttackStopped"
                   + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (attacker.Location.IsBoard())
        {
            return $"Meteor 收到 OnAttackStopped 后应离场，实际位置={attacker.Location}";
        }

        if (!state.Deck(Side.Right).Any(c => c.Name == attackerName
                                             && c.Attack == 2 && c.Defense == 2))
        {
            return "Meteor 的 OnAttackStopped 应生成一张 2/2 牌库副本";
        }

        return null;
    }

    /// <summary>
    /// T30 的两个订阅者都在卡片 locals 中。Cold Trap 应在攻击伤害结算前
    /// 生成 SISSI 并把目标改成它；原目标不应承受这次攻击。
    /// </summary>
    private static string? OtherCardAttackSwitchesTarget(CardDatabase db)
    {
        const string attackerName = "card_unit_arado_ar_196";
        const string defenderName = "card_unit_85_pioneer_company";
        const string trapName = "card_event_cold_trap";
        const string replacementName = "card_unit_sissi";
        foreach (var name in new[] { attackerName, defenderName, trapName, replacementName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Right;
        state.SetKredits(Side.Right, 12);
        state.SetMaxKredits(Side.Right, 12);

        var attacker = state.CreateWithId(attackerName, Side.Right, 210,
            CardLocation.BoardFrontline, 0);
        var defender = state.CreateWithId(defenderName, Side.Left, 211,
            CardLocation.BoardFrontline, 0);
        var trap = state.CreateWithId(trapName, Side.Left, 212,
            CardLocation.BoardHqLeft, 1);
        attacker.EnteredPlayOnTurn = -1;
        defender.EnteredPlayOnTurn = -1;
        trap.EnteredPlayOnTurn = -1;
        engine.Api.RemoveKeyword(defender, Keyword.Smokescreen);

        int defenderBefore = defender.Defense;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        if (!engine.Attack(attacker, defender, out string reason))
        {
            return $"带 Cold Trap 的攻击没有进入攻击结算：{reason}";
        }

        if (!trace.Any(x => x.StartsWith("OnOtherCardAttackSwitchTarget", StringComparison.Ordinal)
                         && x.Contains(trapName, StringComparison.Ordinal)))
        {
            return "T30 locals 没有派发到 Cold Trap"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (defender.Defense != defenderBefore)
        {
            return $"Cold Trap 已改目标，但原目标仍受到伤害：{defenderBefore} → {defender.Defense}"
                 + $"\n       trace={string.Join(" | ", trace)}"
                 + $"\n       cards={string.Join(" | ", state.AllCards.Where(c => c.Name == replacementName).Select(c => $"{c.CardId}@{c.Location}/{c.Defense}"))}";
        }

        var sissi = state.AllCards.FirstOrDefault(c => c.Name == replacementName);
        if (sissi is null)
        {
            return "Cold Trap 没有生成 SISSI";
        }

        if (sissi.Location != CardLocation.Discard)
        {
            return $"攻击应落到 SISSI 并摧毁它，实际位置={sissi.Location} 防御={sissi.Defense}";
        }

        return null;
    }

    // ==================================================================
    //  §①.9 三条基本规则
    // ==================================================================

    /// <summary>
    /// 部署落点 = **自己半场**（`Side.HqOf()`，即 5/6），**不是前线**。
    ///
    /// 真实数据：250 条快照里**前线为空的占 83%**（207/250）—— 单位绝大多数待在底线。
    /// 出厂口 `BP_Logic::CanPlayCardFromHand` i=959 判的也是
    /// `IsLocationFull(SupplyLineLocationFromSide(side))`，只查半场。
    ///
    /// 同时守容量：半场 **5 格含 HQ**（`FetchCardsByLocation` case 5,6 → MaxQty=5，
    /// 计数循环只按 location 过滤、不排除 HQ）⇒ 每边最多 **4 个单位**。
    /// </summary>
    private static string? HalfBoardDeploymentAndCapacity(CardDatabase db)
    {
        const string unit = "card_unit_arado_ar_196";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;

        // 逐个打出，直到半场满
        int played = 0;
        for (int i = 0; i < 6; i++)
        {
            state.SetKredits(Side.Left, 12);
            state.SetMaxKredits(Side.Left, 12);
            var c = state.Create($"{unit}", Side.Left, CardLocation.HandLeft,
                                 state.NextLocationNumber(Side.Left, CardLocation.HandLeft));
            if (engine.PlayCard(c))
            {
                played++;
                continue;
            }

            // 打不出来 → 必须是因为半场满（不是别的原因）
            if (!engine.CanPlay(c, out string why))
            {
                if (why != "半场已满")
                {
                    return $"第 {i + 1} 个单位的拒绝原因不是「半场已满」而是「{why}」";
                }

                break;
            }
        }

        if (played != 4)
        {
            return $"半场应能放 4 个单位（5 格 − HQ 1 格），实际放下 {played} 个"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        foreach (var u in state.Board(Side.Left))
        {
            if (u.Location != CardLocation.BoardHqLeft)
            {
                return $"单位应部署到自己的半场 BoardHqLeft，实际 {u.Location}（{u.Name}）";
            }
        }

        // 半场里的第 0 格是 HQ —— 所以单位占的是 1..4
        var hq = state.Hq(Side.Left);
        if (hq.LocationNumber != 0 || !hq.IsHq)
        {
            return $"HQ 应占半场第 0 格，实际 #{hq.LocationNumber} IsHq={hq.IsHq}";
        }

        if (state.Cards(Side.Left, CardLocation.BoardHqLeft).Count != 5)
        {
            return $"半场总格数应为 5（HQ + 4 单位），实际 {state.Cards(Side.Left, CardLocation.BoardHqLeft).Count}";
        }

        return null;
    }

    /// <summary>
    /// 召唤失调：`enterPlayOnTurn == currentTurn &amp;&amp; !getHasBlitz()` ⇒ 当回合
    /// **攻击和移动都被挡**（`BP_Logic::CanCardDoAnything` i=1352-1568 在两条检查之前；
    /// `cardsCheckFunctions::CanAttack` i=2087-2167 写 `failReason="deployment_sickness"`）。
    ///
    /// ⚠️ 这条断言同时也守「卡面自带 Blitz 已加载」——
    /// `cards.live.json` 里没有 `hasBlitz`，不额外加载的话 Blitz 例外根本判不出来。
    /// </summary>
    private static string? DeploymentSickness(CardDatabase db)
    {
        const string plain = "card_unit_arado_ar_196";      // 无 Blitz
        const string blitz = "card_unit_m20_scout_car";     // CDO hasBlitz = True
        if (db.Find(plain) is null || db.Find(blitz) is null)
        {
            return $"卡库里缺 {plain} 或 {blitz}";
        }

        if (!db.Find(blitz)!.Keywords.Contains(Keyword.Blitz, StringComparer.Ordinal))
        {
            return $"{blitz} 的卡面 Blitz 没被加载（CardDefinition.Keywords 里没有 Blitz）";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var foot = state.CreateWithId(plain, Side.Left, 2, CardLocation.HandLeft, 0);
        var fast = state.CreateWithId(blitz, Side.Left, 3, CardLocation.HandLeft, 1);
        if (!engine.PlayCard(foot) || !engine.PlayCard(fast))
        {
            return "单位打不出来";
        }

        // 同回合：非 Blitz 不能动，Blitz 能
        if (foot.CanOperateThisTurn(state))
        {
            return $"非 Blitz 单位 {plain} 在部署当回合不应该能攻击";
        }

        if (foot.CanMoveThisTurn(state))
        {
            return $"非 Blitz 单位 {plain} 在部署当回合不应该能移动";
        }

        if (!fast.CanOperateThisTurn(state))
        {
            return $"Blitz 单位 {blitz} 在部署当回合**应该**能攻击（召唤失调的例外）";
        }

        if (engine.Attack(foot, state.Hq(Side.Right)))
        {
            return $"非 Blitz 单位 {plain} 部署当回合的攻击竟然成功了";
        }

        // 下一回合就能动了（把进场回合改成上一回合）
        foot.EnteredPlayOnTurn = state.Turn - 1;
        if (!foot.CanOperateThisTurn(state))
        {
            return "过了部署回合之后应该能攻击";
        }

        if (!engine.Attack(foot, state.Hq(Side.Right)))
        {
            return "过了部署回合之后攻击应该成功";
        }

        return null;
    }

    /// <summary>
    /// **奋战（Fury）**：一回合可以攻击两次 —— 回归对局 `389594` 的 ⑤b。
    ///
    /// ## 修的是什么
    /// `CardInstance.CanOperateThisTurn` 原先用 `!HasAttackedThisTurn`（布尔）当攻击门，
    /// 于是**第一次攻击之后**这个单位本回合再也攻击不了。奋战单位因此永远只打一次。
    ///
    /// ## 蓝图判据（不是规则表口述）
    /// <list type="bullet">
    /// <item>回合开始把额度设成 `getHasFury() ? 2 : 1`：
    ///   `DoOnStartOfTurn`（`ref/kards-sim/.../_deps/BP_Logic.g.cs:3104/3110/3124`）。</item>
    /// <item>攻击一次额度 -1：`SetAttackerHasAttacked`
    ///   （`.../BP_CardFunctions.g.cs:33930-33942`）。</item>
    /// <item>`CanAttack` 的门是 `HasAttackLeft(attackerCard)`
    ///   （`.../_deps/cardsCheckFunctions.g.cs:761-805`）。</item>
    /// </list>
    ///
    /// ## 这条为什么必须断言「第三次被拒」
    /// 把门从布尔改成"额度 &gt; 0"时，最容易犯的错是**忘了减额度**
    /// （那样就变成无限次攻击）。所以正反两面都要钉：
    /// 奋战 = 恰好 2 次、无奋战 = 恰好 1 次、第三次必须拒。
    /// </summary>
    private static string? FuryAllowsSecondAttack(CardDatabase db)
    {
        const string fury = "card_unit_111th_indian_brigade";   // 卡面 Fury，步兵
        const string plain = "card_unit_arado_ar_196";          // 无 Fury
        if (db.Find(fury) is null || db.Find(plain) is null)
        {
            return $"卡库里缺 {fury} 或 {plain}";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        // 直接放到**前线**：这样 `CanReachAcrossFrontline` 的射程判据不会插手
        // （步兵 range=1，站半场时够不着对面 HQ，会把攻击失败的原因搅在一起）。
        var a = state.CreateWithId(fury, Side.Left, 2, CardLocation.BoardFrontline, 0);
        var b = state.CreateWithId(plain, Side.Left, 3, CardLocation.BoardFrontline, 1);
        foreach (var u in new[] { a, b })
        {
            u.EnteredPlayOnTurn = state.Turn - 1;   // 免召唤失调
            u.HasAttackedThisTurn = false;
            u.HasMovedThisTurn = false;
            u.AttacksThisTurn = 0;
        }

        if (!a.Keywords.Contains(Keyword.Fury, StringComparer.Ordinal))
        {
            return $"{fury} 的卡面 Fury 没被加载（CardInnateTable → Keywords）";
        }

        if (a.MaxAttacksThisTurn != 2 || b.MaxAttacksThisTurn != 1)
        {
            return $"额度上限错：奋战={a.MaxAttacksThisTurn}（应 2）、无奋战={b.MaxAttacksThisTurn}（应 1）";
        }

        // ---- 奋战：第一次 ----
        if (!engine.Attack(a, state.Hq(Side.Right)))
        {
            return $"奋战单位 {fury} 的**第一次**攻击就被拒了"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (!a.HasAttackLeft)
        {
            return $"奋战单位打完一次之后额度就该还剩 1（AttacksThisTurn={a.AttacksThisTurn}/" +
                   $"{a.MaxAttacksThisTurn}）";
        }

        // ---- 奋战：第二次（**修复前就死在这一条**）----
        if (!engine.Attack(a, state.Hq(Side.Right)))
        {
            return $"奋战单位 {fury} 的**第二次**攻击被拒了 —— Fury 的多次攻击没建模"
                 + Dump(state, ("已攻击次数", $"{a.AttacksThisTurn}/{a.MaxAttacksThisTurn}"),
                        ("HasAttackedThisTurn", a.HasAttackedThisTurn.ToString()));
        }

        // ---- 奋战：第三次必须拒（防止"只放行、不减额度"）----
        if (a.HasAttackLeft)
        {
            return $"奋战单位打完两次之后额度就该是 0（AttacksThisTurn={a.AttacksThisTurn}）";
        }

        if (engine.Attack(a, state.Hq(Side.Right)))
        {
            return $"奋战**只能**攻击两次，第三次竟然放行了（额度没被扣）";
        }

        // ---- 无奋战：恰好一次 ----
        if (!engine.Attack(b, state.Hq(Side.Right)))
        {
            return $"无奋战单位 {plain} 的第一次攻击被拒了（门收得太紧）";
        }

        if (engine.Attack(b, state.Hq(Side.Right)))
        {
            return $"无奋战单位 {plain} 不该能攻击两次";
        }

        return null;
    }

    /// <summary>
    /// **METEOR 的"攻击后留一张攻防翻倍的副本"** —— 回归对局 `389594` 的 ④（那 1 点 HQ 差）。
    ///
    /// 卡面（`cards.live.json`）：
    /// <c>Remove after this unit attacks. Add a copy to your deck with double attack and defense.</c>
    ///
    /// IR（`klink bot/docs/card-ir.json` → `card_unit_meteor`）：
    /// <code>
    /// OnAfterAttack i=745   IsLocatedOnBoard → 不在场直接返回
    ///              i=774   attTotal = getTotalAttack()
    ///              i=820   defTotal = getTotalDefense()
    ///              i=866   RemoveCardFromBoard(cardID)
    ///              i=929   SpawnCardInDeckBySide(…, "card_unit_meteor", …, out spawnedCardIDs)
    ///              i=1017  → 循环头 i=717/689/577
    ///              i=577   Array_Length(spawnedCardIDs)          ← ★ 出参不是数组时恒 0
    ///              i=636   Less_IntInt(计数, 长度) → popFlowIfNot  ⇒ 长度 0 = 整段跳过
    ///              i=47    attTotal * 2
    ///              i=89    Array_Get(spawnedCardIDs, idx)
    ///              i=148   GetCardFromID(item)
    ///              i=202   ChangeAttack(那张卡, cardID, attTotal*2, changeType=2 /*SetValue*/)
    ///              i=432   ChangeDefense(那张卡, cardID, defTotal*2, changeType=2)
    /// </code>
    ///
    /// 两处都要对，缺一不可：
    /// <list type="number">
    /// <item>出参必须是**数组**（`TArray&lt;int32&gt; spawnedCardIDs`，
    ///   签名 `_index.g.cs:4336`）—— 否则循环一次都不跑。</item>
    /// <item>`changeType=2` 是 **SetValue（总量赋成该值）**，不是"加"：
    ///   `ChangeAttack` 的 L_0357 分支 `setAndEncryptAttack(Clamp(amount,0,99))`
    ///   （`BP_CardFunctions.g.cs` 的 `ChangeAttack`，函数起始 `:6491`）。
    ///   按"加"算，1/1 的副本会变成 1+2=**3** 攻。</item>
    /// </list>
    ///
    /// 实测后果（修之前）：t7 生成的 `#7001` 是 1/1（客户端 2/2），
    /// t15 用它打右 HQ 只扣 1（客户端扣 2）⇒ `#73` 起 HQ 校验和差 1、闸门触发。
    /// </summary>
    private static string? MeteorCopyDoublesStats(CardDatabase db)
    {
        const string meteor = "card_unit_meteor";
        if (db.Find(meteor) is null)
        {
            return $"卡库里没有 {meteor}";
        }

        if (KismetLibrary.Default?.FindProgram(meteor, "OnAfterAttack") is null)
        {
            return $"IR 里没有 {meteor} 的 OnAfterAttack（card-ir.json 是否加载？）";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var m = state.CreateWithId(meteor, Side.Left, 2, CardLocation.BoardFrontline, 0);
        m.EnteredPlayOnTurn = state.Turn - 1;
        m.HasAttackedThisTurn = false;
        m.HasMovedThisTurn = false;
        m.AttacksThisTurn = 0;

        int atk = m.Attack, def = m.Defense;
        int hqBefore = state.HqDefense(Side.Right);
        if (!engine.Attack(m, state.Hq(Side.Right)))
        {
            return $"{meteor} 攻击被拒" + Dump(state, ("未实现", Unimpl(state)));
        }

        // 前置条件自检：原体必须是 1/1，否则"翻倍"这条断言就不是 2/2 了
        if (atk != 1 || def != 1)
        {
            return $"前提变了：{meteor} 卡面应为 1/1，实际 {atk}/{def}（本用例的期望值要跟着改）";
        }

        if (state.HqDefense(Side.Right) != hqBefore - atk)
        {
            return $"打 HQ 的伤害应是 {atk}，实际扣了 {hqBefore - state.HqDefense(Side.Right)}";
        }

        // ---- 原体必须被移出棋盘（卡面 "Remove after this unit attacks"）----
        if (m.Location.IsBoard())
        {
            return $"{meteor} 攻击之后必须离开棋盘，实际还在 {m.Location}";
        }

        // ---- 副本必须出现在牌库里，且是 2/2 ----
        var copies = state.Deck(Side.Left).Where(c => c.Name == meteor).ToList();
        if (copies.Count != 1)
        {
            return $"牌库里应当正好有 1 张生成的 {meteor} 副本，实际 {copies.Count} 张"
                 + Dump(state, ("牌库", string.Join("  ", state.Deck(Side.Left).Select(c => c.Name))),
                        ("未实现", Unimpl(state)));
        }

        var copy = copies[0];
        if (copy.Attack != atk * 2 || copy.Defense != def * 2)
        {
            return $"副本的攻/防应当是原值×2 = {atk * 2}/{def * 2}，实际 {copy.Attack}/{copy.Defense}"
                 + "（出参不是数组 ⇒ 循环没跑；或 changeType=2 被当成加法）";
        }

        // ---- 副本是**新的一张卡**，不能是原体本身换了个位置 ----
        if (copy.CardId == m.CardId)
        {
            return "生成的副本必须是新卡（新 cardID），不能复用原体的号";
        }

        return null;
    }

    /// <summary>
    /// 前线互斥（`BP_CardFunctions::MoveUnitFromSupportToFrontLine` i=281-543）：
    /// 对面占着前线 ⇒ 推不进去；我方占着且满（5）⇒ 也推不进去。
    /// **没有"把对面挤回半场"这条规则。**
    /// </summary>
    private static string? FrontlineMutualExclusion(CardDatabase db)
    {
        const string unit = "card_unit_arado_ar_196";
        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.SetKredits(Side.Right, 12);
        state.SetMaxKredits(Side.Right, 12);

        // 左方推一个上去
        state.ActiveSide = Side.Left;
        var left1 = state.CreateWithId(unit, Side.Left, 2, CardLocation.HandLeft, 0);
        engine.PlayCard(left1);
        left1.HasMovedThisTurn = false;
        left1.EnteredPlayOnTurn = state.Turn - 1;   // 免召唤失调
        if (!engine.MoveUnit(left1, 0))
        {
            return "空前线居然推不上去"
                 + Dump(state, ("未实现", Unimpl(state)),
                        ("前线归属", state.FrontlineOwner.ToString()));
        }

        if (state.FrontlineOwner != Side.Left)
        {
            return $"推上去之后前线归属应为 Left，实际 {state.FrontlineOwner}";
        }

        // 右方再推 → 必须被拒（对面占着）
        state.ActiveSide = Side.Right;
        var right1 = state.CreateWithId(unit, Side.Right, 42, CardLocation.HandRight, 0);
        engine.PlayCard(right1);
        right1.HasMovedThisTurn = false;
        right1.EnteredPlayOnTurn = state.Turn - 1;
        if (engine.MoveUnit(right1, 0))
        {
            return "对面占着前线时竟然推得进去（蓝图 i=333 应该拒绝）";
        }

        if (right1.Location == CardLocation.BoardFrontline)
        {
            return "被拒的推进不应该改变位置";
        }

        // 左方把前线填满（容量 5）→ 第 6 个必须被拒
        state.ActiveSide = Side.Left;
        for (int i = 0; i < 4; i++)
        {
            var u = state.CreateWithId(unit, Side.Left, 10 + i, CardLocation.HandLeft, i);
            engine.PlayCard(u);
            u.HasMovedThisTurn = false;
            u.EnteredPlayOnTurn = state.Turn - 1;
            engine.MoveUnit(u, i + 1);
        }

        int count = state.Cards(Side.Left, CardLocation.BoardFrontline).Count;
        if (count != GameState.DefaultFrontlineCapacity)
        {
            return $"前线应能站满 {GameState.DefaultFrontlineCapacity} 个，实际 {count}";
        }

        var extra = state.CreateWithId(unit, Side.Left, 20, CardLocation.HandLeft, 0);
        engine.PlayCard(extra);
        extra.HasMovedThisTurn = false;
        extra.EnteredPlayOnTurn = state.Turn - 1;
        if (engine.MoveUnit(extra, 0))
        {
            return "前线已满时竟然还推得进去（蓝图 i=543 应该拒绝）";
        }

        return null;
    }

    // ==================================================================
    //  前线归属残留（2026-10-01，用户实测 replay-214436）
    // ==================================================================

    /// <summary>
    /// 摆一个「左方在前线站着 1 个 1 防单位、右方**半场**有一门 range=2 的炮兵」的局面。
    ///
    /// 两个关键设计：
    /// <list type="bullet">
    /// <item>杀手放在**半场**而不是前线 —— 否则它自己就把前线占了，
    ///   归属永远不会变成 <c>NotAvailable</c>，测不到这个 bug。</item>
    /// <item>杀手的 <c>Attack</c> 抬到 9、被杀者 <c>Defense=1</c>，保证**真的一击致死**
    ///   （而不是把 <c>Defense</c> 设成 0 假装死了 —— 那只是"待销毁"，
    ///   真正的死亡发生在 <c>CheckDeaths()</c> 里，见 `DeadUnitCannotMoveOrAttack` 的注释）。</item>
    /// </list>
    /// </summary>
    private static (MatchEngine Engine, GameState State, CardInstance Victim, CardInstance Killer)?
        FrontlineDeathSetup(CardDatabase db)
    {
        if (db.Find(InfRange1) is null || db.Find(ArtRange2) is null)
        {
            return null;
        }

        var (engine, state) = EmptyBoard(db);
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            state.SetKredits(side, 20);
            state.SetMaxKredits(side, 20);
        }

        // 左方：先落在**自己半场**，再走真实路径 `MoveUnit` 推进前线
        // （直接 Create 到前线不会经过 `State.Move`，也就测不到钩子）。
        state.ActiveSide = Side.Left;
        var victim = state.CreateWithId(InfRange1, Side.Left, 2, CardLocation.BoardHqLeft, 1);
        victim.Attack = 0;               // 别让它反击把炮兵换掉，局面才好断言
        victim.Defense = 1;
        victim.MaxDefense = 1;
        victim.EnteredPlayOnTurn = -99;

        if (!engine.MoveUnit(victim, 0))
        {
            return null;
        }

        if (state.FrontlineOwner != Side.Left)
        {
            return null;
        }

        // 右方：半场槽 1 上的炮兵（range=2 ⇒ 够得着前线；自己**不**进前线）
        var killer = state.CreateWithId(ArtRange2, Side.Right, 42, CardLocation.BoardHqRight, 1);
        killer.Attack = 9;
        killer.Defense = 9;
        killer.MaxDefense = 9;
        killer.EnteredPlayOnTurn = -99;

        if (db.Find(ArtRange2)!.Range < 2)
        {
            return null;
        }

        return (engine, state, victim, killer);
    }

    /// <summary>
    /// **前线最后一个单位死亡后，`FrontlineOwner` 必须回到 `NotAvailable`。**
    ///
    /// 旧代码这条必挂：`Destroy` 里的 `State.Move(card, CardLocation.Discard)`
    /// 把卡搬出前线，却没有任何人重算归属 ⇒ 归属残留在 `Side.Left`。
    /// </summary>
    private static string? FrontlineOwnerResetOnLastUnitDeath(CardDatabase db)
    {
        var made = FrontlineDeathSetup(db);
        if (made is null)
        {
            return $"造不出局面（缺 {InfRange1} / {ArtRange2} 或推进被拒）—— 本测试前提不成立";
        }

        var (engine, state, victim, killer) = made.Value;

        if (state.FrontlineOwner != Side.Left)
        {
            return $"推进后前线归属应为 Left，实际 {state.FrontlineOwner}（前置不成立）";
        }

        // 右方开炮（半场 range=2 → 前线）
        state.ActiveSide = Side.Right;
        if (!engine.Attack(killer, victim))
        {
            return $"半场炮兵(range=2) 打不到前线单位 —— 本测试前提不成立"
                 + Dump(state, ("未实现", Unimpl(state)), ("前线归属", state.FrontlineOwner.ToString()));
        }

        if (victim.Location != CardLocation.Discard)
        {
            return $"1 防单位挨了 9 点伤害却没进弃牌堆（Location={victim.Location}，"
                 + $"Defense={victim.Defense}）—— 判死链路有问题";
        }

        if (state.FrontlineOwner != Side.NotAvailable)
        {
            return $"**前线最后一个单位已死，归属却仍是 {state.FrontlineOwner}**"
                 + $"（前线现有 {state.Cards(Side.Left, CardLocation.BoardFrontline).Count()}"
                 + $"+{state.Cards(Side.Right, CardLocation.BoardFrontline).Count()} 张）"
                 + " —— `Destroy` 把卡搬出前线却没重算归属，"
                 + "旧代码就是这样把 AI 的推进永久堵死的";
        }

        return null;
    }

    /// <summary>
    /// **归属残留的杀伤力：死者的对面必须能重新推进前线。**
    ///
    /// 这条是"症状复现"：旧代码里前线明明空了，`MoveUnit` 却因为
    /// `FrontlineOwner` 残留在 Left 而走 `<frontline-blocked-by-opponent>` 分支被拒
    /// —— 而 `MoveUnit` 的互斥门本身是**对的**（蓝图 i=281/333），
    /// 错的是没人清归属。所以这里断言的是**端到端可达性**，不是那道门。
    /// </summary>
    private static string? FrontlineRetakeableAfterOwnerDied(CardDatabase db)
    {
        var made = FrontlineDeathSetup(db);
        if (made is null)
        {
            return $"造不出局面（缺 {InfRange1} / {ArtRange2} 或推进被拒）—— 本测试前提不成立";
        }

        var (engine, state, victim, killer) = made.Value;

        // 右方先在半场备一个能推进的单位（半场槽 2）
        var mover = state.CreateWithId(InfRange1, Side.Right, 43, CardLocation.BoardHqRight, 2);
        mover.Attack = 1;
        mover.Defense = 3;
        mover.MaxDefense = 3;
        mover.EnteredPlayOnTurn = -99;

        // ① 归属还挂在 Left 时，右方推进**必须被拒**（互斥门 i=281/333 是对的，别动它）
        state.ActiveSide = Side.Right;
        if (engine.MoveUnit(mover, 0))
        {
            return "对面（Left）占着前线时右方竟然推得进去 —— 互斥门坏了";
        }

        int blocked = state.UnimplementedCalls.GetValueOrDefault("<frontline-blocked-by-opponent>");

        // ② 打死左方前线的最后一个单位
        if (!engine.Attack(killer, victim))
        {
            return $"半场炮兵(range=2) 打不到前线单位 —— 本测试前提不成立"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (state.FrontlineOwner != Side.NotAvailable)
        {
            return $"单位已死但归属仍是 {state.FrontlineOwner} —— 见上一条用例";
        }

        // ③ 现在前线真空了 ⇒ 右方**必须**推得进去（旧代码在这里被误拒）
        mover.HasMovedThisTurn = false;
        mover.HasAttackedThisTurn = false;
        state.ActiveSide = Side.Right;
        if (!engine.MoveUnit(mover, 0))
        {
            int blockedAfter = state.UnimplementedCalls.GetValueOrDefault("<frontline-blocked-by-opponent>");
            return "**前线已空，右方却推不进去**"
                 + $"（`<frontline-blocked-by-opponent>` 计数 {blocked}→{blockedAfter}，"
                 + $"前线归属={state.FrontlineOwner}）—— 这正是「AI 只能空过」的症状"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (state.FrontlineOwner != Side.Right)
        {
            return $"右方推进后归属应为 Right，实际 {state.FrontlineOwner}";
        }

        return null;
    }

    /// <summary>
    /// `card_event_fog_of_war`（英国 4 费指令，卡面：
    /// 「Remove target unit from the battlefield. Put two copies on top of owner's deck.」）
    /// —— 对局 `773639` `#45 t9 ML` 的根因用例。
    ///
    /// ## 现场证据（`out/_server-replays/replay-773639.actions.json`）
    ///
    /// <code>
    /// #25 t7 R ML {"0":"60", "1":"0"}       ; bot 把 card_unit_1st_airborne#60 推上前线
    /// #29 t7 L PC {"0":"19", "2":"60"}      ; 人类打 19=card_event_fog_of_war，目标正是 #60
    /// #45 t9 L ML {"0":"21", "1":"0"}       ; 人类推 card_unit_no_46_commando#21 上前线
    /// </code>
    ///
    /// 客户端里 `#60` 在 `#29` 就被移出战场了，所以 `#45` 的推进是合法的；
    /// 我们这边 `#60` 一直待在前线（审计 ① 直到 `#64 t13` 才报它离场），
    /// `FrontlineOwner` 仍是 `Right` ⇒ `#45` 被 `MoveUnit` 的互斥门拒
    /// （审计 ⑤b：`移动被拒：前线被对面占着（前线归属=Right，本单位=Left）`）。
    ///
    /// ## IR（`klink bot/docs/card-ir.json` → `card_event_fog_of_war`）
    ///
    /// <code>
    /// entrypoints: { "OnPlayedFromHand": 10 }
    /// i=10   RemoveCardFromBoard(cardID@targetCard, cardID@self, out qqq)
    /// i=95   SpawnCardInDeckBySide(originalSide, name, cardID@self, 2, salvageFaction, …)
    /// i=252  return
    /// </code>
    /// 只有两条语句，所以**不可能是** `MaxStepsPerProgram` 截断（那个坑见
    /// `AtlanticConvoyBoardAndHand`）。
    ///
    /// ## 断言口径（直接断言中间状态，不靠"连打 N 局看结果"）
    ///
    /// 1. 目标必须**离开战场**（`Location.IsBoard()` 为假）
    /// 2. `FrontlineOwner` 必须回到 `NotAvailable`（这是 `#45` 被误拒的直接原因）
    /// 3. 目标所有者的牌库必须多出 **2 张**同名卡（i=95 的 `SpawnCardInDeckBySide(…, 2, …)`）
    /// </summary>
    private static string? FogOfWarRemovesTargetFromBattlefield(CardDatabase db)
    {
        const string Card = "card_event_fog_of_war";

        if (db.Find(Card) is null)
        {
            return $"卡库里缺 {Card}";
        }

        if (KismetLibrary.Default?.FindProgram(Card, "OnPlayedFromHand") is null)
        {
            return $"IR 里没有 {Card} 的 OnPlayedFromHand（card-ir.json 是否加载？）";
        }

        var (engine, state) = EmptyBoard(db);
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            state.SetKredits(side, 20);
            state.SetMaxKredits(side, 20);
        }

        // 右方一个单位在**前线**：走真实路径 `MoveUnit`，这样 `FrontlineOwner`
        // 才会被算成 `Right`（直接 Create 到前线不经过换区钩子，见 `FrontlineDeathSetup`）。
        state.ActiveSide = Side.Right;
        var victim = state.CreateWithId(InfRange1, Side.Right, 42, CardLocation.BoardHqRight, 1);
        victim.EnteredPlayOnTurn = -99;

        if (!engine.MoveUnit(victim, 0, out string mvWhy))
        {
            return $"前置不成立：右方推不进空前线（{mvWhy}）";
        }

        if (state.FrontlineOwner != Side.Right)
        {
            return $"前置不成立：推上去之后前线归属应为 Right，实际 {state.FrontlineOwner}";
        }

        // 左方从手牌打出雾战，目标是那个前线单位。
        state.ActiveSide = Side.Left;
        var order = state.CreateWithId(Card, Side.Left, 2, CardLocation.HandLeft, 0);
        order.EnteredPlayOnTurn = -99;

        if (!engine.CanPlay(order, out string why))
        {
            return $"前置不成立：雾战打不出（{why}）";
        }

        int deckBefore = state.Deck(Side.Right).Count;
        int leftDeckBefore = state.Deck(Side.Left).Count;
        int unimplBefore = state.UnimplementedCalls.Count;
        engine.PlayCard(order, victim);

        if (victim.Location.IsBoard())
        {
            return $"**雾战没有把目标移出战场**（目标仍在 {victim.Location}#{victim.LocationNumber}）"
                 + " —— IR i=10 `RemoveCardFromBoard` 这一路没生效"
                 + Dump(state, ("未实现", Unimpl(state)),
                        ("前线归属", state.FrontlineOwner.ToString()));
        }

        if (state.FrontlineOwner != Side.NotAvailable)
        {
            return $"目标已离场，但 `FrontlineOwner` 仍是 {state.FrontlineOwner}"
                 + " —— 前线归属没跟着释放，对面会被互斥门误拒（773639 #45 的症状）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        int spawned = state.Deck(Side.Right).Count - deckBefore;
        if (spawned != 2)
        {
            return $"i=95 `SpawnCardInDeckBySide(…, 2, …)` 应该往**目标所有者**（Right）的牌库塞 2 张，" +
                   $"实际 {spawned} 张（左方牌库变化 {state.Deck(Side.Left).Count - leftDeckBefore}）" +
                   " —— `originalSide` 没被认出来时 `SideArg` 会退回施法者"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        if (state.UnimplementedCalls.Count > unimplBefore)
        {
            return "雾战执行期间撞到了新的未实现原语"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        return null;
    }

    private static string? SpawnCardInDeckHonorsBottom(CardDatabase db)
    {
        const string sourceName = "card_event_confusion";
        const string existingName = "card_unit_arado_ar_196";
        const string spawnedName = "card_unit_1st_infantry_regiment_us";
        foreach (string name in new[] { sourceName, existingName, spawnedName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 2, CardLocation.Discard, 0);
        var first = state.CreateWithId(existingName, Side.Right, 42, CardLocation.DeckRight, 0);
        var second = state.CreateWithId(existingName, Side.Right, 43, CardLocation.DeckRight, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        // side=Right is the integer 2; it must not override bottom=true.
        object? bottomResult = engine.Api.InvokeByName("SpawnCardInDeckBySide", source,
            new object?[] { (int)Side.Right, spawnedName, source.CardId, 1, "", false,
                true, false, false, false, null }, ctx, out bool bottomHandled);
        if (!bottomHandled || bottomResult is not System.Collections.IEnumerable)
        {
            return "SpawnCardInDeckBySide(bottom=true) 未接入派发表";
        }

        var deck = state.Deck(Side.Right);
        if (deck.Count != 3 || !ReferenceEquals(deck[0], first) || !ReferenceEquals(deck[1], second)
            || deck[2].Name != spawnedName)
        {
            return $"bottom=true 应把新卡放到牌库底，实际顺序：{string.Join(",", deck.Select(x => x.Name))}";
        }

        object? topResult = engine.Api.InvokeByName("SpawnCardInDeckBySide", source,
            new object?[] { (int)Side.Right, spawnedName, source.CardId, 1, "", false,
                false, false, false, false, null }, ctx, out bool topHandled);
        if (!topHandled || topResult is not System.Collections.IEnumerable)
        {
            return "SpawnCardInDeckBySide(bottom=false) 未接入派发表";
        }

        deck = state.Deck(Side.Right);
        if (deck.Count != 4 || deck[0].Name != spawnedName || !ReferenceEquals(deck[1], first)
            || !ReferenceEquals(deck[2], second))
        {
            return $"bottom=false 应把新卡放到牌库顶，实际顺序：{string.Join(",", deck.Select(x => x.Name))}";
        }

        return null;
    }

    private static string? SpawnCardInDeckRandomInsertion(CardDatabase db)
    {
        const string sourceName = "card_event_confusion";
        const string existingName = "card_unit_arado_ar_196";
        const string spawnedName = "card_unit_1st_infantry_regiment_us";
        foreach (string name in new[] { sourceName, existingName, spawnedName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 2, CardLocation.Discard, 0);
        var first = state.CreateWithId(existingName, Side.Right, 42, CardLocation.DeckRight, 0);
        var second = state.CreateWithId(existingName, Side.Right, 43, CardLocation.DeckRight, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        var expectedRandom = new UeRandomStream(1);
        int firstIndex = expectedRandom.RandRange(0, 3);
        int secondIndex = expectedRandom.RandRange(0, 4);
        object? result = engine.Api.InvokeByName("SpawnCardInDeckBySide", source,
            new object?[] { (int)Side.Right, spawnedName, source.CardId, 2, "", false,
                true, false, false, true, null }, ctx, out bool handled);
        if (!handled || result is not System.Collections.IEnumerable)
        {
            return "SpawnCardInDeckBySide(RandomWithoutShuffle=true) 未接入派发表";
        }

        var spawnedIds = (result as System.Collections.IEnumerable)!
            .Cast<int>().ToList();
        if (spawnedIds.Count != 2 || state.Random.ConsumedCount != 2)
        {
            return $"随机模式应生成 2 张且消费 2 次随机流，实际生成 {spawnedIds.Count} 张、消费 {state.Random.ConsumedCount} 次";
        }

        var expected = new List<int> { first.CardId, second.CardId };
        expected.Insert(Math.Min(firstIndex, expected.Count), spawnedIds[0]);
        expected.Insert(Math.Min(secondIndex, expected.Count), spawnedIds[1]);
        var actual = state.Deck(Side.Right).Select(card => card.CardId).ToList();
        if (!actual.SequenceEqual(expected))
        {
            return $"随机模式应按索引插入（first={firstIndex}, second={secondIndex}），实际顺序：" +
                   string.Join(",", actual) + "，期望：" + string.Join(",", expected);
        }

        return null;
    }

    private static string? SpawnCardInDeckShuffle(CardDatabase db)
    {
        const string sourceName = "card_event_confusion";
        const string existingName = "card_unit_arado_ar_196";
        const string spawnedName = "card_unit_1st_infantry_regiment_us";
        foreach (string name in new[] { sourceName, existingName, spawnedName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 2, CardLocation.Discard, 0);
        var first = state.CreateWithId(existingName, Side.Right, 42, CardLocation.DeckRight, 0);
        var second = state.CreateWithId(existingName, Side.Right, 43, CardLocation.DeckRight, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("SpawnCardInDeckBySide", source,
            new object?[] { (int)Side.Right, spawnedName, source.CardId, 1, "", false,
                true, true, false, false, null }, ctx, out bool handled);
        if (!handled || result is not System.Collections.IEnumerable)
        {
            return "SpawnCardInDeckBySide(shuffle=true) 未接入派发表";
        }

        var spawnedIds = (result as System.Collections.IEnumerable)!
            .Cast<int>().ToList();
        if (spawnedIds.Count != 1)
        {
            return $"shuffle=true 应返回 1 个新卡 ID，实际 {spawnedIds.Count} 个";
        }

        var expectedRandom = new UeRandomStream(1);
        expectedRandom.RandRange(0, 3); // one generated card, even when RandomWithoutShuffle=false
        var expected = new List<int> { first.CardId, second.CardId, spawnedIds[0] };
        expectedRandom.Shuffle(expected);

        var actual = state.Deck(Side.Right).Select(card => card.CardId).ToList();
        if (!actual.SequenceEqual(expected))
        {
            return "shuffle=true 应按蓝图随机流洗牌，实际顺序：" + string.Join(",", actual) +
                   "，期望：" + string.Join(",", expected);
        }

        if (state.Random.ConsumedCount != 4)
        {
            return $"生成 1 张后洗 3 张应消费 4 次随机流，实际 {state.Random.ConsumedCount} 次";
        }

        return null;
    }

    // ⚠️ 2026-10-02：`KreditSlotsGrowEveryTurn`（断言"kredit 槽位每回合双方各 +1"）
    // **永久撤下，不要恢复** —— 它断言的模型是**错的**。
    //
    // 定案：槽位 = **自己第几个回合**（+ 卡牌效果的额外槽），即 `StartTurn` 里现成那段。
    // 证据：真人玩家的规则描述（"对面也变成1 / 我第2回合变成2 / 战争机器+1→3 /
    // 我的下个回合自然增长→4"）。
    //
    // 上一轮之所以会怀疑它，是拿 **`cards.live.json` 的卡面费用**当花费反推槽位 ——
    // 而**卡面费用 ≠ 实际支付**，偏差两个方向都有：PAMS 开发出来的牌客户端当 0 费
    // （`card_event_pams` IR i=348 `ChangeKreditCost(卡,自己,0,changeType=2)`）会被**高估**；
    // 回合内的 `iron_from_the_north`（+3 kredit）/ `war_bonds`（+2 槽）会被**低估**。
    // 换成实际支付重算（`tools/ServerBridgeTest --kredit-table`）后，
    // 4 局 / 60 个人类回合里 self 模型**违反 0 条**，原先那"5 条违反"全部消失。
    //
    // ⚠️ 那张表**不能**反过来当"全局回合号"的证据：global 槽位恒 ≥ self 槽位，
    // 「花费 ≤ 槽位」只能证伪 self、永远证伪不了 global，是**单侧弱约束**。
    // 完整说明在 `MatchEngine.StartTurn` 的「已定案」注释里。

    /// <summary>
    /// **直接生成到前线（不是推进）也必须更新 `FrontlineOwner`。**
    ///
    /// 为什么单独一条：`CardApi.SpawnOnBattlefield` 的落点是
    /// `State.Create(name, side, BoardFrontline, 0)`，紧接着
    /// `State.Move(card, BoardFrontline, slot)` —— 这是**同区移动**，
    /// `GameState.Move` 里 `moved == false` ⇒ **换区钩子不发** ⇒ 归属不会更新。
    ///
    /// 蓝图在 `SpawnCardToBoard` 里是**显式**补这一句的
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`）：
    /// <code>
    ///   L_03D3  EqualEqual_ByteByte(location, 7)
    ///   L_03F2  JumpIfNot L_0417
    ///   L_0400  UpdateFrontlineIfNeeded(spawnedID)     ← ★ 显式，不靠换区钩子
    /// </code>
    /// 走这条路的真实入口是 `SpawnCardInFrontline`（108 调用点 / 29 张卡，
    /// 例 `card_event_airdrop` i=219）。
    ///
    /// ## 断言口径
    ///
    /// 第一条断言中间状态（`FrontlineOwner` 变成生成方）；
    /// 第二条断言**后果** —— 不修的话互斥门形同虚设，对面能推进到**已经被占**的前线。
    /// </summary>
    /// <summary>
    /// `JSON_Clear(card, variableName, out found)` —— 蓝图 `BP_CardFunctions.g.cs:24383-24395`：
    /// 先 `JsonHasField` 查，**只删指定的那一个键**，并把 `found = existed` 写出去。
    /// 旧实现 `card.CustomJson.Clear()` 会**清空整张表**、忽略键名、且 `found` 从不写入。
    /// </summary>
    private static string? JsonClearRemovesOnlyNamedKey(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var card = engine.Api.SpawnOnBattlefield(Side.Left, InfRange1, frontline: false);

        engine.Api.JsonSetInt(card, "keep", 7);
        engine.Api.JsonSetInt(card, "drop", 9);

        bool found = engine.Api.JsonClear(card, "drop");
        if (!found)
        {
            return "**`JsonClear` 对**存在**的键回报 false**（应 true）"
                 + Dump(state, ("customJson", string.Join(",", card.CustomJson.Select(kv => $"{kv.Key}={kv.Value}"))));
        }

        if (card.CustomJson.ContainsKey("drop"))
        {
            return "**`JsonClear` 没有删掉指定的键**"
                 + Dump(state, ("customJson", string.Join(",", card.CustomJson.Select(kv => $"{kv.Key}={kv.Value}"))));
        }

        if (engine.Api.JsonGetInt(card, "keep") != 7)
        {
            return "**`JsonClear` 把**别的**键也删了** —— 旧实现是 `CustomJson.Clear()` 清空整张表，"
                 + "蓝图只 `JsonRemoveField(…, variableName)`"
                 + Dump(state, ("customJson", string.Join(",", card.CustomJson.Select(kv => $"{kv.Key}={kv.Value}"))));
        }

        if (engine.Api.JsonClear(card, "nope"))
        {
            return "**`JsonClear` 对**不存在**的键回报 true**（应 false）";
        }

        return null;
    }

    private static string? JsonRemoveFromIntArray(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var card = engine.Api.SpawnOnBattlefield(Side.Left, InfRange1, frontline: false);
        engine.Api.JsonSetInt(card, "keep", 7);
        engine.Api.JsonSetIntArray(card, "values", new[] { 4, 7, 4, 9 });
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = card, Controller = Side.Left,
        };

        object? removed = engine.Api.InvokeByName("JSON_RemoveFromIntArray", card,
            new object?[] { card, "values", 4, null }, ctx, out bool handled);
        if (!handled || removed is not true
            || !engine.Api.JsonGetIntArray(card, "values").SequenceEqual(new[] { 7, 4, 9 }))
        {
            return $"应只移除第一个匹配值并返回 found=true（handled={handled}, result={removed ?? "null"}, "
                 + $"values={string.Join(",", engine.Api.JsonGetIntArray(card, "values"))}）";
        }

        if (engine.Api.JsonGetInt(card, "keep") != 7)
        {
            return "移除数组元素不应影响同卡其他 JSON 字段";
        }

        object? absent = engine.Api.InvokeByName("JSON_RemoveFromIntArray", card,
            new object?[] { card, "values", 5, null }, ctx, out bool absentHandled);
        if (!absentHandled || absent is not false
            || !engine.Api.JsonGetIntArray(card, "values").SequenceEqual(new[] { 7, 4, 9 }))
        {
            return "移除不存在的值应返回 found=false 且保持数组不变";
        }

        object? missing = engine.Api.InvokeByName("JSON_RemoveFromIntArray", card,
            new object?[] { card, "missing", 1, null }, ctx, out bool missingHandled);
        if (!missingHandled || missing is not false || card.CustomJson.ContainsKey("missing"))
        {
            return "缺少 JSON 数组字段时应返回 found=false，且不能新建字段";
        }

        return null;
    }

    private static string? CustomGameplayTagDispatch(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = engine.Api.SpawnOnBattlefield(Side.Left, InfRange1, frontline: false);
        var target = engine.Api.SpawnOnBattlefield(Side.Left, InfRange1, frontline: false);
        engine.Api.JsonSetInt(target, "keep", 7);
        engine.Api.CustomAbilityAdd(target, "destruction", source);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Target = target, Controller = Side.Left,
        };

        object? added = engine.Api.InvokeByName("AddCustomGameplayTag", source,
            new object?[] { new List<string> { "Subtype.Navy" }, target.CardId, source.CardId, null },
            ctx, out bool addHandled);
        if (!addHandled || added is not null || !engine.Api.HasCustomGameplayTag(target, "subtype.navy"))
        {
            return $"AddCustomGameplayTag 未按目标 cardID 生效（handled={addHandled}, tag={engine.Api.HasCustomGameplayTag(target, "subtype.navy")})";
        }

        engine.Api.InvokeByName("AddCustomGameplayTag", source,
            new object?[] { new List<string> { "subtype.navy" }, target.CardId, source.CardId, null }, ctx, out _);
        if (target.CustomJson.GetValueOrDefault("__customGameplayTags") != "subtype.navy")
        {
            return "重复添加 GameplayTag 不应产生重复存储";
        }

        if (!engine.Api.HasCustomGameplayTag(target, "subtype.navy")
            || !engine.Api.HasCustomAbility(target, "destruction")
            || engine.Api.JsonGetInt(target, "keep") != 7)
        {
            return "动态 GameplayTag 不应污染 CustomAbility 或其他 JSON 字段";
        }

        object? removed = engine.Api.InvokeByName("RemoveCustomGameplayTag", source,
            new object?[] { new List<string> { "SUBTYPE.NAVY" }, target.CardId, source.CardId, false, null },
            ctx, out bool removeHandled);
        if (!removeHandled || removed is not null || engine.Api.HasCustomGameplayTag(target, "subtype.navy")
            || target.CustomJson.ContainsKey("__customGameplayTags"))
        {
            return "RemoveCustomGameplayTag 未正确删除动态标签";
        }

        var staticNavy = db.Find("card_event_hms_belfast");
        if (staticNavy is not null)
        {
            var staticCard = engine.Api.SpawnOnBattlefield(Side.Left, staticNavy.Name, frontline: false);
            var staticCtx = new EffectContext
            {
                Engine = engine, State = state, Self = staticCard, Controller = Side.Left,
            };
            object? result = engine.Api.InvokeByName("getHasGameplayTag", staticCard,
                new object?[] { new List<string> { "subtype.navy" }, null }, staticCtx, out bool handled);
            if (!handled || result is not true)
            {
                return "删除动态标签不应影响卡面静态 GameplayTag 查询";
            }
        }

        return null;
    }

    private static string? SpawnToFrontlineUpdatesOwner(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            state.SetKredits(side, 20);
            state.SetMaxKredits(side, 20);
        }

        if (state.FrontlineOwner != Side.NotAvailable)
        {
            return $"前置不成立：开局前线应为空，实际归属 {state.FrontlineOwner}";
        }

        // 左方**直接生成**到前线 —— 不是 `MoveUnit` 推进
        var spawned = engine.Api.SpawnOnBattlefield(Side.Left, InfRange1, frontline: true);
        if (spawned.Location != CardLocation.BoardFrontline)
        {
            return $"前置不成立：`SpawnOnBattlefield(frontline: true)` 没落到前线（{spawned.Location}）";
        }

        if (state.FrontlineOwner != Side.Left)
        {
            return $"**直接生成到前线之后 `FrontlineOwner` 仍是 {state.FrontlineOwner}**（应为 Left）"
                 + " —— `Create` 到前线 + `Move` 到同一区 ⇒ 换区钩子不发 ⇒ 归属没重算"
                 + Dump(state, ("前线归属", state.FrontlineOwner.ToString()));
        }

        // 后果断言：右方推进必须被互斥门拒（不修的话这里会推得进去）
        state.ActiveSide = Side.Right;
        var mover = state.CreateWithId(InfRange1, Side.Right, 42, CardLocation.BoardHqRight, 1);
        mover.EnteredPlayOnTurn = -99;

        if (engine.MoveUnit(mover, 0, out string why))
        {
            return "对面**直接生成**到前线之后，右方竟然还推得进去 —— 互斥门失效"
                 + Dump(state, ("前线归属", state.FrontlineOwner.ToString()));
        }

        if (!why.Contains("对面占着", StringComparison.Ordinal))
        {
            return $"被拒原因应是「前线被对面占着」，实际：{why}";
        }

        return null;
    }

    /// <summary>
    /// **归属真的变化时 `OnFrontlineOwnershipChange` 必须被派发；没变时一次都不许发。**
    ///
    /// 断言口径：读 `CardApi.TriggerTrace`（现成的派发诊断记录），而不是看某张卡的
    /// 效果 —— 探针卡用真实卡 `card_unit_panzer_35t_commander`，它自己就订阅了
    /// `OnFrontlineOwnershipChange`（`klink bot/docs/card-ir.json` i=463）。
    ///
    /// ⚠️ **必须让探针卡自己当事件主体**（由它本人推进前线）。原因是 `FireTrigger`
    /// 的派发约定：`OnFrontlineOwnershipChange` 不以 `OnOther` 开头 ⇒ 走"只有主体
    /// 自己"那一路（`CardApi.cs:227-252`），非主体的订阅者只会在
    /// `OnOtherFrontlineOwnershipChange` 那一路被派发 —— 而全卡池里
    /// **`OnOtherFrontlineOwnershipChange` 的订阅者是 0 张**、`OnFrontlineOwnershipChange`
    /// 是 28 张（`card-ir.json` 全量扫描）。也就是说这个事件的"广播"其实到不了那 28 张卡，
    /// 那是**另一个**（派发层的）bug，已单独记录在报告里，不在这条用例的断言范围。
    ///
    /// 四个子断言，正好覆盖蓝图 `UpdateFrontlineIfNeeded` 的两条门
    /// （si=352 判等后返回 / si=186 空且本来就没归属时返回）：
    /// <list type="number">
    /// <item>空前线 → 推进：归属 NotAvailable→Left **必须发**</item>
    /// <item>已有归属 → 再推进（挪槽位）：归属没变 **不许发**</item>
    /// <item>杀掉"多出来的"那个：归属仍没变 **不许发**</item>
    /// <item>杀掉最后一个：归属 Left→NotAvailable **必须发**（本 bug 的主战场）</item>
    /// </list>
    /// </summary>
    private static string? FrontlineOwnershipTriggerDispatched(CardDatabase db)
    {
        const string probe = "card_unit_panzer_35t_commander";
        if (db.Find(probe) is null) return $"卡库里缺 {probe}";
        if (db.Find(InfRange1) is null) return $"卡库里缺 {InfRange1}";
        if (db.Find(ArtRange2) is null) return $"卡库里缺 {ArtRange2}";

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);
        state.SetKredits(Side.Right, 20);
        state.SetMaxKredits(Side.Right, 20);
        state.ActiveSide = Side.Left;

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        bool OwnershipFired() => trace.Any(t => t.StartsWith("OnFrontlineOwnershipChange", StringComparison.Ordinal));

        // ---- ① 空前线推进 ⇒ 归属变了 ⇒ 必须发 ----
        var probeCard = state.CreateWithId(probe, Side.Left, 5, CardLocation.BoardHqLeft, 1);
        probeCard.Attack = 2;
        probeCard.Defense = 2;
        probeCard.MaxDefense = 2;
        probeCard.EnteredPlayOnTurn = -99;

        if (state.FrontlineOwner != Side.NotAvailable)
        {
            return $"开局归属应为 NotAvailable，实际 {state.FrontlineOwner}";
        }

        if (!engine.MoveUnit(probeCard, 0))
        {
            return "空前线居然推不上去（前置不成立）" + Dump(state, ("未实现", Unimpl(state)));
        }

        if (state.FrontlineOwner != Side.Left)
        {
            return $"推进后归属应为 Left，实际 {state.FrontlineOwner}";
        }

        if (!OwnershipFired())
        {
            return $"归属 NotAvailable→Left 真的变了，`OnFrontlineOwnershipChange` 却没派发到订阅卡 {probe}"
                 + Dump(state, ("未实现", Unimpl(state)),
                        ("前线归属", state.FrontlineOwner.ToString()),
                        ("派发记录", trace.Count == 0 ? "（空）" : string.Join(" | ", trace.Take(8))));
        }

        // ---- ② 归属没变（Left 仍占着）⇒ 不许发 ----
        trace.Clear();
        var second = state.CreateWithId(InfRange1, Side.Left, 6, CardLocation.BoardHqLeft, 2);
        second.Attack = 1;
        second.Defense = 1;
        second.MaxDefense = 1;
        second.EnteredPlayOnTurn = -99;

        if (!engine.MoveUnit(second, 1))
        {
            return "已占前线时第二个单位推不上去（前置不成立）"
                 + Dump(state, ("未实现", Unimpl(state)), ("前线归属", state.FrontlineOwner.ToString()));
        }

        if (state.FrontlineOwner != Side.Left)
        {
            return $"第二个单位推进后归属仍应为 Left，实际 {state.FrontlineOwner}";
        }

        if (OwnershipFired())
        {
            return "归属**没变**却又发了一次 `OnFrontlineOwnershipChange`"
                 + "（蓝图 si=352 `EnumCompareSide(old, FirstCard.side)` 相等 ⇒ 直接返回）";
        }

        // ---- ③ 杀掉"多出来的"那个：前线还有人 ⇒ 归属没变 ⇒ 不许发 ----
        var killerA = state.CreateWithId(ArtRange2, Side.Right, 42, CardLocation.BoardHqRight, 1);
        killerA.Attack = 9; killerA.Defense = 9; killerA.MaxDefense = 9; killerA.EnteredPlayOnTurn = -99;
        state.ActiveSide = Side.Right;
        if (!engine.Attack(killerA, second))
        {
            return "半场炮兵(range=2) 打不到前线单位（前置不成立）" + Dump(state, ("未实现", Unimpl(state)));
        }

        if (state.FrontlineOwner != Side.Left)
        {
            return $"前线还剩 {probe} 时归属应保持 Left，实际 {state.FrontlineOwner}";
        }

        if (OwnershipFired())
        {
            return "前线还剩单位（归属没变）却又发了一次 `OnFrontlineOwnershipChange`";
        }

        // ---- ④ 杀掉最后一个 ⇒ 归属 Left→NotAvailable ⇒ 必须发（本 bug 的主战场）----
        trace.Clear();
        var killerB = state.CreateWithId(ArtRange2, Side.Right, 43, CardLocation.BoardHqRight, 2);
        killerB.Attack = 9; killerB.Defense = 9; killerB.MaxDefense = 9; killerB.EnteredPlayOnTurn = -99;
        state.ActiveSide = Side.Right;
        if (!engine.Attack(killerB, probeCard))
        {
            return "半场炮兵(range=2) 打不到前线单位（前置不成立）" + Dump(state, ("未实现", Unimpl(state)));
        }

        if (probeCard.Location != CardLocation.Discard)
        {
            return $"{probe} 挨了 9 点伤害却没进弃牌堆（Location={probeCard.Location}）";
        }

        if (state.FrontlineOwner != Side.NotAvailable)
        {
            return $"**前线最后一个单位已死，归属却仍是 {state.FrontlineOwner}** —— 见上一条用例";
        }

        if (!OwnershipFired())
        {
            return "归属 Left→NotAvailable 真的变了（单位死在 `Destroy` 的 `State.Move` 里），"
                 + "`OnFrontlineOwnershipChange` 却没派发 —— 旧代码就是连这一步都没有"
                 + Dump(state, ("未实现", Unimpl(state)),
                        ("派发记录", trace.Count == 0 ? "（空）" : string.Join(" | ", trace.Take(8))));
        }

        return null;
    }

    private static string? FrontlineOwnershipTriggerReachesHand(CardDatabase db)
    {
        const string probe = "card_unit_panzer_35t_commander";
        if (db.Find(probe) is null) return $"卡库里缺 {probe}";

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);
        state.ActiveSide = Side.Left;

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        var handProbe = state.CreateWithId(probe, Side.Left, 500, CardLocation.HandLeft, 0);
        var mover = state.CreateWithId(InfRange1, Side.Left, 501, CardLocation.BoardHqLeft, 1);
        mover.EnteredPlayOnTurn = -99;

        if (!engine.MoveUnit(mover, 0, out string reason))
        {
            return $"前线归属变化前置失败：{reason}";
        }

        if (handProbe.Location != CardLocation.BoardFrontline)
        {
            return $"手牌 Pz 未响应 OnFrontlineOwnershipChange：位置={handProbe.Location}，"
                 + Dump(state, ("派发记录", trace.Count == 0 ? "（空）" : string.Join(" | ", trace.Take(12))));
        }

        if (!trace.Any(x => x.StartsWith($"OnFrontlineOwnershipChange → {probe}#",
                                          StringComparison.Ordinal)))
        {
            return "手牌 Pz 已上前线但没有对应触发记录，事件载荷/诊断口径不完整";
        }

        return null;
    }

    /// <summary>
    /// 卡面自带关键字要真的加载进 `CardInstance.Keywords`。
    ///
    /// ⚠️ `cards.live.json` 里**只有 `hasGuard`**，`hasBlitz` 等全缺 ——
    /// 不额外加载的话所有卡的 `Keywords` 建出来都是空集（实测），
    /// 于是 205 张 Blitz、128 张 Guard 全部不生效。
    /// </summary>
    private static string? InnateKeywordsLoaded(CardDatabase db)
    {
        var (_, state) = EmptyBoard(db);
        int blitz = 0, guard = 0, checkedCards = 0;
        foreach (var def in db.All)
        {
            var c = state.Create(def.Name, Side.Left, CardLocation.DeckLeft,
                                 state.NextLocationNumber(Side.Left, CardLocation.DeckLeft));
            checkedCards++;
            if (c.Keywords.Contains(Keyword.Blitz, StringComparer.Ordinal))
            {
                blitz++;
            }

            if (c.Keywords.Contains(Keyword.Guard, StringComparer.Ordinal))
            {
                guard++;
            }
        }

        // 期望值来自 pak CDO（gen-card-keywords.py 的输出）：Blitz 205 / Guard 128。
        // 这里只做**下界**断言，避免表重新生成后数字微调就假失败。
        if (blitz < 200)
        {
            return $"带 Blitz 的卡只有 {blitz} 张（CDO 里是 205）—— 卡面关键字没加载全";
        }

        if (guard < 120)
        {
            return $"带 Guard 的卡只有 {guard} 张（CDO 里是 128）";
        }

        return null;
    }

    // ==================================================================
    //  跨前线射程（蓝图定案）
    // ==================================================================

    // 卡池实测射程（klink bot/docs/cards.live.json）：
    //   infantry 567 张 range=1（另有 1 张 range=2）
    //   tank     154 张 range=1
    //   artillery 60 张 range=2
    //   fighter  144 张 range=2
    //   bomber    97 张 range=2（另有 1 张 range=4）
    // 所以「只有炮兵/战斗机/轰炸机能跨前线」是 range≥2 的**数据后果**。
    private const string InfRange1  = "card_unit_16th_infantry_brigade";   // infantry range=1
    private const string TankRange1 = "card_unit_valentine_mk_iii";        // tank     range=1
    private const string ArtRange2  = "card_unit_25_pounder";              // artillery range=2
    private const string FtrRange2  = "card_unit_typhoon_mk_ib";           // fighter  range=2
    private const string BmbRange2  = "card_unit_hampden";                 // bomber   range=2

    /// <summary>摆一个「左方攻击者在 <paramref name="attackerLoc"/>、右方步兵在
    /// <paramref name="defenderLoc"/>」的干净局面。左右 HQ 各 20 防。</summary>
    private static (MatchEngine Engine, GameState State, CardInstance Attacker, CardInstance Defender)
        RangeSetup(CardDatabase db, string attackerCard, CardLocation attackerLoc, CardLocation defenderLoc)
    {
        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var attacker = state.CreateWithId(attackerCard, Side.Left, 2, attackerLoc, 1);
        var defender = state.CreateWithId(InfRange1, Side.Right, 42, defenderLoc, 1);
        return (engine, state, attacker, defender);
    }

    /// <summary>
    /// 半场（<c>BoardHqLeft</c>，即 5）的 **infantry / tank（range=1）**：
    /// 既不能打对方 HQ，也不能打对方半场单位 —— 因为双方都不在前线，
    /// 距离 2 &gt; range 1。
    ///
    /// 出处：<c>cardsCheckFunctions::CanAttack</c> i=90-99
    /// （<c>not_enough_range</c>）；反编译产物 <c>out/xr-cardscheck.bpasm</c> 行 467-544。
    /// </summary>
    private static string? CrossFrontlineInfantryBlocked(CardDatabase db)
    {
        foreach (var (card, label) in new[] { (InfRange1, "infantry"), (TankRange1, "tank") })
        {
            if (db.Find(card) is null)
            {
                return $"卡库里缺 {card}";
            }

            if (db.Find(card)!.Range != 1)
            {
                return $"{card} 的 range 应为 1，实际 {db.Find(card)!.Range}（测试前提变了）";
            }

            // (a) 打对方 HQ
            var (e1, s1, a1, _) = RangeSetup(db, card, CardLocation.BoardHqLeft, CardLocation.BoardHqRight);
            int hqBefore = s1.HqDefense(Side.Right);
            int krBefore = s1.Kredits(Side.Left);
            if (e1.Attack(a1, s1.Hq(Side.Right)))
            {
                return $"{label}({card}) 在半场竟然打到了对方 HQ（蓝图 i=90-99 应拒）";
            }

            if (s1.HqDefense(Side.Right) != hqBefore || s1.Kredits(Side.Left) != krBefore)
            {
                return $"{label} 被拒的跨前线攻击改了状态：HQ {hqBefore}→{s1.HqDefense(Side.Right)}"
                     + $"，kredit {krBefore}→{s1.Kredits(Side.Left)}";
            }

            // (b) 打对方半场单位
            var (e2, s2, a2, d2) = RangeSetup(db, card, CardLocation.BoardHqLeft, CardLocation.BoardHqRight);
            if (e2.Attack(a2, d2))
            {
                return $"{label}({card}) 在半场竟然打到了对方半场单位 {d2.Name}（蓝图 i=90-99 应拒）";
            }

            // (c) LegalTargets 也不能把它列出来
            var targets = e2.LegalTargets(a2).Select(t => t.CardId).ToHashSet();
            if (targets.Contains(d2.CardId) || targets.Contains(s2.Hq(Side.Right).CardId))
            {
                return $"{label} 在半场时 LegalTargets 仍列出了打不到的目标："
                     + $"{string.Join(",", targets)}";
            }

            if (e1.OutOfRangeAttacksRejected != 1 || e2.OutOfRangeAttacksRejected != 1)
            {
                return $"{label} 每次越界攻击都应记一次射程拒绝，实际 HQ 那次 {e1.OutOfRangeAttacksRejected}、"
                     + $"半场单位那次 {e2.OutOfRangeAttacksRejected}";
            }
        }

        return null;
    }

    /// <summary>
    /// 半场（5）的 **artillery（range=2）**：能跨前线打对方 HQ 和对方半场单位。
    /// 出处同上（<c>attacker.range &lt; 2</c> 不成立 ⇒ 不拒绝）。
    /// </summary>
    private static string? CrossFrontlineArtilleryAllowed(CardDatabase db)
    {
        if (db.Find(ArtRange2) is null)
        {
            return $"卡库里缺 {ArtRange2}";
        }

        if (db.Find(ArtRange2)!.Range != 2)
        {
            return $"{ArtRange2} 的 range 应为 2，实际 {db.Find(ArtRange2)!.Range}";
        }

        // (a) 打对方 HQ
        var (e1, s1, a1, _) = RangeSetup(db, ArtRange2, CardLocation.BoardHqLeft, CardLocation.BoardHqRight);
        int hqBefore = s1.HqDefense(Side.Right);
        if (!e1.Attack(a1, s1.Hq(Side.Right)))
        {
            return $"半场炮兵(range=2) 应该能跨前线打对方 HQ，却被拒了"
                 + Dump(s1, ("未实现", Unimpl(s1)));
        }

        if (s1.HqDefense(Side.Right) != hqBefore - a1.Attack)
        {
            return $"炮兵打 HQ 的伤害不对：期望 {hqBefore - a1.Attack}，实际 {s1.HqDefense(Side.Right)}";
        }

        if (e1.OutOfRangeAttacksRejected != 0)
        {
            return $"炮兵不该被射程拒绝，实际拒了 {e1.OutOfRangeAttacksRejected} 次";
        }

        // (b) 打对方半场单位
        var (e2, s2, a2, d2) = RangeSetup(db, ArtRange2, CardLocation.BoardHqLeft, CardLocation.BoardHqRight);
        if (!e2.Attack(a2, d2))
        {
            return $"半场炮兵应该能跨前线打对方半场单位 {d2.Name}，却被拒了";
        }

        if (d2.Defense >= db.Find(InfRange1)!.Defense)
        {
            return $"炮兵打对方半场单位没有生效（防御仍是 {d2.Defense}）";
        }

        return null;
    }

    /// <summary>
    /// 半场（5）的 **fighter / bomber（range=2）**：能跨前线打对方 HQ。
    /// 出处同上。注意 <c>CanAttack</c> 里**没有</c> <c>IsFighter</c>，
    /// <c>IsArtillery</c>/<c>IsBomber</c> 只出现在 <c>isBeingGuarded</c> 分支
    /// （i=100-104），与射程无关 —— 所以判据是射程，不是类型。
    /// </summary>
    private static string? CrossFrontlineFighterBomberAllowed(CardDatabase db)
    {
        foreach (var (card, label) in new[] { (FtrRange2, "fighter"), (BmbRange2, "bomber") })
        {
            if (db.Find(card) is null)
            {
                return $"卡库里缺 {card}";
            }

            if (db.Find(card)!.Range != 2)
            {
                return $"{card} 的 range 应为 2，实际 {db.Find(card)!.Range}";
            }

            var (engine, state, attacker, _) =
                RangeSetup(db, card, CardLocation.BoardHqLeft, CardLocation.BoardHqRight);
            int hqBefore = state.HqDefense(Side.Right);
            if (!engine.Attack(attacker, state.Hq(Side.Right)))
            {
                return $"半场 {label}({card}, range=2) 应该能跨前线打对方 HQ，却被拒了"
                     + Dump(state, ("未实现", Unimpl(state)));
            }

            if (state.HqDefense(Side.Right) != hqBefore - attacker.Attack)
            {
                return $"{label} 打 HQ 的伤害不对：期望 {hqBefore - attacker.Attack}，"
                     + $"实际 {state.HqDefense(Side.Right)}";
            }

            if (engine.OutOfRangeAttacksRejected != 0)
            {
                return $"{label} 不该被射程拒绝，实际拒了 {engine.OutOfRangeAttacksRejected} 次";
            }
        }

        return null;
    }

    /// <summary>
    /// **任一方在前线（7）⇒ 距离 ≤ 1 ⇒ 任何射程都够得着。**
    ///
    /// 覆盖三种布局：
    /// (a) 攻击者在前线 → 能打对方 HQ / 对方半场单位
    /// (b) 攻击者在半场、目标在前线 → 能打（range=1 也行）
    /// 出处：<c>CanAttack</c> i=90/91 的两个 <c>location != 7</c> 用 AND 连起来 ——
    /// 只要有一个等于 7，整个条件就是假，射程检查根本不触发。
    /// </summary>
    private static string? FrontlineAlwaysInRange(CardDatabase db)
    {
        // (a) 前线的 infantry(range=1) 打对方 HQ
        var (e1, s1, a1, _) = RangeSetup(db, InfRange1, CardLocation.BoardFrontline, CardLocation.BoardHqRight);
        int hqBefore = s1.HqDefense(Side.Right);
        if (!e1.Attack(a1, s1.Hq(Side.Right)))
        {
            return "前线步兵(range=1) 应该能打对方 HQ，却被拒了"
                 + Dump(s1, ("未实现", Unimpl(s1)));
        }

        if (s1.HqDefense(Side.Right) != hqBefore - a1.Attack)
        {
            return $"前线步兵打 HQ 的伤害不对：期望 {hqBefore - a1.Attack}，实际 {s1.HqDefense(Side.Right)}";
        }

        // (b) 前线的 infantry 打对方半场单位
        var (e2, s2, a2, d2) = RangeSetup(db, InfRange1, CardLocation.BoardFrontline, CardLocation.BoardHqRight);
        if (!e2.Attack(a2, d2))
        {
            return "前线步兵应该能打对方半场单位，却被拒了";
        }

        // (c) 半场的 infantry(range=1) 打**对方前线**单位 —— 目标在前线，距离 1，够得着
        var (e3, s3, a3, d3) = RangeSetup(db, InfRange1, CardLocation.BoardHqLeft, CardLocation.BoardFrontline);
        if (!e3.Attack(a3, d3))
        {
            return "半场步兵应该能打对方**前线**单位（距离 1），却被拒了";
        }

        // (d) 上面三种都不该记射程拒绝
        foreach (var (e, label) in new[] { (e1, "a"), (e2, "b"), (e3, "c") })
        {
            if (e.OutOfRangeAttacksRejected != 0)
            {
                return $"布局 {label} 不该被射程拒绝，实际拒了 {e.OutOfRangeAttacksRejected} 次";
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="MatchEngine.CanReachAcrossFrontline"/> 的真值表 ——
    /// 把「距离」和「射程」两个维度都钉死，防止实现被放宽或收窄。
    ///
    /// 距离：支援线↔支援线 = 2，支援线↔前线 = 1，前线↔任意 = 1。
    /// 规则：<c>range ≥ 距离</c>。等价于蓝图那条
    /// <c>attacker.location != 7 &amp;&amp; defender.location != 7 &amp;&amp; attacker.range &lt; 2 ⇒ 拒</c>。
    /// </summary>
    private static string? CrossFrontlineTruthTable(CardDatabase db)
    {
        var (_, state) = EmptyBoard(db);
        var left1 = state.CreateWithId(InfRange1, Side.Left, 2, CardLocation.HandLeft, 0);
        var left2 = state.CreateWithId(ArtRange2, Side.Left, 3, CardLocation.HandLeft, 0);
        var right = state.CreateWithId(InfRange1, Side.Right, 42, CardLocation.HandRight, 0);

        // (攻击者位置, 目标位置, 攻击者, 期望)
        var table = new (CardLocation A, CardLocation D, CardInstance Card, bool Expect, string Why)[]
        {
            (CardLocation.BoardHqLeft, CardLocation.BoardHqRight, left1, false, "半场→半场 距离2，range1 不够"),
            (CardLocation.BoardHqLeft, CardLocation.BoardHqRight, left2, true,  "半场→半场 距离2，range2 够"),
            (CardLocation.BoardHqLeft, CardLocation.BoardFrontline, left1, true, "半场→前线 距离1，range1 够"),
            (CardLocation.BoardHqLeft, CardLocation.BoardFrontline, left2, true, "半场→前线 距离1，range2 够"),
            (CardLocation.BoardFrontline, CardLocation.BoardHqRight, left1, true, "前线→半场 距离1，range1 够"),
            (CardLocation.BoardFrontline, CardLocation.BoardHqRight, left2, true, "前线→半场 距离1，range2 够"),
            (CardLocation.BoardFrontline, CardLocation.BoardFrontline, left1, true, "前线→前线 距离1，range1 够"),
        };

        foreach (var (a, d, card, expect, why) in table)
        {
            card.Location = a;
            right.Location = d;
            bool got = MatchEngine.CanReachAcrossFrontline(card, right);
            if (got != expect)
            {
                return $"{why}：{a}→{d}（range={card.Definition.Range}）期望 {expect}，实际 {got}";
            }
        }

        // 射程 0（非单位卡，比如 order/location）也必须按「不够」处理
        var order = state.CreateWithId("card_event_merchant_navy", Side.Left, 4, CardLocation.HandLeft, 0);
        order.Location = CardLocation.BoardHqLeft;
        right.Location = CardLocation.BoardHqRight;
        if (MatchEngine.CanReachAcrossFrontline(order, right))
        {
            return $"range=0 的卡不该够得着（实际 range={order.Definition.Range}）";
        }

        return null;
    }

    /// <summary>
    /// `CanAttack` 没有“前线嘲讽”分支；敌方前线存在时，range=2 的攻击者
    /// 仍可选择敌方半场/HQ，range=1 则只会被射程门过滤掉后方目标。
    /// </summary>
    private static string? FrontlineDoesNotBlockRangedTargets(CardDatabase db)
    {
        var (rangedEngine, rangedState, ranged, rangedFront) =
            RangeSetup(db, ArtRange2, CardLocation.BoardHqLeft, CardLocation.BoardFrontline);
        var rangedBack = rangedState.CreateWithId(InfRange1, Side.Right, 43, CardLocation.BoardHqRight, 1);
        var rangedTargets = rangedEngine.LegalTargets(ranged).Select(c => c.CardId).ToHashSet();
        if (!rangedTargets.Contains(rangedFront.CardId) || !rangedTargets.Contains(rangedBack.CardId)
            || !rangedTargets.Contains(rangedState.Hq(Side.Right).CardId))
        {
            return "敌方前线存在时，range=2 攻击者应仍能列出前线、后方单位和 HQ";
        }

        var (shortEngine, shortState, shortRangeAttacker, shortFront) =
            RangeSetup(db, InfRange1, CardLocation.BoardHqLeft, CardLocation.BoardFrontline);
        var shortBack = shortState.CreateWithId(InfRange1, Side.Right, 43, CardLocation.BoardHqRight, 1);
        var shortTargets = shortEngine.LegalTargets(shortRangeAttacker).Select(c => c.CardId).ToHashSet();
        if (!shortTargets.Contains(shortFront.CardId)
            || shortTargets.Contains(shortBack.CardId)
            || shortTargets.Contains(shortState.Hq(Side.Right).CardId))
        {
            return "敌方前线存在时，range=1 攻击者只能保留前线目标";
        }

        return null;
    }

    // ==================================================================
    //  P0 第 1 族：事件层派发的自测（2026-09-27）
    // ==================================================================

    /// <summary>
    /// 建一个空局 + 放一张探针卡 + 记下 TriggerTrace。
    /// <paramref name="fire"/> 返回"应当收到**自己那一路**事件"的那张卡
    /// （多数情况就是探针本身；`OnCreateCard` / `OnCardSpawnedInHand` 是新建出来的那张）。
    /// </summary>
    private static (List<string> Trace, CardInstance Card, string? Error) Probe(
        CardDatabase db, string probeName, CardLocation where,
        Func<MatchEngine, GameState, CardInstance, CardInstance> fire)
    {
        if (db.Find(probeName) is null)
        {
            return (new List<string>(), null!, $"卡库里缺 {probeName}");
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        var probe = state.CreateWithId(probeName, Side.Left, 20, where, 1);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        try
        {
            var subject = fire(engine, state, probe);
            return (trace, subject, null);
        }
        catch (Exception ex)
        {
            return (trace, probe, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>TriggerTrace 里有没有「事件名 → 卡名#ID」这一条。</summary>
    private static bool Reached(List<string> trace, string ev, CardInstance card)
        => trace.Any(t => t.StartsWith($"{ev} → {card.Name}#{card.CardId}", StringComparison.Ordinal));

    private static string? EventLayerSelfEvents(CardDatabase db)
    {
        // (事件, 探针卡, 探针放在哪, 怎么触发)
        var table = new (string Event, string Card, CardLocation Where,
                         Func<MatchEngine, GameState, CardInstance, CardInstance> Fire)[]
        {
            ("OnCardReset", "card_unit_raaf_walrus", CardLocation.BoardHqLeft,
                (e, s, p) => { e.Api.ResetCardInBattle(p); return p; }),
            ("OnSuppressed", "card_unit_raaf_walrus", CardLocation.BoardHqLeft,
                (e, s, p) => { e.Api.SuppressUnit(p); return p; }),
            ("OnBecomingVeteran", "card_unit_7th_brigade_anzac", CardLocation.BoardHqLeft,
                (e, s, p) => { e.Api.MakeVeteran(p); return p; }),
            ("OnCardLocationMoved", "card_unit_spitfire_mk_ii", CardLocation.BoardHqLeft,
                (e, s, p) => { s.Move(p, CardLocation.BoardFrontline, 0); return p; }),
            ("OnAfterLeaveBoard", "card_unit_royal_west_kents", CardLocation.BoardHqLeft,
                (e, s, p) => { e.FireLeaveTrigger(p, CardLocation.Discard); return p; }),
            ("OnAfterGainDefense", "card_unit_kyushu_j7w3", CardLocation.BoardHqLeft,
                (e, s, p) => { e.Api.ChangeDefense(p, 1, null); return p; }),
            ("OnFullyRepaired", "card_unit_kyushu_j7w3", CardLocation.BoardHqLeft,
                (e, s, p) => { p.Defense = Math.Max(1, p.MaxDefense - 1); e.Api.HealCard(p, 9); return p; }),
            ("OnBeforeStartOfTurn", "card_unit_cedar_division", CardLocation.BoardHqLeft,
                (e, s, p) => { e.StartTurn(Side.Left, draw: false); return p; }),
            ("OnCardDrawnFromDeck", "card_unit_13e_dragons", CardLocation.DeckLeft,
                (e, s, p) => e.DrawCard(Side.Left) ?? p),
            ("OnCreateCard", "card_unit_185th_folgore_cov", CardLocation.NotAvailable,
                (e, s, p) => s.Create("card_unit_185th_folgore_cov", Side.Left, CardLocation.DeckLeft, 0)),
            ("OnOtherCardCreatedAlterCard", "card_unit_67th_baranovichi", CardLocation.BoardHqLeft,
                (e, s, p) => { s.Create("card_unit_185th_folgore_cov", Side.Left, CardLocation.DeckLeft, 0); return p; }),
            ("OnCardSpawnedInHand", "card_unit_13e_dragons", CardLocation.NotAvailable,
                (e, s, p) => e.Api.SpawnCardInHand(Side.Left, "card_unit_13e_dragons")),
        };

        foreach (var (ev, card, where, fire) in table)
        {
            var (trace, subject, err) = Probe(db, card, where, fire);
            if (err is not null)
            {
                return $"{ev}（探针 {card}）出错了：{err}";
            }

            if (!Reached(trace, ev, subject))
            {
                string diag = "";
                if (KismetLibrary.Default is { } lib)
                {
                    diag = $"；FindProgram({card}, {ev}) = {(lib.FindProgram(card, ev) is null ? "null" : "有")}";
                }

                return $"{ev} 没有派发到主体 {subject.Name}#{subject.CardId}（探针 {card}）；"
                     + $"主体位置={subject.Location}{diag}"
                     + $"\n       实际派发记录：{string.Join(" | ", trace.Take(6))}";
            }
        }

        return null;
    }

    private static string? EventLayerOtherEvents(CardDatabase db)
    {
        const string victim = "card_unit_arado_ar_196";
        var table = new (string Event, string Card, Func<MatchEngine, GameState, CardInstance, CardInstance> Fire)[]
        {
            ("OnOtherCardReset", "card_unit_coastwatchers",
                (e, s, p) => { e.Api.ResetCardInBattle(Plain(s, victim, 60)); return p; }),
            ("OnOtherCardSuppressed", "card_unit_kings_own_scottish",
                (e, s, p) => { e.Api.SuppressUnit(Plain(s, victim, 61)); return p; }),
            ("OnAfterOtherCardSuppressed", "card_unit_adler_command_vehicle",
                (e, s, p) => { e.Api.SuppressUnit(Plain(s, victim, 62)); return p; }),
            ("OnOtherCardBecomingVeteran", "card_unit_6th_brigade_nz",
                (e, s, p) => { e.Api.MakeVeteran(Plain(s, victim, 63)); return p; }),
            ("OnOtherCardAbilitiesChanged", "card_unit_royal_west_kents",
                (e, s, p) => { e.Api.GiveKeyword(Plain(s, victim, 64), Keyword.Blitz); return p; }),
            ("OnOtherCardBlitzChanged", "card_unit_panzer_iii_l",
                (e, s, p) => { var changed = Plain(s, victim, 65); e.Api.GiveKeyword(changed, Keyword.Blitz); e.Api.RemoveKeyword(changed, Keyword.Blitz); return p; }),
            ("OnOtherCardLoseSmokescreen", "card_unit_hirosaki_regiment",
                (e, s, p) =>
                {
                    var changed = s.CreateWithId(victim, Side.Left, 73, CardLocation.BoardHqLeft, 2);
                    e.Api.GiveKeyword(changed, Keyword.Smokescreen);
                    e.Api.RemoveKeyword(changed, Keyword.Smokescreen);
                    return p;
                }),
            ("OnOtherCardKreditCostChanged", "card_unit_the_silent_seventh",
                (e, s, p) => { e.Api.ChangeKreditCost(Plain(s, victim, 66), 1); return p; }),
            ("OnOtherCardDealDamage", "card_unit_blenheim_mk_i",
                (e, s, p) =>
                {
                    var src = Plain(s, victim, 65);
                    var dst = Plain(s, victim, 66);
                    dst.Defense = 20;
                    e.Api.DealDamage(dst, 1, src);
                    return p;
                }),
            ("OnOtherCardLocationMoved", "card_unit_spitfire_mk_ii",
                (e, s, p) => { s.Move(Plain(s, victim, 67), CardLocation.BoardFrontline, 0); return p; }),
            ("OnOtherCardMoveFromFrontline", "card_unit_raaf_walrus",
                (e, s, p) => { var moved = Plain(s, victim, 68); s.Move(moved, CardLocation.BoardFrontline, 0); s.Move(moved, CardLocation.BoardHqLeft, 0); return p; }),
            ("OnDeckShuffled", "card_unit_110e_regiment_motorize",
                (e, s, p) =>
                {
                    var deckCard = Plain(s, victim, 72);
                    s.Move(deckCard, CardLocation.DeckLeft, 0);
                    e.Api.InvokeByName("ShuffleDeckBySide", p,
                        new object?[] { (int)Side.Left, false, 0, null },
                        new EffectContext { Engine = e, State = s, Self = p, Controller = Side.Left }, out _);
                    return p;
                }),
            ("OnOtherCardDestroyed", "card_unit_hudson",
                (e, s, p) => { e.Destroy(Plain(s, victim, 69)); return p; }),
            ("OnBeforeOtherCardDestroyed", "card_unit_marder_iii_h",
                (e, s, p) => { e.Destroy(Plain(s, victim, 70)); return p; }),
            ("OnOtherCardDiscarded", "card_unit_7th_brigade_anzac",
                (e, s, p) => { e.Api.DiscardCard(Plain(s, victim, 71)); return p; }),
            ("OnAfterExtraKreditSlotGain", "card_unit_144th_infantry_regiment",
                (e, s, p) => { e.Api.GainKreditSlot(Side.Left, 1); return p; }),
            ("OnBeforeOtherCardPlayedFromHand", "card_unit_flaming_matilda_anzac",
                (e, s, p) =>
                {
                    var c = s.CreateWithId(victim, Side.Left, 71, CardLocation.HandLeft, 2);
                    if (!e.PlayCard(c))
                    {
                        throw new InvalidOperationException($"打不出 {victim}（测试布景问题）");
                    }

                    return p;
                }),
        };

        foreach (var (ev, card, fire) in table)
        {
            var (trace, subject, err) = Probe(db, card, CardLocation.BoardHqLeft, fire);
            if (err is not null)
            {
                return $"{ev}（探针 {card}）出错了：{err}";
            }

            if (!Reached(trace, ev, subject))
            {
                return $"{ev} 没有广播到旁观探针 {subject.Name}#{subject.CardId}"
                     + $"\n       实际派发记录：{string.Join(" | ", trace.Take(6))}";
            }
        }

        return null;

        // 敌方一张普通单位（没有订阅任何东西），当"事件的受害者"
        static CardInstance Plain(GameState s, string name, int id)
            => s.CreateWithId(name, Side.Right, id, CardLocation.BoardHqRight, id - 59);
    }

    /// <summary>
    /// `OnOtherCardReset` 的两个槽位不能靠事件主体兜底：
    /// `cardReset` 是被重置的卡对象，`resetCardID` 是同一张卡的 ID。
    ///
    /// Coastwatchers 的真实蓝图会读取 `resetCardID`，通过 `GetCardFromID`
    /// 找回重置的手牌天气牌，再把它的费用降低 1。若载荷缺失，VM 会把
    /// 事件主体/当前接收者当成 ID，真实效果就会静默跳过。
    /// </summary>
    private static string? ResetEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_coastwatchers";
        const string weatherName = "card_event_rain2_deluge";
        if (db.Find(watcherName) is null || db.Find(weatherName) is null)
        {
            return $"卡库里缺 {watcherName} 或 {weatherName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var weather = state.CreateWithId(weatherName, Side.Left, 60,
            CardLocation.HandLeft, 1);
        int before = weather.EffectiveKreditCost;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            engine.Api.ResetCardInBattle(weather);
        }
        catch (Exception ex)
        {
            return $"ResetCardInBattle 抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string prefix = $"OnOtherCardReset → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={weather.Name}#{weather.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardReset 没有把被重置的天气牌作为事件主体传给 Coastwatchers";
        }

        int after = weather.EffectiveKreditCost;
        if (after != Math.Max(CardInstance.MinKreditCost, before - 1))
        {
            return $"Coastwatchers 没有按 resetCardID 给天气牌减费：重置前={before}，重置后={after}";
        }

        return null;
    }

    /// <summary>
    /// `OnOtherCardDealDamage` 的六个事件参数必须传给真实订阅程序：
    /// 15th RECCE 读取 `cardDealingDamage`，只在带有 `targetAbility` 的伤害来源
    /// 造成伤害时给自己 +1/+1 并获得 Guard。仅验证 TriggerTrace 不能证明
    /// 来源卡槽位被正确填充，因为缺载荷时事件仍然会留下广播记录。
    /// </summary>
    private static string? DealDamageEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_15th_recon_regiment";
        const string sourceName = PlainUnit;
        const string victimName = PlainUnit;
        if (db.Find(watcherName) is null || db.Find(sourceName) is null)
        {
            return $"卡库里缺 {watcherName} 或 {sourceName}";
        }

        var (engine, state) = EmptyBoard(db);
        var watcher = PutOnBoard(state, watcherName, Side.Left, 20, 1);
        var source = PutOnBoard(state, sourceName, Side.Left, 21, 2);
        var victim = PutOnBoard(state, victimName, Side.Right, 60, 1);
        source.CustomAbility = "targetAbility";
        victim.Defense = 20;

        int attack = watcher.Attack;
        int defense = watcher.Defense;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        try
        {
            engine.Api.DealDamage(victim, 1, source);
        }
        catch (Exception ex)
        {
            return $"DealDamage 抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string prefix = $"OnOtherCardDealDamage → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={source.Name}#{source.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardDealDamage 没有把伤害来源作为事件主体传给 15th RECCE"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (watcher.Attack != attack + 1 || watcher.Defense != defense + 1)
        {
            return $"15th RECCE 未消费 cardDealingDamage=source：期望攻防={attack + 1}/{defense + 1}，"
                 + $"实际={watcher.Attack}/{watcher.Defense}（来源 CustomAbility={source.CustomAbility}）";
        }

        if (!watcher.Keywords.Contains(Keyword.Guard, StringComparer.Ordinal))
        {
            return "15th RECCE 收到伤害事件后应获得 Guard，但关键字没有写入";
        }

        return null;
    }

    private static string? CustomDamageEventEntrypoints(CardDatabase db)
    {
        const string riflesName = "card_unit_220th_rifles";
        const string kyushuName = "card_unit_kyushu_j7w3";
        const string suName = "card_unit_su_100";
        if (db.Find(riflesName) is null || db.Find(kyushuName) is null || db.Find(suName) is null)
        {
            return $"卡库里缺 {riflesName} / {kyushuName} / {suName}";
        }

        // 220th Rifles: the wrapper calls CustomEventOnCardDealDamage, which
        // must count a damaged unit and apply the live +2 attack buff.
        var (engine, state) = EmptyBoard(db);
        var rifles = PutOnBoard(state, riflesName, Side.Left, 300, 1);
        var damaged = PutOnBoard(state, PlainUnit, Side.Left, 301, 2);
        damaged.Defense = Math.Max(0, damaged.MaxDefense - 1);
        int riflesAttack = rifles.Attack;
        FireSelfDamageEvent(engine, rifles, damaged);
        if (rifles.Attack != riflesAttack + 2)
        {
            return $"220th Rifles 自定义伤害事件未执行：期望攻击 {riflesAttack + 2}，实际 {rifles.Attack}";
        }

        // Kyushu J7W3: its own damage event removes the one-time +2/Fury buff
        // after the unit becomes damaged.
        var (kyushuEngine, kyushuState) = EmptyBoard(db);
        var kyushu = PutOnBoard(kyushuState, kyushuName, Side.Left, 310, 1);
        kyushuEngine.Api.FireTrigger("OnEnterPlay", kyushu, Side.Left,
            eventSubject: kyushu);
        kyushu.Defense = Math.Max(0, kyushu.MaxDefense - 1);
        int kyushuAttack = kyushu.Attack;
        if (!kyushu.Keywords.Contains(Keyword.Fury) || kyushuAttack <= kyushu.Definition.Attack)
        {
            return $"Kyushu 入场 buff 未建立：期望基础攻击 {kyushu.Definition.Attack} 以上且带 Fury，"
                 + $"实际={kyushuAttack}/{kyushu.Keywords.Contains(Keyword.Fury)}";
        }
        FireSelfDamageEvent(kyushuEngine, kyushu, kyushu);
        if (kyushu.Attack != kyushuAttack - 2 || kyushu.Keywords.Contains(Keyword.Fury))
        {
            return $"Kyushu 自定义伤害事件未执行：期望攻击/Fury={kyushuAttack - 2}/false，"
                 + $"实际={kyushu.Attack}/{kyushu.Keywords.Contains(Keyword.Fury)}";
        }

        // SU-100: a damaged source dealing damage runs SelfCustomEventOnCardDealDamage
        // and receives its +4 attack buff.
        var (suEngine, suState) = EmptyBoard(db);
        var su = PutOnBoard(suState, suName, Side.Left, 320, 1);
        var suTarget = PutOnBoard(suState, PlainUnit, Side.Right, 321, 1);
        su.Defense = Math.Max(0, su.MaxDefense - 1);
        int suAttack = su.Attack;
        FireSelfDamageEvent(suEngine, su, suTarget);
        return su.Attack == suAttack + 4
            ? null
            : $"SU-100 自定义伤害事件未执行：期望攻击 {suAttack + 4}，实际 {su.Attack}";
    }

    private static void FireSelfDamageEvent(MatchEngine engine, CardInstance source, CardInstance target)
    {
        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardDealingDamage"] = source,
            ["toCard"] = target,
            ["Damage"] = 1,
            ["damage"] = 1,
            ["isCombatDamage"] = false,
            ["CounterDamage"] = false,
            ["isRedirected"] = false,
        };
        engine.Api.FireTrigger("OnCardDealDamage", source, source.Owner,
            eventArgs: new object?[] { source, target, 1, false, false, false },
            eventSubject: source, namedArgs: named);
    }

    /// <summary>
    /// 48th Armored Infantry 的事件 38 订阅：最终伤害每有一个相邻单位减少 2。
    /// 这条必须在统一 DealDamage 漏斗中验证，单独调用私有函数无法证明事件已接通。
    /// </summary>
    private static string? DealDamageAfterCalcAdjacentDefense(CardDatabase db)
    {
        const string armoredName = "card_unit_48th_armored_infantry";
        const string unitName = PlainUnit;
        if (db.Find(armoredName) is null || db.Find(unitName) is null)
        {
            return $"卡库里缺 {armoredName} 或 {unitName}";
        }

        var (engine, state) = EmptyBoard(db);
        var left = PutOnBoard(state, armoredName, Side.Left, 20, 2);
        PutOnBoard(state, unitName, Side.Left, 21, 1);
        PutOnBoard(state, unitName, Side.Left, 22, 3);
        var source = PutOnBoard(state, unitName, Side.Right, 60, 1);
        int before = left.Defense;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = left, Controller = Side.Left,
        };
        object? adjacent = engine.Api.InvokeByName("GetDefenseBuffFromAdjacentUnits", null,
            new object?[] { null }, ctx, out bool handled);
        if (!handled || adjacent is not int amount || amount != 4)
        {
            return $"两个相邻单位应提供 4 点防御修正，handled={handled}, result={adjacent}";
        }

        engine.Api.DealDamage(left, 5, source, isCombatDamage: true);
        if (left.Defense != before - 1)
        {
            return $"事件 38 应将 5 点伤害减为 1 点，实际防御 {before} → {left.Defense}"
                 + Dump(state, ("目标", left.ToString()));
        }

        return null;
    }

    /// <summary>
    /// 天气候选池的正版蓝图只按 subtype.rain/storm/sunny 筛选活动静态卡；
    /// `ability.forecast` 是触发 Forecast 的能力标签，不能混作天气卡判据。
    /// </summary>
    private static string? ForecastCardPrimitives(CardDatabase db)
    {
        const string weatherName = "card_event_rain2_deluge";
        if (db.Find(weatherName) is null || db.Find(PlainUnit) is null)
        {
            return $"卡库里缺 {weatherName} 或 {PlainUnit}";
        }

        var (engine, state) = EmptyBoard(db);
        var weather = PutOnBoard(state, weatherName, Side.Left, 20, 1);
        var unit = PutOnBoard(state, PlainUnit, Side.Left, 21, 2);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = weather, Controller = Side.Left,
        };

        object? weatherResult = engine.Api.InvokeByName("IsForecastCard", null,
            new object?[] { null }, ctx, out bool weatherHandled);
        if (!weatherHandled || weatherResult is not bool isWeather || !isWeather)
        {
            return $"天气卡应被 IsForecastCard 判真，handled={weatherHandled}, result={weatherResult}";
        }

        ctx.Self = unit;
        object? unitResult = engine.Api.InvokeByName("IsForecastCard", null,
            new object?[] { null }, ctx, out bool unitHandled);
        if (!unitHandled || unitResult is not bool isUnit || isUnit)
        {
            return $"普通单位不应被判为天气卡，handled={unitHandled}, result={unitResult}";
        }

        ctx.Self = weather;
        object? candidates = engine.Api.InvokeByName("GetAllForecastCards", null,
            new object?[] { true, false, null }, ctx, out bool poolHandled);
        if (!poolHandled || candidates is not IEnumerable<CardInstance> pool)
        {
            return $"GetAllForecastCards 未返回候选池，handled={poolHandled}, result={candidates}";
        }

        var list = pool.ToList();
        if (list.Count == 0 || list.Any(card => !card.Definition.IsOrder)
            || list.All(card => !string.Equals(card.Name, weatherName, StringComparison.Ordinal)))
        {
            return $"天气候选池应包含天气模板且排除普通卡，数量={list.Count}";
        }

        return null;
    }

    private static string? WeatherCardPerTurn(CardDatabase db)
    {
        const string weatherName = "card_event_rain2_deluge";
        const string ordinaryOrderName = "card_event_forward_observers";
        if (db.Find(weatherName) is null || db.Find(ordinaryOrderName) is null)
        {
            return $"卡库里缺 {weatherName} 或 {ordinaryOrderName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 40);
        state.SetMaxKredits(Side.Left, 40);

        var firstWeather = state.Create(weatherName, Side.Left, CardLocation.HandLeft, 0);
        var secondWeather = state.Create(weatherName, Side.Left, CardLocation.HandLeft, 1);
        var ordinaryOrder = state.Create(ordinaryOrderName, Side.Left, CardLocation.HandLeft, 2);

        if (!engine.CanPlay(firstWeather, out string firstReason))
        {
            return $"第一张天气牌不应被拒绝：{firstReason}";
        }

        if (!engine.PlayCard(firstWeather))
        {
            return "第一张天气牌实际打出失败";
        }

        if (!state.HasPlayedWeatherCardThisTurn)
        {
            return "打出天气牌后未设置本回合标志";
        }

        if (engine.CanPlay(secondWeather, out string secondReason))
        {
            return "同一回合第二张天气牌未被拒绝";
        }

        if (!engine.CanPlay(ordinaryOrder, out string ordinaryReason))
        {
            return $"天气限制错误地拒绝普通指令：{ordinaryReason}";
        }

        engine.StartTurn(Side.Left, draw: false);
        if (state.HasPlayedWeatherCardThisTurn)
        {
            return "开始新回合后天气标志未清零";
        }

        if (!engine.CanPlay(secondWeather, out string resetReason))
        {
            return $"天气标志清零后第二张天气牌仍被拒绝：{resetReason}";
        }

        return null;
    }

    /// <summary>
    /// Forecast is a client-facing selection primitive in the Blueprint, but
    /// these two real subscribers only consume the completed event payload:
    /// Kawanishi H8K draws one card and 2nd Pioneers buffs every other friendly
    /// unit.  Headless resolution accepts the forecast synchronously, so this
    /// checks the complete event path and that the triggering card is excluded.
    /// </summary>
    private static string? ForecastBroadcastGameplay(CardDatabase db)
    {
        const string h8kName = "card_unit_kawanishi_h8k";
        const string pioneersName = "card_unit_2_2nd_pioneers";
        foreach (string name in new[] { h8kName, pioneersName, PlainUnit })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        var trigger = PutOnBoard(state, h8kName, Side.Left, 20, 1);
        var h8k = PutOnBoard(state, h8kName, Side.Left, 21, 2);
        var pioneers = PutOnBoard(state, pioneersName, Side.Left, 22, 3);
        var otherUnit = PutOnBoard(state, PlainUnit, Side.Left, 23, 4);
        state.CreateWithId(PlainUnit, Side.Left, 30, CardLocation.DeckLeft, 0);

        int h8kAttack = h8k.Attack;
        int h8kDefense = h8k.Defense;
        int otherAttack = otherUnit.Attack;
        int otherDefense = otherUnit.Defense;
        int pioneersAttack = pioneers.Attack;
        int pioneersDefense = pioneers.Defense;
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = trigger,
            Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("Forecast", trigger,
            new object?[] { null }, ctx, out bool handled);
        if (!handled)
        {
            return "Forecast 未接入派发表";
        }

        if (result is not int resultCode || resultCode != 0)
        {
            return $"Forecast 应同步返回 0，实际 result={result ?? "null"}";
        }

        if (state.Hand(Side.Left).Count != 1 || state.Hand(Side.Left)[0].Name != PlainUnit)
        {
            return "旁观 H8K 应抽到 1 张牌，而 Forecast 触发 H8K 不应重复抽牌，"
                 + $"实际手牌={state.Hand(Side.Left).Count}";
        }

        if (h8k.Attack != h8kAttack + 1 || h8k.Defense != h8kDefense + 1
            || otherUnit.Attack != otherAttack + 1 || otherUnit.Defense != otherDefense + 1)
        {
            return "2nd Pioneers 应给其它友军各 +1/+1，实际 "
                 + $"H8K={h8k.Attack}/{h8k.Defense}（原 {h8kAttack}/{h8kDefense}），"
                 + $"普通单位={otherUnit.Attack}/{otherUnit.Defense}（原 {otherAttack}/{otherDefense}）";
        }

        if (pioneers.Attack != pioneersAttack || pioneers.Defense != pioneersDefense)
        {
            return "2nd Pioneers 自身不应被 OnOtherCardForecasted 增益："
                 + $"Pioneers={pioneers.Attack}/{pioneers.Defense}（原 {pioneersAttack}/{pioneersDefense}）";
        }

        return null;
    }

    /// <summary>
    /// `card_unit_511th_regiment` keeps a source-specific +2 attack buff in
    /// sync with its private Blueprint predicate.  Exercise both transitions
    /// directly through the dispatch table so the no-op fallback cannot hide a
    /// stale aura.
    /// </summary>
    private static string? FiveEleventhRegimentVeteranBuff(CardDatabase db)
    {
        const string sourceName = "card_unit_511th_regiment";
        const string targetName = "card_unit_16th_infantry_brigade";
        if (db.Find(sourceName) is null || db.Find(targetName) is null)
        {
            return $"卡库里缺 {sourceName} 或 {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = PutOnBoard(state, sourceName, Side.Left, 20, 1);
        var target = PutOnBoard(state, targetName, Side.Left, 21, 2);
        if (target.Keywords.Contains(Keyword.Veteran)
            || !string.Equals(target.Definition.Type, "infantry", StringComparison.OrdinalIgnoreCase))
        {
            return $"测试目标必须是普通步兵，实际 keywords=[{string.Join(',', target.Keywords)}] type={target.Definition.Type}";
        }

        // The live database stores the veteran as a separate `_vet` definition;
        // the gameplay predicate itself consumes the runtime Veteran keyword.
        engine.Api.MakeVeteran(target);
        if (!target.Keywords.Contains(Keyword.Veteran))
        {
            return "MakeVeteran 未将普通步兵目标转换为老兵";
        }

        int baseAttack = target.Attack;
        target.Defense = 1;
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        engine.Api.InvokeByName("checkAndUpdateBuffOnCard", null,
            new object?[] { target }, ctx, out bool handled);
        if (!handled || target.Attack != baseAttack + 2
            || !target.BuffsBySource.TryGetValue((source.CardId, false), out var buff)
            || buff.Attack != 2)
        {
            return $"1 防御老兵步兵应得到来源 {source.CardId} 的 +2 攻击，"
                 + $"handled={handled} attack={target.Attack} base={baseAttack}";
        }

        target.Defense = 2;
        engine.Api.InvokeByName("checkAndUpdateBuffOnCard", null,
            new object?[] { target }, ctx, out bool removeHandled);
        if (!removeHandled || target.Attack != baseAttack
            || target.BuffsBySource.ContainsKey((source.CardId, false)))
        {
            return $"防御离开 1 后应撤回 511th 增益，handled={removeHandled} "
                 + $"attack={target.Attack} base={baseAttack} buffCount={target.BuffsBySource.Count}";
        }

        return null;
    }

    /// <summary>
    /// Veteran upgrade queries use the explicit `_vet` card definitions rather
    /// than the database's variant fallback.  Cards without a distinct variant
    /// still return their base static definition for attack/defense lookups.
    /// </summary>
    private static string? VeteranUpgradeQueries(CardDatabase db)
    {
        const string upgradeCard = "card_unit_266th_guards_rifles";
        const string noUpgradeCard = "card_unit_37mm_m1_aa_gun";
        if (db.Find(upgradeCard) is null || db.Find(upgradeCard + "_vet") is null
            || db.Find(noUpgradeCard) is null)
        {
            return $"卡库里缺 {upgradeCard}(_vet) 或 {noUpgradeCard}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_event_battle_valor", Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var target = state.CreateWithId(upgradeCard, Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        var plain = state.CreateWithId(noUpgradeCard, Side.Left, 22,
            CardLocation.BoardHqLeft, 3);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        object? hasUpgrade = engine.Api.InvokeByName("getHasVeteranUpgrade", target,
            new object?[] { null }, ctx, out bool upgradeHandled);
        if (!upgradeHandled || hasUpgrade is not bool canUpgrade || !canUpgrade)
        {
            return $"{upgradeCard} 应识别为有 `_vet` 变体，handled={upgradeHandled}, result={hasUpgrade}";
        }

        object? noUpgrade = engine.Api.InvokeByName("getHasVeteranUpgrade", plain,
            new object?[] { null }, ctx, out bool plainHandled);
        if (!plainHandled || noUpgrade is not bool hasNoUpgrade || hasNoUpgrade)
        {
            return $"{noUpgradeCard} 不应伪造 `_vet` 升级，handled={plainHandled}, result={noUpgrade}";
        }

        object? veteran = engine.Api.InvokeByName("getStaticVeteranUpgrade", target,
            new object?[] { null }, ctx, out bool staticHandled);
        if (!staticHandled || veteran is not CardInstance veteranCard
            || veteranCard.Name != upgradeCard + "_vet")
        {
            return $"静态老兵模板应为 {upgradeCard}_vet，handled={staticHandled}, result={veteran}";
        }

        object? baseStatic = engine.Api.InvokeByName("getStaticVeteranUpgrade", plain,
            new object?[] { null }, ctx, out bool baseHandled);
        if (!baseHandled || baseStatic is not CardInstance baseCard
            || baseCard.Name != noUpgradeCard)
        {
            return $"无升级卡应回退基础静态模板，handled={baseHandled}, result={baseStatic}";
        }

        return null;
    }

    /// <summary>
    /// `OnOtherCardCreatedAlterCard` 的 `cardPlayed` 载荷必须指向实际新建的卡。
    /// 67th BARANOVICHI 的真实蓝图据此给同阵营 `card_unit_light_infantry`
    /// 授予 Alpine，并拒绝敌方同名单位。
    /// </summary>
    private static string? CreatedAlterEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_67th_baranovichi";
        const string lightInfantry = "card_unit_light_infantry";
        if (db.Find(watcherName) is null || db.Find(lightInfantry) is null)
        {
            return $"卡库里缺 {watcherName} 或 {lightInfantry}";
        }

        var (engine, state) = EmptyBoard(db);
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var enemy = state.CreateWithId(lightInfantry, Side.Right, 61,
            CardLocation.BoardHqRight, 1);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        var ally = state.CreateWithId(lightInfantry, Side.Left, 62,
            CardLocation.BoardHqLeft, 2);

        if (!ally.Keywords.Contains(Keyword.Alpine, StringComparer.Ordinal))
        {
            return "67th BARANOVICHI 没有按 cardPlayed 载荷给己方 light infantry 授予 Alpine";
        }

        if (enemy.Keywords.Contains(Keyword.Alpine, StringComparer.Ordinal))
        {
            return "OnOtherCardCreatedAlterCard 错把敌方 light infantry 当成己方目标并授予 Alpine";
        }

        if (!trace.Any(t => t.StartsWith($"OnOtherCardCreatedAlterCard → {watcher.Name}#{watcher.CardId}",
                                         StringComparison.Ordinal)
                         && t.Contains($"eventCard={ally.Name}#{ally.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardCreatedAlterCard 没有记录实际创建的 cardPlayed 事件主体";
        }

        return null;
    }

    /// <summary>
    /// `OnOtherCardLocationMoved` 的事件主体必须是实际换区的卡，并且新位置参数
    /// 要能让真实 35th INFANTRY REGIMENT 蓝图识别「敌方单位进入前线」。
    /// 该效果随后只给己方场上单位 +1/+1，敌方单位不得被误 buff。
    /// </summary>
    private static string? LocationMovedEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_35th_infantry_regiment";
        const string allyName = PlainUnit;
        const string enemyName = PlainUnit;
        if (db.Find(watcherName) is null || db.Find(allyName) is null)
        {
            return $"卡库里缺 {watcherName} 或 {allyName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Right;
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var ally = state.CreateWithId(allyName, Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        var enemy = state.CreateWithId(enemyName, Side.Right, 60,
            CardLocation.BoardHqRight, 1);
        int allyAttack = ally.Attack;
        int allyDefense = ally.Defense;
        int enemyAttack = enemy.Attack;
        int enemyDefense = enemy.Defense;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            state.Move(enemy, CardLocation.BoardFrontline, 0);
        }
        catch (Exception ex)
        {
            return $"移动敌方单位到前线抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string prefix = $"OnOtherCardLocationMoved → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={enemy.Name}#{enemy.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardLocationMoved 没有把实际推进的敌方单位作为事件主体传给 35th INFANTRY REGIMENT"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (enemy.Location != CardLocation.BoardFrontline)
        {
            return $"测试布景失败：敌方单位未进入前线（实际位置={enemy.Location}）";
        }

        if (ally.Attack != allyAttack + 1 || ally.Defense != allyDefense + 1)
        {
            return $"35th INFANTRY REGIMENT 未给己方单位 +1/+1：期望={allyAttack + 1}/{allyDefense + 1}，"
                 + $"实际={ally.Attack}/{ally.Defense}";
        }

        if (enemy.Attack != enemyAttack || enemy.Defense != enemyDefense)
        {
            return $"35th INFANTRY REGIMENT 错给敌方单位加 buff：期望={enemyAttack}/{enemyDefense}，"
                 + $"实际={enemy.Attack}/{enemy.Defense}";
        }

        return null;
    }

    /// <summary>
    /// `OnOtherCardDiscarded` 的事件主体必须是实际被弃掉的卡。
    /// NAKAJIMA B5N 据此对敌方场上单位造成 1 点伤害；友方单位不应被波及。
    /// </summary>
    private static string? DiscardedEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_nakajima_b5n";
        const string discardedName = PlainUnit;
        if (db.Find(watcherName) is null || db.Find(discardedName) is null)
        {
            return $"卡库里缺 {watcherName} 或 {discardedName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var ally = state.CreateWithId(discardedName, Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        var enemy = state.CreateWithId(discardedName, Side.Right, 60,
            CardLocation.BoardHqRight, 1);
        var discarded = state.CreateWithId(discardedName, Side.Left, 61,
            CardLocation.HandLeft, 3);
        int allyDefense = ally.Defense;
        int enemyDefense = enemy.Defense;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            engine.Api.DiscardCard(discarded, watcher);
        }
        catch (Exception ex)
        {
            return $"DiscardCard 抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string prefix = $"OnOtherCardDiscarded → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={discarded.Name}#{discarded.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardDiscarded 没有把实际被弃掉的卡作为事件主体传给 NAKAJIMA B5N"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (discarded.Location != CardLocation.Discard)
        {
            return $"测试布景失败：被弃卡没有进入弃牌堆（实际位置={discarded.Location}）";
        }

        if (enemy.Defense != enemyDefense - 1)
        {
            return $"NAKAJIMA B5N 未对敌方单位造成 1 点伤害：期望防御={enemyDefense - 1}，实际={enemy.Defense}";
        }

        if (ally.Defense != allyDefense)
        {
            return $"NAKAJIMA B5N 错误伤害己方单位：期望防御={allyDefense}，实际={ally.Defense}";
        }

        return null;
    }

    private static string? SuccessfulHandDiscardEffects(CardDatabase db)
    {
        const string sallyName = "card_unit_sally";
        const string observerName = "card_unit_211th_basargino";
        const string drawName = "card_unit_t_34_85";
        if (db.Find(sallyName) is null || db.Find(observerName) is null || db.Find(drawName) is null)
        {
            return $"卡库缺少测试卡 {sallyName}、{observerName} 或 {drawName}";
        }

        var (engine, state) = EmptyBoard(db);
        var sally = state.CreateWithId(sallyName, Side.Left, 830,
            CardLocation.HandLeft, 0);
        state.CreateWithId(drawName, Side.Left, 831, CardLocation.DeckLeft, 0);
        state.CreateWithId(drawName, Side.Left, 832, CardLocation.DeckLeft, 1);
        engine.Api.DiscardCard(sally);

        if (sally.Location != CardLocation.Discard
            || state.Hand(Side.Left).Count != 2
            || state.Deck(Side.Left).Count != 0)
        {
            return $"未压制的 Sally 从手牌成功弃掉后应抽 2 张，实际位置={sally.Location}、手牌={state.Hand(Side.Left).Count}、牌库={state.Deck(Side.Left).Count}";
        }

        var (suppressedEngine, suppressedState) = EmptyBoard(db);
        var watcher = suppressedState.CreateWithId(observerName, Side.Left, 833,
            CardLocation.BoardHqLeft, 1);
        var suppressedSally = suppressedState.CreateWithId(sallyName, Side.Left, 834,
            CardLocation.HandLeft, 0);
        suppressedSally.Keywords.Add(Keyword.Suppressed);
        int watcherAttack = watcher.Attack;
        suppressedEngine.Api.DiscardCard(suppressedSally);

        if (suppressedSally.Location != CardLocation.Discard
            || suppressedState.Hand(Side.Left).Count != 0
            || watcher.Attack != watcherAttack + 2)
        {
            return $"压制应只跳过 Sally 自身抽牌、仍让 211th 响应弃牌；实际 Sally={suppressedSally.Location}、手牌={suppressedState.Hand(Side.Left).Count}、211th 攻击={watcher.Attack}（原 {watcherAttack}）";
        }

        return null;
    }

    /// <summary>
    /// HIROSAKI REGIMENT 读取 `cardLostSmokescreen`，为同阵营目标 +1/+1 并降低 1 点行动费。
    /// </summary>
    private static string? LoseSmokescreenEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_hirosaki_regiment";
        const string targetName = PlainUnit;
        if (db.Find(watcherName) is null || db.Find(targetName) is null)
        {
            return $"卡库里缺 {watcherName} 或 {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var target = state.CreateWithId(targetName, Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        engine.Api.GiveKeyword(target, Keyword.Smokescreen);
        int attack = target.Attack;
        int defense = target.Defense;
        int operationCost = target.OperationCost;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            engine.Api.RemoveKeyword(target, Keyword.Smokescreen);
        }
        catch (Exception ex)
        {
            return $"RemoveKeyword(Smokescreen) 抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string prefix = $"OnOtherCardLoseSmokescreen → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={target.Name}#{target.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardLoseSmokescreen 没有把失去烟幕的实际目标作为事件主体传给 HIROSAKI REGIMENT"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (target.Keywords.Contains(Keyword.Smokescreen, StringComparer.Ordinal))
        {
            return "测试布景失败：目标仍保留 Smokescreen";
        }

        if (target.Attack != attack + 1 || target.Defense != defense + 1)
        {
            return $"HIROSAKI REGIMENT 未给目标 +1/+1：期望={attack + 1}/{defense + 1}，"
                 + $"实际={target.Attack}/{target.Defense}";
        }

        if (target.OperationCost != Math.Max(0, operationCost - 1))
        {
            return $"HIROSAKI REGIMENT 未降低目标行动费：期望={Math.Max(0, operationCost - 1)}，"
                 + $"实际={target.OperationCost}";
        }

        return null;
    }

    /// <summary>
    /// 110e REGIMENT MOTORIZE 读取 deckSide/instigatorCard：己方首次洗牌时，
    /// 其它己方单位获得 +1/+1，敌方单位不应被影响。
    /// </summary>
    private static string? DeckShuffledEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_110e_regiment_motorize";
        const string unitName = PlainUnit;
        if (db.Find(watcherName) is null || db.Find(unitName) is null)
        {
            return $"卡库里缺 {watcherName} 或 {unitName}";
        }

        var (engine, state) = EmptyBoard(db);
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var ally = state.CreateWithId(unitName, Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        var enemy = state.CreateWithId(unitName, Side.Right, 60,
            CardLocation.BoardHqRight, 1);
        var deckCard = state.CreateWithId(unitName, Side.Left, 72,
            CardLocation.DeckLeft, 0);
        int allyAttack = ally.Attack;
        int allyDefense = ally.Defense;
        int enemyAttack = enemy.Attack;
        int enemyDefense = enemy.Defense;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            engine.Api.InvokeByName("ShuffleDeckBySide", watcher,
                new object?[] { (int)Side.Left, false, watcher.CardId, null },
                new EffectContext
                {
                    Engine = engine,
                    State = state,
                    Self = watcher,
                    Controller = Side.Left,
                }, out _);
        }
        catch (Exception ex)
        {
            return $"ShuffleDeckBySide 抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string prefix = $"OnDeckShuffled → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={watcher.Name}#{watcher.CardId}", StringComparison.Ordinal)))
        {
            return "OnDeckShuffled 没有把实际施动卡作为事件主体传给 110e REGIMENT MOTORIZE"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (deckCard.Location != CardLocation.DeckLeft)
        {
            return $"测试布景失败：牌库卡位置被错误改变（实际位置={deckCard.Location}）";
        }

        if (ally.Attack != allyAttack + 1 || ally.Defense != allyDefense + 1)
        {
            return $"110e REGIMENT MOTORIZE 未给己方单位 +1/+1：期望={allyAttack + 1}/{allyDefense + 1}，"
                 + $"实际={ally.Attack}/{ally.Defense}";
        }

        if (enemy.Attack != enemyAttack || enemy.Defense != enemyDefense)
        {
            return $"110e REGIMENT MOTORIZE 错误给敌方单位加 buff：期望={enemyAttack}/{enemyDefense}，"
                 + $"实际={enemy.Attack}/{enemy.Defense}";
        }

        return null;
    }

    /// <summary>
    /// PANZER III L 的真实订阅程序只应给己方带 Blitz 的其它坦克 +2 攻击，
    /// 并在 Blitz 移除时撤销该来源的增益。
    /// </summary>
    private static string? BlitzChangedEventCarriesPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_panzer_iii_l";
        const string targetName = "card_unit_t_34_85";
        if (db.Find(watcherName) is null || db.Find(targetName) is null)
        {
            return $"卡库里缺 {watcherName} 或 {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var ally = state.CreateWithId(targetName, Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        var enemy = state.CreateWithId(targetName, Side.Right, 60,
            CardLocation.BoardHqRight, 1);
        int allyAttack = ally.Attack;
        int enemyAttack = enemy.Attack;
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            engine.Api.GiveKeyword(ally, Keyword.Blitz);
        }
        catch (Exception ex)
        {
            return $"GiveKeyword(Blitz) 抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string prefix = $"OnOtherCardAbilitiesChanged → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={ally.Name}#{ally.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardAbilitiesChanged 没有把实际获得 Blitz 的己方坦克作为事件主体传给 PANZER III L"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (ally.Attack != allyAttack + 2)
        {
            return $"PANZER III L 未给己方 Blitz 坦克 +2 攻击：期望={allyAttack + 2}，实际={ally.Attack}"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (enemy.Attack != enemyAttack)
        {
            return $"PANZER III L 错误给敌方坦克加攻：期望={enemyAttack}，实际={enemy.Attack}";
        }

        try
        {
            engine.Api.RemoveKeyword(ally, Keyword.Blitz);
        }
        catch (Exception ex)
        {
            return $"RemoveKeyword(Blitz) 抛出 {ex.GetType().Name}: {ex.Message}";
        }

        string removePrefix = $"OnOtherCardBlitzChanged → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(removePrefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={ally.Name}#{ally.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardBlitzChanged 没有把实际失去 Blitz 的己方坦克作为事件主体传给 PANZER III L"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (ally.Attack != allyAttack)
        {
            return $"移除 Blitz 后 PANZER III L 增益未撤销：期望={allyAttack}，实际={ally.Attack}";
        }

        return null;
    }

    /// <summary>
    /// `getCardsBuffedByThisCard` 的蓝图出参是来源卡维护的 int[]。
    /// 内核同时覆盖直接 ChangeAttack/ChangeDefense 形成的 BuffsBySource 账本，
    /// 因此这里用两个不同目标、一个无关来源验证并集口径。
    /// </summary>
    private static string? CardsBuffedByThisCardReturnsIds(CardDatabase db)
    {
        const string sourceName = "card_unit_panzer_iii_l";
        const string targetName = "card_unit_t_34_85";
        if (db.Find(sourceName) is null || db.Find(targetName) is null)
        {
            return $"卡库里缺 {sourceName} 或 {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 801, CardLocation.BoardHqLeft, 1);
        var attackTarget = state.CreateWithId(targetName, Side.Left, 802, CardLocation.BoardHqLeft, 2);
        var defenseTarget = state.CreateWithId(targetName, Side.Left, 803, CardLocation.BoardHqLeft, 3);
        var unrelatedSource = state.CreateWithId(sourceName, Side.Left, 804, CardLocation.BoardHqLeft, 4);
        var unrelatedTarget = state.CreateWithId(targetName, Side.Left, 805, CardLocation.BoardHqLeft, 5);

        engine.Api.ChangeAttack(attackTarget, 2, source);
        engine.Api.ChangeDefense(defenseTarget, 1, source);
        engine.Api.ChangeAttack(unrelatedTarget, 1, unrelatedSource);

        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = source.Owner,
        };
        object? raw = engine.Api.InvokeByName("getCardsBuffedByThisCard", null,
            new object?[] { null }, ctx, out bool handled);

        if (!handled)
        {
            return "getCardsBuffedByThisCard 没进派发表";
        }

        if (raw is not System.Collections.IList list)
        {
            return $"getCardsBuffedByThisCard 返回 {raw?.GetType().Name ?? "null"}，应为 int[] 出参";
        }

        var ids = list.Cast<object?>().OfType<int>().ToHashSet();
        if (!ids.Contains(attackTarget.CardId) || !ids.Contains(defenseTarget.CardId))
        {
            return $"来源卡未列出全部增益目标：实际=[{string.Join(",", ids.OrderBy(x => x))}]，"
                 + $"期望包含 {attackTarget.CardId}/{defenseTarget.CardId}";
        }

        if (ids.Contains(unrelatedTarget.CardId) || ids.Contains(unrelatedSource.CardId))
        {
            return $"来源卡错误列出无关卡：实际=[{string.Join(",", ids.OrderBy(x => x))}]";
        }

        return null;
    }

    /// <summary>
    /// `FullyHealCard` 的 out 参数是实际恢复量，而不是目标的最大防御或请求量。
    /// 同时守住满血幂等和弃牌堆目标门，避免静默改变状态。
    /// </summary>
    private static string? FullyHealCardPrimitive(CardDatabase db)
    {
        const string cardName = "card_unit_t_34_85";
        if (db.Find(cardName) is null)
        {
            return $"卡库里缺 {cardName}";
        }

        var (engine, state) = EmptyBoard(db);
        var target = state.CreateWithId(cardName, Side.Left, 806,
            CardLocation.BoardHqLeft, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = target,
            Controller = target.Owner,
        };

        int max = target.MaxDefense;
        if (max < 2)
        {
            return $"测试卡 {cardName} 的 MaxDefense={max}，不足以构造受伤目标";
        }

        target.Defense = max - 2;
        object? raw = engine.Api.InvokeByName("FullyHealCard", null,
            new object?[] { target }, ctx, out bool handled);
        if (!handled)
        {
            return "FullyHealCard 没进派发表";
        }

        if (raw is not object?[] result || result.Length != 1 || result[0] is not int healed)
        {
            return $"FullyHealCard 返回形状错误：{raw?.GetType().Name ?? "null"}，应为 [int]";
        }

        if (healed != 2 || target.Defense != max)
        {
            return $"受伤目标应恢复 2 点并回到 {max}，实际返回 {healed}、防御 {target.Defense}";
        }

        raw = engine.Api.InvokeByName("FullyHealCard", null,
            new object?[] { target }, ctx, out handled);
        if (!handled || raw is not object?[] fullResult || fullResult.Length != 1
            || fullResult[0] is not int fullHealed || fullHealed != 0
            || target.Defense != max)
        {
            return $"满血目标应返回 0 且保持 {max}，实际返回 {raw ?? "null"}、防御 {target.Defense}";
        }

        target.Location = CardLocation.Discard;
        raw = engine.Api.InvokeByName("FullyHealCard", null,
            new object?[] { target }, ctx, out handled);
        if (!handled || raw is not object?[] deadResult || deadResult.Length != 1
            || deadResult[0] is not int deadHealed || deadHealed != 0
            || target.Defense != max)
        {
            return $"弃牌堆目标应返回 0 且不变更防御，实际返回 {raw ?? "null"}、防御 {target.Defense}";
        }

        return null;
    }

    private static string? OkayamaBlocksUnitFullRepair(CardDatabase db)
    {
        const string observerName = "card_unit_okayama_regiment";
        const string targetName = "card_unit_t_34_85";
        if (db.Find(observerName) is null || db.Find(targetName) is null)
        {
            return $"卡库缺少测试卡 {observerName} 或 {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.CreateWithId(observerName, Side.Left, 812, CardLocation.BoardHqLeft, 1);
        var unit = state.CreateWithId(targetName, Side.Right, 813, CardLocation.BoardHqRight, 1);
        int unitMax = unit.MaxDefense;
        unit.Defense = unitMax - 1;
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = unit,
            Controller = unit.Owner,
        };

        object? raw = engine.Api.InvokeByName("FullyHealCard", null,
            new object?[] { unit }, ctx, out bool handled);
        if (!handled || raw is not object?[] unitResult || unitResult.Length != 1
            || unitResult[0] is not int unitHealed || unitHealed != 0
            || unit.Defense != unitMax - 1)
        {
            return $"Okayama 在场时单位修复应返回 0 并保持 {unitMax - 1} 防御，实际返回 {raw ?? "null"}、防御 {unit.Defense}";
        }

        var hq = state.ById(41)!;
        int hqMax = hq.MaxDefense;
        hq.Defense = hqMax - 1;
        ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = hq,
            Controller = hq.Owner,
        };
        raw = engine.Api.InvokeByName("FullyHealCard", null,
            new object?[] { hq }, ctx, out handled);
        if (!handled || raw is not object?[] hqResult || hqResult.Length != 1
            || hqResult[0] is not int hqHealed || hqHealed != 1
            || hq.Defense != hqMax)
        {
            return $"Okayama 不应阻止 HQ 修复 1 点，实际返回 {raw ?? "null"}、防御 {hq.Defense}";
        }

        return null;
    }

    private static string? GetAllCardsInFrontlinePrimitive(CardDatabase db)
    {
        const string cardName = "card_unit_t_34_85";
        if (db.Find(cardName) is null)
        {
            return $"卡库里缺 {cardName}";
        }

        var (engine, state) = EmptyBoard(db);
        var leftFront = state.CreateWithId(cardName, Side.Left, 807,
            CardLocation.BoardFrontline, 0);
        var rightFront = state.CreateWithId(cardName, Side.Right, 808,
            CardLocation.BoardFrontline, 0);
        var leftSupport = state.CreateWithId(cardName, Side.Left, 809,
            CardLocation.BoardHqLeft, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = leftSupport,
            Controller = leftSupport.Owner,
        };

        object? raw = engine.Api.InvokeByName("GetAllCardsInFrontline", null,
            Array.Empty<object?>(), ctx, out bool handled);
        if (!handled)
        {
            return "GetAllCardsInFrontline 没进派发表";
        }

        if (raw is not System.Collections.IList list)
        {
            return $"GetAllCardsInFrontline 返回 {raw?.GetType().Name ?? "null"}，应为卡数组";
        }

        var ids = list.Cast<object?>().OfType<CardInstance>().Select(card => card.CardId).ToHashSet();
        if (!ids.SetEquals(new[] { leftFront.CardId, rightFront.CardId }))
        {
            return $"前线查询应只返回双方前线卡 {leftFront.CardId}/{rightFront.CardId}，实际=[{string.Join(",", ids.OrderBy(x => x))}]";
        }

        return null;
    }

    private static string? PinnedEventLayer(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        var watcher = state.CreateWithId("card_unit_cromwell_mk_iv", Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var target = state.CreateWithId("card_unit_arado_ar_196", Side.Right, 60,
            CardLocation.BoardFrontline, 1);
        var unpinWatcher = state.CreateWithId("card_event_creeping_barrage", Side.Left, 21,
            CardLocation.Discard, 0);
        unpinWatcher.CustomJson["affectedCard"] = target.CardId.ToString();

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        engine.Api.PinUnit(target);
        if (!target.Keywords.Contains(Keyword.Pinned))
        {
            return "PinUnit 没有写入 Pinned";
        }

        string pinTrace = $"OnOtherUnitPinned → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(pinTrace, StringComparison.Ordinal)
                         && t.Contains($"eventCard={target.Name}#{target.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherUnitPinned 没有把被钉目标作为事件参数传给 Cromwell";
        }

        if (!target.IsSuppressed)
        {
            return "Cromwell 收到 OnOtherUnitPinned 后没有抑制被钉目标";
        }

        engine.Api.RemoveKeyword(target, Keyword.Pinned);
        string unpinTrace = $"OnOtherUnitUnpinned → {unpinWatcher.Name}#{unpinWatcher.CardId}";
        if (!trace.Any(t => t.StartsWith(unpinTrace, StringComparison.Ordinal)
                         && t.Contains($"eventCard={target.Name}#{target.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherUnitUnpinned 没有把解除钉住的目标作为事件参数传给订阅卡";
        }

        if (target.Keywords.Contains(Keyword.Pinned) || target.Location != CardLocation.BoardHqRight)
        {
            return $"解除钉住后的订阅效果未执行：pinned={target.Keywords.Contains(Keyword.Pinned)}，位置={target.Location}";
        }

        return null;
    }

    private static string? PanzergrenadierPinnedOverrideLocalPredicate(CardDatabase db)
    {
        const string sourceName = "card_unit_14_panzergrenadier";
        const string targetName = "card_unit_arado_ar_196";
        if (db.Find(sourceName) is null) return $"卡库里缺 {sourceName}";
        if (db.Find(targetName) is null) return $"卡库里缺 {targetName}";
        if (KismetLibrary.Default?.FindLocalProgram(sourceName, "_qualifiesForUnpinning") is null)
        {
            return "14th Panzergrenadier 的 _qualifiesForUnpinning IR 未加载";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var target = state.CreateWithId(targetName, Side.Left, 21,
            CardLocation.BoardHqRight, 2);
        source.Keywords.Add(Keyword.Veteran);
        target.Keywords.Add(Keyword.Veteran);

        engine.Api.PinUnit(target);
        if (target.CustomAbility != "canOperateWhilePinned"
            || !source.BlueprintSets.TryGetValue("pinned_units_with_override", out var overrides)
            || !overrides.Contains(target))
        {
            return "OnOtherUnitPinned 未把目标加入 override 集合并授予 canOperateWhilePinned";
        }

        engine.ExecuteOnCardLocationMoved(target, CardLocation.BoardHqRight,
            CardLocation.BoardFrontline);
        if (target.CustomAbility != "canOperateWhilePinned")
        {
            return "合法棋盘位置移动后，14th Panzergrenadier 错误撤销了 pinned 操作能力";
        }

        engine.Api.RemoveKeyword(target, Keyword.Pinned);
        if (target.CustomAbility == "canOperateWhilePinned"
            || overrides.Contains(target))
        {
            return "OnOtherUnitUnpinned 未通过 InputPin 命中目标并撤销能力/集合项";
        }

        if (state.UnimplementedCalls.ContainsKey("_qualifiesForUnpinning"))
        {
            return "_qualifiesForUnpinning 仍被记为未实现";
        }

        return null;
    }

    private static string? RetreatEventLayer(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var watcher = state.CreateWithId("card_unit_typhoon_mk_ib", Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var target = state.CreateWithId("card_unit_3_panzergrenadier", Side.Right, 60,
            CardLocation.BoardFrontline, 1);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = watcher,
            Controller = watcher.Owner,
        };
        engine.Api.InvokeByName("MakeCardRetreat", watcher,
            new object?[] { new List<CardInstance> { target }, watcher.CardId }, ctx, out bool handled);

        if (!handled)
        {
            return "MakeCardRetreat 没有进入派发表";
        }

        string prefix = $"OnOtherCardRetreat → {watcher.Name}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={target.Name}#{target.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardRetreat 没有把实际撤退目标传给 Typhoon 订阅程序";
        }

        if (target.Location != CardLocation.BoardHqRight)
        {
            return $"前线单位撤退后应回到右侧半场，实际位置={target.Location}";
        }

        return null;
    }

    private static string? MoveToFrontlineEventPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_blue_legion";
        if (db.Find(watcherName) is null) return $"卡库里缺 {watcherName}";
        if (db.Find(InfRange1) is null) return $"卡库里缺 {InfRange1}";

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);

        // Blue Legion 的 OnOtherCardMoveToFrontline 会把 moveCost 作为推进目标的
        // 攻防增量，因此它能同时证明 cardMoved 目标和具名/位置载荷都正确。
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        watcher.EnteredPlayOnTurn = -99;
        var mover = state.CreateWithId(InfRange1, Side.Left, 60,
            CardLocation.BoardHqLeft, 2);
        mover.EnteredPlayOnTurn = -99;
        int cost = mover.OperationCost;
        int attack = mover.Attack;
        int defense = mover.Defense;

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        if (!engine.MoveUnit(mover, 0, out string why))
        {
            return $"测试单位推进失败：{why}";
        }

        string prefix = $"OnOtherCardMoveToFrontline → {watcherName}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={mover.Name}#{mover.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardMoveToFrontline 没有把实际推进目标传给 Blue Legion"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (mover.Attack != attack + cost || mover.Defense != defense + cost)
        {
            return $"Blue Legion 未消费正确 moveCost：期望攻防={attack + cost}/{defense + cost}，"
                 + $"实际={mover.Attack}/{mover.Defense}（cost={cost}）";
        }

        return null;
    }

    private static string? ReceiveDamageEventPayload(CardDatabase db)
    {
        const string watcherName = "card_unit_panzer_iii_j_late";
        const string victimName = "card_unit_arado_ar_196";
        if (db.Find(watcherName) is null) return $"卡库里缺 {watcherName}";
        if (db.Find(victimName) is null) return $"卡库里缺 {victimName}";

        var (engine, state) = EmptyBoard(db);
        var watcher = state.CreateWithId(watcherName, Side.Left, 20,
            CardLocation.BoardHqLeft, 1);
        var source = state.CreateWithId(victimName, Side.Left, 21,
            CardLocation.BoardHqLeft, 2);
        // Panzer III 的蓝图用 IsLocation(toCard) 判敌方 HQ；这里直接让真实 HQ
        // 受伤，避免把“卡牌类型 Location”和“棋盘位置”混为一谈。
        var victim = state.Hq(Side.Right);

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        engine.Api.DealDamage(victim, 1, source);

        string prefix = $"OnOtherCardReceiveDamage → {watcherName}#{watcher.CardId}";
        if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                         && t.Contains($"eventCard={victim.Name}#{victim.CardId}", StringComparison.Ordinal)))
        {
            return "OnOtherCardReceiveDamage 没有把受伤目标传给 Panzer III"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (watcher.CustomJson.GetValueOrDefault("total_damage") != "1")
        {
            return "Panzer III 没有消费正确的 fromCard/toCard/damage 载荷"
                 + $"（total_damage={watcher.CustomJson.GetValueOrDefault("total_damage", "<缺>")}，"
                 + $"trace={string.Join(" | ", trace)}）";
        }

        return null;
    }

    private static string? EventLayerSurvivedCombat(CardDatabase db)
    {
        foreach (string probe in new[] { "card_unit_bloody_eleventh", "card_unit_slashers", "card_unit_hurricane_mk_ii_c_trop" })
        {
            if (db.Find(probe) is null)
            {
                return $"卡库里缺 {probe}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);

        // 攻击者 = 探针（订阅 OnSurvivedCombat）；旁观者 = 订阅 OnOtherCardSurvivedCombat；
        // 防守方也必须是一张**自己有这个程序**的卡，否则"它收不到"证明不了任何事
        // （这正是上一版自测的漏洞：防守方用了普通单位）。
        var attacker = state.CreateWithId("card_unit_bloody_eleventh", Side.Left, 20, CardLocation.BoardFrontline, 0);
        var bystander = state.CreateWithId("card_unit_slashers", Side.Left, 21, CardLocation.BoardHqLeft, 1);
        var defender = state.CreateWithId("card_unit_hurricane_mk_ii_c_trop", Side.Right, 60, CardLocation.BoardHqRight, 0);
        attacker.EnteredPlayOnTurn = -1;
        bystander.EnteredPlayOnTurn = -1;
        defender.EnteredPlayOnTurn = -1;
        defender.Attack = 0;      // 没有反击 ⇒ 攻击者必定活下来
        defender.Defense = 30;    // 防守方也活下来

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        if (!engine.Attack(attacker, defender))
        {
            return "攻击没打出去（测试布景问题）";
        }

        if (!attacker.IsAlive || !defender.IsAlive)
        {
            return $"布景不对：攻击者 alive={attacker.IsAlive}，防守方 alive={defender.IsAlive}";
        }

        if (!Reached(trace, "OnSurvivedCombat", attacker))
        {
            return "攻击者没有收到 OnSurvivedCombat"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (!Reached(trace, "OnSurvivedCombat", defender))
        {
            return "防守方没有收到 OnSurvivedCombat"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        if (!Reached(trace, "OnOtherCardSurvivedCombat", bystander))
        {
            return "旁观的订阅者没有收到 OnOtherCardSurvivedCombat"
                 + $"\n       实际派发记录：{string.Join(" | ", trace)}";
        }

        // 打 HQ 不进这一段（蓝图 si=3130 IsUnit(defender) 的守卫）
        var (engine2, state2) = EmptyBoard(db);
        state2.ActiveSide = Side.Left;
        state2.SetKredits(Side.Left, 12);
        state2.SetMaxKredits(Side.Left, 12);
        var attacker2 = state2.CreateWithId("card_unit_bloody_eleventh", Side.Left, 20, CardLocation.BoardFrontline, 0);
        var bystander2 = state2.CreateWithId("card_unit_slashers", Side.Left, 21, CardLocation.BoardHqLeft, 1);
        attacker2.EnteredPlayOnTurn = -1;
        bystander2.EnteredPlayOnTurn = -1;

        var trace2 = new List<string>();
        engine2.Api.TriggerTrace = trace2;
        if (!engine2.Attack(attacker2, state2.Hq(Side.Right)))
        {
            return "打 HQ 的攻击没打出去（测试布景问题）";
        }

        if (Reached(trace2, "OnSurvivedCombat", attacker2)
            || Reached(trace2, "OnOtherCardSurvivedCombat", bystander2))
        {
            return "打 HQ 时不该发战斗存活事件（蓝图 `ExecuteAttackCard` si=3130/3171 的 IsUnit 守卫）"
                 + $"\n       实际派发记录：{string.Join(" | ", trace2)}";
        }

        return null;
    }

    /// <summary>
    /// 抑制的三个触发点**先后次序**（2026-10-03 蓝图定案）。
    ///
    /// 蓝图 `SuppressMultipleUnits` 的控制流（`BP_CardFunctions.g.cs`，`L_xxxx` = 十六进制字节偏移，
    /// 与 `out/bp-cardfn.json` 里 `PushExecutionFlow/JumpIfNot` 的目标一一对应）：
    /// <code>
    /// L_0314 (:35806)  if (!_wasAlreadySuppressed) goto L_174B;   ; 只在**首次**抑制时发"自己"
    /// L_174B (:36284)  _card.OnSuppressed()                        ; ← ① 自己那一路
    /// L_176F (:36286)  goto L_0322
    /// L_0322 (:35808)  FetchAllCardsWithEventTrigger(58) → item.OnOtherCardSuppressed(_card)
    /// L_03CA (:35818)  58 那轮循环跑完 → 弹执行流栈到 1358 = L_054E（摘关键词/增益）
    /// L_114B (:36077)  摘完 → 弹到 4510 = L_119E（数值回落）
    /// L_1532 (:36171)  回落完 → 弹到 5541 = L_15A5
    /// L_15C6 (:36221)  FetchAllCardsWithEventTrigger(11) → item.OnAfterOtherCardSuppressed(_card)
    /// </code>
    /// ⇒ **自己 OnSuppressed → T58 广播 → T11 广播**（摘除/回落夹在 T58 与 T11 之间）。
    /// 旧内核写成 T11 → OnSuppressed → T58（三个全错位）。
    ///
    /// 反向证据（防"读错分支方向"）：`out/bp-cardfn.json` 里 `si=788` 是
    /// <c>JumpIfNot(Condition = _wasAlreadySuppressed, Offset = 6194)</c>，
    /// 而 6194 正是 `si=6194 Context-&gt;OnSuppressed` —— **条件为假才跳到 OnSuppressed**；
    /// 紧接着 `si=6230` 是 `Jump(Offset = 802)`，802 = `si=802` = 58 号 `FetchAllCardsWithEventTrigger`。
    /// </summary>
    private static string? SuppressTriggerOrderMatchesBlueprint(CardDatabase db)
    {
        const string victimName = "card_unit_raaf_walrus";         // 订阅 OnSuppressed（自己那一路）
        const string t58Name = "card_unit_kings_own_scottish";     // 订阅 OnOtherCardSuppressed（触发号 58）
        const string t11Name = "card_unit_adler_command_vehicle";  // 订阅 OnAfterOtherCardSuppressed（触发号 11）

        foreach (string n in new[] { victimName, t58Name, t11Name })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        var victim = state.CreateWithId(victimName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        state.CreateWithId(t58Name, Side.Left, 21, CardLocation.BoardHqLeft, 2);
        state.CreateWithId(t11Name, Side.Left, 22, CardLocation.BoardHqLeft, 3);

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            engine.Api.SuppressUnit(victim);
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }

        if (!victim.Keywords.Contains(Keyword.Suppressed))
        {
            return "布景失败：victim 没被抑制（本用例的前提）";
        }

        static int First(List<string> tr, string ev)
            => tr.FindIndex(t => t.StartsWith(ev + " → ", StringComparison.Ordinal));

        int iSelf = First(trace, "OnSuppressed");
        int i58 = First(trace, "OnOtherCardSuppressed");
        int i11 = First(trace, "OnAfterOtherCardSuppressed");

        string Dump() => "\n       实际派发记录：" + string.Join(" | ", trace.Take(12));

        if (iSelf < 0)
        {
            return "自己那一路 OnSuppressed 没发（蓝图 L_174B）" + Dump();
        }

        if (i58 < 0)
        {
            return "T58 广播 OnOtherCardSuppressed 没发（蓝图 L_0322）" + Dump();
        }

        if (i11 < 0)
        {
            return "T11 广播 OnAfterOtherCardSuppressed 没发（蓝图 L_15C6）" + Dump();
        }

        if (!(iSelf < i58 && i58 < i11))
        {
            return $"触发次序错了：蓝图是 自己 OnSuppressed → T58 → T11，"
                 + $"实际次序 index 为 OnSuppressed={iSelf} / OnOtherCardSuppressed(58)={i58} / "
                 + $"OnAfterOtherCardSuppressed(11)={i11}" + Dump();
        }

        return null;
    }

    /// <summary>
    /// `PlayCard` 里 T51 与 T43 的**逐卡次序**（2026-10-03 蓝图定案）。
    ///
    /// 蓝图 `BP_CardFunctions::CardPlayedFromHand`：
    /// <code>
    /// :6060  otherCards = FetchAllCardsWithEventTrigger(51)      ; T51 = OnOtherCardPlayedFromHand
    /// :6064  tmp       = FetchAllCardsWithEventTrigger(43)       ; T43 = OnOtherCardEnterPlay
    /// :6066  Array_Append(otherCards, tmp)                       ; ★ 两者进**同一个**数组
    /// ...
    /// :6462  OnOtherCardPlayedFromHand(_tmpOtherCard, cardPlayed) ; ★ 同一张卡：先 T51
    /// :6464  OnOtherCardEnterPlay(_tmpOtherCard, cardPlayed, 1)   ; ★ 再 T43
    /// :6466  cardsDone.Add(_tmpOtherCard.cardID)
    /// </code>
    /// 订阅这两个触发点的**交集只有 4 张**：`card_brawl_test1` / `card_location_british_scen5` /
    /// `card_unit_269th_rifles` / `card_unit_kv_1s`（对 `docs/card-ir.json` 的 `entrypoints` 求交）。
    /// 这里拿 `card_unit_kv_1s` 当**旁观探针**（放在场上），打一张普通单位，看派发记录里的先后。
    ///
    /// ⚠️ 本用例**只**守 T51/T43 的相对次序，不涉及「广播 vs 卡自己的 `RunDeploymentEffect`」。
    /// </summary>
    private static string? PlayCardOtherTriggersOrder(CardDatabase db)
    {
        const string watcherName = "card_unit_kv_1s";        // 同时订阅 T51 与 T43
        const string playedName = "card_unit_269th_rifles";  // 从手牌打出的普通单位

        foreach (string n in new[] { watcherName, playedName })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);

        var watcher = state.CreateWithId(watcherName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var played = state.CreateWithId(playedName, Side.Left, 21, CardLocation.HandLeft, 0);

        if (!engine.CanPlay(played, out string why))
        {
            return $"前置不成立：{playedName} 打不出（{why}）";
        }

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;

        try
        {
            engine.PlayCard(played);
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }

        int Index(string ev)
            => trace.FindIndex(t => t.StartsWith($"{ev} → {watcher.Name}#{watcher.CardId}",
                                                StringComparison.Ordinal));

        int i51 = Index("OnOtherCardPlayedFromHand");
        int i43 = Index("OnOtherCardEnterPlay");

        string Dump() => "\n       实际派发记录：" + string.Join(" | ", trace.Take(14));

        if (i51 < 0)
        {
            return $"T51 `OnOtherCardPlayedFromHand` 没派发到旁观卡 {watcher.Name}#{watcher.CardId}" + Dump();
        }

        if (i43 < 0)
        {
            return $"T43 `OnOtherCardEnterPlay` 没派发到旁观卡 {watcher.Name}#{watcher.CardId}" + Dump();
        }

        if (!(i51 < i43))
        {
            return $"逐卡次序错了：蓝图是 T51 在前（:6462）、T43 在后（:6464），"
                 + $"实际 index 为 OnOtherCardPlayedFromHand={i51} / OnOtherCardEnterPlay={i43}" + Dump();
        }

        return null;
    }

    /// <summary>
    /// The played-card broadcasts must use the recipient list captured before
    /// the card's own effect.  NIGHT RAID creates a COMMANDO; that new unit
    /// also subscribes to T51, but must not react to the order that created it.
    /// </summary>
    private static string? PlayCardBroadcastUsesEarlySnapshot(CardDatabase db)
    {
        const string raidName = "card_event_night_raid";
        const string commandoName = "card_unit_commandos";
        const string victimName = "card_unit_85_pioneer_company";
        const string deckName = "card_unit_arado_ar_196";
        foreach (string name in new[] { raidName, commandoName, victimName, deckName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);

        var victim = state.CreateWithId(victimName, Side.Right, 60,
            CardLocation.BoardHqRight, 1);
        victim.EnteredPlayOnTurn = -1;
        victim.Defense = 10;
        state.CreateWithId(deckName, Side.Right, 61, CardLocation.DeckRight, 0);
        var raid = state.CreateWithId(raidName, Side.Left, 2, CardLocation.HandLeft, 0);

        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        int victimBefore = victim.Defense;
        if (!engine.PlayCard(raid))
        {
            return "NIGHT RAID 打出失败：" + string.Join(" | ", trace);
        }

        var commandos = state.AllCards.Where(c => c.Name == commandoName).ToList();
        if (commandos.Count != 1)
        {
            return $"NIGHT RAID 应生成 1 张 COMMANDO，实际 {commandos.Count} 张";
        }

        if (trace.Any(x => x.StartsWith("OnOtherCardPlayedFromHand", StringComparison.Ordinal)
                        && x.Contains($"{commandoName}#", StringComparison.Ordinal)))
        {
            return "战吼新生成的 COMMANDO 错误收到创建它的 T51 广播";
        }

        if (victim.Defense != victimBefore)
        {
            return $"新生成的 COMMANDO 错误触发 T51 并伤害敌方单位：{victimBefore} → {victim.Defense}";
        }

        return null;
    }

    /// <summary>
    /// 蓝图 `CardPlayedFromHand` 的未揭示 Covert 专用分支：
    /// `OnCovertCardPlayedFromHand`（主体）→ `OnOtherCovertCardPlayedFromHand`
    ///（排除主体）→ 普通出牌前触发。这里只验证可观测的旁观广播与载荷，
    /// 并确认普通牌不会误触发该分支。
    /// </summary>
    private static string? PlayCardCovertTriggers(CardDatabase db)
    {
        const string watcherName = "card_unit_sissiosasto_5d";
        const string covertName = "card_unit_115th_separate_btn";
        const string ordinaryName = "card_unit_1st_infantry_regiment_us";
        foreach (string name in new[] { watcherName, covertName, ordinaryName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        static (MatchEngine Engine, GameState State, CardInstance Watcher, CardInstance Played)
            Setup(CardDatabase db, string playedName)
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Right;
            state.SetKredits(Side.Right, 12);
            state.SetMaxKredits(Side.Right, 12);
            var watcher = state.CreateWithId("card_unit_sissiosasto_5d", Side.Left, 20,
                CardLocation.BoardHqLeft, 1);
            var played = state.CreateWithId(playedName, Side.Right, 42,
                CardLocation.HandRight, 0);
            return (engine, state, watcher, played);
        }

        var ordinary = Setup(db, ordinaryName);
        var ordinaryTrace = new List<string>();
        ordinary.Engine.Api.TriggerTrace = ordinaryTrace;
        if (!ordinary.Engine.PlayCard(ordinary.Played))
        {
            return $"普通牌打出失败：{string.Join(" | ", ordinaryTrace)}";
        }

        if (ordinaryTrace.Any(x => x.StartsWith("OnOtherCovertCardPlayedFromHand",
                                                  StringComparison.Ordinal)))
        {
            return "普通牌错误触发 OnOtherCovertCardPlayedFromHand";
        }

        var covert = Setup(db, covertName);
        var covertTrace = new List<string>();
        covert.Engine.Api.TriggerTrace = covertTrace;
        if (!covert.Engine.PlayCard(covert.Played))
        {
            return $"Covert 牌打出失败：{string.Join(" | ", covertTrace)}";
        }

        string? eventTrace = covertTrace.FirstOrDefault(x =>
            x.StartsWith($"OnOtherCovertCardPlayedFromHand → {covert.Watcher.Name}#{covert.Watcher.CardId}",
                         StringComparison.Ordinal));
        if (eventTrace is null)
        {
            return "未揭示 Covert 牌没有派发到旁观卡，实际记录："
                   + string.Join(" | ", covertTrace.Take(16));
        }

        if (!eventTrace.Contains($"eventCard={covert.Played.Name}#{covert.Played.CardId}",
                                 StringComparison.Ordinal))
        {
            return $"Covert 广播载荷错误：{eventTrace}";
        }

        return null;
    }

    /// <summary>
    /// 攻击前触发点的**接收者**与**先后**（2026-10-03 蓝图定案）。
    ///
    /// 蓝图 `BP_CardFunctions::AttackCard` 的控制流（`L_xxxx` = 十六进制字节偏移，
    /// 与 `out/bp-cardfn.json` 的 `StatementIndex` 一一对应 —— 那份 dump 的 `StatementIndex`
    /// 就是**字节偏移**，已在 `SuppressMultipleUnits` 上逐条核对过）：
    /// <code>
    /// si=2911 (:0B5F)  _attackerCard.IsLocatedOnBoard(out isIt_2)
    /// si=2952 (:0B88)  JumpIfNot(isIt_2) → 3640          ; 攻击者不在场 ⇒ 整段跳过
    /// si=2966 (:0B96)  JumpIfNot(_attackerCard.isSuppressed) → 3590   ; ★ 条件为**假**才跳
    /// si=3002 (:0BBA)  FetchAllCardsWithEventTrigger(13)               ; T13 那一轮
    /// si=3382 (:0D36)      item.OnBeforeOtherCardAttacks(_attackerCard, _defenderCard)
    /// si=3511 (:0DB7)  _attackerCard.cardFunction.RemoveSmokescreen(…)
    /// si=3589 (:0E05)  PopExecutionFlow
    /// si=3590 (:0E06)  _attackerCard.OnBeforeAttack(_defenderCard)     ; ★ 接收者只有攻击者
    /// si=3635 (:0E33)  Jump → 3002                                     ; ★ 回到 T13 那一轮
    /// </code>
    /// ⇒ 真实次序 = **`OnBeforeAttack`(只给攻击者、被压制时跳过) → T13 广播(排除攻击者) → RemoveSmokescreen**。
    /// 旧内核写成「一次 `FireTrigger` 里按遍历序混发 + 再给防御方补一次 `OnBeforeAttack`」，
    /// 于是 (a) **防御方多发**、(b) 次序随建卡序漂移。
    ///
    /// ⚠️ 本用例**不**守 `RemoveSmokescreen` 的位置与门槛（旧内核把它放在这两个触发点**之前**
    /// 且加了"被压制就不移除"的门，蓝图两条都不是）—— 那是另一处独立偏差，见报告。
    /// </summary>
    private static string? AttackBeforeTriggersRecipientsAndOrder(CardDatabase db)
    {
        const string attackerName = "card_unit_infantry_regiment_25";   // 订阅 OnBeforeAttack（13 张之一）
        const string defenderName = "card_unit_75mm_field_artillery";   // 同样订阅 OnBeforeAttack —— 但**不该**收到
        const string t13Name = "card_unit_the_rangers";                 // 订阅 OnBeforeOtherCardAttacks（8 张之一）

        foreach (string n in new[] { attackerName, defenderName, t13Name })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        // 布景：旁观者的 `LocationNumber` **更小**（0 < 1）。`FireTrigger` 的快照走
        // `State.Board(s)` → `Cards(s)`，排序键是 `(LocationNumber, CardId)`，
        // 所以旁观者会被**先**访问 —— 这样"T13 排在 OnBeforeAttack 之前"这个次序错误
        // 才不会被遍历序掩盖。
        (MatchEngine Engine, GameState State, CardInstance Bystander, CardInstance Attacker, CardInstance Defender)
            Stage(bool attackerSuppressed)
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 12);
            state.SetMaxKredits(Side.Left, 12);

            var bystander = state.CreateWithId(t13Name, Side.Left, 20, CardLocation.BoardFrontline, 0);
            var attacker = state.CreateWithId(attackerName, Side.Left, 21, CardLocation.BoardFrontline, 1);
            var defender = state.CreateWithId(defenderName, Side.Right, 60, CardLocation.BoardHqRight, 0);
            bystander.EnteredPlayOnTurn = -1;
            attacker.EnteredPlayOnTurn = -1;
            defender.EnteredPlayOnTurn = -1;
            defender.Attack = 0;      // 没有反击 ⇒ 攻击者必定活下来
            defender.Defense = 30;    // 防守方也活下来

            if (attackerSuppressed)
            {
                attacker.Keywords.Add(Keyword.Suppressed);
            }

            return (engine, state, bystander, attacker, defender);
        }

        static int Index(List<string> tr, string ev, CardInstance c)
            => tr.FindIndex(t => t.StartsWith($"{ev} → {c.Name}#{c.CardId}", StringComparison.Ordinal));

        // ---- ① 没被压制：OnBeforeAttack(攻击者) → T13 广播；防御方一个都不该收到 ----
        {
            var (engine, st, bystander, attacker, defender) = Stage(false);
            var trace = new List<string>();
            engine.Api.TriggerTrace = trace;

            if (!engine.Attack(attacker, defender, out string why))
            {
                return $"前置不成立：攻击打不出去（{why}）";
            }

            string Dump() => "\n       实际派发记录：" + string.Join(" | ", trace)
                           + "\n       左场建卡序：" + string.Join(", ",
                                 st.Board(Side.Left).Select(c => $"{c.Name}#{c.CardId}"));

            int iSelf = Index(trace, "OnBeforeAttack", attacker);
            int i13 = Index(trace, "OnBeforeOtherCardAttacks", bystander);
            int iDef = Index(trace, "OnBeforeAttack", defender);

            if (iDef >= 0)
            {
                return $"防御方 {defender.Name}#{defender.CardId} **不该**收到 `OnBeforeAttack`"
                     + $"（蓝图 :4633 的接收者是 `_attackerCard`）" + Dump();
            }

            if (iSelf < 0)
            {
                return $"攻击者自己那一路 `OnBeforeAttack` 没发（蓝图 :3590）" + Dump();
            }

            if (i13 < 0)
            {
                return $"T13 `OnBeforeOtherCardAttacks` 没派发到旁观卡 {bystander.Name}#{bystander.CardId}" + Dump();
            }

            if (!(iSelf < i13))
            {
                return $"次序错了：蓝图是 `OnBeforeAttack` 在前（:3590 → :3635 Jump 3002）、"
                     + $"T13 广播在后（:3382），实际 index 为 OnBeforeAttack={iSelf} / OnBeforeOtherCardAttacks={i13}"
                     + Dump();
            }
        }

        // ---- ② 被压制：攻击者**自己**那一路不发（蓝图 si=2966 的门），但 T13 广播照发 ----
        {
            var (engine, _, bystander, attacker, defender) = Stage(true);
            var trace = new List<string>();
            engine.Api.TriggerTrace = trace;

            if (!engine.Attack(attacker, defender, out string why))
            {
                return $"前置不成立：被压制的攻击者打不出去（{why}）";
            }

            if (Index(trace, "OnBeforeAttack", attacker) >= 0)
            {
                return "被压制的攻击者**不该**收到自己那一路 `OnBeforeAttack`（蓝图 si=2966 的门）"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }

            if (Index(trace, "OnBeforeOtherCardAttacks", bystander) < 0)
            {
                return "被压制只是跳过攻击者**自己**那一路；T13 广播仍应照发（蓝图 si=3002 两条路都会走到）"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }
        }

        return null;
    }

    private static string? EventLayerSuppressionGate(CardDatabase db)
    {
        const string victim = "card_unit_arado_ar_196";

        // ① 被压制 ⇒ 不广播 OnOtherCardBecomingVeteran，但自己那一路照发
        {
            var (trace, gate, err) = Probe(db, "card_unit_6th_brigade_nz", CardLocation.BoardHqLeft,
                (e, s, p) =>
                {
                    var v = s.CreateWithId(victim, Side.Right, 60, CardLocation.BoardHqRight, 0);
                    e.Api.SuppressUnit(v);
                    e.Api.MakeVeteran(v);
                    return p;   // ★ 断言看的是**旁观探针**，不是被压制的那张卡
                });
            if (err is not null)
            {
                return $"压制/老兵布景出错：{err}";
            }

            if (Reached(trace, "OnOtherCardBecomingVeteran", gate))
            {
                return "被压制的卡不该广播 OnOtherCardBecomingVeteran（蓝图 MakeVeteran si=2427）";
            }

            // 被压制的那张卡自己仍然收到 OnBecomingVeteran —— 找一张有该程序的卡来验
            var (trace2, _, err2) = Probe(db, "card_unit_7th_brigade_anzac", CardLocation.BoardHqLeft,
                (e, s, p) =>
                {
                    e.Api.SuppressUnit(p);
                    e.Api.MakeVeteran(p);
                    return p;
                });
            if (err2 is not null)
            {
                return $"压制/老兵布景出错(2)：{err2}";
            }

            if (!trace2.Any(t => t.StartsWith("OnBecomingVeteran → ", StringComparison.Ordinal)))
            {
                return "被压制的卡**自己**仍然应该收到 OnBecomingVeteran（蓝图 MakeVeteran si=2806）"
                     + $"\n       实际派发记录：{string.Join(" | ", trace2)}";
            }
        }

        // ② 没被压制 ⇒ 广播要发（证明 ① 的"没发"不是因为程序名写错/探针没订阅）
        {
            var (trace, gate, err) = Probe(db, "card_unit_6th_brigade_nz", CardLocation.BoardHqLeft,
                (e, s, p) =>
                {
                    e.Api.MakeVeteran(s.CreateWithId(victim, Side.Right, 61, CardLocation.BoardHqRight, 0));
                    return p;
                });
            if (err is not null)
            {
                return $"老兵布景出错：{err}";
            }

            if (!Reached(trace, "OnOtherCardBecomingVeteran", gate))
            {
                return "没被压制的卡**应该**广播 OnOtherCardBecomingVeteran"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }
        }

        // ③ 被压制 ⇒ OnBeforeDestroyed / OnBeforeOtherCardDestroyed 两个都不发
        {
            var (trace, victimCard, err) = Probe(db, "card_unit_marder_iii_h", CardLocation.BoardHqLeft,
                (e, s, p) =>
                {
                    // 让**探针自己**被压制后被摧毁：它是 OnBeforeOtherCardDestroyed 的订阅者，
                    // 同时我们要验"被压制者收不到 OnBeforeDestroyed"。
                    e.Api.SuppressUnit(p);
                    e.Destroy(p);
                    return p;
                });
            if (err is not null)
            {
                return $"摧毁布景出错：{err}";
            }

            if (Reached(trace, "OnBeforeDestroyed", victimCard))
            {
                return "被压制的卡不该收到 OnBeforeDestroyed（蓝图 ExecuteOnBeforeOtherCardDestroyed si=140）";
            }

            var (trace2, other, err2) = Probe(db, "card_unit_marder_iii_h", CardLocation.BoardHqLeft,
                (e, s, p) =>
                {
                    var v = s.CreateWithId(victim, Side.Right, 62, CardLocation.BoardHqRight, 0);
                    e.Api.SuppressUnit(v);
                    e.Destroy(v);
                    return p;
                });
            if (err2 is not null)
            {
                return $"摧毁布景出错(2)：{err2}";
            }

            if (Reached(trace2, "OnBeforeOtherCardDestroyed", other))
            {
                return "被压制的卡被摧毁时不该广播 OnBeforeOtherCardDestroyed（si=140 的守卫）";
            }

            // ④ 没被压制 ⇒ 广播要发（对照，防止 ③ 恒真）
            var (trace3, other3, err3) = Probe(db, "card_unit_marder_iii_h", CardLocation.BoardHqLeft,
                (e, s, p) =>
                {
                    e.Destroy(s.CreateWithId(victim, Side.Right, 63, CardLocation.BoardHqRight, 0));
                    return p;
                });
            if (err3 is not null)
            {
                return $"摧毁布景出错(3)：{err3}";
            }

            if (!Reached(trace3, "OnBeforeOtherCardDestroyed", other3))
            {
                return "没被压制的卡被摧毁时**应该**广播 OnBeforeOtherCardDestroyed"
                     + $"\n       实际派发记录：{string.Join(" | ", trace3)}";
            }
        }

        // ⑤ 全局 suppression gate：普通广播的旁观接收者被抑制时跳过，
        // 未被抑制时仍应收到。直接调用能力变化事件，避免把 SuppressUnit
        // 自身产生的多次关键词变化混进断言。
        {
            const string watcherName = "card_unit_royal_west_kents";
            if (db.Find(watcherName) is null)
            {
                return $"卡库里缺 {watcherName}";
            }

            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            var changing = state.CreateWithId(victim, Side.Right, 64, CardLocation.BoardHqRight, 0);
            var watcher = state.CreateWithId(watcherName, Side.Left, 65, CardLocation.BoardHqLeft, 1);
            var trace = new List<string>();
            engine.Api.TriggerTrace = trace;

            engine.Api.FireAbilitiesChanged(changing);
            if (!Reached(trace, "OnOtherCardAbilitiesChanged", watcher))
            {
                return "未被抑制的旁观卡应该收到 OnOtherCardAbilitiesChanged"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }

            trace.Clear();
            watcher.Keywords.Add(Keyword.Suppressed);
            engine.Api.FireAbilitiesChanged(changing);
            if (Reached(trace, "OnOtherCardAbilitiesChanged", watcher))
            {
                return "被抑制的旁观卡不该收到 OnOtherCardAbilitiesChanged"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }

            trace.Clear();
            watcher.SuppressionExceptionTriggers.Add("OnOtherCardAbilitiesChanged");
            engine.Api.FireAbilitiesChanged(changing);
            if (!Reached(trace, "OnOtherCardAbilitiesChanged", watcher))
            {
                return "suppressionExceptionTriggers 命中的程序应该允许被抑制旁观卡接收"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }
        }

        // ⑥ T7 的蓝图专属门：被抑制的目标不收到自己的 OnAfterGainDefense。
        {
            const string targetName = "card_unit_kyushu_j7w3";
            var (engine, state) = EmptyBoard(db);
            var target = state.CreateWithId(targetName, Side.Left, 66, CardLocation.BoardHqLeft, 1);
            target.Keywords.Add(Keyword.Suppressed);
            var trace = new List<string>();
            engine.Api.TriggerTrace = trace;
            engine.Api.ChangeDefense(target, 1, null);
            if (Reached(trace, "OnAfterGainDefense", target))
            {
                return "被抑制的目标不该收到自己的 OnAfterGainDefense（蓝图 T7 :7954 门）"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }
        }

        return null;
    }

    // ==================================================================
    //  P0 第 2 族：同形「接收者/参数位」bug 的自测
    // ==================================================================

    /// <summary>直接按蓝图实参形状调一次派发表原语。</summary>
    private static (object? Result, EffectContext Ctx, CardInstance Probe) CallPrimitive(
        CardDatabase db, string fn, string probeName, object? receiver, params object?[] args)
        => CallPrimitive(db, fn, probeName, receiver, selfIsProbe: true, args);

    private static (object? Result, EffectContext Ctx, CardInstance Probe) CallPrimitive(
        CardDatabase db, string fn, string probeName, object? receiver, bool selfIsProbe, params object?[] args)
    {
        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        var probe = state.CreateWithId(probeName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = selfIsProbe ? probe : null,
            Controller = Side.Left,
        };
        object? result = engine.Api.InvokeByName(fn, receiver, args, ctx, out _);
        return (result, ctx, probe);
    }

    private static string? CustomAbilityArgs(CardDatabase db)
    {
        const string card = "card_unit_arado_ar_196";
        if (db.Find(card) is null)
        {
            return $"卡库里缺 {card}";
        }

        // ① 接收者身上挂 destruction ⇒ HasCustomAbilityFromCard("destruction", …) 必须真
        var (engine, state) = EmptyBoard(db);
        var target = state.CreateWithId(card, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        target.CustomAbility = "destruction";
        var ctx2 = new EffectContext { Engine = engine, State = state, Self = target, Controller = Side.Left };

        bool FromCard(object?[] args) => (bool)(engine.Api.InvokeByName(
            "HasCustomAbilityFromCard", target, args, ctx2, out _) ?? false);

        // 蓝图实参形状：a[0]=能力名(str)  a[1]=giverID/cardID  a[2]=out 槽
        if (!FromCard(new object?[] { "destruction", target.CardId, null }))
        {
            return "HasCustomAbilityFromCard(\"destruction\", …) 对一张挂了 destruction 的卡应为真（旧实现读 a[0] 当卡 ⇒ 恒 false）";
        }

        if (FromCard(new object?[] { "trigger", target.CardId, null }))
        {
            return "HasCustomAbilityFromCard(\"trigger\", …) 对只挂了 destruction 的卡应为假（能力名必须被读进去）";
        }

        // ② 隐式 self（无 recv）时也要认得出接收者
        if (!FromCard(new object?[] { "destruction", target.CardId }))
        {
            return "HasCustomAbilityFromCard 少了 out 槽时（隐式 self 形态）仍应为真";
        }

        // ③ HasCustomAbility 同理：能力名必须传下去
        bool HasAbility(string ability) => (bool)(engine.Api.InvokeByName(
            "HasCustomAbility", null, new object?[] { ability, null }, ctx2, out _) ?? false);
        if (!HasAbility("destruction"))
        {
            return "HasCustomAbility(\"destruction\") 应为真";
        }

        if (HasAbility("cantRetreat"))
        {
            return "HasCustomAbility(\"cantRetreat\") 对只挂了 destruction 的卡应为假（旧实现丢掉能力名 ⇒ 只要「有任意能力」就真）";
        }

        return null;
    }

    private static string? ImplicitSelfReceivers(CardDatabase db)
    {
        const string card = "card_unit_arado_ar_196";
        if (db.Find(card) is null)
        {
            return $"卡库里缺 {card}";
        }

        var (engine, state) = EmptyBoard(db);
        var probe = state.CreateWithId(card, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };

        // ① IsVeteran：IR 里 70/75 个调用点没有 recv（实参只有 bool + out 槽）
        if ((bool)(engine.Api.InvokeByName("IsVeteran", null, new object?[] { false, null }, ctx, out _) ?? false))
        {
            return "非老兵单位被判成老兵了";
        }

        probe.Keywords.Add(Keyword.Veteran);
        if (!(bool)(engine.Api.InvokeByName("IsVeteran", null, new object?[] { false, null }, ctx, out _) ?? false))
        {
            return "隐式 self 的 IsVeteran 恒 false（旧实现读 recv，而 recv 是 null）——老兵卡的所有分支都会走错";
        }

        // ② IsDamaged：3/10 个调用点无 recv
        probe.Defense = probe.MaxDefense;
        if ((bool)(engine.Api.InvokeByName("IsDamaged", null, new object?[] { null }, ctx, out _) ?? false))
        {
            return "满血单位被判成受伤了";
        }

        probe.Defense = Math.Max(1, probe.MaxDefense - 1);
        if (!(bool)(engine.Api.InvokeByName("IsDamaged", null, new object?[] { null }, ctx, out _) ?? false))
        {
            return "隐式 self 的 IsDamaged 恒 false（旧实现读 recv = null）";
        }

        // ③ getTotalHeavyArmor：3 个调用点里 2 个无 recv。
        //    ⚠️ 重甲点数**不是**关键字，是 `Definition.HeavyArmor + 各来源的 heavyArmorBuff`
        //    （CardInstance.cs:148-160），所以这里用一张天生带重甲的卡当探针。
        const string armored = "card_unit_char_b1_bis";   // CDO heavyArmor=2（CardInnateTable）
        if (db.Find(armored) is null || db.Find(armored)!.HeavyArmor <= 0)
        {
            return $"{armored} 应该天生带重甲（CDO heavyArmor>0），卡库里的值是 "
                 + $"{(db.Find(armored) is null ? "卡不存在" : db.Find(armored)!.HeavyArmor.ToString())}";
        }

        var (engine2, state2) = EmptyBoard(db);
        var armoredCard = state2.CreateWithId(armored, Side.Left, 21, CardLocation.BoardHqLeft, 2);
        var ctx2 = new EffectContext { Engine = engine2, State = state2, Self = armoredCard, Controller = Side.Left };
        int armor = Convert.ToInt32(engine2.Api.InvokeByName("getTotalHeavyArmor", null, new object?[] { null }, ctx2, out _) ?? -1);
        if (armor != armoredCard.HeavyArmor || armor <= 0)
        {
            return $"隐式 self 的 getTotalHeavyArmor 恒 0（{armored} 卡面重甲 {armoredCard.HeavyArmor}，实际拿到 {armor}）";
        }

        return null;
    }

    private static string? DeactivateOtherSniped(CardDatabase db)
    {
        const string sniped = "card_event_sniped";
        if (db.Find(sniped) is null)
        {
            return $"卡库里缺 {sniped}";
        }

        var (engine, state) = EmptyBoard(db);
        var resolving = state.CreateWithId(sniped, Side.Left, 30, CardLocation.HandLeft, 0);
        var other = state.CreateWithId(sniped, Side.Left, 31, CardLocation.HandLeft, 1);
        var enemy = state.CreateWithId(sniped, Side.Right, 32, CardLocation.HandRight, 0);
        var ctx = new EffectContext { Engine = engine, State = state, Self = resolving, Controller = Side.Left };

        engine.Api.InvokeByName("DeactivateOtherSniped", null, Array.Empty<object?>(), ctx, out _);

        if (!engine.Api.HasCustomAbility(other, "DisableOtherSniped"))
        {
            return "同阵营手牌中的其他 Sniped 未添加 DisableOtherSniped";
        }

        if (engine.Api.HasCustomAbility(resolving, "DisableOtherSniped"))
        {
            return "正在结算的 Sniped 不应标记自身";
        }

        if (engine.Api.HasCustomAbility(enemy, "DisableOtherSniped"))
        {
            return "不应标记对手手牌中的 Sniped";
        }

        return null;
    }

    private static string? DeactivateOtherNfs(CardDatabase db)
    {
        const string nfs = "card_event_national_fire_service";
        if (db.Find(nfs) is null)
        {
            return $"卡库里缺 {nfs}";
        }

        var (engine, state) = EmptyBoard(db);
        var resolving = state.CreateWithId(nfs, Side.Left, 140, CardLocation.HandLeft, 0);
        var other = state.CreateWithId(nfs, Side.Left, 141, CardLocation.HandLeft, 1);
        var enemy = state.CreateWithId(nfs, Side.Right, 142, CardLocation.HandRight, 0);
        engine.Api.JsonSetBool(resolving, "isActive", true);
        engine.Api.JsonSetBool(other, "isActive", true);
        engine.Api.JsonSetBool(enemy, "isActive", true);
        var ctx = new EffectContext { Engine = engine, State = state, Self = resolving, Controller = Side.Left };

        engine.Api.InvokeByName("DeactivateOtherNFS", null, Array.Empty<object?>(), ctx, out _);

        if (!engine.Api.JsonGetBool(resolving, "isActive"))
        {
            return "正在结算的 NFS 不应被 DeactivateOtherNFS 关闭";
        }

        if (engine.Api.JsonGetBool(other, "isActive"))
        {
            return "同阵营手牌中的其他 NFS 未被关闭";
        }

        if (!engine.Api.JsonGetBool(enemy, "isActive"))
        {
            return "不应关闭对手手牌中的 NFS";
        }

        return null;
    }

    private static string? MakeVeteranTargetArg(CardDatabase db)
    {
        // ① 目标在 a[0]、接收者是 cardFunction（= ctx.Self）—— 这是 IR 的真实形状。
        //    取证：`card_unit_266th_guards_rifles` i=1026（OnOtherCardPlayedFromHand）
        //    的 `MakeVeteran(K2Node_Event_cardPlayed)`。
        string? unitName = FindType(db, "infantry");
        if (unitName is null)
        {
            return "卡库里没有 infantry（无法测）";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        var caster = state.CreateWithId(unitName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var target = state.CreateWithId(unitName, Side.Right, 21, CardLocation.BoardHqRight, 2);
        var ctx = new EffectContext { Engine = engine, State = state, Self = caster, Controller = Side.Left };

        // 前置条件：两张探针**本来都不是老兵** —— 否则断言恒真，等于什么都没测。
        if (target.Keywords.Contains(Keyword.Veteran) || caster.Keywords.Contains(Keyword.Veteran))
        {
            return $"探针 {unitName} 本来就带 Veteran 关键字，选错了样本（断言会恒真）";
        }

        engine.Api.InvokeByName("MakeVeteran", caster, new object?[] { target, null }, ctx, out _);

        if (!target.Keywords.Contains(Keyword.Veteran))
        {
            return $"a[0] 那张卡（{target.Name}）没变成老兵 —— 旧实现只认接收者 ⇒ 目标从没被命中过";
        }

        if (caster.Keywords.Contains(Keyword.Veteran))
        {
            return "施法者自己被变成老兵了（旧行为）—— 目标位的实参必须**优先于**接收者";
        }

        // ② 对照：42/45 个调用点的 a[0] 是 `{"self":true}`（= ctx.Self），
        //    解析结果必须**逐位不变** —— 否则会把本来正确的那 42 处改坏。
        var caster2 = state.CreateWithId(unitName, Side.Left, 22, CardLocation.BoardHqLeft, 3);
        var ctx2 = new EffectContext { Engine = engine, State = state, Self = caster2, Controller = Side.Left };
        engine.Api.InvokeByName("MakeVeteran", caster2, new object?[] { caster2, null }, ctx2, out _);
        if (!caster2.Keywords.Contains(Keyword.Veteran))
        {
            return "`{\"self\":true}` 形状（42/45 个调用点）必须仍然作用在接收者上 —— 改坏了";
        }

        return null;
    }

    /// <summary>
    /// `IsSameSideUnit` 的**形状**：`Context{卡}.IsSameSideUnit(side)`。
    ///
    /// 旧实现按 `(卡, 卡)` 读两个实参，而 IR 里 `a[0]` 是 int 阵营、`a[1]` 是 out 槽
    /// ⇒ 19/19 个调用点恒 false。取证与影响面见 `CardApiDispatch` 里该键的注释。
    /// </summary>
    private static string? SameSideUnitShape(CardDatabase db)
    {
        string? unitName = FindType(db, "infantry");
        if (unitName is null)
        {
            return "卡库里没有 infantry（无法测）";
        }

        // 非单位探针：位置卡（`type = "location"` ⇒ `IsUnit` 假）。
        // 用它守「reason = friendly_unit 那半句」—— 只判阵营的实现会在这里放行。
        const string locationCard = "card_location_berlin";
        if (db.Find(locationCard) is null)
        {
            return $"卡库里缺 {locationCard}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        var mine = state.CreateWithId(unitName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var theirs = state.CreateWithId(unitName, Side.Right, 21, CardLocation.BoardHqRight, 2);
        var hq = state.Hq(Side.Left);
        var ctx = new EffectContext { Engine = engine, State = state, Self = mine, Controller = Side.Left };

        bool Call(object? receiver, params object?[] args)
            => (bool)(engine.Api.InvokeByName("IsSameSideUnit", receiver, args, ctx, out _) ?? false);

        // IR 形状：a[0] = side(int)，a[1] = out 槽。
        if (!Call(mine, (int)Side.Left, null))
        {
            return $"己方单位对 side={Side.Left} 应为 true —— 旧实现把 a[0]（int 阵营）当卡读 ⇒ 恒 false";
        }

        if (Call(mine, (int)Side.Right, null))
        {
            return "己方单位对 side=Right 应为 false（阵营必须真的被读进去）";
        }

        if (Call(theirs, (int)Side.Left, null))
        {
            return "敌方单位对 side=Left 应为 false —— 接收者（被查的那张卡）必须被读进去，不能只看 side";
        }

        if (!Call(theirs, (int)Side.Right, null))
        {
            return "敌方单位对 side=Right 应为 true（对照，防止上面两条恒假）";
        }

        if (Call(hq, (int)Side.Left, null))
        {
            return "HQ 是位置卡不是单位 —— 即使阵营相同也应为 false"
                 + "（`card_event_air_corps_ferrying` 失败时的 reason 是 `friendly_unit`，两件事都要判）";
        }

        if (Call(mine, 0, null) || Call(mine, 99, null))
        {
            return "side 读不到（0 / 99）时必须返回 false —— 绝不能退回 receiver.Owner"
                 + "（那会变成「卡属于它自己的阵营」⇒ 判据恒真，把 friendly_unit 门全部放行）";
        }

        return null;
    }

    private static string? GiveKreditsAmountArg(CardDatabase db)
    {
        const string card = "card_unit_arado_ar_196";
        if (db.Find(card) is null)
        {
            return $"卡库里缺 {card}";
        }

        // 蓝图实参形状：a[0]=side  a[1]=数额  a[2]=instigatorID  a[3]=out 槽
        foreach (var (sideValue, amount) in new[] { (1, 10), (2, 10), (1, 3), (2, -2) })
        {
            var (engine, state) = EmptyBoard(db);
            var probe = state.CreateWithId(card, Side.Left, 20, CardLocation.BoardHqLeft, 1);
            var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };
            var side = (Side)sideValue;
            state.SetKredits(side, 5);
            engine.Api.InvokeByName("GiveKreditsBySide", null,
                new object?[] { sideValue, amount, probe.CardId, null }, ctx, out _);
            int after = state.Kredits(side);
            if (after != Math.Max(0, 5 + amount))
            {
                return $"GiveKreditsBySide(side={sideValue}, {amount}) 应把 {sideValue} 方的费从 5 变成 {Math.Max(0, 5 + amount)}，实际 {after}"
                     + "（旧实现把 a[0]=side 当数额 ⇒ 只加 1 或 2，且随左右方变化）";
            }
        }

        return null;
    }

    private static string? BoardQueryOptionalArgs(CardDatabase db)
    {
        const string card = "card_unit_arado_ar_196";
        const string covertCard = "card_unit_174th_independent";
        if (db.Find(card) is null)
        {
            return $"卡库里缺 {card}";
        }
        if (db.Find(covertCard) is null)
        {
            return $"卡库里缺 {covertCard}";
        }

        var (engine, state) = EmptyBoard(db);
        var unit = state.CreateWithId(card, Side.Right, 60, CardLocation.BoardHqRight, 1);
        var covert = state.CreateWithId(covertCard, Side.Right, 61, CardLocation.BoardHqRight, 2);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var hq = state.Hq(Side.Right);

        List<CardInstance> Call(string fn, params object?[] args)
            => engine.Api.InvokeByName(fn, null, args, ctx, out _) as List<CardInstance> ?? new List<CardInstance>();

        // GetCardsOnBoardBySide(side, unitsOnly, includeCovertCards, out cards)
        var unitsOnly = Call("GetCardsOnBoardBySide", 2, true, false, null);
        if (unitsOnly.Count != 1 || !unitsOnly.Contains(unit))
        {
            return $"GetCardsOnBoardBySide(unitsOnly=true) 应只返回那 1 张单位，实际 {unitsOnly.Count} 张"
                 + $"（{string.Join("/", unitsOnly.Select(x => x.Name))}）";
        }
        var unitsWithCovert = Call("GetCardsOnBoardBySide", 2, true, true, null);
        if (!unitsWithCovert.Contains(unit) || !unitsWithCovert.Contains(covert))
        {
            return "GetCardsOnBoardBySide(includeCovertCards=true) 必须包含未揭示 Covert 单位";
        }

        var allCards = Call("GetCardsOnBoardBySide", 2, false, false, null);
        if (!allCards.Contains(hq) || allCards.Contains(covert))
        {
            return "GetCardsOnBoardBySide(unitsOnly=false) 必须包含该方 HQ —— 证据：card_unit_3rd_maizuru_snlf 的"
                 + " OnPlayedFromHand（IR steps 1-6）对随机取出的那张卡做 IsUnit 再决定钉不钉，"
                 + "说明结果集**可能含非单位**；棋盘上唯一的非单位就是 HQ。"
                 + $"实际返回 {allCards.Count} 张：{string.Join("/", allCards.Select(x => x.Name))}。"
                 + "默认查询还必须隐藏未揭示 Covert。";
        }

        // GetAllCardsOnBoard(includeCovertCards, out cards)：两个函数名只差 "units"
        var all = Call("GetAllCardsOnBoard", false, null);
        if (!all.Contains(hq) || !all.Contains(unit) || all.Contains(covert))
        {
            return "GetAllCardsOnBoard 应包含双方 HQ（它和 GetAllUnitsOnBoard 的唯一区别就是「卡」与「单位」），"
                 + $"实际 {all.Count} 张：{string.Join("/", all.Select(x => x.Name))}";
        }

        var units = Call("GetAllUnitsOnBoard", false, null);
        if (units.Contains(hq) || !units.Contains(unit) || units.Contains(covert))
        {
            return "GetAllUnitsOnBoard 默认应隐藏未揭示 Covert、不含 HQ，且必须含普通单位";
        }
        var unitsIncludingCovert = Call("GetAllUnitsOnBoard", true, null);
        if (!unitsIncludingCovert.Contains(unit) || !unitsIncludingCovert.Contains(covert))
        {
            return "GetAllUnitsOnBoard(includeCovertCards=true) 必须包含未揭示 Covert 单位";
        }

        var cardsIncludingCovert = Call("GetAllCardsOnBoard", true, null);
        if (!cardsIncludingCovert.Contains(covert))
        {
            return "GetAllCardsOnBoard(includeCovertCards=true) 必须包含未揭示 Covert 卡";
        }

        engine.Api.RevealCard(covert, unit.CardId);
        if (CardApi.IsUnrevealedCovertCard(covert)
            || !Call("GetCardsOnBoardBySide", 2, true, false, null).Contains(covert)
            || !Call("GetAllUnitsOnBoard", false, null).Contains(covert)
            || !Call("GetAllCardsOnBoard", false, null).Contains(covert))
        {
            return "RevealCard 后默认查询应包含该卡，且它不再被判为未揭示 Covert";
        }

        unit.Defense = 0;
        if (Call("GetCardsOnBoardBySide", 2, true, true, null).Contains(unit)
            || Call("GetAllUnitsOnBoard", true, null).Contains(unit))
        {
            return "防御归零的单位不应出现在 GetCardsOnBoardBySide / GetAllUnitsOnBoard 结果中";
        }

        return null;
    }

    private static string? SpawnFrontlineArg(CardDatabase db)
    {
        const string card = "card_unit_arado_ar_196";
        if (db.Find(card) is null)
        {
            return $"卡库里缺 {card}";
        }

        // SpawnCardOnBattlefield(side, Frontline, card_name, spawnerID, campaignName,
        //                        NewGiveBlitz, locationNumber, salvageFaction, NewMakeVeteran,
        //                        forceGoldCard, out spawnedCardID)
        static object?[] Args(int side, bool frontline, string name)
            => new object?[] { side, frontline, name, 0, "", false, -1, 0, false, false, null };

        {
            var (engine, state) = EmptyBoard(db);
            var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
            var spawned = engine.Api.InvokeByName("SpawnCardOnBattlefield", null,
                Args(1, false, card), ctx, out _) as CardInstance;
            if (spawned is null)
            {
                return "Frontline=false 时没有生成出卡";
            }

            if (spawned.Location != CardLocation.BoardHqLeft)
            {
                return $"Frontline=false 应生成到**半场** BoardHqLeft，实际 {spawned.Location}"
                     + "（旧实现写死 BoardFrontline，215/234 个调用点全部落错地方）";
            }
        }

        {
            var (engine, state) = EmptyBoard(db);
            var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
            var spawned = engine.Api.InvokeByName("SpawnCardOnBattlefield", null,
                Args(1, true, card), ctx, out _) as CardInstance;
            if (spawned is null)
            {
                return "Frontline=true 时没有生成出卡";
            }

            if (spawned.Location != CardLocation.BoardFrontline)
            {
                return $"Frontline=true 应生成到前线，实际 {spawned.Location}";
            }
        }

        // NewGiveBlitz（a[5]）也必须被读
        {
            var (engine, state) = EmptyBoard(db);
            var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
            var args = Args(1, false, card);
            args[5] = true;
            var spawned = engine.Api.InvokeByName("SpawnCardOnBattlefield", null, args, ctx, out _) as CardInstance;
            if (spawned is null || !spawned.Keywords.Contains(Keyword.Blitz))
            {
                return "NewGiveBlitz=true 时生成的卡应自带 Blitz";
            }
        }

        return null;
    }

    // ==================================================================
    //  P0 第 4 族：三条「实现了但语义错」的自测
    // ==================================================================

    private const string GuardUnit = "card_unit_cromwell_mk_iv";   // CDO hasGuard = True
    private const string PlainUnit = "card_unit_arado_ar_196";     // 无 Guard
    private const string BomberUnit = "card_unit_blenheim_mk_iv";  // type = bomber
    private const string ArtilleryUnit = "card_unit_3_7_inch_mountain_howitzer";  // type = artillery

    /// <summary>
    /// 布景：左方攻击者在前线（射程不受限），右方在**半场**摆位。
    /// 半场格位 0 = HQ，1..4 = 单位。
    /// </summary>
    private static (MatchEngine Engine, GameState State, CardInstance Attacker) GuardBoard(
        CardDatabase db, string attackerName, params (string Name, int Slot)[] defenders)
    {
        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);

        var attacker = state.CreateWithId(attackerName, Side.Left, 20, CardLocation.BoardFrontline, 0);
        attacker.EnteredPlayOnTurn = -1;

        int id = 60;
        foreach (var (name, slot) in defenders)
        {
            var d = state.CreateWithId(name, Side.Right, id++, CardLocation.BoardHqRight, slot);
            d.EnteredPlayOnTurn = -1;
            d.Defense = 30;      // 别被一击打死，方便连续断言
        }

        return (engine, state, attacker);
    }

    /// <summary>
    /// 掩护（Guard）的语义 —— 逐字来自 `BP_CardFunctions::UpdateGuarded`（62 条语句）：
    /// 「**邻卡（locationNumber±1）有 Guard ⇒ 这张卡被掩护**」，而不是"Guard 是嘲讽"。
    /// 三条推论（旧实现三条全反）：
    ///   · 掩护卡**自己可以**被打（它自己的 isBeingGuarded 恒 false，si=686/718）
    ///   · **孤立单位可以**被打（没有 Guard 邻卡）
    ///   · **HQ 只在被邻卡掩护时**不可打（HQ 也在这条线上，占第 0 格）
    /// </summary>
    private static string? GuardIsNeighbourCover(CardDatabase db)
    {
        foreach (string c in new[] { GuardUnit, PlainUnit })
        {
            if (db.Find(c) is null)
            {
                return $"卡库里缺 {c}";
            }
        }

        // ① 邻卡有 Guard ⇒ 那张普通单位不可打
        {
            var (engine, _, attacker) = GuardBoard(db, PlainUnit,
                (PlainUnit, 1), (GuardUnit, 2));
            var guarded = engine.State.Cards(Side.Right, CardLocation.BoardHqRight)
                .First(c => c.Name == PlainUnit && c.LocationNumber == 1);
            var guardCard = engine.State.Cards(Side.Right, CardLocation.BoardHqRight)
                .First(c => c.Name == GuardUnit);

            if (!engine.IsBeingGuarded(guarded))
            {
                return "半场槽位 1 的普通单位，邻格 2 有 Guard ⇒ 应判「被掩护」";
            }

            if (engine.Attack(attacker, guarded))
            {
                return "被掩护的单位不该能被攻击（蓝图 CanAttack si=2627-2854，failReason=is_being_guarded）";
            }

            // 掩护卡自己：可打
            if (engine.IsBeingGuarded(guardCard))
            {
                return "掩护卡**自己**不该被判成被掩护（UpdateGuarded si=686/718）";
            }

            if (!engine.Attack(attacker, guardCard))
            {
                return "掩护卡自己**应该**可以被打（覆盖它的邻卡是普通单位，不是 Guard）";
            }

            // 目标集（bot 决策用的就是它）：含掩护卡、不含被掩护的卡
            var targets = engine.LegalTargets(attacker).ToList();
            if (!targets.Contains(guardCard))
            {
                return "LegalTargets 应包含掩护卡自己（它不是被掩护的卡）"
                     + $"\n       实际：{string.Join("/", targets.Select(t => t.Name))}";
            }

            if (targets.Contains(guarded))
            {
                return "LegalTargets 不该包含被掩护的卡（旧实现把 Guard 做成嘲讽，方向恰好相反）"
                     + $"\n       实际：{string.Join("/", targets.Select(t => t.Name))}";
            }
        }

        // ② 孤立单位（邻格没有 Guard）⇒ 可打
        {
            var (engine, _, attacker) = GuardBoard(db, PlainUnit,
                (PlainUnit, 4), (GuardUnit, 1));
            var lonely = engine.State.Cards(Side.Right, CardLocation.BoardHqRight)
                .First(c => c.Name == PlainUnit && c.LocationNumber == 4);

            if (engine.IsBeingGuarded(lonely))
            {
                return "槽位 4 的孤立单位（邻格 3/5 都空）不该判被掩护";
            }

            if (!engine.Attack(attacker, lonely))
            {
                return "孤立单位**应该**可以被打";
            }

            // ★ 关键差异：场上**另有**一张 Guard（槽位 1）时，
            //    孤立单位仍必须留在目标集里。旧实现（把 Guard 当嘲讽）会只返回掩护卡，
            //    于是"孤立单位可打"这条规则在目标集上失效 —— 这条断言就是为它写的。
            var targets = engine.LegalTargets(attacker).ToList();
            if (!targets.Contains(lonely))
            {
                return "场上有 Guard 时，**孤立的**（邻格无 Guard）单位仍应在 LegalTargets 里"
                     + "（旧实现把 Guard 做成嘲讽：只要有 Guard 就只返回 Guard）"
                     + $"\n       实际：{string.Join("/", targets.Select(t => t.Name))}";
            }
        }

        // ③ HQ：被邻卡（槽位 1）掩护 ⇒ 不可打
        {
            var (engine, state, attacker) = GuardBoard(db, PlainUnit, (GuardUnit, 1));
            var hq = state.Hq(Side.Right);
            if (!engine.IsBeingGuarded(hq))
            {
                return "HQ（槽位 0）的邻格 1 有 Guard ⇒ HQ 应判「被掩护」";
            }

            if (engine.Attack(attacker, hq))
            {
                return "被掩护的 HQ 不该能被攻击（failReason=hq_is_being_garded，si=2714/2725）";
            }
        }

        // ④ HQ：邻卡没有 Guard ⇒ 可打
        {
            var (engine, state, attacker) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
            var hq = state.Hq(Side.Right);
            if (engine.IsBeingGuarded(hq))
            {
                return "邻格是普通单位时，HQ 不该判被掩护";
            }

            if (!engine.Attack(attacker, hq))
            {
                return "没被掩护的 HQ **应该**可以被打";
            }

            if (!engine.LegalTargets(attacker).Contains(hq))
            {
                return "没被掩护的 HQ 应在 LegalTargets 里（旧实现只要有 Guard 就把 HQ 从目标集里删掉）";
            }
        }

        return null;
    }

    /// <summary>
    /// 轰炸机 / 炮兵跳过掩护判定 —— `CanAttack`：
    /// `si=2492 IsBomber` / `si=2533 IsArtillery` → `si=2574 or` →
    /// `si=2612 JumpIfNot(or) -> si=2627`（不是轰炸/火炮才落下去查掩护）→
    /// `si=2626 PopExecutionFlow`（是的话直接跳过整段）。
    /// </summary>
    private static string? GuardSkippedByBomberArtillery(CardDatabase db)
    {
        foreach (string c in new[] { BomberUnit, ArtilleryUnit, PlainUnit, GuardUnit })
        {
            if (db.Find(c) is null)
            {
                return $"卡库里缺 {c}";
            }
        }

        foreach (string attackerName in new[] { BomberUnit, ArtilleryUnit })
        {
            var (engine, state, attacker) = GuardBoard(db, attackerName,
                (PlainUnit, 1), (GuardUnit, 2));
            var guarded = state.Cards(Side.Right, CardLocation.BoardHqRight)
                .First(c => c.Name == PlainUnit && c.LocationNumber == 1);

            if (!engine.IsBeingGuarded(guarded))
            {
                return $"{attackerName} 这一局的布景不对：目标应判被掩护";
            }

            if (!engine.Attack(attacker, guarded))
            {
                return $"{attackerName}（type={attacker.Definition.Type}）应跳过掩护判定、可以打被掩护的单位";
            }
        }

        // 反面：普通陆军仍然被挡住（证明上一条不是"掩护判定整体失效"）
        {
            var (engine, state, attacker) = GuardBoard(db, PlainUnit,
                (PlainUnit, 1), (GuardUnit, 2));
            var guarded = state.Cards(Side.Right, CardLocation.BoardHqRight)
                .First(c => c.Name == PlainUnit && c.LocationNumber == 1);
            if (engine.Attack(attacker, guarded))
            {
                return "普通陆军（非轰炸/火炮）不该能打被掩护的单位";
            }
        }

        return null;
    }

    /// <summary>
    /// **被「抑制」（`Suppress`）的单位仍然能攻击、能移动。**
    ///
    /// ## 中文译名（关键，别再混了）
    ///
    /// 中文客户端把两个不同的关键字分别译作：
    /// <list type="bullet">
    /// <item>`Pin` → **压制**：不能移动或攻击；于单位所有者下回合结束时移除。
    ///   ⇒ 门在 `MatchEngine.PinnedBlocksOperation`（**保留**）。</item>
    /// <item>`Suppress` → **抑制**：使被抑制的单位**失去所有特效和关键字**
    ///   （老兵变回普通形态、所有增益失效）。**不含"不能行动"**。</item>
    /// </list>
    /// 玩家（雪雾）权威定义原话：「抑制：使被抑制的单位失去所有特效和关键字；
    /// **压制不会受到抑制的影响**；被抑制的单位会失去所有特效；老兵也会变回原来的；
    /// 所有的增益效果也全部失效 —— 包括友方贴膜、敌方贴膜、友方卡牌给单位添加的额外特效。」
    ///
    /// ## 蓝图正面证据（不是"没搜到"）
    ///
    /// <list type="bullet">
    /// <item>`cardsCheckFunctions::CanAttack`（210 条语句）里 `isSuppressed` **0 次**
    ///   （该文件全文仅两处：`cardsCheckFunctions.g.cs:1150` `CanSelectAsTarget` 的
    ///   `isSuppressed || canIt`，以及 `:2487` `getActiveEffects` 的特效图标）。</item>
    /// <item>`BP_Logic::CanCardDoAnything` 全文 `Suppress` 子串 **0 次** ——
    ///   客户端枚举可行动单位用的就是它（`_index.g.cs:1843`）。</item>
    /// <item>全库 60 处 `isSuppressed` **没有一处是"拒绝行动"**：全是事件广播分流
    ///   （`FireTrigger` si=325/710、`ExecuteOnCardLocationMoved` `:15475` …）
    ///   与数值改动分流（`ChangeDefense` `:7854`、`ExecuteBeforeReceiveDamage` `:13886` …）。</item>
    /// </list>
    ///
    /// ## 这条用例的历史（教训）
    ///
    /// 2026-10-02 第一轮它被**反过来**成 `SuppressedCannotAct`，理由是"权威规则表
    /// 159-160 说被压制的单位不能移动或攻击" —— 但**那两行讲的是「压制」（`Pin`）**，
    /// 是一次关键字误读。当时还写下"判定也许在调用方"，本轮**查了调用方**：
    /// 客户端生成候选走的 `CanCardDoAnything`/`CanAttack` 都不看 `isSuppressed`。
    ///
    /// ## 判死方式（"修复前必须红"）
    ///
    /// ①段（能攻击）/②段（能移动）/③段（两个谓词）在**修复前全部为红**
    /// —— 那时 `MatchEngine.Attack`/`MoveUnit`/`CanOperateThisTurn`/`CanMoveThisTurn`
    /// 里各有一道 `Keywords.Contains(Suppressed)` 的门。④⑤是阴性对照。
    /// </summary>
    private static string? SuppressedCanStillAct(CardDatabase db)
    {
        if (db.Find(PlainUnit) is null)
        {
            return $"卡库里缺 {PlainUnit}";
        }

        // ---- ① 仍然能攻击（修复前：被那道门拒掉 ⇒ 红）----
        var (engine, state, attacker) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        attacker.Keywords.Add(Keyword.Suppressed);
        var defender = state.Cards(Side.Right, CardLocation.BoardHqRight).First(c => !c.IsHq);

        if (!engine.Attack(attacker, defender))
        {
            return "被**抑制**的单位**不能攻击** —— 「不能行动」属于**压制**（Pin），" +
                   "不是抑制（Suppress）。蓝图 `CanAttack` / `CanCardDoAnything` 里 " +
                   "`isSuppressed` 出现 0 次；玩家原话里也没有这一条";
        }

        // ---- ② 仍然能移动（修复前：被那道门拒掉 ⇒ 红）----
        //
        // ⚠️ 用**另一张**单位来测移动：① 里刚攻击过的那张已经 `HasAttackedThisTurn`，
        //    非坦克"移动或攻击二选一"，会**因为另一条规则**被拒 —— 那是假红。
        var (mEngine, mState, mover) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        mover.Keywords.Add(Keyword.Suppressed);
        if (!mEngine.MoveUnit(mover, 1))
        {
            return "被**抑制**的单位**不能移动** —— 同上，那是「压制」（Pin）的规则" +
                   "（`BP_Logic::CanCardDoAnything` 全文没有 `isSuppressed`）";
        }

        // ---- ③ 两个谓词也要一致（枚举候选走的是它们，只删 Attack 的门会让 AI 枚举不出动作）----
        // ⚠️ 用**第三张**单位：① 那张已经攻击过、② 那张已经移动过，
        //    非坦克"移动或攻击二选一"，拿它们查谓词会**因为别的规则**返回 false（假红）。
        var (pEngine, _, probe) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        probe.Keywords.Add(Keyword.Suppressed);
        if (!probe.CanOperateThisTurn(pEngine.State))
        {
            return "`CanOperateThisTurn` 对**抑制**单位返回 false —— " +
                   "AI 枚举候选走的就是它，抑制单位会被误判成不能行动";
        }

        if (!probe.CanMoveThisTurn(pEngine.State))
        {
            return "`CanMoveThisTurn` 对**抑制**单位返回 false —— 同上";
        }

        // ---- ④ 阴性对照：没被抑制的同类单位当然能攻击（避免"门坏了所以什么都放"）----
        var (engine2, state2, ok) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        var defender2 = state2.Cards(Side.Right, CardLocation.BoardHqRight).First(c => !c.IsHq);
        if (!engine2.Attack(ok, defender2))
        {
            return "**未被抑制**的同类单位也不能攻击 —— 门写坏了（阴性对照失败）";
        }

        // ---- ⑤ 阳性对照：`Pinned`（压制）**仍然**禁止攻击与移动（玩家：压制不受抑制影响）----
        var (engine3, state3, pinned) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        pinned.Keywords.Add(Keyword.Pinned);
        var defender3 = state3.Cards(Side.Right, CardLocation.BoardHqRight).First(c => !c.IsHq);
        if (engine3.Attack(pinned, defender3))
        {
            return "被**钉住**（压制）的单位能攻击 —— 压制那道门被误删了" +
                   "（`CanAttack` 里的 `IsPinned` 判据 / 规则表 159）";
        }

        var (engine4, state4, pinned2) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        pinned2.Keywords.Add(Keyword.Pinned);
        if (engine4.MoveUnit(pinned2, 1))
        {
            return "被**钉住**（压制）的单位能移动 —— 压制那道门被误删了" +
                   "（`CanCardDoAnything` i=198 的 `IsPinned` / 规则表 159）";
        }

        return null;
    }

    /// <summary>
    /// **抑制【永不解除】** —— 被抑制的单位一直白板到游戏结束。
    ///
    /// ⚠️⚠️ 本用例在 2026-10-02（第三轮）**反转**了旧用例（旧名 `SuppressionExpires`，
    /// 旧断言是"抑制于单位所有者下回合结束时解除"）。那条到期机制是**内核自己发明的**，
    /// 两方独立证据都指向"永不解除"：
    /// <list type="number">
    /// <item>玩家（雪雾）权威定义：「**抑制**：使被抑制的单位失去所有特效和关键字
    ///   （**压制不受影响**、老兵变回原形、所有增益失效）。解除时机：**【永不解除】**
    ///   —— 一直白板到游戏结束。」</item>
    /// <item>蓝图：`isSuppressed` 全库**只有一处写点，而且写的是 `True`**
    ///   （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:35781`）；
    ///   `SetMember(..., "isSuppressed", …)` 全库**没有任何一处写 `False`**。
    ///   `klink bot/docs/KARDS基础规则参考.md` 只给**压制**写了移除时机（`:160`
    ///   「压制效果于单位所有者下个回合结束时移除」），抑制那一节（`:123-125`）**没有时机**。</item>
    /// </list>
    ///
    /// 两个方向都断言，防止"把抑制与压制的到期一起改坏"：
    /// <list type="bullet">
    /// <item>① 抑制：双方各结束 3 次回合（6 次 `EndTurn`）之后，`Keywords.Contains(Suppressed)`
    ///   **必须仍为 true**，被摘掉的关键字**必须仍不在**（仍是白板），
    ///   `SuppressedOnTurn` **必须仍记录着当初的回合号**（`RestoreAfterSuppression` 会把它复位成 -1
    ///   ⇒ 它一旦变了就说明还有一条"到期还原"在被调用）。</item>
    /// <item>② 反向断言：压制（`Pinned`）在同样的回合推进之后**必须**已经到期解除
    ///   —— 依据是规则表 `:160` 与 `_deps/BP_Logic.g.cs:2218` `DecrementPinnedTurnsEndTurn`，
    ///   这条到期**保留**。</item>
    /// </list>
    /// </summary>
    private static string? SuppressionNeverExpires(CardDatabase db)
    {
        string? name = FindType(db, "infantry") ?? FindType(db, "tank");
        if (name is null) return "卡库里没有可用单位";

        var made = MakeBoard(db, name);
        if (made is null) return $"造不出单位 {name}";
        var (engine, unit) = made.Value;

        Side owner = unit.Owner;

        // 布景：装一个**会被抑制摘掉**的关键字，这样"白板"这件事才是可观测的。
        unit.Keywords.Add(Keyword.Guard);

        int suppressedOnTurn = engine.State.Turn;
        engine.Api.SuppressUnit(unit);

        if (!unit.IsSuppressed) return "`SuppressUnit` 没有加上抑制关键字";
        if (unit.Keywords.Contains(Keyword.Guard))
        {
            return "抑制没有摘掉 `Guard` —— 布景失败（本用例要靠「摘掉的关键字不会回来」判白板）";
        }

        // ---- ① 双方各结束 3 次回合：跨过任何可能被误当成"到期"的时机 ----
        for (int i = 0; i < 3; i++)
        {
            engine.EndTurn(owner);
            if (!unit.IsSuppressed)
            {
                return $"抑制在**所有者**第 {i + 1} 次结束回合时被解除了 —— " +
                       "但抑制按玩家确认与蓝图是【永不解除】：" +
                       "`isSuppressed` 全库只有 `BP_CardFunctions.g.cs:35781` 一处写点（写 True），" +
                       "无任何写 False 处；规则表也只给压制（:160）写了移除时机";
            }

            engine.EndTurn(owner.Opposite());
            if (!unit.IsSuppressed)
            {
                return $"抑制在**对手**第 {i + 1} 次结束回合时被解除了 —— 同上，抑制永不解除";
            }
        }

        if (unit.Keywords.Contains(Keyword.Guard))
        {
            return "过了 6 次回合结束，被抑制的单位**又把关键字拿回来了** —— " +
                   "抑制是永久的，`CardApi.RestoreAfterSuppression` 不该被任何到期路径调用";
        }

        if (unit.SuppressedOnTurn != suppressedOnTurn)
        {
            return $"过了 6 次回合结束，`SuppressedOnTurn` 从 {suppressedOnTurn} 变成了 " +
                   $"{unit.SuppressedOnTurn} —— `RestoreAfterSuppression` 会把它复位成 -1，" +
                   "值变了就说明仍有一条'到期还原'在被调用";
        }

        // ---- ② 反向断言：压制（Pinned）**仍然**会到期（规则表 :160 / BP_Logic.g.cs:2218）----
        var pinnedMade = MakeBoard(db, PlainUnit);
        if (pinnedMade is null) return $"卡库里缺 {PlainUnit}";
        var (pinEngine, pinned) = pinnedMade.Value;

        pinEngine.State.ActiveSide = pinned.Owner.Opposite();   // 敌方回合钉我 ⇒ 初值 2
        pinEngine.Api.PinUnit(pinned);
        if (!pinned.Keywords.Contains(Keyword.Pinned)) return "`PinUnit` 没有加上钉住关键字";

        // 顺带覆盖：同一张卡**先被钉住、再被抑制** ⇒ 压制照样按期解除
        //（玩家原话「压制不会受到抑制的影响」，`SuppressStrips` 里也没有 `Pinned`）。
        pinEngine.Api.SuppressUnit(pinned);

        pinEngine.EndTurn(pinned.Owner.Opposite());   // 2 → 1
        pinEngine.EndTurn(pinned.Owner);              // 1 → 解除

        if (pinned.Keywords.Contains(Keyword.Pinned))
        {
            return "**压制（Pinned）没有到期解除** —— 规则表 :160「压制效果于单位所有者下个回合结束时移除」" +
                   "与 `_deps/BP_Logic.g.cs:2218` `DecrementPinnedTurnsEndTurn` 都要求它解除；" +
                   "它与被抑制单位的「永不解除」是**两件事**，不要一起改";
        }

        if (!pinned.Keywords.Contains(Keyword.Suppressed))
        {
            return "压制到期时把 `Suppressed`（抑制）也一起清了 —— 抑制与压制是两个独立关键字：" +
                   "抑制永不解除，压制按规则表到期（玩家：压制不受抑制的影响）";
        }

        return null;
    }

    /// <summary>
    /// **`SuppressMultipleUnits` 必须在派发表里**（2026-10-02，玩家报「抑制不生效」）。
    ///
    /// 背景：这是「抑制」的**真正实现体**，`SuppressUnit` 只是把单张卡包成数组转发进来
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:36484`）。
    /// 内核原先**只注册了 `SuppressUnit`**，而 IR 里有 **3 个调用点直接调
    /// `SuppressMultipleUnits`**（`card_event_white_death`「Suppress **all** enemy units」
    /// i=382、`card_unit_38th_independent` i=590/i=2808）
    /// ⇒ 那两张卡的抑制**完全没发生**，而且不报错（静默空转）。
    ///
    /// 判死方式：`handled` 必须为 true（键存在），且**数组里每一张都被抑制**。
    /// 键缺失时 `InvokeByName` 回 `handled=false`、两张都不会被抑制 ⇒ 必然红。
    /// </summary>
    private static string? SuppressMultipleUnitsDispatch(CardDatabase db)
    {
        string? name = FindType(db, "infantry") ?? FindType(db, "tank");
        if (name is null) return "卡库里没有可用单位";

        var made = MakeBoard(db, name);
        if (made is null) return $"造不出单位 {name}";
        var (engine, first) = made.Value;

        // 数组里的第二张（敌方），以及一张**不在数组里**的阴性对照。
        var second = engine.State.Create(name, Side.Right, CardLocation.BoardHqRight, 1);
        second.EnteredPlayOnTurn = -99;
        var bystander = engine.State.Create(name, Side.Right, CardLocation.BoardHqRight, 2);
        bystander.EnteredPlayOnTurn = -99;

        var ctx = new EffectContext
        {
            Engine = engine,
            State = engine.State,
            Self = first,
            Controller = Side.Left,
        };

        engine.Api.InvokeByName("SuppressMultipleUnits", null,
            new object?[] { new List<CardInstance> { first, second }, 0 }, ctx, out bool handled);

        if (!handled)
        {
            return "派发表里**没有** `SuppressMultipleUnits` 这个键 ⇒ " +
                   "`card_event_white_death` / `card_unit_38th_independent` 的抑制静默空转";
        }

        if (!first.IsSuppressed || !second.IsSuppressed)
        {
            return $"`SuppressMultipleUnits` 没有把数组里**每一张**都抑制掉" +
                   $"（第一张={first.IsSuppressed} 第二张={second.IsSuppressed}）";
        }

        if (bystander.IsSuppressed)
        {
            return "`SuppressMultipleUnits` 抑制了**不在数组里**的卡 —— 目标集写错了";
        }

        return null;
    }

    /// <summary>
    /// **抑制 = 失去所有关键词 + 所有增益失效 + 老兵变回普通形态**，且**永不自动还原**
    /// （2026-10-02 第二轮：玩家报「抑制不生效」；第三轮：玩家确认「解除时机：【永不解除】」
    /// ⇒ 内核自加的"到期还原"已删，还原路径只保留为手工直调能力，见段⑤/⑥）。
    ///
    /// 玩家（雪雾）权威定义：「抑制：使被抑制的单位失去所有特效和关键字；
    /// **压制不会受到抑制的影响**；被抑制的单位会失去所有特效；**老兵也会变回原来的**；
    /// **所有的增益效果也全部失效** —— 包括友方贴膜、敌方贴膜、
    /// 友方卡牌给单位添加的额外特效。」
    ///
    /// 出处：`BP_CardFunctions::SuppressMultipleUnits`（`:35659-36468`），逐段见
    /// `CardApi.SuppressUnit` 的注释。本用例覆盖的三段：
    /// <code>
    /// L_054E..L_077B RemoveGuard/Fury/Blitz/Immune/Alpine/Ambush/Mobilize/Smokescreen
    ///                /ChangeHeavyArmor(0)/**RemoveSalvage**/RemoveShock
    /// L_07B8         hasDestruction = false
    /// L_0925         customJson 只保留 suppressionException（customName1/2 也在里面）
    /// L_119E/136A/1441/1504  攻/防/行动费回落成 GetStaticCard 的卡面值
    /// L_12B3         JSON_Clear(card,"veteran")                    ; ★ 老兵变回原形
    /// L_1533/156C    ChangeBuffsFromCards(buffType=1/0, changeType=3 Suppress) ; 攻防 buff 清空
    /// </code>
    ///
    /// ⚠️ `Salvage`（收缴**能力**）**在**摘除集里：`L_073F` → `RemoveSalvage`
    /// （`:35922`），而 `RemoveSalvage` 的实现体第一条就是 `card.hasSalvage = False`
    /// （`:32127`）。规则表 `KARDS基础规则参考.md:125` 那句「抑制不能使单位失去
    /// 『被收缴』」讲的是 `isSalvaged`（1/1 收缴复制品的**状态**，蓝图整段没碰它），
    /// 不是"有收缴能力" —— 两件事不矛盾，详见 `CardApi.SuppressStrips` 的注释。
    /// `Pinned`（压制）**不在**摘除集里（整段没有 `RemovePin`；玩家也明确说过
    /// 「压制不会受到抑制的影响」）。
    ///
    /// ## 每段"修复前是否失败"（判死方式）
    /// · 段② 关键字：修复前**绿**（第一轮已经实现了摘关键字）；
    /// · 段④ `Salvage` 必须被摘：修复前**红**（旧实现把它排除在摘除集之外）；
    /// · 段③ 增益失效（攻/防/重甲/行动费回落 + 老兵 + customJson + CustomAbility）：
    ///   修复前**红**（旧实现只摘关键字）；
    /// · 段⑤ 抑制**永不自动还原**（跑 4 次回合结束，关键字/增益都不许回来）：
    ///   修复前**红**（旧实现在所有者下回合结束时自动还原）；
    /// · 段⑥ 还原路径**手工直调**仍能完整还原：修复前**绿**（守卫它的实现没动）。
    /// · 段⑦ `cantBeSuppressed` 守卫：修复前**红**（旧实现没查，会误抑制）。
    /// </summary>
    private static string? SuppressStripsEverythingAndRestores(CardDatabase db)
    {
        string? name = FindType(db, "infantry") ?? FindType(db, "tank");
        if (name is null) return "卡库里没有可用单位";

        var made = MakeBoard(db, name);
        if (made is null) return $"造不出单位 {name}";
        var (engine, unit) = made.Value;

        // ---- 布景：把数值对齐卡面，再加两份"贴膜"增益 ----
        unit.Attack = unit.Definition.Attack;
        unit.Defense = unit.Definition.Defense;
        unit.MaxDefense = unit.Definition.Defense;

        var giverA = engine.State.CreateWithId(name, Side.Left, 993, CardLocation.HandLeft, 0);
        var giverB = engine.State.CreateWithId(name, Side.Left, 994, CardLocation.HandLeft, 0);

        engine.Api.ChangeAttack(unit, 2, giverA);       // 攻 +2（记进 BuffsBySource）
        engine.Api.ChangeDefense(unit, 2, giverA);      // 防 +2
        unit.BuffsBySource[(giverA.CardId, false)].HeavyArmor = 1;
        unit.BuffsBySource[(giverA.CardId, false)].OperationCost = 1;
        engine.Api.ChangeAttack(unit, 1, giverB);       // 第二条来源：攻 +1
        unit.RecalculateStats();                       // 重甲 1 / 行动费 +1

        // 纯改费条目 —— 蓝图整段**没有** `ChangeKreditCost` ⇒ 抑制**不该**动它。
        unit.BuffsBySource[(7777, false)] = new CardBuff { SourceCardId = 7777, KreditCost = -1 };
        unit.RecalculateStats();

        int baseAttack = unit.Attack;                  // 卡面攻 + 3
        int baseDefense = unit.Defense;                // 卡面防 + 2
        int baseKredit = unit.KreditCost;              // 卡面费 - 1
        int baseHeavy = unit.HeavyArmor;               // 1
        int baseOp = unit.OperationCost;               // 卡面行动费 + 1

        if (baseAttack <= unit.Definition.Attack || baseDefense <= unit.Definition.Defense)
        {
            return "布景失败：增益没落到攻/防上（本用例的前提）";
        }

        // 蓝图 Remove* 链对应的关键词，逐条装上（`Salvage` 也在里面，见上方注释）。
        string[] stripped =
        {
            Keyword.Guard, Keyword.Fury, Keyword.Blitz, Keyword.Immune, Keyword.Alpine,
            Keyword.Ambush, Keyword.Mobilize, Keyword.Smokescreen, Keyword.HeavyArmor,
            Keyword.Shock, Keyword.Destruction, Keyword.Salvage,
        };

        foreach (string k in stripped)
        {
            unit.Keywords.Add(k);
        }

        // 不在摘除集里的两个：`Pinned`（压制 —— 玩家明说抑制不影响压制）、`Veteran`（★ 该被摘）。
        unit.Keywords.Add(Keyword.Pinned);
        unit.Keywords.Add(Keyword.Veteran);

        // 卡自己的状态：`suppressionException` 必须活下来，其余键必须被洗掉。
        unit.CustomJson["suppressionException"] = "1";
        unit.CustomJson["pincer_receiver"] = "42";
        unit.CustomAbility = "someCardGrantedAbility";

        engine.Api.SuppressUnit(unit);

        if (!unit.IsSuppressed)
        {
            return "`SuppressUnit` 没有加上抑制关键字（L_02D2）";
        }

        // ---- ② 关键词 ----
        foreach (string k in stripped)
        {
            if (unit.Keywords.Contains(k))
            {
                return $"抑制之后**仍然带着** `{k}` —— 「失去所有关键字」没实现" +
                       "（蓝图 Remove* 链：Guard/Fury/Blitz/Immune/Alpine/Ambush/" +
                       "Mobilize/Smokescreen/HeavyArmor/Shock/Destruction/Salvage）";
            }
        }

        if (!unit.Keywords.Contains(Keyword.Pinned))
        {
            return "抑制**摘掉了 `Pinned`（压制）** —— 蓝图整段没有 RemovePin，" +
                   "玩家原话「压制不会受到抑制的影响」";
        }

        // ---- ③ 老兵变回原形（L_12B3 `JSON_Clear(card,"veteran")`）----
        if (unit.Keywords.Contains(Keyword.Veteran))
        {
            return "抑制之后**仍然是老兵** —— 「老兵也会变回原来的」没实现" +
                   "（蓝图 L_12B3 `JSON_Clear(_card, \"veteran\")`）";
        }

        // ---- ④ 所有增益失效（攻/防回落 + 重甲清零 + 行动费回落）----
        if (unit.Attack != unit.Definition.Attack)
        {
            return $"抑制之后攻击是 {unit.Attack}，应为卡面值 {unit.Definition.Attack} " +
                   "（蓝图 L_136A `ChangeAttack(卡, …, _staticAttack, Suppress(3))`）" +
                   "—— 「所有的增益效果也全部失效」没实现";
        }

        if (unit.Defense != unit.Definition.Defense || unit.MaxDefense != unit.Definition.Defense)
        {
            return $"抑制之后防御 {unit.Defense}/{unit.MaxDefense}，应为卡面值 " +
                   $"{unit.Definition.Defense}/{unit.Definition.Defense}" +
                   "（蓝图 L_1441 `maxDefense = _staticDefense`；只在更高时压下来）";
        }

        if (unit.HeavyArmor != 0 || unit.Keywords.Contains(Keyword.HeavyArmor))
        {
            return $"抑制之后还有重甲 {unit.HeavyArmor} —— 蓝图 L_070E " +
                   "`ChangeHeavyArmor(卡, 0, 0, changeType=3 Suppress, skipAction=true)`";
        }

        if (unit.OperationCost != unit.Definition.OperationCost)
        {
            return $"抑制之后行动费是 {unit.OperationCost}，应为卡面值 " +
                   $"{unit.Definition.OperationCost}（蓝图 L_1504 `ChangeOperationCost(…, 3)`）";
        }

        // ⚠️ 防御**只降不升**：低了不许补（否则抑制变成治疗）。这里用一个"被打残"的
        //    副本验证 —— 走真实路径（重新造一个对局，把防御压到卡面值以下）。
        {
            var hurtMade = MakeBoard(db, name);
            if (hurtMade is null) return $"造不出单位 {name}（第二局）";
            var (hurtEngine, hurt) = hurtMade.Value;
            hurt.Defense = Math.Max(1, hurt.Definition.Defense - 1);
            hurtEngine.Api.SuppressUnit(hurt);
            if (hurt.Defense != Math.Max(1, hurt.Definition.Defense - 1))
            {
                return $"抑制把防御从 {Math.Max(1, hurt.Definition.Defense - 1)} " +
                       $"**抬**到了 {hurt.Defense} —— 蓝图的 `ChangeDefense` 只在 " +
                       "`getTotalDefense > _staticDefense` 时才执行，抑制**不能治疗**";
            }
        }

        // ---- ⑦ customJson / CustomAbility ----
        if (unit.CustomJson.GetValueOrDefault("suppressionException") != "1")
        {
            return "抑制把 `suppressionException` 也洗掉了 —— 蓝图 L_0925 明确**只保留**它" +
                   "（卡靠它把自己保存的状态读回来）";
        }

        if (unit.CustomJson.ContainsKey("pincer_receiver") || unit.CustomJson.Count != 1)
        {
            return "抑制之后 `customJson` 里还有别的键 —— 蓝图是 `customJson = JsonMake()` " +
                   "（等于全洗，只写回 suppressionException）";
        }

        if (unit.CustomAbility is not null)
        {
            return "抑制之后 `CustomAbility` 还在 —— 「友方卡牌给单位添加的额外特效」应失效" +
                   "（蓝图 L_0B93 那一遍 receivedAbilitiesFromCards 清洗）";
        }

        // 纯改费**不该**被抑制动（蓝图整段没有 ChangeKreditCost）
        if (unit.KreditCost != baseKredit)
        {
            return $"抑制改了卡费（{baseKredit} → {unit.KreditCost}）—— 蓝图那段" +
                   "只回落攻/防/行动费，**没有** ChangeKreditCost";
        }

        // ---- ⑤ 抑制**永不**自动还原（玩家：【永不解除】；蓝图无写 False 处）
        //         还原能力本身保留 —— 只能**手工直调** `RestoreAfterSuppression`（段⑥）----
        //
        // ⚠️ 2026-10-02（第三轮）：这里原先走的是"真正的到期路径"（owner → opposite → owner
        //    触发 `MatchEngine.ClearExpiredSuppression`）。那条到期是**内核自己发明的**，已删
        //    ⇒ 现在必须断言"跑完这些回合，抑制**还在**、摘掉的东西**没回来**"。
        Side owner = unit.Owner;
        engine.EndTurn(owner);
        engine.EndTurn(owner.Opposite());
        engine.EndTurn(owner);
        engine.EndTurn(owner.Opposite());

        if (!unit.IsSuppressed)
        {
            return "抑制被**自动解除**了 —— 按玩家确认（【永不解除】）与蓝图（`isSuppressed` 全库" +
                   "只有 `BP_CardFunctions.g.cs:35781` 一处写点、写的是 True，无任何写 False 处）" +
                   "它必须一直挂到游戏结束；内核那条'到期解除'正是要删的东西";
        }

        foreach (string k in stripped)
        {
            if (unit.Keywords.Contains(k))
            {
                return $"抑制期间 `{k}` **自己回来了** —— 抑制是永久的，摘掉的关键字不该被自动装回";
            }
        }

        if (unit.Attack != unit.Definition.Attack)
        {
            return $"抑制期间攻击自己回到了 {unit.Attack}（卡面值 {unit.Definition.Attack}）—— " +
                   "「所有的增益效果也全部失效」是永久的，增益不该被自动装回";
        }

        // ---- ⑥ 还原路径本身仍然可用：**直接调用**（产品代码里已无调用方）----
        // 保留它的理由：它记录了"抑制按蓝图摘掉了什么"的对照面，也是未来真需要还原时的能力；
        // 删掉"到期"不等于删掉能力（`CardApi.RestoreAfterSuppression` 本体保留）。
        engine.Api.RestoreAfterSuppression(unit);

        if (unit.IsSuppressed)
        {
            return "手工调用 `RestoreAfterSuppression` 之后**仍被抑制** —— 还原实现本身坏了";
        }

        foreach (string k in stripped)
        {
            if (!unit.Keywords.Contains(k))
            {
                return $"`RestoreAfterSuppression` 没有还原 `{k}` —— 还原实现本身坏了" +
                       "（蓝图用 `customJson.suppressionException` 备份/还原，" +
                       "内核用 `CardInstance.SuppressStrippedKeywords`）";
            }
        }

        if (unit.Attack != baseAttack || unit.Defense != baseDefense)
        {
            return $"抑制解除后攻/防是 {unit.Attack}/{unit.Defense}，应为 {baseAttack}/{baseDefense} " +
                   "—— 贴膜的增益**没有装回来**（`SuppressStrippedBuffs` 那条路径）";
        }

        if (unit.HeavyArmor != baseHeavy || unit.OperationCost != baseOp)
        {
            return $"抑制解除后重甲/行动费是 {unit.HeavyArmor}/{unit.OperationCost}，" +
                   $"应为 {baseHeavy}/{baseOp} —— 增益没装回来";
        }

        if (unit.KreditCost != baseKredit)
        {
            return $"抑制解除后卡费变成了 {unit.KreditCost}（应 {baseKredit}）—— 装回时串了槽位";
        }

        if (unit.SuppressStrippedKeywords is { Count: > 0 } || unit.SuppressStrippedBuffs is { Count: > 0 })
        {
            return "还原之后 `SuppressStripped*` 没清空 —— 下次解除会重复发放";
        }

        // ---- ⑦ `cantBeSuppressed` 守卫（蓝图 L_01E9/L_026A；IR 里出现 9 次）----
        {
            var guardMade = MakeBoard(db, name);
            if (guardMade is null) return $"造不出单位 {name}（第三局）";
            var (guardEngine, immune) = guardMade.Value;
            immune.CustomAbility = "cantBeSuppressed";
            guardEngine.Api.SuppressUnit(immune);
            if (immune.IsSuppressed)
            {
                return "带 `cantBeSuppressed` 的单位被抑制了 —— 蓝图 L_01E9 会先查这个自定义能力，" +
                       "命中就连 `isSuppressed` 都不写（「Cannot Retreat or be Suppressed.」那三张卡）";
            }
        }

        return null;
    }

    // ==================== 行动限制（2026-10-02） ====================
    //
    // 针对服务器实测暴露的真 bug：
    //   「AI 的步兵部署后立刻移动并攻击」+「AI 会移动死亡的单位」。
    //
    // 规则出处 `KARDS基础规则参考.md` 兵种表：
    //   「**坦克**：能在同一回合移动并攻击（一次移动 + 一次攻击，顺序任意）」
    //   表里**只有坦克**有这条例外 ⇒ 其余兵种「移动**或**攻击，二选一」。

    /// <summary>造一个可控的对局：指定类型的单位直接放在前线，行动方是它自己那一方。</summary>
    private static (MatchEngine Engine, CardInstance Unit)? MakeBoard(CardDatabase db,
                                                                    string unitName, int attack = 2, int defense = 3)
    {
        if (db.Find(unitName) is null) return null;

        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 7);
        engine.Start();

        var unit = engine.State.Create(unitName, Side.Left, CardLocation.BoardFrontline, 0);
        unit.Attack = attack;
        unit.Defense = defense;
        unit.MaxDefense = defense;
        unit.EnteredPlayOnTurn = -99;      // 早就进场，排除召唤失调干扰
        engine.State.ActiveSide = Side.Left;
        engine.State.SetKredits(Side.Left, 20);

        return (engine, unit);
    }

    /// <summary>找一个指定 type 的真实卡名。</summary>
    private static string? FindType(CardDatabase db, string type)
        => db.All.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase))?.Name;

    private static string? NonTankCannotMoveThenAttack(CardDatabase db)
    {
        string? name = FindType(db, "infantry");
        if (name is null) return "卡库里没有 infantry（无法测）";

        var made = MakeBoard(db, name);
        if (made is null) return $"造不出单位 {name}";
        var (engine, unit) = made.Value;

        if (unit.IsTank) return $"`{name}` 被判成坦克，选错了样本";

        unit.HasMovedThisTurn = true;      // 模拟「本回合已经移动过」

        if (unit.CanOperateThisTurn(engine.State))
        {
            return $"非坦克 {name} 移动后**仍然可以攻击** —— 规则表里只有坦克能移动+攻击";
        }

        if (engine.Attack(unit, engine.State.Hq(Side.Right)))
        {
            return $"非坦克 {name} 移动后被允许执行 Attack —— 内核没有拦住";
        }

        return null;
    }

    private static string? TankCanMoveThenAttack(CardDatabase db)
    {
        string? name = FindType(db, "tank");
        if (name is null) return "卡库里没有 tank（无法测）";

        var made = MakeBoard(db, name);
        if (made is null) return $"造不出单位 {name}";
        var (engine, unit) = made.Value;

        if (!unit.IsTank) return $"`{name}` 的 type 不是 tank，`IsTank` 判据有问题";

        unit.HasMovedThisTurn = true;

        if (!unit.CanOperateThisTurn(engine.State))
        {
            return $"坦克 {name} 移动后**不能攻击** —— 规则表明确写了坦克可以（顺序任意）";
        }

        return null;
    }

    private static string? NonTankCannotAttackThenMove(CardDatabase db)
    {
        string? name = FindType(db, "infantry");
        if (name is null) return "卡库里没有 infantry";

        var made = MakeBoard(db, name);
        if (made is null) return $"造不出单位 {name}";
        var (engine, unit) = made.Value;

        if (unit.IsTank) return $"`{name}` 被判成坦克，选错了样本";

        unit.HasAttackedThisTurn = true;

        if (unit.CanMoveThisTurn(engine.State))
        {
            return $"非坦克 {name} 攻击后**仍然可以移动** —— 与「移动后不能攻击」不对称";
        }

        string? tankName = FindType(db, "tank");
        if (tankName is not null && MakeBoard(db, tankName) is { } made2)
        {
            var (e2, t2) = made2;
            t2.HasAttackedThisTurn = true;
            if (!t2.CanMoveThisTurn(e2.State))
            {
                return $"坦克 {tankName} 攻击后**不能移动** —— 规则表说顺序任意";
            }
        }

        return null;
    }

    private static string? AlpineMoveAndAttackAbility(CardDatabase db)
    {
        string? name = FindType(db, "infantry");
        if (name is null) return "卡库里没有 infantry";

        var made = MakeBoard(db, name);
        if (made is null) return $"造不出单位 {name}";
        var (engine, unit) = made.Value;
        if (unit.IsTank) return $"`{name}` 被判成坦克，选错了非坦克样本";

        var ctx = new EffectContext
        {
            Engine = engine, State = engine.State, Self = unit, Controller = Side.Left,
        };

        engine.Api.InvokeByName("CustomName1Add", unit,
            new object?[] { "CanMoveAndAttackInTheSameTurn" }, ctx, out bool handled);
        if (!handled) return "派发表没有处理 CustomName1Add";
        if (!unit.CanMoveAndAttackInSameTurn)
            return "CustomName1Add 没有写入 CanMoveAndAttackInTheSameTurn";

        unit.HasMovedThisTurn = true;
        if (!unit.CanOperateThisTurn(engine.State))
            return "获得 Alpine 能力后，移动过的非坦克仍不能攻击";

        unit.HasMovedThisTurn = false;
        unit.HasAttackedThisTurn = true;
        if (!unit.CanMoveThisTurn(engine.State))
            return "获得 Alpine 能力后，攻击过的非坦克仍不能移动";

        engine.Api.InvokeByName("CustomName1Remove", unit,
            new object?[] { "CanMoveAndAttackInTheSameTurn" }, ctx, out _);
        if (unit.CanMoveAndAttackInSameTurn)
            return "CustomName1Remove 没有移除 Alpine 能力标记";

        unit.HasAttackedThisTurn = false;
        unit.HasMovedThisTurn = true;
        if (unit.CanOperateThisTurn(engine.State))
            return "撤销 Alpine 能力后，移动过的非坦克仍可攻击";

        return null;
    }

    /// <summary>
    /// **死亡单位不能移动、也不能攻击。**
    ///
    /// ⚠️ 写法很关键：不能只把 `Defense` 设成 0 就断言 ——
    /// `Defense = 0` 只是"**待**销毁"，真正的死亡发生在 `CheckDeaths()` 里：
    /// <code>
    /// !card.IsHq &amp;&amp; card.IsAlive &amp;&amp; card.Location.IsBoard() &amp;&amp; card.Defense &lt;= 0
    /// </code>
    /// 所以这里走**真实路径**：把防御设成 1，再用攻击力足够的单位打死它。
    ///
    /// ⚠️ 而且**不能用 `IsAlive` 判死活** —— 那个属性只判**位置**，
    /// `Defense` 掉到 0 的卡在 `Destroy` 真正搬走它之前仍是 `IsAlive = true`。
    /// </summary>
    private static string? DeadUnitCannotMoveOrAttack(CardDatabase db)
    {
        string? name = FindType(db, "infantry") ?? FindType(db, "tank");
        if (name is null) return "卡库里没有可用单位";

        var made = MakeBoard(db, name, attack: 1, defense: 1);
        if (made is null) return $"造不出单位 {name}";
        var (engine, victim) = made.Value;

        string? killerName = FindType(db, "tank") ?? name;
        var killer = engine.State.Create(killerName, Side.Left, CardLocation.BoardFrontline, 1);
        killer.Attack = 5;
        killer.Defense = 5;
        killer.MaxDefense = 5;
        killer.EnteredPlayOnTurn = -99;

        if (!engine.Attack(killer, victim))
        {
            return $"攻击没能执行（{killerName} → {name}）—— 本测试前提不成立";
        }

        if (victim.Location.IsBoard() && victim.Defense > 0)
        {
            return $"挨了 5 点伤害的 1 防单位还活着（Defense={victim.Defense}）—— 伤害/死亡链路有问题";
        }

        if (victim.AliveOnBoard)
        {
            return $"死亡单位仍被判为「在场上且活着」（Location={victim.Location} " +
                   $"Defense={victim.Defense}）—— `AliveOnBoard` 判据有问题";
        }

        if (engine.MoveUnit(victim, 1))
        {
            return $"**死亡单位被允许移动**（{name}，Location={victim.Location}）";
        }

        if (victim.CanOperateThisTurn(engine.State))
        {
            return $"**死亡单位被允许行动**（{name}，Location={victim.Location}）";
        }

        if (engine.Attack(victim, engine.State.Hq(Side.Right)))
        {
            return $"**死亡单位被允许攻击**（{name}）";
        }

        return null;
    }

    // ==================== 钉住（Pinned）不能移动（2026-10-02） ====================
    //
    // 玩家实测 + 日志实证的真 bug：
    //   对局 637706（`rel/data/fyserver/bot-log/bot-20261002.log`）
    //     t12 `:140-144` 五条攻击全被拒「攻击者被钉住」
    //     t12 `:159`     CHOSEN 移动 #67 @BoardFrontline#1 → 槽位 0   ← 同一张卡却动了
    //     t14 `:175-179` 五条攻击被拒「攻击者被钉住」
    //     t14 `:194`     CHOSEN 移动 #58 @BoardFrontline#0 → 槽位 0   ← 又动了
    //   ⇒ `Attack` 有钉住门、`MoveUnit` 没有。
    //
    // 出处（移动侧）：`BP_Logic::CanCardDoAnything` i=198 —— 详见
    // `MatchEngine.PinnedBlocksOperation` 的注释。

    /// <summary>
    /// **被钉住的单位不能移动**（`BP_Logic::CanCardDoAnything` i=198）。
    ///
    /// ⚠️ 这条用例**修复前必须是红的**（`MoveUnit` 会返回 true）。若它是绿的，
    /// 说明门加错了地方（不是 `MoveUnit` 这道）。
    /// </summary>
    private static string? PinnedCannotMove(CardDatabase db)
    {
        var made = MakeBoard(db, PlainUnit);
        if (made is null) return $"卡库里缺 {PlainUnit}";
        var (engine, unit) = made.Value;

        unit.Keywords.Add(Keyword.Pinned);

        if (engine.MoveUnit(unit, 1, out string why))
        {
            return "被钉住的单位**不该**能移动（CanCardDoAnything i=198：钉住的单位不能操作）";
        }

        // 原因必须是「钉住」本身 —— 否则是别的门抢先拒了，这条用例就变成假绿。
        if (!why.Contains("钉住"))
        {
            return $"移动确实被拒了，但原因是「{why}」而不是钉住 —— 门被别的判据抢先，用例无意义";
        }

        // 阴性对照：去掉钉住就该能移动（避免"门坏了所以什么都拒"）。
        unit.Keywords.Remove(Keyword.Pinned);
        if (!engine.MoveUnit(unit, 1, out string why2))
        {
            return $"**未被钉住**的同类单位也不能移动（{why2}）—— 门写坏了（阴性对照失败）";
        }

        return null;
    }

    /// <summary>
    /// **被钉住的单位不能攻击** —— 对照组：这条**修复前就应该是绿的**
    /// （`Attack` 的钉住门早就有了，出处 `_deps/cardsCheckFunctions.g.cs:368`）。
    /// 它与 `PinnedCannotMove` 成对，用来证明"门本身是对的、只是移动侧漏了"。
    /// </summary>
    private static string? PinnedCannotAttack(CardDatabase db)
    {
        var (engine, state, pinned) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        var defender = state.Cards(Side.Right, CardLocation.BoardHqRight).First(c => !c.IsHq);
        pinned.Keywords.Add(Keyword.Pinned);

        if (engine.Attack(pinned, defender))
        {
            return "被钉住的单位**不该**能攻击（CanAttack 的 IsPinned 门在 cardsCheckFunctions.g.cs:368）";
        }

        // 阴性对照
        var (engine2, state2, ok) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        var defender2 = state2.Cards(Side.Right, CardLocation.BoardHqRight).First(c => !c.IsHq);
        if (!engine2.Attack(ok, defender2))
        {
            return "**未被钉住**的同类单位也不能攻击 —— 门写坏了（阴性对照失败）";
        }

        return null;
    }

    /// <summary>
    /// **钉住会在「单位所有者下个回合结束时」解除。**
    ///
    /// 出处：`BP_CardFunctions::PinUnit` i=955（`BP_CardFunctions.g.cs:27957-27964`）
    /// `pinnedTurns = Max(pinnedTurns, IsSideActive(卡) ? 3 : 2)`，
    /// 加上 `BP_Logic::DecrementPinnedTurnsEndTurn`（`_deps/BP_Logic.g.cs:2218`）
    /// —— **每个回合结束**减 1，减到 1 的那次结束就 `RemovePin`。
    ///
    /// ⚠️ 这条与 `PinnedCannotMove` 是**一对**：光加门不补到期，就是
    ///    "被钉住永久冻住"（压制那条漏的同款，见 `SuppressionExpires`）。
    ///    实证：回放 389594 —— `monty` 在 t7 钉住右方单位（初值 2），
    ///    应于 t8 结束解除，内核记成永久 ⇒ t9/t11/t13/t15 共 6 条 `ML` 被误拒。
    /// </summary>
    private static string? PinnedExpiresAtOwnerNextTurnEnd(CardDatabase db)
    {
        // ---- ① 敌方在**我的**回合钉我 ⇒ 初值 2 ⇒ 我的下个回合结束时解除 ----
        var made = MakeBoard(db, PlainUnit);
        if (made is null) return $"卡库里缺 {PlainUnit}";
        var (engine, unit) = made.Value;

        Side owner = unit.Owner;                        // Left（MakeBoard 的设定）
        engine.State.ActiveSide = owner.Opposite();     // 模拟"敌方回合钉我"
        engine.Api.PinUnit(unit);

        if (!unit.Keywords.Contains(Keyword.Pinned)) return "`PinUnit` 没有加上钉住关键字";

        if (unit.PinnedTurns != 2)
        {
            return $"敌方回合钉住时 `PinnedTurns` 应为 2（蓝图 SelectInt(3,2,IsSideActive)=2），" +
                   $"实际 {unit.PinnedTurns}";
        }

        if (engine.MoveUnit(unit, 1, out _))
        {
            return "刚被钉住就能移动 —— 钉住门没生效";
        }

        // 第 1 次回合结束（敌方结束）⇒ 只递减、**不**解除
        engine.EndTurn(owner.Opposite());
        if (!unit.Keywords.Contains(Keyword.Pinned) || unit.PinnedTurns != 1)
        {
            return $"第 1 次回合结束后应当**仍然被钉住**且 `PinnedTurns`=1，实际 " +
                   $"钉住={unit.Keywords.Contains(Keyword.Pinned)} turns={unit.PinnedTurns}";
        }

        // 第 2 次回合结束（**我的**回合结束）⇒ 解除
        engine.EndTurn(owner);
        if (unit.Keywords.Contains(Keyword.Pinned))
        {
            return "钉住**没有**在「单位所有者下个回合结束时」移除 ⇒ 单位被永久冻住";
        }

        if (unit.PinnedTurns != 0)
        {
            return $"钉住已解除但 `PinnedTurns` 还是 {unit.PinnedTurns}（应复位为 0）";
        }

        // 解除之后必须又能移动（补行动方 + 油费，免得被别的门挡成假阴性）
        engine.State.ActiveSide = owner;
        engine.State.SetKredits(owner, 20);
        if (!engine.MoveUnit(unit, 1, out string why))
        {
            return $"钉住解除后单位**仍然不能移动**（{why}）—— 关键字清掉了但门还堵着";
        }

        // ---- ② 我**自己**的回合被钉 ⇒ 初值 3 ⇒ 我自己下个回合结束时解除 ----
        var made2 = MakeBoard(db, PlainUnit);
        if (made2 is null) return $"造不出单位 {PlainUnit}";
        var (engine2, unit2) = made2.Value;

        engine2.Api.PinUnit(unit2);                     // ActiveSide == Left == unit2.Owner
        if (unit2.PinnedTurns != 3)
        {
            return $"自己回合被钉时 `PinnedTurns` 应为 3，实际 {unit2.PinnedTurns}";
        }

        engine2.EndTurn(unit2.Owner);                    // 3→2
        engine2.EndTurn(unit2.Owner.Opposite());         // 2→1
        if (!unit2.Keywords.Contains(Keyword.Pinned))
        {
            return "自己回合被钉住时**过早解除**了（应到自己下个回合结束）";
        }

        engine2.EndTurn(unit2.Owner);                    // 1→解除
        if (unit2.Keywords.Contains(Keyword.Pinned))
        {
            return "自己回合被钉住时**没有**在自己下个回合结束时解除";
        }

        return null;
    }

    private static string? PinUnitHonorsBlueprintGuards(CardDatabase db)
    {
        var made = MakeBoard(db, PlainUnit);
        if (made is null) return $"卡库里缺 {PlainUnit}";
        var (engine, target) = made.Value;

        target.CustomAbility = "cantBePinned";
        engine.Api.PinUnit(target);
        if (target.Keywords.Contains(Keyword.Pinned) || target.PinnedTurns != 0)
        {
            return "cantBePinned 目标不应获得 Pinned 或 pinnedTurns";
        }

        target.CustomAbility = null;
        var hand = engine.State.CreateWithId(PlainUnit, Side.Right, 901,
            CardLocation.HandRight, 0);
        engine.Api.PinUnit(hand);
        if (hand.Keywords.Contains(Keyword.Pinned) || hand.PinnedTurns != 0)
        {
            return "不在棋盘上的单位不应被 PinUnit 处理";
        }

        var hq = engine.State.Hq(Side.Right);
        engine.Api.PinUnit(hq);
        if (hq.Keywords.Contains(Keyword.Pinned) || hq.PinnedTurns != 0)
        {
            return "HQ（非单位）不应被 PinUnit 处理";
        }

        return null;
    }

    /// <summary>
    /// **带 `canOperateWhilePinned` 的被钉住单位可以移动、也可以攻击。**
    ///
    /// 例外在蓝图里**两侧都有**，而且同名同形：
    /// <code>
    /// 移动侧 BP_Logic.g.cs:757          HasCustomAbility(_card, "canOperateWhilePinned")
    /// 攻击侧 cardsCheckFunctions.g.cs:368 HasCustomAbility(attackerCard, "canOperateWhilePinned")
    /// </code>
    /// 授予者：`card_unit_14_panzergrenadier`（`klink bot/docs/card-ir.json` 里
    /// **唯一**一处 `CustomAbilityAdd("canOperateWhilePinned", …)`；
    /// 见 `ref/…/Germany/OceaniaStorm/units/card_unit_14_panzergrenadier.g.cs:149`）。
    ///
    /// ⚠️ 这条用例**修复前也是红的**：旧 `Attack` 只判 `Keywords.Contains(Pinned)`、
    /// 把例外漏了；`MoveUnit` 则整道门都没有。
    /// </summary>
    private static string? PinnedWithOperateAbilityCanAct(CardDatabase db)
    {
        // ---- ① 移动放行 ----
        var made = MakeBoard(db, PlainUnit);
        if (made is null) return $"卡库里缺 {PlainUnit}";
        var (engine, unit) = made.Value;

        unit.Keywords.Add(Keyword.Pinned);
        engine.Api.CustomAbilityAdd(unit, "canOperateWhilePinned", giver: null);

        if (!engine.Api.HasCustomAbility(unit, "canOperateWhilePinned"))
        {
            return "`CustomAbilityAdd` 之后 `HasCustomAbility` 仍为假 —— 例外判据接不上（测试前提不成立）";
        }

        if (!engine.MoveUnit(unit, 1, out string why))
        {
            return $"带 `canOperateWhilePinned` 的被钉住单位**应该**能移动，却被拒（{why}）" +
                   " —— 例外没实现（蓝图 BP_Logic.g.cs:757）";
        }

        // ---- ② 攻击也放行 ----
        var (engine2, state2, atk) = GuardBoard(db, PlainUnit, (PlainUnit, 1));
        var defender2 = state2.Cards(Side.Right, CardLocation.BoardHqRight).First(c => !c.IsHq);
        atk.Keywords.Add(Keyword.Pinned);
        engine2.Api.CustomAbilityAdd(atk, "canOperateWhilePinned", giver: null);

        if (!engine2.Attack(atk, defender2))
        {
            return "带 `canOperateWhilePinned` 的被钉住单位**应该**能攻击，却被拒" +
                   " —— 例外没实现（蓝图 cardsCheckFunctions.g.cs:368）";
        }

        return null;
    }

    /// <summary>
    /// **`monty` 必须钉住「目标单位**及其相邻单位**」，而且钉的是敌方。**
    ///
    /// ## 背景（雪雾 2026-10-01 实测）
    ///
    /// 他报告「为啥可以移动压制？」并猜到：
    /// 「有一张牌 效果是压制目标单位**及其相邻单位**，然后模拟器**只压制了目标单位**」。
    /// （中文客户端把 **Pin 译成「压制」**，英文卡面是 Pin。）
    ///
    /// 找到的卡：
    /// <code>
    /// card_event_monty   1费   "Pin target unit and adjacent units. Draw a card."
    /// </code>
    ///
    /// ## 静态分析发现的 bug
    ///
    /// `monty` 的 IR 里：
    /// <code>
    /// [23] i=770 GetAdjacentCards
    ///      args=[{"var":"Target Card"}, …]      ← 真正的目标在 args[0]
    ///      recv={"var":"cardFunction"}          ← 接收者恒为**施法的那张牌自己**
    /// </code>
    ///
    /// 而我们的实现是 `AsCard(r) ?? AsCard(a[0]) ?? c.Self` ——
    /// **`AsCard(r)` 先命中**（`cardFunction` = monty 自己，非 null）
    /// ⇒ 相邻判定用的是 **monty 这张指令牌**的位置（它不在场上、`LocationNumber = 0`）
    /// ⇒ 算出来的是**施法方自己槽位 1 的单位**，而不是目标周围。
    ///
    /// 同一个顺序问题也在 `DoGiveKeyword` / `DoRemoveKeyword` 里
    /// （`PinUnit` 有 **69 处**调用点的实参是整数 cardID）。
    ///
    /// ## 这个测试钉什么
    ///
    /// 1. 敌方目标**及其相邻**都被钉住（卡面要求）
    /// 2. **施法方自己的单位一个都不许被钉**（旧 bug 会把效果落到自己人身上）
    /// </summary>
    private static string? PinTargetsCorrectUnits(CardDatabase db)
    {
        const string PinCard = "card_event_monty";
        if (db.Find(PinCard) is null)
        {
            return $"卡库里缺 {PinCard}";
        }

        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 41);
        engine.Start();
        engine.State.ActiveSide = Side.Left;
        engine.State.SetKredits(Side.Left, 40);

        // 敌方（右）三个单位：槽 1 / 2 / 3 —— 目标是槽 2，相邻是槽 1 和 3
        var foe = new CardInstance?[4];
        for (int slot = 1; slot <= 3; slot++)
        {
            var u = engine.State.Create("card_unit_panzer_ii_a", Side.Right,
                                        CardLocation.BoardHqRight, slot);
            u.Attack = 1; u.Defense = 5; u.MaxDefense = 5; u.EnteredPlayOnTurn = -99;
            foe[slot] = u;
        }

        // 我方（左）两个单位：槽 1 / 2 —— 一个都不该被钉
        var own = new CardInstance?[3];
        for (int slot = 1; slot <= 2; slot++)
        {
            var u = engine.State.Create("card_unit_panzer_ii_a", Side.Left,
                                        CardLocation.BoardHqLeft, slot);
            u.Attack = 1; u.Defense = 5; u.MaxDefense = 5; u.EnteredPlayOnTurn = -99;
            own[slot] = u;
        }

        var card = engine.State.Create(PinCard, Side.Left, CardLocation.HandLeft, 0);
        if (!engine.CanPlay(card, out string why))
        {
            return $"打不出 {PinCard}：{why}";
        }

        if (!engine.PlayCard(card, foe[2]))
        {
            return $"{PinCard} 打出失败（目标 = 敌方槽 2 的单位）";
        }

        // ① 目标必须被钉
        if (!foe[2]!.Keywords.Contains(Keyword.Pinned))
        {
            return "**目标单位没有被钉住** —— 卡面第一个效果就失效了";
        }

        // ② 相邻（槽 1 / 槽 3）也必须被钉 —— 这是卡面「and adjacent units」的要求
        var notPinned = new List<string>();
        if (!foe[1]!.Keywords.Contains(Keyword.Pinned)) notPinned.Add("敌方槽 1");
        if (!foe[3]!.Keywords.Contains(Keyword.Pinned)) notPinned.Add("敌方槽 3");
        if (notPinned.Count > 0)
        {
            return $"**相邻单位没有被钉**（{string.Join("、", notPinned)}）—— " +
                   "卡面是「Pin target unit **and adjacent units**」。" +
                   "嫌疑：`GetAdjacentCards` 的实参解析顺序（接收者优先 ⇒ 拿到的是施法牌自己）";
        }

        // ③ 自己的单位一个都不许被钉（旧 bug 会把效果落到自己人身上）
        var wronglyPinned = new List<string>();
        if (own[1]!.Keywords.Contains(Keyword.Pinned)) wronglyPinned.Add("我方槽 1");
        if (own[2]!.Keywords.Contains(Keyword.Pinned)) wronglyPinned.Add("我方槽 2");
        if (wronglyPinned.Count > 0)
        {
            return $"**施法方自己的单位被钉住了**（{string.Join("、", wronglyPinned)}）—— " +
                   "效果落到了自己人身上（`AsCard(r)` 拿到的是施法牌）";
        }

        // ④ 负数对照：没被钉的单位应当仍能行动
        if (!foe[1]!.CanOperateThisTurn(engine.State) && !foe[1]!.Keywords.Contains(Keyword.Pinned))
        {
            return "敌方槽 1 既没被钉、又不能行动 —— 门写坏了";
        }

        return null;
    }


    /// <summary>
    /// **`card_event_desert_dust` 的两个 GARRISON 必须落到「支援线」（半场），不是牌库。**
    ///
    /// 卡面（`cards.live.json`）：「Pin target unit. **Add two GARRISON units to your
    /// support line.**」——真客户端在 214436 里对这两个单位发的是
    /// <code>
    /// #39 t9 L ML {"0":"7003","1":"0","2":"AS"}   ; AS = card_unit_garrison
    /// #40 t9 L ML {"0":"7002","1":"1","2":"AS"}
    /// </code>
    /// 只有它们**已经在支援线上**，这两条移动才可能被接受。
    ///
    /// 效果侧的出处：IR `card_event_desert_dust` 的 i=213 / i=440 两个
    /// `SpawnCardOnBattlefield(side, Frontline=false, "card_unit_garrison", …)`。
    /// `Frontline=false` 就是「半场（支援线）」，见 `DoSpawnOnBattlefield`。
    /// </summary>
    private static string? DesertDustGarrisonToSupportLine(CardDatabase db)
    {
        const string Card = "card_event_desert_dust";
        const string Token = "card_unit_garrison";
        const string Foe = "card_unit_panzer_ii_a";

        if (db.Find(Card) is null || db.Find(Token) is null || db.Find(Foe) is null)
        {
            return $"卡库里缺 {Card} / {Token} / {Foe}";
        }

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);

        // PinUnit 需要一个目标：敌方前线放一个单位（钉不钉得住不影响本用例）
        var target = state.CreateWithId(Foe, Side.Right, 42, CardLocation.BoardFrontline, 0);
        target.EnteredPlayOnTurn = -99;

        var order = state.CreateWithId(Card, Side.Left, 2, CardLocation.HandLeft, 0);
        int deckBefore = state.Deck(Side.Left).Count;

        if (!engine.CanPlay(order, out string why))
        {
            return $"打不出 {Card}：{why}";
        }

        if (!engine.PlayCard(order, target))
        {
            return $"{Card} 打出失败";
        }

        var spawned = state.Cards(Side.Left).Where(c => c.Name == Token).ToList();

        // ① 必须生成**两个**
        if (spawned.Count != 2)
        {
            return $"生成了 {spawned.Count} 个 {Token}（卡面要求 two）"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        // ② 两个都必须在**支援线（半场）**上
        var wrong = spawned.Where(c => c.Location != CardLocation.BoardHqLeft).ToList();
        if (wrong.Count > 0)
        {
            return $"{wrong.Count}/2 个 {Token} 不在支援线上："
                 + string.Join("、", spawned.Select(c => $"#{c.CardId}@{c.Location}"))
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        // ③ 一个都不许留在牌库里（旧症状：生成完还在 DeckLeft）
        if (state.Deck(Side.Left).Count != deckBefore)
        {
            return $"牌库从 {deckBefore} 变成 {state.Deck(Side.Left).Count} —— 生成的单位落进了牌库";
        }

        if (spawned.Any(c => state.Deck(Side.Left).Contains(c)))
        {
            return "生成的 GARRISON 仍在牌库里";
        }

        return null;
    }


    /// <summary>
    /// `card_event_atlantic_convoy`（英国指令，卡面：
    /// 「Add one random US unit with cost 3 or less to **the support line** and
    ///   another **to hand**.」）—— 对局 `508065` 第 36 号动作的根因用例。
    ///
    /// ## 现场证据（`out/_server-replays/replay-508065.actions.json`）
    ///
    /// <code>
    /// #36 t9  L PC {"0":"9001", "4":"04"}            ; 04 = card_event_atlantic_convoy
    /// #44 t11 L ML {"0":"9002", "1":"0", "2":"DB"}   ; DB = card_unit_fifth_ohio
    /// #45 t11 L PC {"0":"9003", "4":"dd"}            ; dd = card_unit_p40_warhawk（**从手牌打出**）
    /// </code>
    ///
    /// 客户端里：`9002` 在**支援线**上（所以第 44 号才敢 `ML` 它），`9003` 在**手牌**里
    /// （第 45 号是从手牌 `PC` 出去的）。我们这边这条 `ML` 被拒
    /// （审计 ⑤b「#44 t11 ML：移动被拒（当前 HandLeft）」）。
    ///
    /// ## 真正的根因（2026-10-01 查明，**不是**"支援线那一张落到了手牌"）
    ///
    /// 本卡在旧内核里**一张都没生成**：它的 `OnPlayedFromHand` 把
    /// 「扫全卡池再过滤」的循环（IR i=15..384，判据 USA + IsUnit + 总费≤3）
    /// **直接写在事件程序里**，需要 2021 张 × 13 步 ≈ 28,000 步；
    /// 而事件程序的步数上限当时写死 `KismetVm.MaxStepsPerProgram = 5000`
    /// ⇒ 在第 5000 步（卡池第 363 张 `card_event_industrial_might`，全是非单位的指令）
    /// 被**静默截断** ⇒ `possibleCards` 恒空 ⇒ `Array_IsNotEmpty` 为假
    /// ⇒ i=549 `SpawnCardOnBattlefield` 与 i=758 `SpawnCardInHandBySide` **两条分支一次都没进**。
    ///
    /// 审计里那句「当前 HandLeft」是**卡号撞车**造成的假象：我们这边 `9002` 其实是
    /// pams 开发出来的 `card_event_forward_observers`（不是 fifth_ohio），
    /// `9003` 是 `ReplayRunner.ResolveCard` 兜底建的占位卡 —— 与
    /// `ClientCardIdAllocator` 里记的 `desert_dust 7002/7003` 是同一类坑。
    ///
    /// ## 断言口径
    ///
    /// 选哪两张是 `GetRandomCard` 随机的，所以**不能断言具体卡名**，只能断言结构：
    /// 新增的卡里**恰好一张**在支援线（`BoardHqLeft`，施法方是左）、**恰好一张**在手牌，
    /// 两张都是「美国 + 总费 ≤ 3」，且**一张都不在牌库里**。
    ///
    /// ## 为什么连打 10 局 + 为什么还要单独探一次原语
    ///
    /// 「费用 ≤ 3」这条判据走的是 `getAndDecryptKredit`。旧派发表**没有这个键**
    /// ⇒ 每次调用返回 null ⇒ `LessEqual(null, 3)` 恒真 ⇒ **费用过滤整个失效**，
    /// 候选池从「US 单位 + 费 ≤ 3」变成「全部 363 张 US 单位」。
    ///
    /// ⚠️ **实测教训**：只靠"连打 10 局、每局 2 张"**抓不住**这条 ——
    /// 种子固定（`EmptyBoard` 用 `seed: 1`），那条序列里 20 次抽样**恰好都 ≤3 费**，
    /// 把 `getAndDecryptKredit` 的派发键删掉这个用例照样绿。
    /// 所以另外用 <see cref="CardApi.InvokeByName"/> **直接探一次这个原语**：
    /// 返回 null / `handled=false` 就是"键不存在"，判据恒真这件事立刻现形。
    /// </summary>
    private static string? AtlanticConvoyBoardAndHand(CardDatabase db)
    {
        const string Card = "card_event_atlantic_convoy";
        if (db.Find(Card) is null)
        {
            return $"卡库里缺 {Card}";
        }

        // ---- 守卫：费用过滤所依赖的原语必须真的存在且有值 ----
        // 形状（全 IR 52 个调用点唯一）：`getAndDecryptKredit(卡)`，主语在接收者，
        // 唯一实参是出参槽 —— 所以这里传 `[null]` 当出参槽。
        {
            var (probeEngine, probeState) = EmptyBoard(db);
            var probeCard = probeState.CreateWithId("card_unit_fifth_ohio", Side.Left, 3,
                                                    CardLocation.DeckLeft, 0);
            var ctx = new EffectContext
            {
                Engine = probeEngine, State = probeState, Controller = Side.Left,
            };

            object? cost = probeEngine.Api.InvokeByName(
                "getAndDecryptKredit", probeCard, new object?[] { null }, ctx, out bool handled);
            if (!handled)
            {
                return "`getAndDecryptKredit` 不在派发表里 ⇒ 每次调用返回 null ⇒ "
                     + "`LessEqual(null, 3)` 恒真 ⇒ 「US 单位 + 费 ≤ 3」的过滤整个失效"
                     + "（候选池会变成全部 363 张 US 单位，随机抽出 6 费卡）";
            }

            if (cost is null || Convert.ToInt32(cost) != probeCard.KreditCost)
            {
                return $"`getAndDecryptKredit` 返回 {cost ?? "null"}，"
                     + $"应为 {probeCard.KreditCost}（卡自己的费用）";
            }
        }

        for (int round = 0; round < 10; round++)
        {
            string? err = AtlanticConvoyOnce(db, round);
            if (err is not null)
            {
                return err;
            }
        }

        return null;
    }

    /// <summary>上面那个用例的一轮（每轮一张全新的空局面 + 一个新种子）。</summary>
    private static string? AtlanticConvoyOnce(CardDatabase db, int round)
    {
        const string Card = "card_event_atlantic_convoy";

        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);

        var order = state.CreateWithId(Card, Side.Left, 2, CardLocation.HandLeft, 0);
        int deckBefore = state.Deck(Side.Left).Count;
        var idsBefore = state.Cards(Side.Left).Select(x => x.CardId).ToHashSet();

        if (!engine.CanPlay(order, out string why))
        {
            return $"第 {round + 1} 轮：打不出 {Card}：{why}";
        }

        int stepLimitsBefore = engine.Api.Vm.StepLimitHits;
        if (!engine.PlayCard(order, null))
        {
            return $"第 {round + 1} 轮：{Card} 打出失败";
        }

        int stepLimits = engine.Api.Vm.StepLimitHits - stepLimitsBefore;

        var spawned = state.Cards(Side.Left).Where(x => !idsBefore.Contains(x.CardId)).ToList();
        string trace = $"\n       （第 {round + 1} 轮）"
                     + Dump(state, ("未实现", Unimpl(state)))
                     + $"\n       新增: {string.Join("  ", spawned.Select(x => $"{x.Name}#{x.CardId}@{x.Location}({x.Attack}/{x.Defense} 费{x.KreditCost})"))}";

        // ① 必须生成**两张**（一张场上 + 一张手牌）
        if (spawned.Count != 2)
        {
            return $"生成了 {spawned.Count} 张（卡面要求 one to the support line + another to hand）"
                 + $"；本次 VM 撞步数上限 {stepLimits} 次"
                 + "（>0 ⇒ 事件程序被截断，两条生成分支根本没进 —— 见本用例的根因注释）" + trace;
        }

        // ② 两张都必须是「美国 + 总费 ≤ 3」—— 卡面限定
        foreach (var c in spawned)
        {
            if (!string.Equals(c.Definition.Faction, "USA", StringComparison.OrdinalIgnoreCase))
            {
                return $"生成的是 {c.Definition.Faction} 阵营（卡面要求 US）：{c.Name}" + trace;
            }

            if (c.KreditCost > 3)
            {
                return $"生成的是 {c.KreditCost} 费（卡面要求 cost 3 or less）：{c.Name}"
                     + "—— 费用过滤失效的症状（`getAndDecryptKredit` 没实现时判据恒真）" + trace;
            }
        }

        // ③ **恰好一张**在支援线（施法方是左 ⇒ BoardHqLeft）
        var onBoard = spawned.Where(x => x.Location == CardLocation.BoardHqLeft).ToList();
        var inHand = spawned.Where(x => x.Location == CardLocation.HandLeft).ToList();
        if (onBoard.Count != 1)
        {
            return $"支援线上应有 **1** 张，实际 {onBoard.Count} 张"
                 + "（旧症状：整个 OnPlayedFromHand 被步数上限截断 ⇒ 两张都没生成）" + trace;
        }

        // ④ **恰好一张**在手牌
        if (inHand.Count != 1)
        {
            return $"手牌里应有 **1** 张，实际 {inHand.Count} 张" + trace;
        }

        // ⑤ 两张都不许留在牌库里
        if (state.Deck(Side.Left).Count != deckBefore)
        {
            return $"牌库从 {deckBefore} 变成 {state.Deck(Side.Left).Count} —— 生成的单位落进了牌库" + trace;
        }

        if (spawned.Any(x => state.Deck(Side.Left).Contains(x)))
        {
            return "生成的卡仍在牌库里" + trace;
        }

        return null;
    }


    /// <summary>
    /// **生成卡的卡号必须与真实客户端一致**，而且**对双方一致**。
    ///
    /// ## 权威依据（蓝图级，2026-10-02 修正）
    ///
    /// `BP_CardFunctions::CreateCard` 调
    /// `GameStateRef.GenerateNextCardID(turnNumber, out nextCardID)`
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:10542`），实现是：
    /// <code>
    /// GenerateNextCardID(turnNumber):
    ///     IncrementCardsCreatedThisTurn()              // cardsCreatedCountThisTurn++（**全局一个**）
    ///     GetCurrentCardID(turnNumber):
    ///         mult = SelectInt(500, 1000, turnNumber == 0)
    ///         id   = cardsCreatedCountThisTurn + turnNumber * mult
    /// </code>
    /// （`ref/kards-sim/KardsSim/Generated/_deps/BP_GameState_Battle.g.cs:1545` / `:1788`）
    ///
    /// ⇒ **分配器没有 side 参数**，规则是 `回合号 × 1000 + 本回合第几张`（序号从 1 起、
    /// 每回合归零，`ResetCardsCreatedThisTurn` 在 `:3188`）。
    ///
    /// ## 旧实现错在哪（这就是「虚空部署」的根因）
    ///
    /// 旧实现只让「官方客户端那一方」（`ClientIdSide`）走这条规则，我们 bot 自己走顺序号
    /// （81/82/83…）。但客户端在本地执行**我们打出的效果**时用的是**它自己的**
    /// `GenerateNextCardID` ⇒ 我们发 `PC {"0": 81}` 客户端**认不出 81**
    /// ⇒ 记牌器 +1、场上什么都没有（真人玩家实测，对局 458321 的
    /// `card_unit_1st_airborne#81/#82`）。
    ///
    /// 旧注释里「右方 KLink 生成的是 81/83/84」是**循环论证** —— 那些号出自我们自己的
    /// 动作流。反向证据：4 局回放里**人类动作引用的所有 &gt;80 的 cardID 全是
    /// `1000×回合+序号` 形状**，没有一条落在 81..99。
    ///
    /// ⚠️ 本用例在旧实现下必然失败（bot 那一方会拿到顺序号），这就是
    /// 「修复前红、修复后绿」的那条。
    /// </summary>
    private static string? ClientCardIdAllocator(CardDatabase db)
    {
        const string Token = "card_unit_garrison";
        if (db.Find(Token) is null)
        {
            return $"卡库里缺 {Token}";
        }

        var (engine, state) = EmptyBoard(db);
        state.Turn = 7;

        // ① 人类（左）连生成 3 张 ⇒ 7001/7002/7003
        var c1 = state.Create(Token, Side.Left, CardLocation.BoardHqLeft, 1);
        var c2 = state.Create(Token, Side.Left, CardLocation.BoardHqLeft, 2);
        var c3 = state.Create(Token, Side.Left, CardLocation.BoardHqLeft, 3);

        if (c1.CardId != 7001 || c2.CardId != 7002 || c3.CardId != 7003)
        {
            return $"t7 左方连生成 3 张，卡号应是 7001/7002/7003，实际 " +
                   $"{c1.CardId}/{c2.CardId}/{c3.CardId}";
        }

        // ② ★ 同一个回合里 **bot（右）** 生成一张 ⇒ 必须接着 7004，
        //    证明「分配器没有 side 参数、计数器是全局的」。
        //    旧实现会给出顺序号（42/81…），这条断言在旧实现下必然红。
        var bot = state.Create(Token, Side.Right, CardLocation.BoardHqRight, 1);
        if (bot.CardId != 7004)
        {
            return $"t7 右方生成的卡必须接着 7004（分配器无 side 参数、计数器全局），" +
                   $"实际 {bot.CardId}（顺序号 = 旧实现的错误行为）";
        }

        // ③ 回合变了 ⇒ 序号归零（不是接着 7005 往下）
        state.Turn = 9;
        var c4 = state.Create(Token, Side.Right, CardLocation.BoardHqRight, 2);
        if (c4.CardId != 9001)
        {
            return $"t9 生成的第一张卡号应是 9001（序号每回合归零），实际 {c4.CardId}";
        }

        // ④ 回放路径：内核自己的回合号会漂开（214436 人类 t7 时内核已是 Turn=10），
        //    所以要用动作流自带的回合号覆盖发号 —— 不覆盖就会发出 10001 而不是 7001。
        //    用**新局**做这一步：上面那局的 7001..7004 已被占，撞号会触发跳号
        //    （那本身是对的，但会让这条断言不干净）。
        var (engine3, state3) = EmptyBoard(db);
        state3.Turn = 10;
        state3.ClientIdTurnOverride = 7;
        var c5 = state3.Create(Token, Side.Left, CardLocation.BoardHqLeft, 1);
        if (c5.CardId != 7001)
        {
            return $"回合号被覆盖成 7 时卡号应是 7001（按覆盖后的回合号发号），实际 {c5.CardId}";
        }

        state3.ClientIdTurnOverride = null;

        // ⑤ 自对弈的开局建牌库走**顺序号**（左 2.. / 右 42..），
        //    不能被效果发号规则污染 —— `MatchEngine.BuildDeck` 传的就是 sequentialId: true。
        var (engine2, state2) = EmptyBoard(db);
        state2.Turn = 1;
        var seqL = state2.Create(Token, Side.Left, CardLocation.DeckLeft, 0, sequentialId: true);
        var seqR = state2.Create(Token, Side.Right, CardLocation.DeckRight, 0, sequentialId: true);
        if (seqL.CardId != 2 || seqR.CardId != 42)
        {
            return $"顺序号分配器应给左 2 / 右 42，实际 {seqL.CardId} / {seqR.CardId}";
        }

        return null;
    }


    /// <summary>
    /// **「从手牌挑一张」必须真的落实** —— `card_unit_gordon_highlanders`：
    /// 「Deployment: Choose an order in hand. Set its cost to 0 and put it on top of your deck.」
    ///
    /// ## 背景（雪雾 2026-10-01，对局 781364）
    ///
    /// <code>
    /// #155 t25 L PC  {"0":"10", …}   card_unit_gordon_highlanders
    /// #156 t25 L HT  {"0":"10","1":"7"}      ← 选了手牌 7
    /// </code>
    ///
    /// 两条路以前**都是断的**：
    /// <list type="bullet">
    /// <item>`selectTargetFromHand` 的实现是 `(c, r, a) => c.Target`（**空壳**，
    ///   既不问玩家也不触发 `OnHandTargetSelected`）；</item>
    /// <item>`ReplayRunner` **没有 `HT` 分支**（进的是"未处理的动作类型"）。</item>
    /// </list>
    /// ⇒ 人类选的手牌**既没被设成 0 费、也没回牌库** ⇒ 状态从 t25 起漂开
    /// （实测 t27 一片动作应用失败）。
    ///
    /// 这个测试钉：选中的指令**费用变 0**、而且**被移到牌库顶**（不在手牌里了）。
    /// </summary>
    private static string? HandTargetSelectWorks(CardDatabase db)
    {
        const string Gordon = "card_unit_gordon_highlanders";
        if (db.Find(Gordon) is null)
        {
            return $"卡库里缺 {Gordon}";
        }

        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 51);
        engine.Start();
        engine.State.ActiveSide = Side.Left;
        engine.State.SetKredits(Side.Left, 40);

        // 手牌里放一张**贵的指令**当候选（`forward_observers` 4 费）
        const string OrderCard = "card_event_forward_observers";
        var order = engine.State.Create(OrderCard, Side.Left, CardLocation.HandLeft, 0);
        int costBefore = order.EffectiveKreditCost;

        // 再放一张便宜的指令，确保"挑费用最高"这条启发式真的在起作用
        var cheap = engine.State.Create("card_event_pams", Side.Left, CardLocation.HandLeft, 1);

        var gordon = engine.State.Create(Gordon, Side.Left, CardLocation.HandLeft, 2);
        if (!engine.CanPlay(gordon, out string why))
        {
            return $"打不出 {Gordon}：{why}";
        }

        engine.ClearLastHandTarget();
        if (!engine.PlayCard(gordon))
        {
            return $"{Gordon} 打出失败";
        }

        // ① 必须留下留痕（`BotTurnService` 靠它发 `HT`）
        if (engine.LastHandTarget is not { } ht)
        {
            return "**没有留下「手牌目标」留痕** ⇒ bot 发不出 `HT` " +
                   "⇒ 客户端不知道选了哪张手牌（`selectTargetFromHand` 可能还是空壳）";
        }

        if (ht.SelectingCardId != gordon.CardId)
        {
            return $"留痕的挑牌卡不对：{ht.SelectingCardId}，应为 {gordon.CardId}";
        }

        // ② 选中的必须是那张**贵的**指令（费用最高）
        if (ht.ChosenCardId != order.CardId)
        {
            return $"选中的手牌不对：#{ht.ChosenCardId}，应为贵的指令 #{order.CardId}（{OrderCard}）";
        }

        // ③ 必须被移到牌库顶（不再在手牌里）—— 这一步是 `OnHandTargetSelected` 干的
        if (order.Location == Side.Left.HandOf())
        {
            return "选中的指令**仍在手牌里** ⇒ `OnHandTargetSelected` 里的 " +
                   "`MoveCardToTopOfOwnersDeck` 没跑到（触发没发出去？）";
        }

        if (order.Location != Side.Left.DeckOf())
        {
            return $"选中的指令跑到了 {order.Location}，应当在牌库里";
        }

        // ④ **费用变 0 发生在它被抽到时**（不是选中的那一刻）。
        //
        //    ⚠️ 这张卡有三个入口，分工不同（`card-ir.json` 的 entrypoints）：
        //      `OnPlayedFromHand`        i=1012 → 只调 `selectTargetFromHand`
        //      `OnHandTargetSelected`    i=1007 → jump 639 → `MoveCardToTopOfOwnersDeck`
        //      `OnOtherCardDrawnFromDeck` i=10  → `GetCardFromID` + **`ChangeKreditCost`**
        //    所以断言"选中即 0 费"是**错的期望**（我第一版就这么写，误报了一次）。
        engine.Api.DrawCards(Side.Left, 1);
        if (order.EffectiveKreditCost != 0)
        {
            return $"抽到之后费用仍不是 0（{costBefore} → {order.EffectiveKreditCost}）" +
                   " ⇒ `OnOtherCardDrawnFromDeck` 里的 `ChangeKreditCost` 没跑到";
        }

        // ⑤ 另一张指令不该被动
        if (cheap.EffectiveKreditCost == 0)
        {
            return "**没被选中的指令也被设成 0 费** —— 效果落到了错的卡上";
        }

        return null;
    }


    /// <summary>
    /// **AoE 伤害必须真的落地** —— `card_event_forward_observers`
    /// （卡面：Deal 2 damage to all enemy units）。
    ///
    /// ## 为什么加这条（雪雾 2026-10-01 实测）
    ///
    /// 他用这张卡**打死了 AI 很多单位**，但 AI 之后**照样移动那些单位**。
    /// 回放审计（`tools/ServerBridgeTest --audit-replay`）显示：
    /// 内核**整局都没登记那次伤害** ⇒ 我们这边它们还活着 ⇒ AI 自然去动它们。
    ///
    /// 铁证：bot 的 `card_unit_5th_parachute_brigade#63` 是 **1/1**，
    /// 在 t7 吃了这张卡的 2 点 AoE 就该死 —— 但它活到最后还攻击了 6 次。
    ///
    /// ## 静态分析的结论（供排查参考）
    ///
    /// `DamageMultipleCards(unitsToDamage, 2, cardID, out destroyed)` 的
    /// 第一个参数是**局部变量 `unitsToDamage`**，而它在整份 IR 里**只出现 2 次**：
    /// 这里，和 `Array_Add(unitsToDamage, cardID)` 那一步。
    /// **没有任何 `set → unitsToDamage`** ⇒ 它完全靠 `Array_Add` 的**就地修改**填充。
    /// 所以只要 `Array_Add` 没真正改到那个变量，伤害就是 0 目标。
    /// </summary>
    private static string? AoeDamageApplies(CardDatabase db)
    {
        const string AoeCard = "card_event_forward_observers";
        if (db.Find(AoeCard) is null)
        {
            return $"卡库里缺 {AoeCard}";
        }

        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 31);
        engine.Start();
        engine.State.ActiveSide = Side.Left;
        engine.State.SetKredits(Side.Left, 40);

        // 敌方（右）场上摆两个单位：一个 1/1（必死）、一个 3/4（该掉到 2）
        var weak = engine.State.Create("card_unit_5th_parachute_brigade", Side.Right,
                                       CardLocation.BoardFrontline, 0);
        weak.Attack = 1; weak.Defense = 1; weak.MaxDefense = 1; weak.EnteredPlayOnTurn = -99;

        var tank = engine.State.Create("card_unit_panzer_ii_a", Side.Right,
                                       CardLocation.BoardFrontline, 1);
        tank.Attack = 1; tank.Defense = 4; tank.MaxDefense = 4; tank.EnteredPlayOnTurn = -99;

        // 自己场上也放一个（卡面是"enemy units"，不该被波及）
        var mine = engine.State.Create("card_unit_panzer_ii_a", Side.Left,
                                       CardLocation.BoardFrontline, 0);
        mine.Attack = 1; mine.Defense = 4; mine.MaxDefense = 4; mine.EnteredPlayOnTurn = -99;

        var card = engine.State.Create(AoeCard, Side.Left, CardLocation.HandLeft, 0);
        if (!engine.CanPlay(card, out string why))
        {
            return $"打不出 {AoeCard}：{why}";
        }

        int weakBefore = weak.Defense, tankBefore = tank.Defense, mineBefore = mine.Defense;
        engine.PlayCard(card);

        string unimpl = engine.State.UnimplementedCalls.Count == 0
            ? "无"
            : string.Join(", ", engine.State.UnimplementedCalls
                .OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}×{kv.Value}"));

        // 1/1 吃 2 点 ⇒ 必死（离场）
        if (weak.AliveOnBoard)
        {
            return $"1/1 的敌方单位挨了 AoE 还活着（{weakBefore}→{weak.Defense}）" +
                   $" ⇒ **AoE 伤害没落地**。撞到的未实现原语：{unimpl}";
        }

        // 3/4 吃 2 点 ⇒ 2
        if (tank.Defense != tankBefore - 2)
        {
            return $"3/4 的敌方单位应当 {tankBefore}→{tankBefore - 2}，实际 →{tank.Defense}" +
                   $" ⇒ 伤害量或目标数不对。撞到的未实现原语：{unimpl}";
        }

        // 自己的单位不该掉血
        if (mine.Defense != mineBefore)
        {
            return $"**自己的单位被误伤**（{mineBefore}→{mine.Defense}）—— 卡面是 enemy units only";
        }

        return null;
    }


    ///
    /// ## 背景（2026-10-02 从真回放查出来的 bug）
    ///
    /// 实测 `out/_server-replays/replay-630801`：
    /// **人类发了 3 条 `CS`，bot 一条都没有。**
    /// `CS`（`XActionCardToDrawSelected`）就是「我选了哪张」的答复，
    /// 格式 `{0:触发选牌的卡, 1:候选下标, 2:选中卡码}`。
    ///
    /// 内核原先确实"选"了（`CardApiDispatch` 退化成候选表第一张），
    /// 但**从不把这个选择说出去** ⇒ 客户端收到一张没有任何选择的开发牌。
    /// 用户报的「AI 不会选开发」就是这个 —— **缺一条协议，不是 AI 强弱**。
    /// </summary>
    /// <summary>
    /// **真打开发牌**，看内核能不能走到「选牌」那一步。
    ///
    /// ## 为什么需要这个测试（2026-10-02 服务器实测）
    ///
    /// 真对局日志（`rel/data/fyserver/bot-log/`）里 bot 打了
    /// `card_event_baker_street_irregulars`（卡面：Develop a British special force unit），
    /// 但**没有任何 `CS`** —— 而部署的 DLL 里明明有 `CS` 的代码。
    ///
    /// ⇒ 说明**内核没走到选牌**（`RecordPick` 没被调用），
    /// 不是 `CS` 的发送逻辑有问题。
    ///
    /// 这个测试把「可达性」变成可观测的：逐张打开发牌，
    /// 报告哪几张真的触发了 `PickCardToDraw`。
    /// **不可达的原因通常是该卡的蓝图效果没进 IR**（或候选池为空）。
    /// </summary>
    private static string? DevelopCardsReachable(CardDatabase db)
    {
        // 人类回放（replay-630801）里 `card_event_pams` 明确产生了 CS ⇒ 它**应该**可达
        string[] suspects =
        {
            "card_event_pams",                          // Develop a British order costing 4 or less
            "card_event_baker_street_irregulars",       // Develop a British special force unit
            "card_event_his_majesty_chosen",            // Choose 1 of 3 random elite British units
            "card_event_heroes_of_the_soviet_union_skirm",
        };

        var report = new List<string>();
        int reachable = 0;

        foreach (string name in suspects)
        {
            if (db.Find(name) is null)
            {
                report.Add($"{name}: 卡库没有");
                continue;
            }

            var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 21);
            engine.Start();
            engine.State.ActiveSide = Side.Left;
            engine.State.SetKredits(Side.Left, 40);

            var card = engine.State.Create(name, Side.Left, CardLocation.HandLeft, 0);
            engine.ClearLastPick();

            // ---- ① 先复现 bug：装一个"只从动作流答复"的钩子（= `ReplayRunner` 在服务器路径上干的事）----
            //         bot 自己回合的动作流里没有 CS ⇒ 它返回 null
            //         ⇒ 内核 `return 0` ⇒ **开发牌什么都不做**。
            int askedNoHook = 0;
            engine.PickCardToDraw = (_, _, _) => { askedNoHook++; return null; };
            int handBefore = engine.State.Hand(Side.Left).Count;
            bool played = engine.PlayCard(card);
            bool recordedWithout = engine.LastPick is not null;
            int handAfter = engine.State.Hand(Side.Left).Count;

            if (recordedWithout)
            {
                report.Add($"{name}: （意外）没有钩子时也留了痕");
            }

            // ---- ② 再装 bot 的选牌钩子（= `BotTurnService.InstallBotPickHook` 干的事）----
            var engine2 = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 21);
            engine2.Start();
            engine2.State.ActiveSide = Side.Left;
            engine2.State.SetKredits(Side.Left, 40);
            var card2 = engine2.State.Create(name, Side.Left, CardLocation.HandLeft, 0);
            engine2.ClearLastPick();
            engine2.PickCardToDraw = (_, _, _) => null;      // 回放用的钩子仍在（服务器路径就是这样）
            engine2.ChooseSpawnCard = (sel, cands) =>
                cands.Count > 0 ? cands[^1] : null;           // 故意挑**最后一张**，验证下标如实记录

            int hand2Before = engine2.State.Hand(Side.Left).Count;
            engine2.PlayCard(card2);
            bool recordedWith = engine2.LastPick is not null;

            if (recordedWith)
            {
                reachable++;
                var pk = engine2.LastPick!;
                report.Add($"{name}: ✅ 有钩子时留痕（第 {pk.Index} 个候选，码 {pk.Code}）" +
                           $"；无钩子时留痕=无（已复现 bug）");
            }
            else
            {
                report.Add($"{name}: ❌ **装了钩子也没留痕**（无钩子时被问 {askedNoHook} 次，" +
                           $"手牌 {handBefore}→{handAfter}，打出={played}）");
            }
        }

        // 只要**有一张**可达就说明机制通（其余可能是蓝图/池子问题，单独查）
        if (reachable == 0)
        {
            return "**所有开发牌都没走到选牌** ⇒ 开发效果在内核里整条路不通" +
                   "（客户端因此永远收不到 `CS`）\n        " + string.Join("\n        ", report);
        }

        // 报告全部结果（哪怕通过也留着，方便看哪几张不可达）
        Console.WriteLine("      [开发牌可达性] " + string.Join("\n                     ", report));
        return null;
    }

    private static string? DevelopPickIsRecorded(CardDatabase db)
    {
        // ⚠️ **为什么这里不"打一张开发牌然后看留痕"**：
        //    开发族的候选表是**卡自己的蓝图**调 `GetChooseSpawnCards` 算出来的，
        //    而那一步能不能算出非空候选，取决于该卡效果在 IR 里的实现与随机池 ——
        //    **测试里没法确定性地造出来**（我试过 `card_event_heroes_of_the_soviet_union_skirm`，
        //    候选为空 ⇒ 走不到留痕那一步，测试变成在测别的东西）。
        //
        //    所以这里测**我实际新增的那段记账**：留痕 API 本身、
        //    以及 `CS` 三个槽位需要的量（触发卡 / 候选下标 / 卡码）是否都拿得到、对得上。
        //    留痕**被调用**的位置由 `CardApiDispatch` 里那一处保证（对照检查见文档）。
        const string PoolCard = "card_event_pams";     // Develop a British order costing 4 or less

        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 11);
        engine.Start();

        var trigger = engine.State.Create(PoolCard, Side.Left, CardLocation.HandLeft, 0);

        // 模拟 `CardApiDispatch` 在"选中候选表第 2 张"时做的记账
        string? pool = db.All.FirstOrDefault(c => c.Type == "order"
                                               && c.Faction == "Britain"
                                               && c.Kredits <= 4)?.Name;
        if (pool is null)
        {
            return "卡库里找不到「英国 4 费以下的指令」—— 开发池的样本前提不成立";
        }

        string? code = db.DeckCodeFor(pool);
        if (string.IsNullOrEmpty(code))
        {
            return $"`DeckCodeFor(\"{pool}\")` 返回空 —— `CS` 的槽位 2 会填不出卡码，" +
                   "客户端解不出选中的牌（表现成'选了但没生效'）";
        }

        engine.ClearLastPick();
        engine.RecordPick(trigger.CardId, 1, code, pool);

        if (engine.LastPick is not { } pick)
        {
            return "`RecordPick` 没有留下记录 ⇒ `BotTurnService` 发不出 `CS` " +
                   "⇒ 客户端收到一张没有选择的开发牌（就是用户报的「不会选开发」）";
        }

        if (pick.SelectingCardId != trigger.CardId)
        {
            return $"留痕的触发卡不对：{pick.SelectingCardId}，应为 {trigger.CardId}";
        }

        if (pick.Index != 1)
        {
            return $"留痕的候选下标不对：{pick.Index}，应为 1（`CS` 槽位 1 直接用它）";
        }

        if (pick.Code != code)
        {
            return $"留痕的卡码不对：{pick.Code}，应为 {code}";
        }

        if (db.DeckCodeFor(pool) != pick.Code)
        {
            return "卡码**反查不一致** —— 客户端按码解不出这张牌，与没选一样";
        }

        // 反面：清掉之后必须真的没了（否则会串到下一个动作上）
        engine.ClearLastPick();
        if (engine.LastPick is not null)
        {
            return "`ClearLastPick` 没有清掉留痕 —— 会串到下一个 `PC` 上，发出错的 `CS`";
        }

        return null;
    }
    private static string? DevelopPickIsRecordedOld(CardDatabase db)
    {
        return null;
    }

    /// <summary>
    /// `CanCardBeBuffed`（`BP_CardFunctions`，30 条语句）的真值表。
    ///
    /// ⚠️ **2026-09-30 修正 —— 这条用例以前守的是【错的】实现。**
    /// 旧版本断言「在场(5/6/7) / 弃牌堆(8) ⇒ false」，那是把 `si=41 JumpIfNot` 的
    /// 分支极性读反的产物（`JumpIfNot` 是「条件为**假**才跳」，跳到 `si=730`
    /// `CanBeBuffed = True` 意味着「**不是**未揭示的隐蔽卡 ⇒ true」）。
    /// 真语义：
    /// <code>
    /// si=41       JumpIfNot(IsUnrevealedCovertCard(Card)) -> si=730   ; ⇒ True
    /// si=55..711  ★ 位置表只对「未揭示的隐蔽卡」生效：
    ///             1/2(牌库) 3/4(手牌) 9 → si=762 True
    ///             0 / 5/6(半场HQ) / 7(前线) / 8(弃牌堆) → si=746 False
    /// </code>
    /// 三条同资产内语义自明的校准点钉死极性（`ChangeAttack` si=57 / `GiveSalvage` si=141 /
    /// `ChangeAttack` si=934），取证见 `klink bot/docs/CanCardBeBuffed矛盾调查.md`。
    ///
    /// 普通卡不受位置表限制；未揭示 Covert 卡才走该位置表。
    /// </summary>
    private static string? CanCardBeBuffedTruthTable(CardDatabase db)
    {
        if (db.Find(PlainUnit) is null)
        {
            return $"卡库里缺 {PlainUnit}";
        }

        var (engine, state) = EmptyBoard(db);
        var card = state.CreateWithId(PlainUnit, Side.Left, 20, CardLocation.DeckLeft, 0);

        // 普通卡无论在哪个位置都跳过 Covert 专用表。
        card.Location = CardLocation.BoardFrontline;
        if (CardApi.IsUnrevealedCovertCard(card))
        {
            return "普通卡不应被判成未揭示 Covert";
        }

        // 蓝图 JumpIfNot(IsUnrevealedCovertCard) 对普通卡直接返回 true。
        foreach (CardLocation where in Enum.GetValues<CardLocation>())
        {
            card.Location = where;
            if (!CardApi.CanCardBeBuffed(card))
            {
                return $"CanCardBeBuffed(location={(int)where} {where}) 应当是 true —— "
                     + "si=41 对「不是未揭示隐蔽卡」的卡直接返回 true，根本不进位置表"
                     + "（旧实现把极性读反，对在场单位返回 false）";
            }
        }

        // ③ 位置表本体（si=55..711 / 落点 si=746/762）逐条核对。
        //    位置表单独抽出，便于在有/无揭示状态时分别核对蓝图分支。
        var table = new (CardLocation Where, bool Expect)[]
        {
            (CardLocation.DeckLeft, true),        // 1  si=762
            (CardLocation.DeckRight, true),       // 2  si=762
            (CardLocation.HandLeft, true),        // 3  si=762
            (CardLocation.HandRight, true),       // 4  si=762
            (CardLocation.Deck, true),            // 9  si=762
            (CardLocation.NotAvailable, false),   // 0  si=746
            (CardLocation.BoardHqLeft, false),    // 5  si=746
            (CardLocation.BoardHqRight, false),   // 6  si=746
            (CardLocation.BoardFrontline, false), // 7  si=746
            (CardLocation.Discard, false),        // 8  si=746
        };
        foreach (var (where, expect) in table)
        {
            bool got = CardApi.CanUnrevealedCovertBeBuffed(where);
            if (got != expect)
            {
                return $"位置表 CanUnrevealedCovertBeBuffed({(int)where} {where}) 期望 {expect}，实际 {got}"
                     + "（蓝图 si=55..711 的 switch，落点 si=746/762）";
            }
        }

        var covert = state.CreateWithId("card_unit_115th_separate_btn", Side.Left, 21,
            CardLocation.BoardHqLeft, 1);
        if (!CardApi.IsUnrevealedCovertCard(covert) || CardApi.CanCardBeBuffed(covert))
        {
            return "未揭示 Covert 单位在棋盘上应命中位置表并禁止 buff";
        }

        covert.Location = CardLocation.HandLeft;
        if (!CardApi.CanCardBeBuffed(covert))
        {
            return "未揭示 Covert 单位在手牌中应允许 buff";
        }

        _ = engine;
        return null;
    }

    private static string? RevealCardSemantics(CardDatabase db)
    {
        const string targetName = "card_unit_133rd_ironman";
        const string revealObserverName = "card_unit_186th_recon_company";
        const string enterObserverName = "card_unit_argyllshire_highlanders";
        foreach (string name in new[] { targetName, revealObserverName, enterObserverName })
        {
            if (db.Find(name) is null) return $"卡库里缺 {name}";
        }

        var (engine, state) = EmptyBoard(db);
        var target = state.CreateWithId(targetName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var revealObserver = state.CreateWithId(revealObserverName, Side.Right, 42,
            CardLocation.BoardHqRight, 1);
        state.CreateWithId(enterObserverName, Side.Right, 43, CardLocation.BoardHqRight, 2);
        var source = state.CreateWithId("card_event_hidden_plans", Side.Left, 44,
            CardLocation.Discard, 0);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("RevealCard", source,
            new object?[] { target, source.CardId, null }, ctx, out bool handled);
        if (!handled) return "RevealCard 未接入派发表";
        if (result is not int outValue || outValue != 0)
            return $"qqq 应保持蓝图默认值 0，实际 {result ?? "null"}";
        if (!target.IsRevealed || target.Keywords.Contains(Keyword.Covert)
            || CardApi.IsUnrevealedCovertCard(target) || !CardApi.CanCardBeBuffed(target))
        {
            return "揭示后应持久化 isRevealed、移除 Covert，且不再受未揭示 Covert 的 buff 限制";
        }

        if (!Reached(trace, "OnCardRevealed", target)
            || !Reached(trace, "OnOtherCardRevealed", revealObserver)
            || !Reached(trace, "OnOtherCardEnterPlay", state.ById(43)!)
            || !Reached(trace, "OnCardLocationMoved", target))
        {
            return "揭示/入场事件未按蓝图到达主体与旁观订阅卡："
                 + string.Join(" | ", trace.Take(12));
        }

        int selfReveal = trace.FindIndex(t => t.StartsWith($"OnCardRevealed → {target.Name}#{target.CardId}", StringComparison.Ordinal));
        int otherReveal = trace.FindIndex(t => t.StartsWith($"OnOtherCardRevealed → {revealObserver.Name}#{revealObserver.CardId}", StringComparison.Ordinal));
        int otherEnter = trace.FindIndex(t => t.StartsWith($"OnOtherCardEnterPlay → {state.ById(43)!.Name}#43", StringComparison.Ordinal));
        if (selfReveal < 0 || otherReveal <= selfReveal || otherEnter <= otherReveal)
        {
            return "揭示事件顺序应为主体 OnCardRevealed → 旁观 OnOtherCardRevealed → 旁观 OnOtherCardEnterPlay";
        }

        var suppressed = state.CreateWithId("card_unit_103rd_cavalry_recon", Side.Left, 22,
            CardLocation.BoardHqLeft, 3);
        suppressed.Keywords.Add(Keyword.Suppressed);
        trace.Clear();
        engine.Api.RevealCard(suppressed, source.CardId);
        if (Reached(trace, "OnCardRevealed", suppressed))
        {
            return "被抑制目标不应触发自身 OnCardRevealed";
        }

        if (!trace.Any(t => t.StartsWith("OnOtherCardRevealed →", StringComparison.Ordinal)))
        {
            return "被抑制目标仍应广播 OnOtherCardRevealed";
        }

        return null;
    }

    // ==================================================================
    //  P0 第 3 族：卡内私有函数（locals 管道）的自测
    // ==================================================================

    /// <summary>
    /// IR 里必须带上这些**卡自己的**函数体（`locals`）。
    /// 数据源：`klink bot/docs/card-ir.json`，生成器 `klink bot/tools/gen-kismet-ir.py`
    /// 的 `LOCAL_FUNCTIONS`（2 → 78 个），清单 `out/audit/private-fns-cards-only.txt`。
    /// </summary>
    private static string? LocalFunctionBodiesPresent(CardDatabase db)
    {
        var want = new (string Card, string Fn)[]
        {
            ("card_unit_royal_west_kents", "ApplyBuff"),
            ("card_unit_royal_west_kents", "RemoveBuff"),
            ("card_event_forward_observers", "didPlayBritishInfantryLastTurn"),
            ("card_event_forward_base_anzac", "GetPlayFromHandDamage"),   // 老先例（129 张卡定义它），别退化
            ("card_brawl_test1", "GetChooseSpawnCards"),                  // 另一个老先例（36 张）
        };

        if (KismetLibrary.Default is not { } lib)
        {
            return "KismetLibrary.Default 是 null（IR 没加载）";
        }

        foreach (var (card, fn) in want)
        {
            if (db.Find(card) is null)
            {
                return $"卡库里缺 {card}";
            }

            if (lib.FindLocalProgram(card, fn) is null)
            {
                return $"{card} 的 locals 里没有 {fn} —— IR 没带函数体（生成器的 LOCAL_FUNCTIONS 退回 2 个了？）";
            }
        }

        return null;
    }

    /// <summary>
    /// 端到端：卡调用一个**派发表里没有**的私有函数时，必须真的执行卡自己的函数体。
    ///
    /// 判据用诊断计数器（`KismetVm.ExecuteCall` 的兜底分支会记 `<local-ran:名字>`）：
    ///   · 出现 `<local-ran:名字>` ⇒ 走了 locals 兜底
    ///   · **不再**出现裸 `名字` ⇒ 不会再被记成"未实现的调用"
    /// 两条都要成立，否则等于"看着接上了、其实还是什么都不做"。
    /// </summary>
    private static string? LocalFunctionActuallyRuns(CardDatabase db)
    {
        // ① 带出参的：card_event_forward_observers 的 OnPlayedFromHand 第一步就调
        //    `didPlayBritishInfantryLastTurn(out didSo)`（IR step 14）
        const string order = "card_event_forward_observers";
        if (db.Find(order) is null)
        {
            return $"卡库里缺 {order}";
        }

        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            var card = state.CreateWithId(order, Side.Left, 20, CardLocation.HandLeft, 1);
            engine.Api.RunCardEffect(card, null);

            bool ran = state.UnimplementedCalls.Keys.Any(k => k == "<local-ran:didPlayBritishInfantryLastTurn>");
            bool stillMissing = state.UnimplementedCalls.ContainsKey("didPlayBritishInfantryLastTurn");
            if (!ran || stillMissing)
            {
                return $"{order} 的 OnPlayedFromHand 调 didPlayBritishInfantryLastTurn："
                     + $"走了 locals 兜底={ran}、仍被记未实现={stillMissing}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ② 无出参、纯副作用形态：card_unit_royal_west_kents 的 OnEnterPlay 调 `ApplyBuff()`
        //    （IR step 3，`args:[] outs:[]`）——这条守的是"零出参时也要执行函数体"。
        const string aura = "card_unit_royal_west_kents";
        if (db.Find(aura) is null)
        {
            return $"卡库里缺 {aura}";
        }

        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            var card = state.CreateWithId(aura, Side.Left, 21, CardLocation.BoardHqLeft, 1);
            card.EnteredPlayOnTurn = -1;
            engine.Api.FireTrigger("OnEnterPlay", card, Side.Left);

            bool ran = state.UnimplementedCalls.Keys.Any(k => k == "<local-ran:ApplyBuff>");
            bool stillMissing = state.UnimplementedCalls.ContainsKey("ApplyBuff");
            if (!ran || stillMissing)
            {
                return $"{aura} 的 OnEnterPlay 调 ApplyBuff()：走了 locals 兜底={ran}、仍被记未实现={stillMissing}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        return null;
    }

    private static string Unimpl(GameState state)        => state.UnimplementedCalls.Count == 0
            ? "无"
            : string.Join(", ", state.UnimplementedCalls.OrderByDescending(kv => kv.Value)
                .Take(8).Select(kv => $"{kv.Key}×{kv.Value}"));

    // ==================================================================
    //  P1：部署 Deployment（2026-09-30）
    //
    //  蓝图 `BP_CardFunctions::CardPlayedFromHand` si=3640..6434 —— **一条统一机制**：
    //    `hasDeployment` 门 → 事件14 取消钩子 → 事件23 取 triggerMultiple
    //    → 跑 `1 + triggerMultiple` 次**卡自己的** `OnPlayedFromHand`。
    //  249 张卡的「Deployment: …」文本各自写在卡自己的 OnPlayedFromHand 里，
    //  引擎侧只有这一条链。下面四条分别守这条链的四段。
    // ==================================================================

    /// <summary>卡库 + 半场放一张卡的样板。</summary>
    private static (MatchEngine Engine, GameState State) DeploymentBoard(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.SetKredits(Side.Right, 12);
        state.SetMaxKredits(Side.Right, 12);
        return (engine, state);
    }

    private static CardInstance PutOnBoard(GameState state, string card, Side side, int id, int slot)
        => state.CreateWithId(card, side, id,
            side == Side.Left ? CardLocation.BoardHqLeft : CardLocation.BoardHqRight, slot);

    /// <summary>
    /// ① 门：`hasDeployment` 只区分「有没有取消钩子 / 翻倍数」，
    /// **不**区分「跑不跑 OnPlayedFromHand」——
    /// 非 hasDeployment 的卡（**全部 732 张指令**）在 si=5840 处 `_triggerMultiple` 恒 0，
    /// 一样落到 si=6114 跑一次。这条同时守「指令照常生效」和「单位照常生效」。
    /// </summary>
    private static string? DeploymentGate(CardDatabase db)
    {
        const string unit = "card_unit_10_5_cm_lefh";     // hasDeployment=True，"Deployment: Deal 2 damage to the enemy HQ."
        const string order = "card_event_aans";           // order，hasDeployment 缺席，"Your HQ gets +3 defense. Gain 1 extra kredit slot."
        foreach (string n in new[] { unit, order })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        // 关键字表里必须有 Deployment（CDO hasDeployment 249 张，指令 0 张）
        if (db.Find(unit) is not { } ud || !ud.Keywords.Contains(Keyword.Deployment))
        {
            return $"{unit} 的 CardInnateTable 里没有 Deployment 关键字（hasDeployment 没抽出来）";
        }

        if (db.Find(order) is { } od && od.Keywords.Contains(Keyword.Deployment))
        {
            return $"{order} 是指令，不该有 Deployment 关键字（CDO 里 hasDeployment 出现 0 次）";
        }

        // 单位：非指向性部署，敌 HQ −2
        {
            var (engine, state) = DeploymentBoard(db);
            int before = state.Hq(Side.Right).Defense;
            var c = state.Create(unit, Side.Left, CardLocation.HandLeft,
                                 state.NextLocationNumber(Side.Left, CardLocation.HandLeft));
            if (!engine.PlayCard(c))
            {
                return $"打不出 {unit}";
            }

            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 2)
            {
                return $"部署 {unit} 应让敌方 HQ −2，实际 −{delta}" + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // 指令：照常跑一次（HQ +3）
        {
            var (engine, state) = DeploymentBoard(db);
            int before = state.Hq(Side.Left).Defense;
            var c = state.Create(order, Side.Left, CardLocation.HandLeft,
                                 state.NextLocationNumber(Side.Left, CardLocation.HandLeft));
            if (!engine.PlayCard(c))
            {
                return $"打不出 {order}";
            }

            int delta = state.Hq(Side.Left).Defense - before;
            if (delta != 3)
            {
                return $"指令 {order} 的 OnPlayedFromHand 应让己方 HQ +3，实际 +{delta}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        return null;
    }

    /// <summary>
    /// ② 取消钩子（事件 14）：`card_unit_petlyakov_pe_2ft`「Deployment effects do not trigger.」
    ///
    /// 函数体（IR `locals` 里的独立事件函数）：
    /// <code>
    /// si=0   IsLocatedOnBoard(out isIt)                      ; self 在场上
    /// si=19  BooleanAND(cardDeploying.hasDeployment, isIt)
    /// si=93  out:cancelDeploymentEffect = true
    /// </code>
    /// 取消后整条部署效果不跑 —— 控制流可达性见 `out/audit/p1-cfg.py CardPlayedFromHand 4297`。
    /// </summary>
    private static string? DeploymentCancelHook(CardDatabase db)
    {
        const string blocker = "card_unit_petlyakov_pe_2ft";
        const string unit = "card_unit_10_5_cm_lefh";
        foreach (string n in new[] { blocker, unit })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        // ① 观察者不在场 ⇒ 不取消（守「不是无条件取消」）
        {
            var (engine, state) = DeploymentBoard(db);
            state.CreateWithId(blocker, Side.Left, 30, CardLocation.HandLeft, 1);   // 只在手牌
            int before = state.Hq(Side.Right).Defense;
            engine.PlayCard(state.Create(unit, Side.Left, CardLocation.HandLeft,
                                         state.NextLocationNumber(Side.Left, CardLocation.HandLeft)));
            if (before - state.Hq(Side.Right).Defense != 2)
            {
                return $"观察者不在场时不该取消（敌 HQ 应 −2）"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ② 观察者在场 ⇒ 取消，敌 HQ 一点不掉
        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, blocker, Side.Left, 31, 1);
            int before = state.Hq(Side.Right).Defense;
            var c = state.Create(unit, Side.Left, CardLocation.HandLeft,
                                 state.NextLocationNumber(Side.Left, CardLocation.HandLeft));
            if (!engine.PlayCard(c))
            {
                return $"打不出 {unit}";
            }

            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 0)
            {
                return $"{blocker} 在场时部署效果必须被取消（敌 HQ 应 −0），实际 −{delta}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }

            // 卡本身仍然正常落场（取消的是"效果"，不是"打出"）
            if (c.Location != CardLocation.BoardHqLeft)
            {
                return $"被取消的是部署**效果**，卡本身仍应落场；实际 {c.Location}";
            }
        }

        return null;
    }

    /// <summary>
    /// ③ 翻倍数（事件 23）：`card_unit_b_26_marauder`
    /// 「Your **non-targeting** deployment effects trigger twice.」
    ///
    /// 函数体：`IsLocatedOnBoard(self) &amp;&amp; cardTriggered.side == self.side` ⇒ `TriggerMultiple = 1`。
    /// 调用点 `CardPlayedFromHand` si=5702 只在 `targetCardID == 0`（非指向性）时取这个数，
    /// 然后 si=6114 跑 1 次 + si=6230 循环再跑 `triggerMultiple` 次。
    /// </summary>
    private static string? DeploymentTriggerMultiple(CardDatabase db)
    {
        const string doubler = "card_unit_b_26_marauder";
        const string unit = "card_unit_10_5_cm_lefh";
        foreach (string n in new[] { doubler, unit })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        // ① 自己这边有 ⇒ 打两次（敌 HQ −4）
        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, doubler, Side.Left, 32, 1);
            int before = state.Hq(Side.Right).Defense;
            engine.PlayCard(state.Create(unit, Side.Left, CardLocation.HandLeft,
                                         state.NextLocationNumber(Side.Left, CardLocation.HandLeft)));
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 4)
            {
                return $"{doubler} 在场时己方非指向性部署效果应触发两次（敌 HQ −4），实际 −{delta}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ② 对面有 ⇒ **不**翻倍（函数体里的 `cardTriggered.side == side` 是"自己的"）
        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, doubler, Side.Right, 33, 1);
            int before = state.Hq(Side.Right).Defense;
            engine.PlayCard(state.Create(unit, Side.Left, CardLocation.HandLeft,
                                         state.NextLocationNumber(Side.Left, CardLocation.HandLeft)));
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 2)
            {
                return $"{doubler} 在**对面**时不该翻倍（敌 HQ 应 −2），实际 −{delta}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ③ 自己这边没这张卡 ⇒ 只打一次（守「不是无条件翻倍」）
        {
            var (engine, state) = DeploymentBoard(db);
            int before = state.Hq(Side.Right).Defense;
            engine.PlayCard(state.Create(unit, Side.Left, CardLocation.HandLeft,
                                         state.NextLocationNumber(Side.Left, CardLocation.HandLeft)));
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 2)
            {
                return $"没有观察者时只该打一次（敌 HQ −2），实际 −{delta}";
            }
        }

        return null;
    }

    /// <summary>
    /// `SM.79` 的真实调用形状是 `TriggerDeployment(targetCard, cardID, out qqq)`。
    /// 这里直接跑它的 OnPlayedFromHand，守住目标解析、Deployment 门和 qqq 无副作用。
    /// </summary>
    private static string? TriggerDeploymentDispatch(CardDatabase db)
    {
        const string trigger = "card_unit_sm_79";
        const string deployment = "card_unit_10_5_cm_lefh";
        const string nonDeployment = "card_event_aans";
        foreach (string n in new[] { trigger, deployment, nonDeployment })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, "card_unit_b_26_marauder", Side.Left, 80, 1);
            var target = PutOnBoard(state, deployment, Side.Left, 81, 1);
            var source = state.CreateWithId(trigger, Side.Left, 82, CardLocation.HandLeft, 1);
            int before = state.Hq(Side.Right).Defense;
            engine.Api.RunCardEffect(source, target);
            if (before - state.Hq(Side.Right).Defense != 4)
            {
                return $"SM.79 应触发并翻倍友方非目标部署（敌 HQ 应 −4），实际 −{before - state.Hq(Side.Right).Defense}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        {
            var (engine, state) = DeploymentBoard(db);
            var target = PutOnBoard(state, nonDeployment, Side.Left, 83, 1);
            var source = state.CreateWithId(trigger, Side.Left, 84, CardLocation.HandLeft, 1);
            int before = state.Hq(Side.Right).Defense;
            engine.Api.RunCardEffect(source, target);
            if (before != state.Hq(Side.Right).Defense)
            {
                return "SM.79 不应对没有 Deployment 的目标触发效果";
            }
        }

        return null;
    }

    private static string? ExecuteOnDeploymentTriggeredDispatch(CardDatabase db)
    {
        const string observer = "card_unit_b_26_marauder";
        const string deployment = "card_unit_10_5_cm_lefh";
        foreach (string n in new[] { observer, deployment })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, observer, Side.Left, 80, 1);
        var target = PutOnBoard(state, deployment, Side.Left, 81, 1);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Target = target,
            Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName(
            "ExecuteOnDeploymentTriggered", source,
            new object?[] { target, source.CardId, null }, ctx, out bool handled);
        if (!handled)
        {
            return "ExecuteOnDeploymentTriggered 未接入派发表";
        }

        if (result is not int triggerMultiple || triggerMultiple != 1)
        {
            return $"ExecuteOnDeploymentTriggered 应返回友方观察者的翻倍数 1，实际 {result ?? "null"}";
        }

        return null;
    }

    /// <summary>
    /// P1 关键字基础设施 ①：`getHas*` 一族进派发表（审计 §6 的 P1#27f）。
    ///
    /// 旧状态：派发表里**没有任何 `getHas*` 键**，IR 里 113 个调用点全部静默取假
    /// ⇒ 「如果这张卡有 X 就…」的分支方向是反的。清单见 `out/audit/p1-gethas-calls.py`。
    /// </summary>
    private static string? GetHasDispatch(CardDatabase db)
    {
        var want = new (string Card, string Fn, bool Expect)[]
        {
            ("card_unit_humber_mk_iv", "getHasFury", true),        // CardInnateTable: Fury + Shock
            ("card_unit_humber_mk_iv", "getHasShock", true),
            ("card_unit_humber_mk_iv", "getHasGuard", false),
            ("card_unit_10_5_cm_lefh", "getHasDeployment", true),  // CDO hasDeployment 249 张
            ("card_unit_petlyakov_pe_2ft", "getHasDeployment", false),
            ("card_unit_coastwatchers", "getHasSmokescreen", true),
            ("card_unit_coldstream_guards", "getHasGuard", true),
        };

        foreach (var (card, fn, expect) in want)
        {
            if (db.Find(card) is null)
            {
                return $"卡库里缺 {card}";
            }

            var (engine, state) = EmptyBoard(db);
            var probe = PutOnBoard(state, card, Side.Left, 20, 1);
            var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };

            // 两种实参形状都要认：带 recv（Context{卡}）和隐式 self（裸调用）
            bool viaRecv = (bool)(engine.Api.InvokeByName(fn, probe, new object?[] { null }, ctx, out _) ?? false);
            bool viaSelf = (bool)(engine.Api.InvokeByName(fn, null, new object?[] { null }, ctx, out _) ?? false);

            if (viaRecv != expect || viaSelf != expect)
            {
                return $"{card}.{fn}() 应为 {expect}：带 recv={viaRecv}、隐式 self={viaSelf}"
                     + "（旧实现没有这个键 ⇒ 恒 false）";
            }
        }

        return null;
    }

    /// <summary>
    /// P1 关键字基础设施 ②：**成员读** `hasXxx`（审计 §6 的 P1#27g）。
    ///
    /// 同一个判据在 IR 里有两种形状：成员读（`card.hasDeployment`）和函数调用
    /// （`getHasDeployment`）。旧成员表只有 7 个，漏掉的
    /// `hasCovert`(10 点)/`hasDestruction`(4)/`hasAlpine`(2)/`hasMobilize`(2)/`hasDeployment`(1)
    /// 全部读成 null → 判假。
    ///
    /// 这里手搓一段最小 IR（`set` + `return`）走 `Frame.Get → KismetVm.GetMember`，
    /// 因为成员读没有别的公开入口。
    /// </summary>
    private static string? KeywordMemberReads(CardDatabase db)
    {
        var want = new (string Card, string Member, bool Expect)[]
        {
            ("card_unit_10_5_cm_lefh", "hasDeployment", true),
            ("card_unit_petlyakov_pe_2ft", "hasDeployment", false),
            ("card_unit_coastwatchers", "hasSmokescreen", true),
            ("card_unit_coldstream_guards", "hasGuard", true),
            ("card_unit_humber_mk_iv", "hasShock", true),
            ("card_unit_1st_airborne", "hasMobilize", true),       // CardInnateTable: Fury + Mobilize + Smokescreen
            ("card_unit_1st_airborne", "hasFury", true),
        };

        foreach (var (card, member, expect) in want)
        {
            if (db.Find(card) is null)
            {
                return $"卡库里缺 {card}";
            }

            var (engine, state) = EmptyBoard(db);
            var probe = PutOnBoard(state, card, Side.Left, 20, 1);
            var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };

            // 最小程序：读成员 → 写进一个本地槽 → 返回。
            var steps = new KismetStep[]
            {
                new(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    new KismetExpr { Var = member }, null, -1, "__probe", null),
                new(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    null, null, -1, null, null),
            };

            var bag = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, "__probe");
            bool got = bag.GetValueOrDefault("__probe") switch
            {
                bool b => b,
                null => false,
                _ => true,
            };
            if (got != expect)
            {
                return $"{card}.{member} 成员读应为 {expect}，实际 {got}"
                     + "（旧成员表只有 7 个 ⇒ 读成 null 判假）";
            }
        }

        return null;
    }

    private static string? CardSeenMemberRead(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var probe = PutOnBoard(state, PlainUnit, Side.Left, 20, 1);
        probe.CardSeen = true;
        var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };
        var steps = new KismetStep[]
        {
            new(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                new KismetExpr { Var = "cardSeen" }, null, -1, "__probe", null),
            new(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                null, null, -1, null, null),
        };

        var bag = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, "__probe");
        return bag.GetValueOrDefault("__probe") is true
            ? null
            : "CardInstance.CardSeen=true，但 Blueprint IR 的 cardSeen 成员读没有返回 true";
    }

    private static string? RuntimeStateMemberReads(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var probe = PutOnBoard(state, PlainUnit, Side.Left, 20, 1);
        probe.HasAttackedThisTurn = true;
        probe.HasBeenAttackedThisTurn = true;
        probe.Keywords.Add(Keyword.Suppressed);
        probe.GotchaActivated = 7;
        var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };

        foreach (var (member, expected) in new (string Name, object Value)[]
        {
            ("hasAttackedThisTurn", true),
            ("hasBeenAttackedThisTurn", true),
            ("isSuppressed", true),
            ("gotchaActivated", 7),
        })
        {
            var steps = new KismetStep[]
            {
                new(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    new KismetExpr { Var = member }, null, -1, "__probe", null),
                new(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    null, null, -1, null, null),
            };
            var bag = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, "__probe");
            if (!Equals(bag.GetValueOrDefault("__probe"), expected))
            {
                return $"Blueprint 成员 {member} 应读取 {expected}，实际 {bag.GetValueOrDefault("__probe") ?? "null"}";
            }
        }

        return null;
    }

    private static string? AttackBuffMemberRead(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var probe = PutOnBoard(state, PlainUnit, Side.Left, 20, 1);
        probe.BuffsBySource[(101, false)] = new CardBuff { SourceCardId = 101, Attack = 2 };
        probe.BuffsBySource[(202, true)] = new CardBuff { SourceCardId = 202, Attack = -1, Temporary = true };
        var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };
        var steps = new KismetStep[]
        {
            new(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                new KismetExpr { Var = "attackBuff" }, null, -1, "__probe", null),
            new(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                null, null, -1, null, null),
        };

        var bag = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, "__probe");
        return Equals(bag.GetValueOrDefault("__probe"), 1)
            ? null
            : $"attackBuff 应为永久 +2 与临时 -1 的合计 1，实际 {bag.GetValueOrDefault("__probe") ?? "null"}";
    }

    private static string? SalvagedMemberAndSuppressionBaseline(CardDatabase db)
    {
        var made = MakeBoard(db, PlainUnit);
        if (made is null) return $"造不出单位 {PlainUnit}";
        var (engine, unit) = made.Value;
        unit.IsSalvaged = true;
        unit.Attack = 5;
        unit.Defense = 6;
        unit.MaxDefense = 6;

        var ctx = new EffectContext { Engine = engine, State = engine.State, Self = unit, Controller = Side.Left };
        var steps = new KismetStep[]
        {
            new(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                new KismetExpr { Var = "isSalvaged" }, null, -1, "__probe", null),
            new(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                null, null, -1, null, null),
        };
        var bag = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, "__probe");
        if (bag.GetValueOrDefault("__probe") is not true)
        {
            return "CardInstance.IsSalvaged=true，但 Blueprint IR 的 isSalvaged 成员读没有返回 true";
        }

        engine.Api.SuppressUnit(unit);
        return unit.Attack == 1 && unit.Defense == 1 && unit.MaxDefense == 1
            ? null
            : $"抑制后打捞复制品数值为 {unit.Attack}/{unit.Defense}（上限 {unit.MaxDefense}），期望 1/1/1";
    }

    private static string? CardDefinitionAndSalvageMemberReads(CardDatabase db)
    {
        const string cardName = "card_unit_3rd_canadian_division";
        if (db.Find(cardName) is null) return $"卡库里缺 {cardName}";
        var (engine, state) = EmptyBoard(db);
        var probe = PutOnBoard(state, cardName, Side.Left, 20, 1);
        probe.SalvageFaction = "Britain";
        var ctx = new EffectContext { Engine = engine, State = state, Self = probe, Controller = Side.Left };

        foreach (var (member, expected) in new (string Name, object? Value)[]
        {
            ("type", probe.Definition.Type),
            ("rarity", probe.Definition.Rarity),
            ("salvageFaction", "Britain"),
        })
        {
            var steps = new KismetStep[]
            {
                new(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    new KismetExpr { Var = member }, null, -1, "__probe", null),
                new(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    null, null, -1, null, null),
            };
            var bag = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, "__probe");
            if (!Equals(bag.GetValueOrDefault("__probe"), expected))
            {
                return $"Blueprint 成员 {member} 应读取 {expected ?? "null"}，实际 {bag.GetValueOrDefault("__probe") ?? "null"}";
            }
        }

        return null;
    }

    // ==================================================================
    //  P1：摧毁 Destruction（2026-09-30）
    //
    //  蓝图 `TriggerDestruction` si=905/965 门 → si=1237 `OnDestroyed`
    //  → si=1286 事件24 `ExecuteOnDestructionEffectTriggered` → si=1346 若
    //  TriggerMultiple > 0 就**再整轮派发**那么多次。
    //  本组守的是**事件 24 那一段**（卡自己的 OnDestroyed 内核早就在发，
    //  且有意不加 hasDestruction 门 —— 理由见 MatchEngine.Destroy 的注释）。
    // ==================================================================

    /// <summary>
    /// 摧毁事件必须把 **killer（击杀者）** 传给 `OnOtherCardDestroyed`。
    ///
    /// 证据链：
    /// · 派发方 `out/bp-cardfn.json` → `ExecuteOnCardDestroyedFunction`
    ///   （入参 `cardID, location, attackerCardID, allCardsGettingDestroyed, destroyedInCombat`）：
    /// <code>
    /// stmt 3/4  localKiller = (attackerCardID > 0) ? GetCardFromID(attackerCardID) : NoObject
    /// stmt 50   localLoopCard.OnOtherCardDestroyed(localDestroyedCard, localKiller, false,
    ///                       location, Array_Contains(allCardsGettingDestroyed, …), destroyedInCombat)
    /// </code>
    ///   —— 第二个出参就是 killer（`klink bot/docs/event-contracts.json` 里
    ///   `OnOtherCardDestroyed` 的槽位 `K2Node_Event_killer: {local: killer, type: BaseCardObject}`）。
    /// · 订阅方 `klink bot/docs/card-ir.json` → `card_unit_142nd_infantry_regiment`
    ///   （USA 步兵，"When this unit destroys an enemy unit, your HQ gains +2 defense."），
    ///   入口 `OnOtherCardDestroyed`（唯一入口，i=10 起）：
    /// <code>
    /// i=10   jumpIfNot(K2Node_Event_TriggerNotDestroyed) → i=29   ; 真 ⇒ 直接返回
    /// i=24   jump → i=403（return）
    /// i=29   EqualEqual_ObjectObject(K2Node_Event_killer, self)   ; ★ 门：killer 就是我
    /// i=59   IsUnit(K2Node_Event_cardDestroyed)
    /// i=100  NotEqual_ByteByte(cardDestroyed.side, 我方 side)     ; 摧毁的是敌方单位
    /// i=236  jumpIfNot(三者与) → i=403
    /// i=250  GetLocationCardBySide(我方 side) → 我的 HQ
    /// i=332  ChangeDefense(HQ, +2, 加)
    /// </code>
    ///
    /// ⚠️ 修之前 `MatchEngine.Destroy` 发的是
    /// `FireTrigger("OnDestroyed", card, card.Owner, "OnOtherCardDestroyed")` ——
    /// **一个载荷都没传**，于是两个槽都落到 `KismetVm.ResolveEventVar` 最后的
    /// `eventSubject ?? ctx.Self` 兜底：
    /// · `K2Node_Event_killer` 读到**被摧毁的那张卡**（i=29 的门恒假）；
    /// · `K2Node_Event_TriggerNotDestroyed` 也读到那张卡 —— Bool 槽里塞的是
    ///   `CardInstance`，而 `KismetVm.Truthy` 对 `CardInstance` 恒真（KismetVm.cs:899），
    ///   于是 i=24 直接 `jump` 到 return，**i=29 之后的整段都跑不到**。
    /// 两条合起来 ⇒ 这个 handler 永不生效（本自测修前必失败）。
    ///
    /// 反例那一段守的是「不能靠兜底成 self 蒙对」：killer 为 null 时门必须为假。
    /// </summary>
    private static string? DestroyEventCarriesKiller(CardDatabase db)
    {
        const string killerCard = "card_unit_142nd_infantry_regiment";
        const string victimCard = "card_unit_arado_ar_196";   // 普通单位，不订阅任何事件
        foreach (string n in new[] { killerCard, victimCard })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        if (KismetLibrary.Default?.FindProgram(killerCard, "OnOtherCardDestroyed") is null)
        {
            return $"IR 里没有 {killerCard} 的 OnOtherCardDestroyed（card-ir.json 是否加载？）";
        }

        // 布景：142 步兵连在左方半场，敌方一张普通单位在右方半场。
        // 返回「左方 HQ 防御的变化量」。`withKiller=false` 时 destroyer 传 null
        // （等价于 `DestroyCard` 没有来源，蓝图里 `localKiller = NoObject`）。
        static (int Delta, string? Error) Run(CardDatabase db, bool withKiller)
        {
            var (engine, state) = DeploymentBoard(db);
            var killer = PutOnBoard(state, killerCard, Side.Left, 20, 1);
            var victim = PutOnBoard(state, victimCard, Side.Right, 21, 2);
            int before = state.Hq(Side.Left).Defense;
            try
            {
                engine.Destroy(victim, withKiller ? killer : null);
            }
            catch (Exception ex)
            {
                return (0, $"{ex.GetType().Name}: {ex.Message}");
            }

            return (state.Hq(Side.Left).Defense - before, null);
        }

        // ① 正例：killer 传进去 ⇒ 订阅卡读到的 killer 必须**是击杀者**（`killer == self`）⇒ HQ +2
        var (delta, err) = Run(db, withKiller: true);
        if (err is not null)
        {
            return $"正例抛异常：{err}";
        }

        if (delta != 2)
        {
            return $"killer 没有传到 OnOtherCardDestroyed："
                 + $"Destroy(受害者={victimCard}, 击杀者={killerCard}) 之后左方 HQ 防御只变了 {delta}（期望 +2）"
                 + " —— `K2Node_Event_killer` 被读成了被摧毁的那张卡（i=29 的门恒假），"
                 + "或 `K2Node_Event_TriggerNotDestroyed` 被读成真、i=24 直接返回。";
        }

        // ② 反例：没有 killer ⇒ 门必须为假，不能又兜底成 self
        var (delta2, err2) = Run(db, withKiller: false);
        if (err2 is not null)
        {
            return $"反例抛异常：{err2}";
        }

        if (delta2 != 0)
        {
            return $"destroyer 为 null 时左方 HQ 防御变了 {delta2}（期望 0）——"
                 + " 说明 killer 不是真载荷，而是又兜底成了 self。";
        }

        return null;
    }

    /// <summary>摧毁一张 `hasDestruction` 的卡时，事件 24 的订阅者要反应。</summary>
    private static string? DestructionEffectTriggered(CardDatabase db)
    {
        const string victim = "card_unit_hayabusa";              // hasDestruction，"Destruction: Deal 2 damage to the enemy HQ."
        const string watcher = "card_unit_matsumoto_regiment";   // "Deal 1 damage to the enemy HQ when a Destruction effect triggers."
        const string plain = "card_unit_arado_ar_196";           // 没有 hasDestruction
        foreach (string n in new[] { victim, watcher, plain })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        if (db.Find(victim) is not { } vd || !vd.Keywords.Contains(Keyword.Destruction))
        {
            return $"{victim} 的关键字表里没有 Destruction";
        }

        if (db.Find(plain) is { } pd && pd.Keywords.Contains(Keyword.Destruction))
        {
            return $"{plain} 不该有 Destruction 关键字（CDO hasDestruction 缺席）";
        }

        // ① 观察者在场 + 被摧毁的是 hasDestruction 卡 ⇒ 敌 HQ 掉 2（自己）+ 1（事件24）
        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, watcher, Side.Left, 50, 1);
            var v = PutOnBoard(state, victim, Side.Left, 51, 2);
            int before = state.Hq(Side.Right).Defense;
            engine.Destroy(v);
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 3)
            {
                return $"{victim} 被摧毁应让敌 HQ −3（自己 2 + {watcher} 反应 1），实际 −{delta}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ② 观察者不在场 ⇒ 只有自己的 2（守「事件 24 不是无条件派发」）
        {
            var (engine, state) = DeploymentBoard(db);
            state.CreateWithId(watcher, Side.Left, 52, CardLocation.HandLeft, 1);   // 只在手牌
            var v = PutOnBoard(state, victim, Side.Left, 53, 2);
            int before = state.Hq(Side.Right).Defense;
            engine.Destroy(v);
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 2)
            {
                return $"观察者不在场时应只有自己的 −2，实际 −{delta}";
            }
        }

        // ③ 被摧毁的卡**没有** hasDestruction ⇒ 事件 24 不派发（守门）
        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, watcher, Side.Left, 54, 1);
            var v = PutOnBoard(state, plain, Side.Left, 55, 2);
            int before = state.Hq(Side.Right).Defense;
            engine.Destroy(v);
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 0)
            {
                return $"{plain} 没有 hasDestruction ⇒ 事件24 不该派发（敌 HQ 应 −0），实际 −{delta}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        return null;
    }

    private static string? AfterDestroyedDispatch(CardDatabase db)
    {
        const string victimName = "card_unit_641st_rifles";
        const string killerName = "card_unit_142nd_infantry_regiment";
        foreach (string name in new[] { victimName, killerName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = DeploymentBoard(db);
        var victim = PutOnBoard(state, victimName, Side.Left, 6314, 1);
        var killer = PutOnBoard(state, killerName, Side.Right, 6315, 2);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        try
        {
            engine.Destroy(victim, killer);
        }
        catch (Exception ex)
        {
            return $"Destroy 触发 OnAfterDestroyed 时抛异常：{ex.GetType().Name}: {ex.Message}";
        }

        if (!Reached(trace, "OnAfterDestroyed", victim))
        {
            return "被摧毁卡没有收到主体 OnAfterDestroyed";
        }

        int destroyedIndex = trace.FindIndex(t =>
            t.StartsWith($"OnDestroyed → {victim.Name}#{victim.CardId}", StringComparison.Ordinal));
        int afterIndex = trace.FindIndex(t =>
            t.StartsWith($"OnAfterDestroyed → {victim.Name}#{victim.CardId}", StringComparison.Ordinal));
        if (destroyedIndex < 0 || afterIndex <= destroyedIndex)
        {
            return $"OnAfterDestroyed 时序错误：OnDestroyed={destroyedIndex}, "
                 + $"OnAfterDestroyed={afterIndex}";
        }

        return null;
    }

    /// <summary>
    /// 事件 24 的**翻倍数**：`card_unit_114th_infantry_regiment`
    /// 「When a **friendly** Destruction effect triggers, it triggers twice.」
    ///
    /// 函数体 `BooleanOR(SelfAlsoDestroyed, IsLocatedOnBoard(self))` 之后判
    /// `cardTriggered.side == side` ⇒ `TriggerMultiple = 1`；`TriggerDestruction` si=1346
    /// 拿到 1 之后**再整轮派发一次**，于是松本连的「敌 HQ −1」发生两次。
    /// </summary>
    private static string? DestructionTriggerMultiple(CardDatabase db)
    {
        const string doubler = "card_unit_114th_infantry_regiment";
        const string watcher = "card_unit_matsumoto_regiment";
        const string victim = "card_unit_hayabusa";
        foreach (string n in new[] { doubler, watcher, victim })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        // ① 自己这边的翻倍者在场 ⇒ 松本连反应两次 ⇒ 敌 HQ −(2 + 1×2) = −4
        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, doubler, Side.Left, 60, 1);
            PutOnBoard(state, watcher, Side.Left, 61, 2);
            var v = PutOnBoard(state, victim, Side.Left, 62, 3);
            int before = state.Hq(Side.Right).Defense;
            engine.Destroy(v);
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 4)
            {
                return $"{doubler} 在场时摧毁效果应触发两次（敌 HQ −4），实际 −{delta}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ② 翻倍者在**对面** ⇒ 不翻倍（函数体里是 `cardTriggered.side == side`）
        {
            var (engine, state) = DeploymentBoard(db);
            PutOnBoard(state, doubler, Side.Right, 63, 1);
            PutOnBoard(state, watcher, Side.Left, 64, 2);
            var v = PutOnBoard(state, victim, Side.Left, 65, 3);
            int before = state.Hq(Side.Right).Defense;
            engine.Destroy(v);
            int delta = before - state.Hq(Side.Right).Defense;
            if (delta != 3)
            {
                return $"{doubler} 在对面时不该翻倍（敌 HQ 应 −3），实际 −{delta}";
            }
        }

        return null;
    }

    private static string? TriggerDestructionDispatch(CardDatabase db)
    {
        const string victim = "card_unit_hayabusa";
        const string watcher = "card_unit_matsumoto_regiment";
        const string plain = "card_unit_arado_ar_196";
        foreach (string n in new[] { victim, watcher, plain })
        {
            if (db.Find(n) is null)
            {
                return $"卡库里缺 {n}";
            }
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, watcher, Side.Left, 70, 1);
        var target = PutOnBoard(state, victim, Side.Left, 71, 2);
        int before = state.Hq(Side.Right).Defense;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Target = target, Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("TriggerDestruction", source,
            new object?[] { target, source.CardId, false, false, null }, ctx, out bool handled);
        int delta = before - state.Hq(Side.Right).Defense;
        if (!handled || target.Location != CardLocation.BoardHqLeft || !target.IsAlive || delta != 3)
        {
            return $"TriggerDestruction 应只触发 Hayabusa(2)+Matsumoto(1) 且不移动目标（handled={handled}, result={result}, "
                 + $"delta={delta}, location={target.Location}, alive={target.IsAlive}）";
        }

        var temporary = PutOnBoard(state, plain, Side.Left, 72, 3);
        engine.Api.CustomAbilityAdd(temporary, "destruction", source);
        var removeCtx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Target = temporary, Controller = Side.Left,
        };
        engine.Api.InvokeByName("TriggerDestruction", source,
            new object?[] { temporary.CardId, source.CardId, false, true, null }, removeCtx, out bool removeHandled);
        if (!removeHandled || engine.Api.HasCustomAbility(temporary, "destruction"))
        {
            return "TriggerDestruction 的 RemoveDestruction=true 应移除临时 destruction 能力";
        }

        return null;
    }

    private static string? DestroyedCardTurnState(CardDatabase db)
    {
        const string unit = "card_unit_10_5_cm_lefh";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = EmptyBoard(db);
        var destroyedUnit = PutOnBoard(state, unit, Side.Left, 901, 1);
        engine.Destroy(destroyedUnit);

        object? ids = engine.Api.InvokeByName("GetDestroyedCardsIDsByTurn", null,
            new object?[] { state.Turn }, new EffectContext
            {
                Engine = engine,
                State = state,
                Self = null,
                Controller = Side.Left,
            }, out bool handled);
        if (!handled || ids is not List<int> turnIds || !turnIds.SequenceEqual(new[] { 901 }))
        {
            return $"GetDestroyedCardsIDsByTurn 返回错误：{ids}";
        }

        object? flag = engine.Api.InvokeByName("GetUnitDestroyedThisTurn", null,
            Array.Empty<object?>(), new EffectContext
            {
                Engine = engine,
                State = state,
                Self = null,
                Controller = Side.Left,
            }, out handled);
        if (!handled || flag is not true)
        {
            return $"GetUnitDestroyedThisTurn 应为 true，实际 {flag}";
        }

        engine.StartTurn(Side.Left, draw: false);
        if (state.UnitDestroyedThisTurn)
        {
            return "新回合未清除 UnitDestroyedThisTurn";
        }

        return null;
    }

    // ==================================================================
    //  P1：重甲 HeavyArmor（2026-09-30）
    // ==================================================================

    /// <summary>
    /// 重甲减伤：**只对战斗伤害生效**（2026-10-02 修正的全局性过度减免）。
    ///
    /// ## 蓝图证据链
    ///
    /// 减伤那一段在 `CalculateDamageDealt` 体内
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:5207-5225`）：
    /// <c>damage = Max(damage − (ignoreHeavyArmor ? 0 : getTotalHeavyArmor()) − …, 0)</c>，
    /// `SelectInt(A,B,pick)` = `pick ? A : B`（`KismetVm.EvalMath` 的 `case "SelectInt"`）。
    ///
    /// 而 `CalculateDamageDealt` 全库**只有 4 个调用点，全是攻击**：
    /// <list type="bullet">
    /// <item>`AttackCard` 主伤害 `g.cs:4657` / 反击 `g.cs:4667`（`ignoreHeavyArmor=False`）；</item>
    /// <item>`BP_Logic::Do_Units_Die` 攻击预览 `_deps/BP_Logic.g.cs:2538/2558`。</item>
    /// </list>
    /// 真正扣血的那一步是 `ApplyDamageToCard`（`g.cs:849-1373`），它是**裸减**：
    /// `g.cs:1053-1057` `getTotalDefense(toCard) − finalDamage → setAndEncryptDefense`
    /// —— 里面一个字都没提重甲。效果伤害那几条链（`DamageCard` `g.cs:11638`、
    /// `MakeCardsFight` `g.cs:26021/26023`、`ApplyDamageToMultipleCards`）全走裸减。
    /// 规则参考独立印证：`KARDS基础规则参考.md:106`「**重甲不减免指令伤害**」。
    ///
    /// ## 为什么这条用例**必须修复前失败**
    ///
    /// 修复前 `MatchEngine.ApplyDamage` 对**每一笔**伤害都扣重甲（全局过度减免），
    /// 所以下面 ① 那段（效果伤害 `DamageCard`）会看到"掉 3 − 重甲"而不是"掉 3"。
    /// ② 那段（战斗伤害 `Attack`）修复前后都该是"掉 3 − 重甲"。
    /// </summary>
    private static string? HeavyArmorReduction(CardDatabase db)
    {
        // 无重甲、无 `OnCardDealDamage_ModifyDamageDealt` 的普通单位
        // （`MakeCardsFightMutualStrike` 里当对照组用的同一张），保证算术干净。
        const string Dealer = "card_unit_panzer_ii_a";

        // (卡, 期望重甲)
        var want = new (string Card, int Armor)[]
        {
            ("card_unit_arado_ar_196", 0),      // 无重甲：全吃
            ("card_unit_comet1", 1),
            ("card_unit_kv_ii", 2),
            ("card_unit_maus", 3),              // 3 点重甲吃 3 点战斗伤害 ⇒ 掉 0，且不死
        };

        foreach (var (card, armor) in want)
        {
            if (db.Find(card) is null)
            {
                return $"卡库里缺 {card}";
            }

            // ---- ① 效果伤害（`DamageCard` 链 = 所有"指令/效果造成伤害"的卡）：
            //         重甲**一点都不该扣**，3 点就是掉 3 ----
            {
                var (engine, state) = DeploymentBoard(db);
                var probe = PutOnBoard(state, card, Side.Left, 70, 1);
                var dealer = PutOnBoard(state, Dealer, Side.Left, 71, 2);

                if (probe.HeavyArmor != armor)
                {
                    return $"{card} 的重甲应为 {armor}，实际 {probe.HeavyArmor}"
                         + "（CardInnateTable / CDO 对不上）";
                }

                var ctx = new EffectContext
                {
                    Engine = engine,
                    State = state,
                    Self = dealer,
                    Controller = Side.Left,
                };

                int before = probe.Defense;
                // 实参形状：`DamageCard(card, amount, damagerCardID, isRedirected, fromFight, …)`
                // （`g.cs:11606-11616`；内核 `DoDamageCard` 读 `a[0]` 目标 / `a[1]` 数值 / `a[2]` 来源）。
                engine.Api.InvokeByName("DamageCard", dealer,
                    new object?[] { probe, 3, dealer, false, false, false }, ctx, out bool handled);
                if (!handled)
                {
                    return "派发表里没有 `DamageCard` 键";
                }

                int dropped = before - probe.Defense;
                if (dropped != 3)
                {
                    return $"① 效果伤害**不该**扣重甲：{card}（重甲 {armor}）吃 `DamageCard` 3 点应掉 3，"
                         + $"实际掉 {dropped}（修复前这里掉 {Math.Max(3 - armor, 0)}）"
                         + Dump(state, ("未实现", Unimpl(state)));
                }
            }

            // ---- ② 战斗伤害（`Attack` 链）：**该**扣重甲 ----
            {
                var (engine, mine, foe) = FightBoard(db, Dealer, card, 3, 9, 0, 6);
                if (foe.HeavyArmor != armor)
                {
                    return $"② {card} 的重甲应为 {armor}，实际 {foe.HeavyArmor}";
                }

                int before = foe.Defense;
                if (!engine.Attack(mine, foe, out string why))
                {
                    return $"② {card} 挡不住攻击（{why}）—— 本用例前提不成立";
                }

                int dropped = before - foe.Defense;
                int expect = Math.Max(3 - armor, 0);
                if (dropped != expect)
                {
                    return $"② 战斗伤害**该**扣重甲：{card}（重甲 {armor}）被 3 攻打中应掉 {expect}，"
                         + $"实际掉 {dropped}";
                }

                if (expect == 0 && !foe.IsAlive)
                {
                    return $"{card} 被 3 点战斗伤害打死了 —— 重甲 {armor} 应该完全吸收"
                         + "（减伤下限 0，不该变成负数伤害）";
                }
            }
        }

        return null;
    }

    // ==================================================================
    //  P1：烟幕 Smokescreen（2026-09-30）
    // ==================================================================

    /// <summary>
    /// 烟幕三条规则（审计 §6 的 P1#29）：
    ///  ① `CanAttack` si=3596/3637/3688/3702/3782：带烟幕的单位**不能被攻击**
    ///     （`location_has_smokescreen` / `defender_has_smokescreen`）
    ///  ② `AttackCard` si=2911/2952/2966/3511：**自己攻击之后消失**
    ///     —— 攻击者不在场会跳过；被压制只跳过自身 `OnBeforeAttack`，
    ///        仍会在攻击后走到 `RemoveSmokescreen`
    ///  ③ `CardLocationMoved` si=643/674/684/725/735：**移到前线(7)就消失**
    /// </summary>
    private static string? SmokescreenRules(CardDatabase db)
    {
        const string smoke = "card_unit_coastwatchers";     // CDO hasSmokescreen = True
        const string plain = "card_unit_arado_ar_196";      // 无烟幕
        const string attacker = "card_unit_3_7_inch_mountain_howitzer";  // 炮兵，射程够
        foreach (string c in new[] { smoke, plain, attacker })
        {
            if (db.Find(c) is null)
            {
                return $"卡库里缺 {c}";
            }
        }

        if (db.Find(smoke) is not { } sd || !sd.Keywords.Contains(Keyword.Smokescreen))
        {
            return $"{smoke} 的关键字表里没有 Smokescreen（CDO hasSmokescreen 没抽出来）";
        }

        // ① 不能被攻击
        {
            var (engine, state, atk) = GuardBoard(db, attacker, (smoke, 1), (plain, 2));
            var smoker = state.Board(Side.Right).First(c => c.Name == smoke);
            var plainer = state.Board(Side.Right).First(c => c.Name == plain);

            if (engine.Attack(atk, smoker))
            {
                return $"带烟幕的 {smoke} 不该能被打（CanAttack si=3637/3793）"
                     + Dump(state, ("未实现", Unimpl(state)));
            }

            if (engine.Attack(atk, plainer) == false)
            {
                return $"同一局面里没有烟幕的 {plain} 应该能打（守「不是把所有目标都拒了」）";
            }

            // `LegalTargets` 也要把它排除掉（调用方 GreedyBot 靠它给目标）
            var targets = engine.LegalTargets(atk).ToList();
            if (targets.Contains(smoker))
            {
                return $"LegalTargets 不该给出带烟幕的 {smoke}";
            }
        }

        // ② 自己攻击之后消失
        {
            var (engine, state, atk) = GuardBoard(db, attacker, (plain, 1));
            engine.Api.GiveKeyword(atk, Keyword.Smokescreen);
            if (!atk.Keywords.Contains(Keyword.Smokescreen))
            {
                return "GiveKeyword(Smokescreen) 没生效，后面两条断言就没意义了";
            }

            var target = state.Board(Side.Right).First(c => c.Name == plain);
            if (!engine.Attack(atk, target))
            {
                return $"攻击应当成功（目标是普通单位）";
            }

            if (atk.Keywords.Contains(Keyword.Smokescreen))
            {
                return $"{attacker} 攻击之后烟幕应当消失（AttackCard si=3511）";
            }
        }

        // ②b 被压制的攻击者仍然移除烟幕：si=2966 只跳过自身
        // `OnBeforeAttack`，随后控制流回到 si=3002 并继续到 si=3511。
        {
            var (engine, state, atk) = GuardBoard(db, attacker, (plain, 1));
            engine.Api.GiveKeyword(atk, Keyword.Smokescreen);
            engine.Api.GiveKeyword(atk, Keyword.Suppressed);
            var target = state.Board(Side.Right).First(c => c.Name == plain);
            if (!engine.Attack(atk, target))
            {
                return "被压制的攻击者仍应能完成攻击（抑制不是行动禁止）";
            }
            if (atk.Keywords.Contains(Keyword.Smokescreen))
            {
                return "被压制的攻击者攻击后仍应移除烟幕（AttackCard si=3511）";
            }
        }

        // ③ 移到前线就消失（`CardLocationMoved` si=643/735）
        {
            var (engine, state, _) = GuardBoard(db, plain);
            state.SetKredits(Side.Left, 20);
            state.SetMaxKredits(Side.Left, 20);
            var mover = state.CreateWithId(smoke, Side.Left, 80, CardLocation.BoardHqLeft, 1);
            mover.EnteredPlayOnTurn = -1;
            if (!mover.Keywords.Contains(Keyword.Smokescreen))
            {
                return $"{smoke} 上场时就该带烟幕";
            }

            if (!engine.MoveUnit(mover, 0))
            {
                return $"把 {smoke} 推进前线失败（这条用例需要它成功）";
            }

            if (mover.Location != CardLocation.BoardFrontline)
            {
                return $"推进后位置应为前线，实际 {mover.Location}";
            }

            if (mover.Keywords.Contains(Keyword.Smokescreen))
            {
                return $"{smoke} 移到前线后烟幕应当消失（CardLocationMoved si=643/735）";
            }
        }

        return null;
    }

    private static string? AmbushAndShockCombat(CardDatabase db)
    {
        const string attackerName = "card_unit_arado_ar_196";
        const string defenderName = "card_unit_arado_ar_196";
        if (db.Find(attackerName) is null || db.Find(defenderName) is null)
        {
            return "缺少战斗测试卡";
        }

        // Shock: defender does not counterattack, and Shock is consumed.
        {
            var (engine, state, attacker) = GuardBoard(db, attackerName, (defenderName, 1));
            var defender = state.Board(Side.Right).First(c => c.Name == defenderName);
            engine.Api.GiveKeyword(attacker, Keyword.Shock);
            int attackerDefense = attacker.Defense;
            if (!engine.Attack(attacker, defender, out string reason))
            {
                return $"Shock 攻击被拒：{reason}";
            }

            if (attacker.Defense != attackerDefense)
            {
                return $"Shock 攻击不应受到反击，防御从 {attackerDefense} 变为 {attacker.Defense}";
            }

            if (attacker.Keywords.Contains(Keyword.Shock))
            {
                return "Shock 攻击后应被移除";
            }
        }

        // Ambush: first attack is answered before the attacker's damage;
        // a lethal ambush prevents the forward hit.
        {
            var (engine, state, attacker) = GuardBoard(db, attackerName, (defenderName, 1));
            var ambusher = state.Board(Side.Right).First(c => c.Name == defenderName);
            engine.Api.GiveKeyword(ambusher, Keyword.Ambush);
            attacker.Attack = 1;
            ambusher.Attack = 99;
            ambusher.Defense = Math.Max(ambusher.Defense, 10);
            int defenderBefore = ambusher.Defense;
            if (!engine.Attack(attacker, ambusher, out string reason))
            {
                return $"Ambush 攻击被拒：{reason}";
            }

            if (attacker.IsAlive)
            {
                return "致命 Ambush 反击后攻击者仍存活";
            }

            if (ambusher.Defense != defenderBefore)
            {
                return "攻击者被 Ambush 击杀后，主攻击伤害不应再落到伏击者";
            }
        }

        return null;
    }

    private static string? LethalCombatDamage(CardDatabase db)
    {
        const string attackerName = "card_unit_arado_ar_196";
        const string defenderName = "card_unit_arado_ar_196";
        var (engine, state, attacker) = GuardBoard(db, attackerName, (defenderName, 1));
        var defender = state.Board(Side.Right).First(c => c.Name == defenderName);
        attacker.Attack = 1;
        defender.Defense = defender.MaxDefense = 5;
        attacker.CustomAbility = "lethal";

        if (!engine.Attack(attacker, defender, out string reason))
        {
            return $"lethal 攻击被拒：{reason}";
        }

        if (defender.IsAlive)
        {
            return "lethal 的正值战斗伤害没有摧毁目标";
        }

        var (effectEngine, effectState) = DeploymentBoard(db);
        var effectTarget = PutOnBoard(effectState, defenderName, Side.Right, 80, 1);
        var effectSource = PutOnBoard(effectState, attackerName, Side.Left, 81, 1);
        effectSource.CustomAbility = "lethal";
        var before = effectTarget.Defense;
        effectEngine.Api.DealDamage(effectTarget, 1, effectSource);
        if (effectTarget.Defense != before - 1)
        {
            return "lethal 不应把非战斗效果伤害变成致命伤害";
        }

        return null;
    }

    // ==================================================================
    //  P1：伤害修正链（2026-09-30）
    //
    //  `BP_CardFunctions::ExecuteOnDealDamageAddDamage`（46 条语句）——
    //  每一次伤害结算**之前**的唯一修正入口：
    //    si=5    JumpIfNot(dealer.isSuppressed) -> si=692
    //            ★ JumpIfNot 在**假**时跳 ⇒ **没被压制**才调
    //              `dealer.OnCardDealDamage_ModifyDamageDealt(toCard, damage, fromAttack,
    //               fromFight, out newDamage)`（si=692）
    //    si=109  事件 37 `OnOtherCardDealDamageAddDamage` 的订阅者逐个问
    //              `(cardDealingDamage, toCard, damage, fromAttack, isDefenderDamage,
    //                out damageToAdd, out reRunAtEnd)`，累加 damageToAdd
    //    si=623  reRunAtEnd 的记进 addDamageToReRun，最后整轮再派发一次（si=805..1295）
    //    si=1300 Clamp(.., 0, 99) -> out calculatedDamage
    //  33 张卡有 `OnCardDealDamage_ModifyDamageDealt`、29 张有
    //  `OnOtherCardDealDamageAddDamage` —— 这些函数体是**独立 export 的函数图**，
    //  P1 §1 才刚把它们编进 IR；在此之前内核没有任何地方按这些名字派发。
    // ==================================================================

    /// <summary>
    /// `OnCardDealDamage_ModifyDamageDealt`（33 张）：
    ///   · `card_unit_m18_hellcat`「Deals double damage against tanks.」
    ///     函数体：`if (toCard.IsTank()) newDamage = damage * 2;`（8 条语句）
    ///   · 压制必须**关掉**它（`si=5` 的极性：没被压制才调）
    ///   · `isRedirected` 的伤害必须跳过整条链（`DamageCard` si=149）
    /// </summary>
    private static string? DamageModifyDealt(CardDatabase db)
    {
        const string dealer = "card_unit_m18_hellcat";   // tank 4/2，"Deals double damage against tanks."
        const string tank = "card_unit_stug_iii_g";      // tank 3/4（没有烟幕/守卫）
        const string plain = PlainUnit;                  // fighter，不是 tank
        foreach (string c in new[] { dealer, tank, plain })
        {
            if (db.Find(c) is null)
            {
                return $"卡库里缺 {c}";
            }
        }

        // ① 打坦克 ⇒ 双倍（4 → 8）
        {
            var (engine, state, atk) = GuardBoard(db, dealer, (tank, 1));
            var target = state.Board(Side.Right).First(c => c.Name == tank);
            int before = target.Defense;
            if (!engine.Attack(atk, target))
            {
                return $"攻击应当成功（{dealer} 在前线、{tank} 在半场）" + Dump(state, ("未实现", Unimpl(state)));
            }

            int dealt = before - target.Defense;
            if (dealt != atk.Attack * 2)
            {
                return $"{dealer} 打坦克应当造成 {atk.Attack * 2}（攻 {atk.Attack} ×2），实际 {dealt}"
                     + "（旧实现：`OnCardDealDamage_ModifyDamageDealt` 在 IR 里躺着但没人派发）"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ② 打非坦克 ⇒ 原值（守「不是把所有伤害都翻倍」）
        {
            var (engine, state, atk) = GuardBoard(db, dealer, (plain, 1));
            var target = state.Board(Side.Right).First(c => c.Name == plain);
            int before = target.Defense;
            if (!engine.Attack(atk, target))
            {
                return $"攻击应当成功（目标是普通单位）";
            }

            int dealt = before - target.Defense;
            if (dealt != atk.Attack)
            {
                return $"{dealer} 打非坦克应当造成 {atk.Attack}，实际 {dealt}";
            }
        }

        // ③ 被**抑制** ⇒ **能攻击，但伤害修正链跳过**（M18 打坦克**不**翻倍）
        //
        // ⚠️ 这一段的历史：2026-10-02 第一轮它被改成「被压制 ⇒ 根本打不出来」，
        //    依据是"权威规则表：被压制的单位不能移动或攻击" —— 那是把中文的
        //    「抑制」（`Suppress`）当成了「压制」（`Pin`）。第二轮删掉那道行动门后，
        //    这里恢复**真正的抑制语义**：
        //      · 被抑制的单位照样能攻击（`CanAttack` 里 `isSuppressed` 0 次）；
        //      · 但它自己的伤害修正被跳过 —— 蓝图 `ExecuteOnDealDamageAddDamage` si=5
        //        `JumpIfNot(_damageDealerCard.isSuppressed)`（内核见 `CardApi.cs:881`）。
        //    顺带这也让"跳过修正"那个分支**重新可达**（上一轮它成了死代码）。
        {
            var (engine, state, atk) = GuardBoard(db, dealer, (tank, 1));
            engine.Api.GiveKeyword(atk, Keyword.Suppressed);
            var target = state.Board(Side.Right).First(c => c.Name == tank);
            int before = target.Defense;

            if (!engine.Attack(atk, target))
            {
                return "被**抑制**的单位**应当仍然能攻击** —— 「抑制」≠「压制」" +
                       "（`Pin` 才禁止行动；`Suppress` 是失去关键词与增益）";
            }

            int dealt = before - target.Defense;
            if (dealt != atk.Attack)
            {
                return $"被抑制的 {dealer} 打坦克造成了 {dealt}，应为原值 {atk.Attack} —— " +
                       "蓝图 `ExecuteOnDealDamageAddDamage` si=5 " +
                       "`JumpIfNot(_damageDealerCard.isSuppressed)`：被抑制就不走修正链";
            }
        }

        // ④ isRedirected 的伤害跳过整条链（`DamageCard` si=149 `JumpIfNot(isRedirected) -> si=690`）
        {
            var (engine, state, _) = GuardBoard(db, dealer, (tank, 1));
            var target = state.Board(Side.Right).First(c => c.Name == tank);
            var source = state.Board(Side.Left).First(c => c.Name == dealer);
            int before = target.Defense;
            engine.Api.DealDamage(target, 4, source, isRedirected: true);
            int dealt = before - target.Defense;
            if (dealt != 4)
            {
                return $"重定向伤害应当跳过修正链（DamageCard si=149），4 点就该掉 4，实际掉 {dealt}";
            }
        }

        return null;
    }

    /// <summary>
    /// `OnOtherCardDealDamageAddDamage`（29 张）：`card_unit_the_rangers`
    /// 「Increase the non-combat, non-attack damage dealt by your units by 1.」
    /// 函数体（19 条语句）：
    /// <c>cardDealingDamage.IsUnit() &amp;&amp; 同阵营 &amp;&amp; IsLocatedOnBoard(dealer)
    /// &amp;&amp; damage &gt; 0 &amp;&amp; !fromAttack &amp;&amp; !isDefenderDamage ⇒ damageToAdd = 1</c>。
    /// 卡面与函数体互证（"non-combat, non-attack" ⟺ `!fromAttack`）。
    /// </summary>
    private static string? DamageAddDamageObservers(CardDatabase db)
    {
        const string rangers = "card_unit_the_rangers";   // 2/5 步兵
        const string plain = PlainUnit;                   // 友方"另一个单位"
        const string victim = "card_unit_stug_iii_g";     // 敌方靶子（自己不做伤害修正）
        foreach (string c in new[] { rangers, plain, victim })
        {
            if (db.Find(c) is null)
            {
                return $"卡库里缺 {c}";
            }
        }

        (MatchEngine Engine, GameState State, CardInstance Victim, CardInstance Source) Board()
        {
            var (engine, state) = DeploymentBoard(db);
            var r = PutOnBoard(state, rangers, Side.Left, 20, 1);
            var s = PutOnBoard(state, plain, Side.Left, 21, 2);
            var v = PutOnBoard(state, victim, Side.Right, 60, 1);
            r.EnteredPlayOnTurn = -1;
            s.EnteredPlayOnTurn = -1;
            v.EnteredPlayOnTurn = -1;
            v.Defense = 30;      // 别被打死，方便连续断言
            return (engine, state, v, s);
        }

        // ① 友方单位的**非攻击**伤害 ⇒ +1（3 → 4）
        {
            var (engine, state, v, s) = Board();
            int before = v.Defense;
            engine.Api.DealDamage(v, 3, s);
            int dealt = before - v.Defense;
            if (dealt != 4)
            {
                return $"游骑兵在场时，友方单位的 3 点非攻击伤害应当变成 4，实际 {dealt}"
                     + "（旧实现：`OnOtherCardDealDamageAddDamage` 没人派发）"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        // ② 攻击伤害 ⇒ **不**加（`!fromAttack` 那一半）
        {
            var (engine, state, v, s) = Board();
            int before = v.Defense;
            engine.Api.DealDamage(v, 3, s, isCombatDamage: true);
            int dealt = before - v.Defense;
            if (dealt != 3)
            {
                return $"游骑兵只加「非攻击」伤害，攻击伤害 3 点应当还是 3，实际 {dealt}";
            }
        }

        // ③ 伤害来自**敌方**单位 ⇒ 不加（`cardDealingDamage.side == side` 那一半）
        {
            var (engine, state, v, _) = Board();
            var enemy = PutOnBoard(state, plain, Side.Right, 61, 2);
            enemy.EnteredPlayOnTurn = -1;
            int before = v.Defense;
            engine.Api.DealDamage(v, 3, enemy);
            int dealt = before - v.Defense;
            if (dealt != 3)
            {
                return $"敌方单位造成的伤害不该被己方游骑兵加成，3 点应当还是 3，实际 {dealt}";
            }
        }

        return null;
    }

    // ==================================================================
    //  P1：山地 Alpine（2026-09-30）
    //
    //  `BP_CardFunctions::GiveAlpineBonus`（39 条语句）——
    //  `hasAlpine && IsLocatedOnBoard && getTotalDefense>0` 时，
    //  `bonus = 场上其它同阵营 Alpine 单位数`，`bonus>0` 则
    //  `ChangeAttack/ChangeDefense(card, card.cardID, bonus, changeType=1)`。
    //  ⚠️ `changeType=1` 是**加法**（`ChangeAttack` si=660 判 1 ⇒ si=1325 `Add_IntInt`），
    //     所以只能在一个单位**刚进场**那一刻调一次。
    //  ⚠️ 18 张 Alpine 卡，`GiveAlpineBonus` 不在派发表里、IR 里调用它的卡 = 0 ——
    //     它是**引擎内函数**，由内核在部署流程主动调。
    // ==================================================================

    /// <summary>
    /// 山地：新部署的 Alpine 单位得到 +N/+N（N = 场上其它同阵营 Alpine 单位数）。
    /// 两个部署入口都覆盖：手牌打出（`PlayCard` ↔ `PlayCardFromHand` si=2207）
    /// 与效果生成（`SpawnOnBattlefield` ↔ `SpawnCardToBoard` si=872）。
    /// 「一次部署只加一次」由数值**恰好**等于 +N 保证（加法重复调会变 +2N）。
    /// </summary>
    private static string? AlpineBonusOnEntry(CardDatabase db)
    {
        const string a1 = "card_unit_3rd_alpini";                 // 1/2，无文本，hasAlpine
        const string a2 = "card_unit_334th_mountain_rifles";      // 2/3，无文本，hasAlpine
        const string a3 = "card_unit_136_gebirgsjager";           // 4/3，无文本，hasAlpine
        const string plain = PlainUnit;                           // 非 Alpine
        foreach (string c in new[] { a1, a2, a3, plain })
        {
            if (db.Find(c) is null)
            {
                return $"卡库里缺 {c}";
            }
        }

        foreach (string c in new[] { a1, a2, a3 })
        {
            if (db.Find(c) is not { } def || !def.Keywords.Contains(Keyword.Alpine))
            {
                return $"{c} 的 CardInnateTable 里没有 Alpine 关键字（CDO hasAlpine 没抽出来）";
            }
        }

        CardInstance Play(MatchEngine engine, GameState state, string name)
        {
            var c = state.Create(name, Side.Left, CardLocation.HandLeft,
                                 state.NextLocationNumber(Side.Left, CardLocation.HandLeft));
            engine.PlayCard(c);
            return c;
        }

        // ① 手牌打出三个 Alpine：第 1 个 +0、第 2 个 +1、第 3 个 +2
        {
            var (engine, state) = DeploymentBoard(db);
            var c1 = Play(engine, state, a1);
            var c2 = Play(engine, state, a2);
            var c3 = Play(engine, state, a3);

            if (!c1.Location.IsBoard() || !c2.Location.IsBoard() || !c3.Location.IsBoard())
            {
                return $"三张 Alpine 都该落到场上，实际 {c1.Location}/{c2.Location}/{c3.Location}";
            }

            if (c1.Attack != 1 || c1.Defense != 2)
            {
                return $"{a1} 是第 1 个 Alpine（场上没有别的）应当保持 1/2，实际 {c1.Attack}/{c1.Defense}";
            }

            if (c2.Attack != 3 || c2.Defense != 4)
            {
                return $"{a2} 是第 2 个 Alpine，应当 2+1/3+1 = 3/4，实际 {c2.Attack}/{c2.Defense}"
                     + "（≠ 表示要么没加、要么加了两次）"
                     + Dump(state, ("未实现", Unimpl(state)));
            }

            if (c3.Attack != 6 || c3.Defense != 5)
            {
                return $"{a3} 是第 3 个 Alpine，应当 4+2/3+2 = 6/5，实际 {c3.Attack}/{c3.Defense}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }

            // ② 加成**不是追溯的**：先上场的不会被后上场的补加
            if (c1.Attack != 1 || c1.Defense != 2)
            {
                return $"加成不该追溯给先上场的 {a1}（蓝图只在进场那一刻调一次），"
                     + $"实际 {c1.Attack}/{c1.Defense}";
            }
        }

        // ③ 打出非 Alpine 不会给已有的 Alpine 再加一次
        {
            var (engine, state) = DeploymentBoard(db);
            Play(engine, state, a1);
            var c2 = Play(engine, state, a2);
            int atk = c2.Attack, def = c2.Defense;
            Play(engine, state, plain);
            if (c2.Attack != atk || c2.Defense != def)
            {
                return $"打出非 Alpine 单位不该改动已有 Alpine 的数值"
                     + $"（{a2} 从 {atk}/{def} 变成 {c2.Attack}/{c2.Defense}）";
            }
        }

        // ④ 效果生成那条路（`SpawnOnBattlefield` ↔ `SpawnCardToBoard` si=872）
        {
            var (engine, state) = DeploymentBoard(db);
            var s1 = engine.Api.SpawnOnBattlefield(Side.Left, a1, frontline: false);
            var s2 = engine.Api.SpawnOnBattlefield(Side.Left, a2, frontline: false);
            if (s1.Attack != 1 || s1.Defense != 2)
            {
                return $"生成的第 1 个 Alpine 应当 1/2，实际 {s1.Attack}/{s1.Defense}";
            }

            if (s2.Attack != 3 || s2.Defense != 4)
            {
                return $"生成的第 2 个 Alpine 应当 2+1/3+1 = 3/4，实际 {s2.Attack}/{s2.Defense}"
                     + Dump(state, ("未实现", Unimpl(state)));
            }
        }

        return null;
    }

    // ==================================================================
    //  ★ 2026-10-02：派发表缺口第一批（10 条）
    //
    //  判据出处：`out/audit/missing-keys-classify2.py`（A/B/C 分类）+
    //            `out/audit/gap-bodies.py <名字>`（直译产物里的函数体）+
    //            `ref/kards-sim/KardsSim/Bridge/EngineHost.cs`（参考实现的语义）。
    //
    //  写法约定（与 `GetHasDispatch` 一致）：用 `engine.Api.InvokeByName` **直接调那个键**，
    //  先断言 `handled == true`（修复前是 false —— 这就是"修复前失败"的直接证据），
    //  再断言它造成的**可观测状态**。不依赖随机、不依赖整局对拍。
    // ==================================================================

    /// <summary>
    /// ★ `SpawnCardInFrontline` —— 108 调用点 / 29 张卡，本次补的最大一块。
    ///
    /// 语义出处：`out/Generated-gap/_deps/BP_CardFunctions.g.cs:35201` 的函数体
    /// —— 唯一一步是 `SpawnCardToBoard(…, 7 /*BoardFrontline*/, …)`。
    /// 所以**落点必须是 `BoardFrontline`**，不是 `SpawnCardOnBattlefield` 那种按 `a[1]` 分的半场。
    /// </summary>
    private static string? SpawnCardInFrontlineLandsOnFrontline(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";      // `card_event_airdrop` 生成的就是它
        const string self = "card_unit_10_5_cm_lefh";
        if (db.Find(unit) is null || db.Find(self) is null)
        {
            return $"卡库里缺 {unit} / {self}";
        }

        // ① 基本落点：side=1(Left)、locationNumber=-1（追加队尾）、不带 Blitz
        {
            var (engine, state) = DeploymentBoard(db);
            var spawner = PutOnBoard(state, self, Side.Left, 20, 1);
            var ctx = new EffectContext
            {
                Engine = engine, State = state, Self = spawner, Controller = Side.Left,
            };

            // IR 实测形状（`out/audit/ir-callsites.py SpawnCardInFrontline`，`card_event_airdrop` i=219）：
            //   a[0]=卡名 a[1]=side a[2]=spawnerID a[3]=out campaignName a[4]=giveBlitz
            //   a[5]=out spawnedCardID a[6]=salvageFaction a[7]=locationNumber a[8]=makeVeteran
            var args = new object?[] { unit, 1, spawner.CardId, null, false, null, 0, -1, false };
            var result = engine.Api.InvokeByName("SpawnCardInFrontline", spawner, args, ctx, out bool handled);

            if (!handled)
            {
                return "派发表里没有 `SpawnCardInFrontline`（**修复前就是这个状态**）";
            }

            if (result is not CardInstance spawned)
            {
                return $"返回值不是卡实例：{result}";
            }

            if (spawned.Location != CardLocation.BoardFrontline)
            {
                return $"落点必须是**前线**(7)，实际 {spawned.Location}#{spawned.LocationNumber}"
                     + "（落半场 = 把 `SpawnCardToBoard` 的 location 实参读错了）"
                     + Dump(state, ("未实现", Unimpl(state)));
            }

            if (spawned.Owner != Side.Left)
            {
                return $"归属应当是左方，实际 {spawned.Owner}";
            }
        }

        // ② `giveBlitz`（a[4]）与 `makeVeteran`（a[8]）要真的生效
        {
            var (engine, state) = DeploymentBoard(db);
            var spawner = PutOnBoard(state, self, Side.Left, 20, 1);
            var ctx = new EffectContext
            {
                Engine = engine, State = state, Self = spawner, Controller = Side.Left,
            };

            var args = new object?[] { unit, 1, spawner.CardId, null, true, null, 0, -1, true };
            var result = engine.Api.InvokeByName("SpawnCardInFrontline", spawner, args, ctx, out bool handled);
            if (!handled || result is not CardInstance spawned)
            {
                return $"handled={handled}、返回 {result}";
            }

            if (!spawned.Keywords.Contains(Keyword.Blitz))
            {
                return $"`giveBlitz=true`（a[4]）必须挂上 Blitz，实际关键字 [{string.Join(",", spawned.Keywords)}]";
            }

            if (!spawned.Keywords.Contains(Keyword.Veteran))
            {
                return $"`makeVeteran=true`（a[8]）必须挂上 Veteran，实际关键字 [{string.Join(",", spawned.Keywords)}]";
            }
        }

        // ③ 生成两张：都必须在**前线**且槽位互不相同。
        //
        //     ⚠️ 这里**不能**断言"指定 a[7]=2 ⇒ 槽位恰好是 2"：`GameState.Move` 末尾会调
        //     `NormalizeLocationNumbers` 把同区槽位压紧成 0..n-1（现有引擎行为，
        //     见 `GameState.cs:465`），所以精确槽号不稳定。可观测的契约是
        //     「两张都在前线、占两个不同槽位」。
        {
            var (engine, state) = DeploymentBoard(db);
            var spawner = PutOnBoard(state, self, Side.Left, 20, 1);
            var ctx = new EffectContext
            {
                Engine = engine, State = state, Self = spawner, Controller = Side.Left,
            };

            var args = new object?[] { unit, 1, spawner.CardId, null, false, null, 0, -1, false };
            var first = engine.Api.InvokeByName("SpawnCardInFrontline", spawner, args, ctx, out bool h1);
            var second = engine.Api.InvokeByName("SpawnCardInFrontline", spawner,
                new object?[] { unit, 1, spawner.CardId, null, false, null, 0, -1, false }, ctx, out bool h2);
            if (!h1 || !h2 || first is not CardInstance c1 || second is not CardInstance c2)
            {
                return $"handled={h1}/{h2}、返回 {first} / {second}";
            }

            if (c1.Location != CardLocation.BoardFrontline || c2.Location != CardLocation.BoardFrontline)
            {
                return $"两次生成都该在前线，实际 {c1.Location} / {c2.Location}";
            }

            if (c1.LocationNumber == c2.LocationNumber)
            {
                return $"两次生成占了同一个槽位 {c1.LocationNumber}（`locationNumber=-1` 应当追加到队尾）";
            }
        }

        return null;
    }

    /// <summary>
    /// `DiscardCardFromHand` —— 39 调用点 / 37 张卡。
    ///
    /// 这条守的是 (B) 类修复里**最容易漏的那一半**：`a[0]` 在 IR 里有**两种形状**
    /// （卡对象 26 个调用点 / 整数 cardID 13 个，见 `out/audit/ir-callsites.py`），
    /// 只认一种就会静默丢掉一半。
    /// </summary>
    private static string? DiscardCardFromHandBothShapes(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        const string self = "card_unit_10_5_cm_lefh";
        if (db.Find(unit) is null || db.Find(self) is null)
        {
            return $"卡库里缺 {unit} / {self}";
        }

        foreach (bool asCardId in new[] { false, true })
        {
            var (engine, state) = DeploymentBoard(db);
            var actor = PutOnBoard(state, self, Side.Left, 20, 1);
            var victim = state.CreateWithId(unit, Side.Left, 21, CardLocation.HandLeft, 1);
            var ctx = new EffectContext
            {
                Engine = engine, State = state, Self = actor, Controller = Side.Left,
            };

            object? first = asCardId ? victim.CardId : victim;
            var args = new object?[] { first, actor.CardId, false, false, null };
            var result = engine.Api.InvokeByName("DiscardCardFromHand", actor, args, ctx, out bool handled);

            string shape = asCardId ? "整数 cardID" : "卡对象";
            if (!handled)
            {
                return $"派发表里没有 `DiscardCardFromHand`（**修复前就是这个状态**）";
            }

            if (victim.Location != CardLocation.Discard)
            {
                return $"形状【{shape}】下卡必须进弃牌堆，实际 {victim.Location}"
                     + "（另一种形状没被认出来 ⇒ 效果静默失效）";
            }

            if (result is not true)
            {
                return $"形状【{shape}】下 out 槽 success 应当是 true，实际 {result}";
            }
        }

        // The hand-discard restriction must leave the card in hand and make
        // the Blueprint success out parameter false.
        {
            var (engine, state) = DeploymentBoard(db);
            var actor = PutOnBoard(state, self, Side.Left, 80, 1);
            var victim = state.CreateWithId(unit, Side.Left, 81, CardLocation.HandLeft, 1);
            state.AddGameplayRestriction(Side.Left,
                GameplayRestrictionType.CannotDiscardAnyCardFromHand, actor.CardId, 2);
            var ctx = new EffectContext
            {
                Engine = engine, State = state, Self = actor, Controller = Side.Left,
            };
            object? result = engine.Api.InvokeByName("DiscardCardFromHand", actor,
                new object?[] { victim.CardId, actor.CardId, false, false, null },
                ctx, out bool handled);
            if (!handled || result is not false || victim.Location != CardLocation.HandLeft)
            {
                return $"弃牌限制下应返回 success=false 且保留手牌：handled={handled},"
                     + $"result={result}, location={victim.Location}";
            }
        }

        // A non-hand target is invalid for this helper and must not be moved.
        {
            var (engine, state) = DeploymentBoard(db);
            var actor = PutOnBoard(state, self, Side.Left, 82, 1);
            var victim = PutOnBoard(state, unit, Side.Left, 83, 2);
            var ctx = new EffectContext
            {
                Engine = engine, State = state, Self = actor, Controller = Side.Left,
            };
            object? result = engine.Api.InvokeByName("DiscardCardFromHand", actor,
                new object?[] { victim.CardId, actor.CardId, false, false, null },
                ctx, out bool handled);
            if (!handled || result is not false || victim.Location != CardLocation.BoardHqLeft)
            {
                return $"非手牌目标应拒绝弃牌：handled={handled}, result={result},"
                     + $" location={victim.Location}";
            }
        }

        return null;
    }

    /// <summary>
    /// `getAndDecryptAttack` / `getAndDecryptDefense` —— 33 / 1 调用点。
    ///
    /// 为什么是真修复：VM 在未处理时**不写 out 槽**，整数槽保持 null ⇒ 读出来是 **0**。
    /// 「这张卡攻击力 0」会让 `ChangeAttack(…, getAndDecryptAttack(card) …)` 一族全算错。
    /// 语义出处：参考实现 `EngineHost.cs:1098/1361`（直接返回卡的实时攻/防）。
    /// </summary>
    private static string? DecryptAttackDefenseRealValues(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = DeploymentBoard(db);
        var card = PutOnBoard(state, unit, Side.Left, 20, 1);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = card, Controller = Side.Left,
        };

        // 形状：recv = 被查的卡，a[0] = out 槽（33/33 调用点一致）
        var atk = engine.Api.InvokeByName("getAndDecryptAttack", card, new object?[] { null }, ctx, out bool h1);
        var def = engine.Api.InvokeByName("getAndDecryptDefense", card, new object?[] { null }, ctx, out bool h2);

        if (!h1 || !h2)
        {
            return $"派发表里缺 getAndDecryptAttack={!h1} / getAndDecryptDefense={!h2}"
                 + "（**修复前就是这个状态**）";
        }

        if ((int)(atk ?? -1) != card.Attack)
        {
            return $"getAndDecryptAttack 应当 {card.Attack}，实际 {atk ?? "null"}"
                 + "（null ⇒ 蓝图侧读成 0）";
        }

        if ((int)(def ?? -1) != card.Defense)
        {
            return $"getAndDecryptDefense 应当 {card.Defense}，实际 {def ?? "null"}";
        }

        if (card.Attack <= 0)
        {
            return $"{unit} 的攻击力是 {card.Attack}，这条断言没有区分度（换一张攻击力 > 0 的卡）";
        }

        // 隐式 self（recv=null）也要能取到 —— IR 里 `getAndDecryptAttack(self, out)` 这种形状
        // 编译出来 recv 可能是 null。
        var viaSelf = engine.Api.InvokeByName("getAndDecryptAttack", null, new object?[] { null }, ctx, out bool h3);
        if (!h3 || (int)(viaSelf ?? -1) != card.Attack)
        {
            return $"隐式 self（recv=null）下应当回落到 ctx.Self 的 {card.Attack}，实际 {viaSelf ?? "null"}";
        }

        return null;
    }

    /// <summary>
    /// `IsBomber`（19 点 / 13 卡）/ `IsFighter`（15 点 / 11 卡）。
    ///
    /// 这两个是**布尔**查询：未实现时 out 槽保持 null ⇒ 恒假。
    /// 语义出处：`MatchEngine.cs:1529`（`Definition.Type == "bomber"`）与
    /// 参考实现 `EngineHost.cs:1015`（`Type == CardType.Fighter`）。
    /// </summary>
    private static string? BomberFighterPredicates(CardDatabase db)
    {
        const string bomber = "card_unit_blenheim_mk_iv";
        const string fighter = "card_unit_typhoon_mk_ib";
        if (db.Find(bomber) is null || db.Find(fighter) is null)
        {
            return $"卡库里缺 {bomber} / {fighter}";
        }

        var (engine, state) = DeploymentBoard(db);
        var b = PutOnBoard(state, bomber, Side.Left, 20, 1);
        var f = PutOnBoard(state, fighter, Side.Left, 21, 2);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = b, Controller = Side.Left,
        };

        var checks = new (string Fn, CardInstance Card, bool Expect)[]
        {
            ("IsBomber", b, true),
            ("IsBomber", f, false),
            ("IsFighter", f, true),
            ("IsFighter", b, false),
        };

        foreach (var (fn, card, expect) in checks)
        {
            // 两种接收者形状都要认（同 `GetHasDispatch`）：显式 recv 与隐式 self
            var viaRecv = engine.Api.InvokeByName(fn, card, new object?[] { null }, ctx, out bool hRecv);
            if (!hRecv)
            {
                return $"派发表里没有 `{fn}`（**修复前就是这个状态**）";
            }

            if ((bool)(viaRecv ?? false) != expect)
            {
                return $"{card.Name}.{fn}() 应为 {expect}，实际 {viaRecv ?? "null"}（null ⇒ 蓝图侧读成假）";
            }
        }

        return null;
    }

    /// <summary>
    /// `IsPinned`（16 点）/ `HasBond`（7 点）—— 运行时授予的关键字查询。
    ///
    /// 本内核的 `CardInnateTable` 里**没有**天生 Bond/Pinned 的卡（两者都是效果授予的），
    /// 所以这里手工把关键字挂上去再查 —— 断言的是"派发键接上了、读的是同一个集合"。
    /// </summary>
    private static string? PinnedBondPredicates(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = DeploymentBoard(db);
        var card = PutOnBoard(state, unit, Side.Left, 20, 1);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = card, Controller = Side.Left,
        };

        // 未授予 ⇒ 假
        var before = engine.Api.InvokeByName("IsPinned", card, new object?[] { null }, ctx, out bool h1);
        if (!h1)
        {
            return "派发表里没有 `IsPinned`（**修复前就是这个状态**）";
        }

        if ((bool)(before ?? false))
        {
            return "没授予 Pinned 时 `IsPinned` 应当为假";
        }

        card.Keywords.Add(Keyword.Pinned);
        card.Keywords.Add(Keyword.Bond);

        var pinned = engine.Api.InvokeByName("IsPinned", card, new object?[] { null }, ctx, out _);
        var bond = engine.Api.InvokeByName("HasBond", card, new object?[] { null }, ctx, out bool h2);

        if (!h2)
        {
            return "派发表里没有 `HasBond`（**修复前就是这个状态**）";
        }

        if (pinned is not true)
        {
            return $"挂上 Pinned 之后 `IsPinned` 应当为真，实际 {pinned ?? "null"}";
        }

        if (bond is not true)
        {
            return $"挂上 Bond 之后 `HasBond` 应当为真，实际 {bond ?? "null"}";
        }

        card.PinnedTurns = 2;
        var removed = engine.Api.InvokeByName("RemovePin", card, new object?[] { null }, ctx,
            out bool removeHandled);
        if (!removeHandled || card.Keywords.Contains(Keyword.Pinned) || card.PinnedTurns != 0)
        {
            return $"RemovePin 应移除 Pinned 并清零时长（handled={removeHandled}, turns={card.PinnedTurns}）";
        }

        return null;
    }

    private static string? SetCountdown(CardDatabase db)
    {
        const string targetName = "card_unit_2nd_parachute";
        if (db.Find(targetName) is null)
        {
            return $"卡库里缺 {targetName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, "card_unit_14th_guards_rifles", Side.Left, 20, 1);
        var target = PutOnBoard(state, targetName, Side.Right, 40, 1);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName(
            "SetCountdown", source, new object?[] { target.CardId, 3, null }, ctx,
            out bool handled);

        if (!handled || result is not true)
        {
            return $"SetCountdown 应报告成功（handled={handled}, result={result ?? "null"}）";
        }

        if (engine.Api.JsonGetInt(target, "countdown_timer") != 3)
        {
            return "SetCountdown 未把倒计时写到目标卡的 countdown_timer";
        }

        if (engine.Api.JsonGetInt(source, "countdown_timer") != 0)
        {
            return "SetCountdown 错把倒计时写到了施动卡";
        }

        return null;
    }

    private static string? DecrementCountdown(CardDatabase db)
    {
        const string targetName = "card_unit_2nd_parachute";
        if (db.Find(targetName) is null)
        {
            return $"卡库里缺 {targetName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, "card_unit_14th_guards_rifles", Side.Left, 20, 1);
        var target = PutOnBoard(state, targetName, Side.Right, 40, 1);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        engine.Api.JsonSetInt(target, "countdown_timer", 2);
        object? first = engine.Api.InvokeByName("DecrementCountdown", source,
            new object?[] { target.CardId, null, null }, ctx, out bool firstHandled);
        if (!firstHandled || first is not object?[] { Length: 2 } firstOut
            || firstOut[0] is not false || firstOut[1] is not true
            || engine.Api.JsonGetInt(target, "countdown_timer") != 1)
        {
            return "倒计时 2→1 应报告 [未结束, 已找到]，并只修改目标卡";
        }

        object? second = engine.Api.InvokeByName("DecrementCountdown", source,
            new object?[] { target.CardId, null, null }, ctx, out bool secondHandled);
        if (!secondHandled || second is not object?[] { Length: 2 } secondOut
            || secondOut[0] is not true || secondOut[1] is not true
            || engine.Api.JsonGetInt(target, "countdown_timer") != 0)
        {
            return "倒计时 1→0 应报告 [已结束, 已找到]";
        }

        object? missing = engine.Api.InvokeByName("DecrementCountdown", source,
            new object?[] { source.CardId, null, null }, ctx, out bool missingHandled);
        if (!missingHandled || missing is not object?[] { Length: 2 } missingOut
            || missingOut[0] is not false || missingOut[1] is not false
            || source.CustomJson.ContainsKey("countdown_timer"))
        {
            return "缺少倒计时字段时应报告 [未结束, 未找到] 且不创建字段";
        }

        if (!state.ActionLog.SelectMany(action => action.SubActions)
            .Any(subAction => subAction.Name == "ZActionPersistCustomFields"
                && subAction.Values.Any(value => value.Name == "cardID" && value.Value == target.CardId)))
        {
            return "递减倒计时后应持久化目标卡 custom fields";
        }

        return null;
    }

    private static string? PincerRelationship(CardDatabase db)
    {
        const string giverName = "card_unit_t_28_pincer";
        const string receiverName = "card_unit_91st_astrakhan";
        if (db.Find(giverName) is null || db.Find(receiverName) is null)
        {
            return $"卡库里缺 {giverName} 或 {receiverName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var giver = PutOnBoard(state, giverName, Side.Left, 20, 1);
        var receiver = PutOnBoard(state, receiverName, Side.Left, 21, 2);
        int attackBefore = receiver.Attack;
        int defenseBefore = receiver.Defense;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = giver, Controller = Side.Left,
        };

        object? applied = engine.Api.InvokeByName("ApplyPincerEffects", giver,
            new object?[] { giver.CardId, receiver.CardId }, ctx, out bool applyHandled);
        if (!applyHandled || applied is not null
            || engine.Api.JsonGetInt(giver, "pincer_receiver") != receiver.CardId
            || !engine.Api.JsonGetIntArray(receiver, "pincer_givers").Contains(giver.CardId))
        {
            return "ApplyPincerEffects 应写入双方的 Pincer 关系字段";
        }

        if (receiver.Attack != attackBefore + 1 || receiver.Defense != defenseBefore + 2)
        {
            return "OnPincerEffectReceived 未按 91st Astrakhan 的蓝图加成 +1/+2";
        }

        engine.Api.InvokeByName("RemovePincerEffects", giver,
            new object?[] { giver.CardId }, ctx, out bool removeHandled);
        if (!removeHandled
            || giver.CustomJson.ContainsKey("pincer_receiver")
            || engine.Api.JsonGetIntArray(receiver, "pincer_givers").Contains(giver.CardId))
        {
            return "RemovePincerEffects 应清掉 receiver/givers 双向关系";
        }

        return null;
    }

    private static string? SetCardSeen(CardDatabase db)
    {
        const string targetName = "card_unit_2nd_parachute";
        if (db.Find(targetName) is null)
        {
            return $"卡库里缺 {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_unit_14th_guards_rifles", Side.Left,
            20, CardLocation.HandLeft, 0);
        var target = state.CreateWithId(targetName, Side.Right, 40,
            CardLocation.HandRight, 0);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName(
            "SetCardSeen", source,
            new object?[] { target.CardId, source.CardId, null }, ctx,
            out bool handled);

        if (!handled || result is not 0)
        {
            return $"SetCardSeen 应处理并返回 qqq=0（handled={handled}, result={result ?? "null"}）";
        }

        if (!target.CardSeen)
        {
            return "SetCardSeen 未把第一个 cardID 参数对应的目标卡标记为已见";
        }

        if (source.CardSeen)
        {
            return "SetCardSeen 错把 instigatorID 对应的施动卡标记为已见";
        }

        object? invalid = engine.Api.InvokeByName(
            "SetCardSeen", source,
            new object?[] { 999999, source.CardId, null }, ctx,
            out bool invalidHandled);
        if (!invalidHandled || invalid is not 0)
        {
            return $"无效 cardID 也应安全返回 qqq=0（handled={invalidHandled}, result={invalid ?? "null"}）";
        }

        return null;
    }

    private static string? IsTopDeckNavy(CardDatabase db)
    {
        const string navyName = "card_event_hms_belfast";
        const string ordinaryName = "card_unit_2nd_parachute";
        if (db.Find(navyName) is null || db.Find(ordinaryName) is null)
        {
            return "IsTopDeckNavy 自测所需卡库条目缺失";
        }

        var (engine, state) = EmptyBoard(db);
        var leftHq = state.ById(1)!;
        state.CreateWithId(navyName, Side.Left, 101, CardLocation.DeckLeft, 0);
        state.CreateWithId(ordinaryName, Side.Left, 102, CardLocation.DeckLeft, 1);
        state.CreateWithId(ordinaryName, Side.Right, 201, CardLocation.DeckRight, 0);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = leftHq, Controller = Side.Left,
        };

        object? leftRaw = engine.Api.InvokeByName(
            "IsTopDeckNavy", leftHq, new object?[] { (int)Side.Left, null }, ctx,
            out bool leftHandled);
        if (!leftHandled || leftRaw is not true)
        {
            return $"左方牌库顶为 HMS Belfast 时应返回 true（handled={leftHandled}, result={leftRaw ?? "null"}）";
        }

        object? rightRaw = engine.Api.InvokeByName(
            "IsTopDeckNavy", leftHq, new object?[] { (int)Side.Right, null }, ctx,
            out bool rightHandled);
        if (!rightHandled || rightRaw is not false)
        {
            return $"显式传入右方 side 时应读取右方非 Navy 顶牌并返回 false（handled={rightHandled}, result={rightRaw ?? "null"}）";
        }

        var (emptyEngine, emptyState) = EmptyBoard(db);
        var emptyHq = emptyState.ById(1)!;
        var emptyCtx = new EffectContext
        {
            Engine = emptyEngine, State = emptyState, Self = emptyHq, Controller = Side.Left,
        };
        object? emptyRaw = emptyEngine.Api.InvokeByName(
            "IsTopDeckNavy", emptyHq, new object?[] { (int)Side.Left, null }, emptyCtx,
            out bool emptyHandled);
        if (!emptyHandled || emptyRaw is not false)
        {
            return $"空牌库应返回 false（handled={emptyHandled}, result={emptyRaw ?? "null"}）";
        }

        var (ordinaryEngine, ordinaryState) = EmptyBoard(db);
        var ordinaryHq = ordinaryState.ById(1)!;
        ordinaryState.CreateWithId(ordinaryName, Side.Left, 301, CardLocation.DeckLeft, 0);
        var ordinaryCtx = new EffectContext
        {
            Engine = ordinaryEngine, State = ordinaryState, Self = ordinaryHq, Controller = Side.Left,
        };
        object? ordinaryRaw = ordinaryEngine.Api.InvokeByName(
            "IsTopDeckNavy", ordinaryHq, new object?[] { (int)Side.Left, null }, ordinaryCtx,
            out bool ordinaryHandled);
        if (!ordinaryHandled || ordinaryRaw is not false)
        {
            return $"非 Navy 顶牌应返回 false（handled={ordinaryHandled}, result={ordinaryRaw ?? "null"}）";
        }

        return null;
    }

    private static string? ChangedPinnedTurns(CardDatabase db)
    {
        const string unitName = "card_unit_2nd_parachute";
        if (db.Find(unitName) is null)
        {
            return $"卡库里缺 {unitName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.ById(1)!;
        var target = PutOnBoard(state, unitName, Side.Left, 20, 1);
        target.PinnedTurns = 4;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        engine.Api.InvokeByName("ChangedPinnedTurns", source,
            new object?[] { target.CardId, source.CardId, 2, null }, ctx,
            out bool handled);
        if (!handled || target.PinnedTurns != 5)
        {
            return $"有效在场单位应将 pinnedTurns=4 按 +2 夹到 5（handled={handled}, actual={target.PinnedTurns}）";
        }

        if (state.ActionLog.LastOrDefault()?.SubActions.FirstOrDefault()?.Name
            != "ZActionChangePinnedTurns")
        {
            return "ChangedPinnedTurns 应记录 ZActionChangePinnedTurns 子动作";
        }

        engine.Api.InvokeByName("ChangedPinnedTurns", source,
            new object?[] { target.CardId, source.CardId, -9, null }, ctx,
            out bool lowerHandled);
        if (!lowerHandled || target.PinnedTurns != 0)
        {
            return $"负增量应将 pinnedTurns 夹到 0（handled={lowerHandled}, actual={target.PinnedTurns}）";
        }

        var hand = state.CreateWithId(unitName, Side.Left, 301, CardLocation.HandLeft, 0);
        engine.Api.InvokeByName("ChangedPinnedTurns", source,
            new object?[] { hand.CardId, source.CardId, 2, null }, ctx,
            out bool handHandled);
        if (!handHandled || hand.PinnedTurns != 0)
        {
            return "不在场上的单位不应修改 pinnedTurns";
        }

        return null;
    }

    private static string? WasRightMostCardWhenPlayedFromHand(CardDatabase db)
    {
        const string sourceName = "card_unit_2nd_parachute";
        const string explicitName = "card_unit_14th_guards_rifles";
        if (db.Find(sourceName) is null || db.Find(explicitName) is null)
        {
            return "WasRightMostCardWhenPlayedFromHand 自测所需卡库条目缺失";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 401, CardLocation.HandLeft, 0);
        var explicitCard = state.CreateWithId(explicitName, Side.Left, 402, CardLocation.HandLeft, 1);
        const string key = "WasRightMostWhenPlayedFromHandKey";
        engine.Api.JsonSetBool(source, key, true);
        engine.Api.JsonSetBool(explicitCard, key, true);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        object? implicitRaw = engine.Api.InvokeByName(
            "WasRightMostCardWhenPlayedFromHand", source,
            new object?[] { source, null }, ctx, out bool implicitHandled);
        if (!implicitHandled || implicitRaw is not true)
        {
            return $"隐式 self 形状应读到 JSON true（handled={implicitHandled}, result={implicitRaw ?? "null"}）";
        }

        engine.Api.JsonSetBool(source, key, false);
        object? explicitRaw = engine.Api.InvokeByName(
            "WasRightMostCardWhenPlayedFromHand", source,
            new object?[] { explicitCard, null }, ctx, out bool explicitHandled);
        if (!explicitHandled || explicitRaw is not true)
        {
            return $"显式 cardPlayed 形状应读取参数卡而非施动卡（handled={explicitHandled}, result={explicitRaw ?? "null"}）";
        }

        object? absentRaw = engine.Api.InvokeByName(
            "WasRightMostCardWhenPlayedFromHand", source,
            new object?[] { source, null }, ctx, out bool absentHandled);
        if (!absentHandled || absentRaw is not false)
        {
            return $"JSON 标记为 false 时应返回 false（handled={absentHandled}, result={absentRaw ?? "null"}）";
        }

        return null;
    }

    private static string? WasLeftMostCardWhenPlayedFromHand(CardDatabase db)
    {
        const string sourceName = "card_unit_2nd_parachute";
        const string explicitName = "card_unit_14th_guards_rifles";
        if (db.Find(sourceName) is null || db.Find(explicitName) is null)
        {
            return "WasLeftMostCardWhenPlayedFromHand 自测所需卡库条目缺失";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 411, CardLocation.HandLeft, 0);
        var explicitCard = state.CreateWithId(explicitName, Side.Left, 412, CardLocation.HandLeft, 1);
        const string key = "WasLeftMostWhenPlayedFromHandKey";
        engine.Api.JsonSetBool(source, key, true);
        engine.Api.JsonSetBool(explicitCard, key, true);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        object? implicitRaw = engine.Api.InvokeByName(
            "WasLeftMostCardWhenPlayedFromHand", source,
            new object?[] { source, null }, ctx, out bool implicitHandled);
        if (!implicitHandled || implicitRaw is not true)
        {
            return $"隐式 self 形状应读到 JSON true（handled={implicitHandled}, result={implicitRaw ?? "null"}）";
        }

        engine.Api.JsonSetBool(source, key, false);
        object? explicitRaw = engine.Api.InvokeByName(
            "WasLeftMostCardWhenPlayedFromHand", source,
            new object?[] { explicitCard, null }, ctx, out bool explicitHandled);
        if (!explicitHandled || explicitRaw is not true)
        {
            return $"显式 cardPlayed 形状应读取参数卡而非施动卡（handled={explicitHandled}, result={explicitRaw ?? "null"}）";
        }

        object? absentRaw = engine.Api.InvokeByName(
            "WasLeftMostCardWhenPlayedFromHand", source,
            new object?[] { source, null }, ctx, out bool absentHandled);
        if (!absentHandled || absentRaw is not false)
        {
            return $"JSON 标记为 false 时应返回 false（handled={absentHandled}, result={absentRaw ?? "null"}）";
        }

        return null;
    }

    private static string? GetDeckLocationBySide(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = state.ById(1)!;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        object? leftRaw = engine.Api.InvokeByName(
            "GetDeckLocationBySide", source, new object?[] { (int)Side.Left, null }, ctx,
            out bool leftHandled);
        if (!leftHandled || leftRaw is not (int)CardLocation.DeckLeft)
        {
            return $"Left 应映射到 DeckLeft=1（handled={leftHandled}, result={leftRaw ?? "null"}）";
        }

        object? rightRaw = engine.Api.InvokeByName(
            "GetDeckLocationBySide", source, new object?[] { (int)Side.Right, null }, ctx,
            out bool rightHandled);
        if (!rightHandled || rightRaw is not (int)CardLocation.DeckRight)
        {
            return $"Right 应映射到 DeckRight=2（handled={rightHandled}, result={rightRaw ?? "null"}）";
        }

        return null;
    }

    private static string? GetHandLocationBySide(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = state.ById(1)!;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        foreach (var (side, expected) in new[]
                 {
                     ((int)Side.Left, (int)CardLocation.HandLeft),
                     ((int)Side.Right, (int)CardLocation.HandRight),
                     ((int)Side.NotAvailable, (int)CardLocation.HandRight),
                 })
        {
            object? raw = engine.Api.InvokeByName(
                "GetHandLocationBySide", source, new object?[] { side, null }, ctx,
                out bool handled);
            if (!handled || raw is not int actual || actual != expected)
            {
                return $"side={side} 应映射到 handLocation={expected}（handled={handled}, result={raw ?? "null"}）";
            }
        }

        return null;
    }

    private static string? HandEdgeQueries(CardDatabase db)
    {
        const string sourceName = "card_unit_2nd_parachute";
        const string leftName = "card_unit_14th_guards_rifles";
        const string rightName = "card_unit_7th_brigade_anzac";
        if (db.Find(sourceName) is null || db.Find(leftName) is null || db.Find(rightName) is null)
        {
            return "手牌边界查询所需卡库条目缺失";
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, sourceName, Side.Left, 20, 1);
        state.CreateWithId(leftName, Side.Left, 90, CardLocation.HandLeft, 1);
        state.CreateWithId(rightName, Side.Left, 91, CardLocation.HandLeft, 4);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = source, Controller = Side.Left,
        };

        object? leftRaw = engine.Api.InvokeByName(
            "GetLeftMostCardInHand", source, new object?[] { source, null, null }, ctx,
            out bool leftHandled);
        object? rightRaw = engine.Api.InvokeByName(
            "GetRightMostCardInHand", source, new object?[] { source, null, null }, ctx,
            out bool rightHandled);

        if (!leftHandled || !rightHandled || leftRaw is not object?[] left || rightRaw is not object?[] right)
        {
            return "手牌边界查询没有注册或未返回双出参";
        }

        if (left.Length < 2 || left[0] is not true || (left[1] as CardInstance)?.CardId != 90)
        {
            return "GetLeftMostCardInHand 未返回位置号最小的手牌";
        }

        if (right.Length < 2 || right[0] is not true || (right[1] as CardInstance)?.CardId != 91)
        {
            return "GetRightMostCardInHand 未返回位置号最大的手牌";
        }

        state.Move(state.ById(90)!, CardLocation.Discard, 0);
        state.Move(state.ById(91)!, CardLocation.Discard, 0);
        object? emptyRaw = engine.Api.InvokeByName(
            "GetLeftMostCardInHand", source, new object?[] { source, null, null }, ctx,
            out bool emptyHandled);
        if (!emptyHandled || emptyRaw is not object?[] empty || empty.Length < 2
            || empty[0] is not false || empty[1] is not null)
        {
            return "空手时 GetLeftMostCardInHand 应返回 found=false/card=null";
        }

        return null;
    }

    private static string? GoldCardPredicate(CardDatabase db)
    {
        const string name = "card_unit_2nd_parachute";
        if (db.Find(name) is null)
        {
            return $"卡库里缺 {name}";
        }

        var (engine, state) = DeploymentBoard(db);
        var regular = PutOnBoard(state, name, Side.Left, 20, 1);
        var gold = state.CreateWithId(name, Side.Left, 90, CardLocation.HandLeft, 2, isGold: true);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = regular, Controller = Side.Left,
        };

        object? regularRaw = engine.Api.InvokeByName(
            "GetIsGoldCard", regular, new object?[] { null }, ctx, out bool regularHandled);
        ctx.Self = gold;
        object? goldRaw = engine.Api.InvokeByName(
            "GetIsGoldCard", gold, new object?[] { null }, ctx, out bool goldHandled);

        if (!regularHandled || !goldHandled || regularRaw is not false || goldRaw is not true)
        {
            return $"GetIsGoldCard 返回错误（普通={regularRaw ?? "null"}, 金卡={goldRaw ?? "null"}）";
        }

        return null;
    }

    /// <summary>
    /// `CustomName1*` / `CustomName2*` 三件套（合计 267 个调用点）。
    ///
    /// 语义出处：参考实现 `EngineHost.cs:2160-2182`（`SuffixAdd`/`SuffixRemove`，幂等、
    /// 逗号分隔）+ `:1088/:1307/:1639`。
    /// 这条同时关掉 `CardApi.cs:631` 里那条 TODO
    /// （「`StopDestructionEffect` 是 `CustomName1` 属性，而 `CustomName1*` 三件套内核里没有」）。
    /// </summary>
    private static string? CustomNameSuffixRoundTrip(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = DeploymentBoard(db);
        var card = PutOnBoard(state, unit, Side.Left, 20, 1);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = card, Controller = Side.Left,
        };

        foreach (string prefix in new[] { "CustomName1", "CustomName2" })
        {
            // 存储键是**小写开头**的 `customName1` / `customName2`（原语名才是大写开头）
            string key = prefix == "CustomName1" ? "customName1" : "customName2";

            // Add
            engine.Api.InvokeByName(prefix + "Add", card, new object?[] { "StopDestructionEffect" }, ctx,
                out bool hAdd);
            if (!hAdd)
            {
                return $"派发表里没有 `{prefix}Add`（**修复前就是这个状态**）";
            }

            // HasAttribute（a[0]=标记串 a[1]=out）
            var has = engine.Api.InvokeByName(prefix + "HasAttribute", card,
                new object?[] { "StopDestructionEffect", null }, ctx, out bool hHas);
            if (!hHas)
            {
                return $"派发表里没有 `{prefix}HasAttribute`（**修复前就是这个状态**）";
            }

            if (has is not true)
            {
                return $"{prefix}Add 之后 HasAttribute 应当为真，实际 {has ?? "null"}";
            }

            // 别的标记不该命中
            var other = engine.Api.InvokeByName(prefix + "HasAttribute", card,
                new object?[] { "played", null }, ctx, out _);
            if (other is true)
            {
                return $"{prefix}HasAttribute(\"played\") 不该为真（后缀串被当成子串匹配了？）";
            }

            // 幂等：再加一次不该重复
            engine.Api.InvokeByName(prefix + "Add", card, new object?[] { "StopDestructionEffect" }, ctx, out _);
            string stored = card.CustomJson.GetValueOrDefault(key, "");
            if (stored != "StopDestructionEffect")
            {
                return $"{prefix}Add 必须**幂等**（重复加同一个标记不重复追加），实际存的是 \"{stored}\"";
            }

            // Remove
            engine.Api.InvokeByName(prefix + "Remove", card, new object?[] { "StopDestructionEffect" }, ctx,
                out bool hRemove);
            if (!hRemove)
            {
                return $"派发表里没有 `{prefix}Remove`（**修复前就是这个状态**）";
            }

            var after = engine.Api.InvokeByName(prefix + "HasAttribute", card,
                new object?[] { "StopDestructionEffect", null }, ctx, out _);
            if (after is true)
            {
                return $"{prefix}Remove 之后 HasAttribute 应当为假，实际 {after}";
            }
        }

        // `GetCustomName2Attributes`（out = 标记数组）
        engine.Api.InvokeByName("CustomName2Add", card, new object?[] { "a" }, ctx, out _);
        engine.Api.InvokeByName("CustomName2Add", card, new object?[] { "b" }, ctx, out _);
        var list = engine.Api.InvokeByName("GetCustomName2Attributes", card, new object?[] { null }, ctx,
            out bool hList);
        if (!hList)
        {
            return "派发表里没有 `GetCustomName2Attributes`（**修复前就是这个状态**）";
        }

        var items = (list as System.Collections.IEnumerable)?.Cast<object?>().Select(x => x?.ToString()).ToList()
                    ?? new List<string?>();
        if (items.Count != 2 || items[0] != "a" || items[1] != "b")
        {
            return $"GetCustomName2Attributes 应当回 [a, b]，实际 [{string.Join(",", items)}]";
        }

        return null;
    }

    /// <summary>
    /// `GetCardsInSupportLineBySide(side, unitsOnly, includeCovertCards, out cards)`
    /// —— 41 调用点 / 34 张卡。
    ///
    /// 语义出处：直译产物 `_deps/BP_CardFunctions.g.cs` 同名函数体的四道过滤
    /// （side / location=半场 / covert / unitsOnly）。本内核的半场 = `side.HqOf()`。
    /// </summary>
    private static string? SupportLineQuery(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = DeploymentBoard(db);
        var own1 = PutOnBoard(state, unit, Side.Left, 20, 1);
        var own2 = PutOnBoard(state, unit, Side.Left, 21, 2);
        var front = state.CreateWithId(unit, Side.Left, 22, CardLocation.BoardFrontline, 0);
        var enemy = PutOnBoard(state, unit, Side.Right, 42, 1);
        var hqLeft = state.ById(1)!;

        var ctx = new EffectContext { Engine = engine, State = state, Self = own1, Controller = Side.Left };

        var result = engine.Api.InvokeByName("GetCardsInSupportLineBySide", null,
            new object?[] { 1, true, false, null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `GetCardsInSupportLineBySide`（**修复前就是这个状态**）";
        }

        var cards = (result as System.Collections.IEnumerable)?.Cast<object?>().OfType<CardInstance>().ToList()
                    ?? new List<CardInstance>();

        foreach (var (card, want, why) in new (CardInstance Card, bool Want, string Why)[]
                 {
                     (own1, true, "本方半场单位"),
                     (own2, true, "本方半场单位"),
                     (front, false, "**前线**单位不属于半场"),
                     (enemy, false, "**敌方**半场单位"),
                     (hqLeft, false, "`unitsOnly=true` 时 HQ（location 卡）不该被算进来"),
                 })
        {
            bool got = cards.Contains(card);
            if (got != want)
            {
                return $"{why} {card.Name}#{card.CardId}@{card.Location}：应当 {(want ? "在" : "不在")}结果里，"
                     + $"实际 {(got ? "在" : "不在")}。结果 = [{string.Join(",", cards.Select(x => $"{x.Name}#{x.CardId}@{x.Location}"))}]";
            }
        }

        // `unitsOnly=false` 时 HQ 要保留（蓝图只按 location 过滤，不排除 HQ）
        var withHq = engine.Api.InvokeByName("GetCardsInSupportLineBySide", null,
            new object?[] { 1, false, false, null }, ctx, out _);
        var all = (withHq as System.Collections.IEnumerable)?.Cast<object?>().OfType<CardInstance>().ToList()
                  ?? new List<CardInstance>();
        if (!all.Contains(hqLeft))
        {
            return "`unitsOnly=false` 时 HQ 应当被保留（蓝图只按 location 过滤）"
                 + $"结果 = [{string.Join(",", all.Select(x => $"{x.Name}#{x.CardId}@{x.Location}"))}]";
        }

        return null;
    }

    /// <summary>
    /// `IsLocationFull(location, out isFull)` —— 20 调用点 / 17 张卡。
    ///
    /// 容量出处：`MatchEngine.HalfBoardFull` 的注释（半场 5、**含 HQ**）+ 前线
    /// `GameState.FrontlineCapacity`（默认 5）+ 手牌 `HandCapacity`（9）。
    /// `EmptyBoard` 里左半场已经有 HQ ⇒ 再放 4 个单位就满。
    /// </summary>
    private static string? LocationFullCapacity(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = DeploymentBoard(db);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = state.ById(1), Controller = Side.Left,
        };

        bool Full(int loc)
        {
            var v = engine.Api.InvokeByName("IsLocationFull", null, new object?[] { loc, null }, ctx, out bool h);
            if (!h)
            {
                throw new InvalidOperationException("IsLocationFull 没进派发表");
            }

            return v is true;
        }

        if (Full((int)CardLocation.BoardHqLeft))
        {
            return "左半场只有 HQ（1 格 / 上限 5）时不该判满";
        }

        // 半场放 4 个单位（+ HQ = 5）⇒ 满
        for (int k = 0; k < 4; k++)
        {
            PutOnBoard(state, unit, Side.Left, 30 + k, 1 + k);
        }

        if (!Full((int)CardLocation.BoardHqLeft))
        {
            return "半场 HQ(1) + 4 个单位 = 5 格，必须判满（上限 `GameState.HalfBoardCapacity`=5，含 HQ）";
        }

        if (Full((int)CardLocation.BoardFrontline))
        {
            return "半场满了**不代表**前线满 —— 两个上限各自独立（`MatchEngine.HalfBoardFull` 的注释已定案）";
        }

        // 前线放 5 个 ⇒ 满
        for (int k = 0; k < GameState.DefaultFrontlineCapacity; k++)
        {
            state.CreateWithId(unit, Side.Left, 50 + k, CardLocation.BoardFrontline, k);
        }

        if (!Full((int)CardLocation.BoardFrontline))
        {
            return $"前线放满 {GameState.DefaultFrontlineCapacity} 个之后必须判满";
        }

        if (Full((int)CardLocation.DeckLeft))
        {
            return "牌库没有容量上限，不该判满";
        }

        return null;
    }

    /// <summary>
    /// `DestroyMultipleCards(cardsToDestroy, destroyerCardID, out)` —— 20 调用点 / 19 张卡。
    ///
    /// 数组元素**两种形状**（卡对象 / 整数 cardID）都要认 ——
    /// 与 `DiscardCardFromHand` 同一类坑（见 `AsCardOrId` 的注释，94 张卡用整数形状攒数组）。
    /// </summary>
    private static string? DestroyMultipleCardsBothShapes(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        const string self = "card_unit_10_5_cm_lefh";
        if (db.Find(unit) is null || db.Find(self) is null)
        {
            return $"卡库里缺 {unit} / {self}";
        }

        var (engine, state) = DeploymentBoard(db);
        var actor = PutOnBoard(state, self, Side.Left, 20, 1);
        var v1 = PutOnBoard(state, unit, Side.Left, 21, 2);        // 卡对象形状
        var v2 = PutOnBoard(state, unit, Side.Left, 22, 3);        // 整数 cardID 形状
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = actor, Controller = Side.Left,
        };

        var list = new List<object?> { v1, v2.CardId };
        var result = engine.Api.InvokeByName("DestroyMultipleCards", actor,
            new object?[] { list, actor.CardId, null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `DestroyMultipleCards`（**修复前就是这个状态**）";
        }

        if (v1.Location != CardLocation.Discard)
        {
            return $"数组第 1 项（**卡对象**形状）必须被摧毁，实际 {v1.Location}";
        }

        if (v2.Location != CardLocation.Discard)
        {
            return $"数组第 2 项（**整数 cardID** 形状）必须被摧毁，实际 {v2.Location}"
                 + "（只认卡对象 ⇒ 整数 ID 被静默丢掉）";
        }

        if ((int)(result ?? -1) != 2)
        {
            return $"返回的摧毁张数应当是 2，实际 {result ?? "null"}";
        }

        return null;
    }

    /// <summary>
    /// `AddDefenseToMultipleCards(receiverIDs, amount, giverCardID, out qqq)`
    /// uses an integer ID array, applies each change once in input order, and
    /// sends targets that reach zero through the normal destruction path.
    /// </summary>
    private static string? AddDefenseToMultipleCards(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        const string actorName = "card_unit_10_5_cm_lefh";
        if (db.Find(unit) is null || db.Find(actorName) is null)
        {
            return $"卡库里缺 {unit} / {actorName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var actor = PutOnBoard(state, actorName, Side.Left, 20, 1);
        var first = PutOnBoard(state, unit, Side.Left, 21, 2);
        var second = PutOnBoard(state, unit, Side.Left, 22, 3);
        first.Defense = first.MaxDefense = 3;
        second.Defense = second.MaxDefense = 4;
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = actor, Controller = Side.Left,
        };

        var result = engine.Api.InvokeByName(
            "AddDefenseToMultipleCards", actor,
            new object?[] { new List<int> { second.CardId, first.CardId, 999999 }, 2, actor.CardId, null },
            ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `AddDefenseToMultipleCards`";
        }

        if (first.Defense != 5 || first.MaxDefense != 5
            || second.Defense != 6 || second.MaxDefense != 6)
        {
            return $"正向批量防御应按 ID 逐项 +2 且同步 MaxDefense，实际 first={first.Defense}/{first.MaxDefense} "
                 + $"second={second.Defense}/{second.MaxDefense}";
        }

        if ((int)(result ?? -1) != 0)
        {
            return $"qqq 是 Int 出参，当前应返回 0，实际 {result ?? "null"}";
        }

        var doomed = PutOnBoard(state, unit, Side.Left, 23, 4);
        doomed.Defense = doomed.MaxDefense = 1;
        result = engine.Api.InvokeByName(
            "AddDefenseToMultipleCards", actor,
            new object?[] { new List<int> { doomed.CardId, 999999 }, -2, actor.CardId, null },
            ctx, out handled);
        if (!handled)
        {
            return "负值调用未进入 `AddDefenseToMultipleCards` 派发";
        }

        if (doomed.Location != CardLocation.Discard)
        {
            return $"防御 1 受 -2 后必须被摧毁，实际位置={doomed.Location} 防御={doomed.Defense}";
        }

        return null;
    }

    private static string? RemoveAlpine(CardDatabase db)
    {
        const string targetName = "card_unit_2nd_parachute";
        const string sourceName = "card_unit_10_5_cm_lefh";
        if (db.Find(targetName) is null || db.Find(sourceName) is null)
        {
            return $"卡库里缺 {targetName} / {sourceName}";
        }

        var (engine, state) = DeploymentBoard(db);
        var sourceA = PutOnBoard(state, sourceName, Side.Left, 20, 1);
        var sourceB = PutOnBoard(state, sourceName, Side.Left, 21, 2);
        var target = PutOnBoard(state, targetName, Side.Left, 22, 3);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = sourceA, Controller = Side.Left,
        };

        void Give(int giverId)
        {
            engine.Api.InvokeByName("GiveAlpine", sourceA,
                new object?[] { target.CardId, null, giverId }, ctx, out bool handled);
            if (!handled)
            {
                throw new InvalidOperationException("GiveAlpine 未进入派发表");
            }
        }

        void Remove(int giverId)
        {
            engine.Api.InvokeByName("RemoveAlpine", sourceA,
                new object?[] { target.CardId, null, false, giverId }, ctx, out bool handled);
            if (!handled)
            {
                throw new InvalidOperationException("RemoveAlpine 未进入派发表");
            }
        }

        Give(sourceA.CardId);
        Give(sourceB.CardId);
        if (!target.Keywords.Contains(Keyword.Alpine))
        {
            return "GiveAlpine 后目标必须拥有 Alpine";
        }

        Remove(sourceA.CardId);
        if (!target.Keywords.Contains(Keyword.Alpine))
        {
            return "移除一个来源后仍有另一来源，不能摘掉 Alpine";
        }

        Remove(sourceB.CardId);
        if (target.Keywords.Contains(Keyword.Alpine))
        {
            return "移除最后一个来源后必须摘掉动态 Alpine";
        }

        var innateDefinition = db.All.FirstOrDefault(d => d.Keywords.Contains(Keyword.Alpine));
        if (innateDefinition is not null)
        {
            var innate = state.CreateWithId(innateDefinition.Name, Side.Left, 23,
                CardLocation.BoardHqLeft, 4);
            Give(sourceA.CardId);
            // Reuse the target variable's call path for an innate card by invoking
            // the primitive directly with the new card ID.
            engine.Api.InvokeByName("GiveAlpine", sourceA,
                new object?[] { innate.CardId, null, sourceA.CardId }, ctx, out _);
            engine.Api.InvokeByName("RemoveAlpine", sourceA,
                new object?[] { innate.CardId, null, false, sourceA.CardId }, ctx, out _);
            if (!innate.Keywords.Contains(Keyword.Alpine))
            {
                return "卡面自带 Alpine 不应被 RemoveAlpine 摘掉";
            }
        }

        return null;
    }

    private static string? MobilizeEvents(CardDatabase db)
    {
        const string guardUnitName = "card_unit_48e_regiment";
        const string repairUnitName = "card_unit_43e_regiment_motorise";
        const string sourceName = "card_unit_10_5_cm_lefh";
        const string damagedName = "card_unit_2nd_parachute";
        foreach (string name in new[] { guardUnitName, repairUnitName, sourceName, damagedName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库里缺 {name}";
            }
        }

        var (engine, state) = DeploymentBoard(db);
        var source = PutOnBoard(state, sourceName, Side.Left, 20, 1);
        var guardUnit = PutOnBoard(state, guardUnitName, Side.Left, 21, 2);
        var repairUnit = PutOnBoard(state, repairUnitName, Side.Left, 22, 3);
        var damaged = PutOnBoard(state, damagedName, Side.Left, 23, 4);
        damaged.Defense = Math.Max(1, damaged.MaxDefense - 2);

        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        object? Invoke(string name, CardInstance target)
        {
            var result = engine.Api.InvokeByName(
                name,
                source,
                new object?[] { target.CardId, source.CardId, null },
                ctx,
                out bool handled);
            if (!handled)
            {
                throw new InvalidOperationException($"{name} 未进入派发表");
            }

            return result;
        }

        // Removing innate Mobilize must run OnLoseMobilize, which grants Guard
        // to the 48e regiment. Re-adding it runs OnGainMobilize and removes it.
        Invoke("RemoveMobilize", guardUnit);
        if (guardUnit.Keywords.Contains(Keyword.Mobilize)
            || !guardUnit.Keywords.Contains(Keyword.Guard))
        {
            return "48e Regiment 失去 Mobilize 后应获得 Guard";
        }

        Invoke("GiveMobilize", guardUnit);
        if (!guardUnit.Keywords.Contains(Keyword.Mobilize)
            || guardUnit.Keywords.Contains(Keyword.Guard))
        {
            return "48e Regiment 获得 Mobilize 后应移除 Guard";
        }

        Invoke("GiveMobilize", guardUnit);
        if (engine.Api.JsonGetIntArray(guardUnit, "mobilizeGivers").Count != 1)
        {
            return "同一来源重复 GiveMobilize 不应重复记录来源";
        }

        // 43e Regiment's OnLoseMobilize repairs every friendly unit.
        Invoke("RemoveMobilize", repairUnit);
        if (damaged.Defense != damaged.MaxDefense)
        {
            return $"43e Regiment 失去 Mobilize 后应修复友军，实际 {damaged.Defense}/{damaged.MaxDefense}";
        }

        return null;
    }

    private static string? MobilizeStartTurnBonus(CardDatabase db)
    {
        const string mobilizeName = "card_unit_2nd_parachute";
        const string plainName = "card_unit_10_5_cm_lefh";
        if (db.Find(mobilizeName) is null || db.Find(plainName) is null)
        {
            return $"卡库缺少测试卡 {mobilizeName} 或 {plainName}";
        }

        var (engine, state) = EmptyBoard(db);
        var active = state.CreateWithId(mobilizeName, Side.Left, 710, CardLocation.BoardHqLeft, 1);
        var activePlain = state.CreateWithId(plainName, Side.Left, 711, CardLocation.BoardHqLeft, 2);
        var activeHand = state.CreateWithId(mobilizeName, Side.Left, 712, CardLocation.HandLeft, 0);
        var inactive = state.CreateWithId(mobilizeName, Side.Right, 713, CardLocation.BoardHqRight, 1);

        int activeAttack = active.Attack;
        int activeDefense = active.Defense;
        int plainAttack = activePlain.Attack;
        int plainDefense = activePlain.Defense;
        int handAttack = activeHand.Attack;
        int handDefense = activeHand.Defense;
        int inactiveAttack = inactive.Attack;
        int inactiveDefense = inactive.Defense;

        engine.StartTurn(Side.Left, draw: false);

        if (active.Attack != activeAttack + 1 || active.Defense != activeDefense + 1
            || activePlain.Attack != plainAttack || activePlain.Defense != plainDefense
            || activeHand.Attack != handAttack || activeHand.Defense != handDefense
            || inactive.Attack != inactiveAttack || inactive.Defense != inactiveDefense)
        {
            return $"左方回合应只给左方在场 Mobilize +1/+1，实际 active={active.Attack}/{active.Defense}、"
                 + $"plain={activePlain.Attack}/{activePlain.Defense}、hand={activeHand.Attack}/{activeHand.Defense}、"
                 + $"inactive={inactive.Attack}/{inactive.Defense}";
        }

        if (!active.BuffsBySource.TryGetValue((active.CardId, false), out var buff)
            || buff.Attack != 1 || buff.Defense != 1)
        {
            return "Mobilize 回合增益应记录为该单位自身来源的永久 +1/+1";
        }

        engine.StartTurn(Side.Right, draw: false);
        if (inactive.Attack != inactiveAttack + 1 || inactive.Defense != inactiveDefense + 1)
        {
            return $"右方回合应给右方在场 Mobilize +1/+1，实际 {inactive.Attack}/{inactive.Defense}";
        }

        engine.StartTurn(Side.Left, draw: false);
        return active.Attack == activeAttack + 2 && active.Defense == activeDefense + 2
            ? null
            : $"同一单位第二次成为活动方时应再次获得 +1/+1，实际 {active.Attack}/{active.Defense}";
    }

    private static string? MobilizeLostOnDamage(CardDatabase db)
    {
        const string targetName = "card_unit_2nd_parachute";
        const string sourceName = "card_unit_10_5_cm_lefh";
        foreach (string name in new[] { targetName, sourceName })
        {
            if (db.Find(name) is null)
            {
                return $"卡库缺少测试卡 {name}";
            }
        }

        // Effect damage uses the same ApplyDamageToCard path as combat, but
        // bypasses Heavy Armor. Any positive resolved hit must remove Mobilize.
        {
            var (engine, state) = DeploymentBoard(db);
            var target = PutOnBoard(state, targetName, Side.Right, 720, 1);
            var source = PutOnBoard(state, sourceName, Side.Left, 721, 2);
            var context = new EffectContext
            {
                Engine = engine,
                State = state,
                Self = source,
                Controller = Side.Left,
            };

            engine.Api.InvokeByName("DamageCard", source,
                new object?[] { target, 1, source, false, false, false }, context, out bool handled);
            if (!handled || target.Keywords.Contains(Keyword.Mobilize))
            {
                return "受到 1 点效果伤害后应失去 Mobilize";
            }
        }

        // Combat damage reduced to zero by Heavy Armor is not a hit and must
        // preserve Mobilize; a later positive combat hit must remove it.
        {
            const string armoredName = "card_unit_maus";
            const string attackerName = "card_unit_panzer_ii_a";
            if (db.Find(armoredName) is null || db.Find(attackerName) is null)
            {
                return $"卡库缺少测试卡 {armoredName} 或 {attackerName}";
            }

            var (engine, attacker, target) = FightBoard(db, attackerName, armoredName, 3, 9, 0, 8);
            target.Keywords.Add(Keyword.Mobilize);
            if (!engine.Attack(attacker, target, out string reason))
            {
                return $"重甲零伤害前提下攻击失败：{reason}";
            }

            if (!target.Keywords.Contains(Keyword.Mobilize))
            {
                return "重甲完全吸收战斗伤害时应保留 Mobilize";
            }

            var (positiveEngine, positiveAttacker, positiveTarget) =
                FightBoard(db, attackerName, armoredName, 6, 9, 0, 8);
            positiveTarget.Keywords.Add(Keyword.Mobilize);
            if (!positiveEngine.Attack(positiveAttacker, positiveTarget, out reason))
            {
                return $"正值战斗伤害前提下攻击失败：{reason}";
            }

            if (positiveTarget.Keywords.Contains(Keyword.Mobilize))
            {
                return "重甲减伤后仍有正值战斗伤害时应失去 Mobilize";
            }
        }

        return null;
    }

    /// <summary>
    /// `MoveMultipleCardsToTopOfOwnersDeck` 的批量顺序回归。
    ///
    /// 蓝图逐项调用单卡移动，故 [leftA,leftB] 结算后应为 leftB、leftA、其余卡；
    /// 另一方牌库不应被触碰。空数组是成功的 no-op，无效 ID 则报告失败但不改变局面。
    /// </summary>
    private static string? MoveMultipleCardsToTopOfOwnersDeck(CardDatabase db)
    {
        const string cardName = "card_unit_2nd_parachute";
        const string actorName = "card_unit_10_5_cm_lefh";
        if (db.Find(cardName) is null || db.Find(actorName) is null)
        {
            return $"卡库里缺 {cardName} / {actorName}";
        }

        var (engine, state) = EmptyBoard(db);
        var actor = PutOnBoard(state, actorName, Side.Left, 20, 1);
        var leftA = state.CreateWithId(cardName, Side.Left, 21, CardLocation.DeckLeft, 2);
        var leftB = state.CreateWithId(cardName, Side.Left, 22, CardLocation.DeckLeft, 3);
        var leftRest = state.CreateWithId(cardName, Side.Left, 23, CardLocation.DeckLeft, 0);
        var right = state.CreateWithId(cardName, Side.Right, 31, CardLocation.DeckRight, 0);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = actor,
            Controller = Side.Left,
        };

        var result = engine.Api.InvokeByName(
            "MoveMultipleCardsToTopOfOwnersDeck",
            actor,
            new object?[] { new List<int> { leftA.CardId, leftB.CardId }, actor.CardId, 0, null },
            ctx,
            out bool handled);

        if (!handled)
        {
            return "派发表里没有 `MoveMultipleCardsToTopOfOwnersDeck`";
        }

        if (result is not true)
        {
            return $"两个有效 ID 批量移动应返回 true，实际 {result ?? "null"}";
        }

        var leftDeck = state.Deck(Side.Left).ToList();
        if (!leftDeck.SequenceEqual(new[] { leftB, leftA, leftRest }))
        {
            return "批量移动后的左侧牌库顺序不对：应为 [后输入卡, 先输入卡, 原有卡]";
        }

        if (right.Location != CardLocation.DeckRight || right.LocationNumber != 0)
        {
            return "批量移动不应改变另一拥有者的牌库";
        }

        var empty = engine.Api.InvokeByName(
            "MoveMultipleCardsToTopOfOwnersDeck",
            actor,
            new object?[] { new List<int>(), actor.CardId, 0, null },
            ctx,
            out _);
        if (empty is not true)
        {
            return $"空数组应是成功 no-op，实际 {empty ?? "null"}";
        }

        var invalid = engine.Api.InvokeByName(
            "MoveMultipleCardsToTopOfOwnersDeck",
            actor,
            new object?[] { new List<int> { 999999 }, actor.CardId, 0, null },
            ctx,
            out _);
        if (invalid is not false)
        {
            return $"无效 ID 应返回 false，实际 {invalid ?? "null"}";
        }

        if (!state.Deck(Side.Left).SequenceEqual(new[] { leftB, leftA, leftRest }))
        {
            return "无效 ID 不应改变已有牌库顺序";
        }

        return null;
    }

    /// <summary>
    /// `PlayCardDirectlyFromHand` 的最小端到端回归：
    /// 不扣 kredit、不要求当前行动方；单位可直接进前线；显式 locationNumber
    /// 参与目标位置排序；无效卡回写 false。
    /// </summary>
    private static string? PlayCardDirectlyFromHand(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var unitDef = db.Find("card_unit_arado_ar_196");
        var gotchaDef = db.Find("card_event_interception");
        if (unitDef is null || gotchaDef is null)
        {
            return "卡库里缺 card_unit_arado_ar_196 / card_event_interception";
        }

        state.SetKredits(Side.Left, 0);
        state.SetKredits(Side.Right, 0);
        var existing = state.CreateWithId(unitDef.Name, Side.Left, 2,
            CardLocation.BoardHqLeft, 2);
        var support = state.CreateWithId(unitDef.Name, Side.Left, 3,
            CardLocation.HandLeft, 0);
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = state.Hq(Side.Left),
            Controller = Side.Left,
        };

        var result = engine.Api.InvokeByName(
            "PlayCardDirectlyFromHand", support,
            new object?[] { support, false, 9001, null, 1 },
            ctx, out bool handled);
        if (!handled || result is not true)
        {
            return $"免费从手牌直接打出应成功且 handled=true，实际 handled={handled}, result={result ?? "null"}";
        }

        if (support.Location != CardLocation.BoardHqLeft || support.LocationNumber != 1
            || existing.LocationNumber != 2)
        {
            return $"显式 locationNumber=1 未按槽位排序：support={support.Location}/{support.LocationNumber}, "
                 + $"existing={existing.LocationNumber}";
        }

        if (state.Kredits(Side.Left) != 0)
        {
            return "直接出牌不应扣除 kredit";
        }

        var frontExisting = state.CreateWithId(unitDef.Name, Side.Right, 43,
            CardLocation.BoardFrontline, 0);
        var frontline = state.CreateWithId(unitDef.Name, Side.Right, 42,
            CardLocation.HandRight, 0);
        var frontlineResult = engine.Api.InvokeByName(
            "PlayCardDirectlyFromHand", frontline,
            new object?[] { frontline, true, 9002, null, 1 },
            ctx, out bool frontlineHandled);
        if (!frontlineHandled || frontlineResult is not true
            || frontline.Location != CardLocation.BoardFrontline
            || frontline.LocationNumber != 1 || frontExisting.LocationNumber != 0)
        {
            return $"非当前行动方单位应能直接进前线：handled={frontlineHandled}, result={frontlineResult ?? "null"}, "
                 + $"location={frontline.Location}/{frontline.LocationNumber}, existing={frontExisting.LocationNumber}";
        }

        var invalid = engine.Api.InvokeByName(
            "PlayCardDirectlyFromHand", frontline,
            new object?[] { frontline, false, 9003, null, -1 },
            ctx, out bool invalidHandled);
        if (!invalidHandled || invalid is not false)
        {
            return $"不在手牌的卡应回写 qqq=false：handled={invalidHandled}, result={invalid ?? "null"}";
        }

        var gotcha = state.CreateWithId(gotchaDef.Name, Side.Left, 4,
            CardLocation.HandLeft, 1);
        var gotchaResult = engine.Api.InvokeByName(
            "PlayCardDirectlyFromHand", gotcha,
            new object?[] { gotcha, false, 9004, null, -1 },
            ctx, out bool gotchaHandled);
        if (!gotchaHandled || gotchaResult is not true || gotcha.GotchaActivated != 1)
        {
            return $"直接出牌必须接通 Gotcha 激活序号：handled={gotchaHandled}, result={gotchaResult ?? "null"}, "
                 + $"activated={gotcha.GotchaActivated}";
        }

        return null;
    }

    /// <summary>
    /// `MoveUnitFromSupportToFrontLine(card, instigatorID, out qqq)` 的效果位移回归：
    /// 不要求当前行动方、不扣油费、不消耗本回合移动额度，也不受召唤失调/压制影响；
    /// `cantMove`、敌方占据前线和我方前线已满时必须拒绝并回写 false。
    /// </summary>
    private static string? MoveUnitFromSupportToFrontLine(CardDatabase db)
    {
        const string unitName = "card_unit_arado_ar_196";
        if (db.Find(unitName) is null)
        {
            return $"卡库里缺 {unitName}";
        }

        static EffectContext Context(MatchEngine engine, GameState state, CardInstance self) => new()
        {
            Engine = engine,
            State = state,
            Self = self,
            Controller = self.Owner,
        };

        // ① 免费效果位移：即使不是当前行动方、已移动/已攻击、本回合刚进场且被压制，
        //    也应当被送入前线；kredit 和 HasMovedThisTurn 均保持不变。
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Right, 0);
            var unit = state.CreateWithId(unitName, Side.Right, 42,
                CardLocation.BoardHqRight, 0);
            unit.HasMovedThisTurn = true;
            unit.HasAttackedThisTurn = true;
            unit.EnteredPlayOnTurn = state.Turn;
            unit.Keywords.Add(Keyword.Pinned);
            var result = engine.Api.InvokeByName(
                "MoveUnitFromSupportToFrontLine", unit,
                new object?[] { unit, 9001 }, Context(engine, state, unit), out bool handled);

            if (!handled || result is not true)
            {
                return $"合法效果位移应成功且 handled=true，实际 handled={handled}, result={result ?? "null"}";
            }

            if (unit.Location != CardLocation.BoardFrontline
                || state.FrontlineOwner != Side.Right)
            {
                return $"效果位移应进入右侧前线，实际 location={unit.Location}, owner={state.FrontlineOwner}";
            }

            if (state.Kredits(Side.Right) != 0 || !unit.HasMovedThisTurn)
            {
                return $"效果位移不应扣油费或清除既有移动标记：kredit={state.Kredits(Side.Right)}, "
                     + $"hasMoved={unit.HasMovedThisTurn}";
            }
        }

        // ② cantMove 是这条蓝图路径唯一的单位能力拒绝门，失败不得改变位置。
        {
            var (engine, state) = EmptyBoard(db);
            var unit = state.CreateWithId(unitName, Side.Left, 2,
                CardLocation.BoardHqLeft, 0);
            unit.CustomAbility = "cantMove";
            var result = engine.Api.InvokeByName(
                "MoveUnitFromSupportToFrontLine", unit,
                new object?[] { unit, 9002 }, Context(engine, state, unit), out bool handled);
            if (!handled || result is not false || unit.Location != CardLocation.BoardHqLeft)
            {
                return $"cantMove 目标应被拒绝且 qqq=false：handled={handled}, result={result ?? "null"}, "
                     + $"location={unit.Location}";
            }
        }

        // ③ 敌方占据前线时，另一方不能借效果越过互斥门。
        {
            var (engine, state) = EmptyBoard(db);
            var enemy = state.CreateWithId(unitName, Side.Right, 42,
                CardLocation.BoardHqRight, 0);
            state.Move(enemy, CardLocation.BoardFrontline, 0);
            var unit = state.CreateWithId(unitName, Side.Left, 2,
                CardLocation.BoardHqLeft, 0);
            var result = engine.Api.InvokeByName(
                "MoveUnitFromSupportToFrontLine", unit,
                new object?[] { unit, 9003 }, Context(engine, state, unit), out bool handled);
            if (!handled || result is not false || unit.Location != CardLocation.BoardHqLeft)
            {
                return $"对方占据前线时应拒绝且 qqq=false：handled={handled}, result={result ?? "null"}, "
                     + $"location={unit.Location}";
            }
        }

        // ④ 我方前线达到容量后，第六个单位必须被拒绝。
        {
            var (engine, state) = EmptyBoard(db);
            for (int i = 0; i < GameState.DefaultFrontlineCapacity; i++)
            {
                var occupied = state.CreateWithId(unitName, Side.Left, 2 + i,
                    CardLocation.BoardHqLeft, i);
                state.Move(occupied, CardLocation.BoardFrontline, i);
            }

            var extra = state.CreateWithId(unitName, Side.Left, 20,
                CardLocation.BoardHqLeft, 0);
            var result = engine.Api.InvokeByName(
                "MoveUnitFromSupportToFrontLine", extra,
                new object?[] { extra, 9004 }, Context(engine, state, extra), out bool handled);
            if (!handled || result is not false
                || extra.Location != CardLocation.BoardHqLeft
                || state.Cards(Side.Left, CardLocation.BoardFrontline).Count
                    != GameState.DefaultFrontlineCapacity)
            {
                return $"我方前线已满时应拒绝且保持容量：handled={handled}, result={result ?? "null"}, "
                     + $"location={extra.Location}, count={state.Cards(Side.Left, CardLocation.BoardFrontline).Count}";
            }
        }

        return null;
    }

    private static string? ForceCardChangeLocation(CardDatabase db)
    {
        const string unitName = "card_unit_arado_ar_196";
        if (db.Find(unitName) is null)
        {
            return $"卡库里缺 {unitName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_event_air_land_sea", Side.Left, 2,
            CardLocation.HandLeft, 0);
        var target = state.CreateWithId(unitName, Side.Right, 42,
            CardLocation.BoardHqRight, 1);
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        var result = engine.Api.InvokeByName("ForceCardChangeLocation", source,
            new object?[] { target.CardId, source.CardId, (int)CardLocation.BoardFrontline, 0, null, null, null },
            ctx, out bool handled);
        if (!handled || result is not object?[] outs || outs.Length < 3
            || outs[0] is not true || Convert.ToInt32(outs[1]) != (int)CardLocation.BoardHqRight
            || Convert.ToInt32(outs[2]) != 1
            || target.Location != CardLocation.BoardFrontline || target.LocationNumber != 0)
        {
            return $"移动应成功并回写旧位置：handled={handled}, result={result ?? "null"}, "
                 + $"target={target.Location}/{target.LocationNumber}";
        }

        var noOp = engine.Api.InvokeByName("ForceCardChangeLocation", source,
            new object?[] { target.CardId, source.CardId, (int)CardLocation.BoardFrontline, 0, null, null, null },
            ctx, out bool noOpHandled);
        if (!noOpHandled || noOp is not object?[] noOpOuts || noOpOuts.Length < 3
            || noOpOuts[0] is not false || target.Location != CardLocation.BoardFrontline)
        {
            return $"相同位置/槽位应 no-op：handled={noOpHandled}, result={noOp ?? "null"}, "
                 + $"target={target.Location}/{target.LocationNumber}";
        }

        target.CustomAbility = "cantMove";
        var blocked = engine.Api.InvokeByName("ForceCardChangeLocation", source,
            new object?[] { target.CardId, source.CardId, (int)CardLocation.BoardHqRight, 1, null, null, null },
            ctx, out bool blockedHandled);
        if (!blockedHandled || blocked is not object?[] blockedOuts || blockedOuts.Length < 3
            || blockedOuts[0] is not false || target.Location != CardLocation.BoardFrontline)
        {
            return $"cantMove 应拒绝且不改位置：handled={blockedHandled}, result={blocked ?? "null"}, "
                 + $"target={target.Location}/{target.LocationNumber}";
        }

        return null;
    }

    private static string? SpawnMultipleCardsOnBattlefield(CardDatabase db)
    {
        const string unitName = "card_unit_arado_ar_196";
        if (db.Find(unitName) is null)
        {
            return $"卡库里缺 {unitName}";
        }

        var (engine, state) = EmptyBoard(db);
        var spawner = state.CreateWithId(unitName, Side.Left, 2,
            CardLocation.HandLeft, 0, isGold: true);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = spawner, Controller = Side.Left,
        };
        var names = new List<string> { unitName, unitName, unitName, unitName, unitName };
        var result = engine.Api.InvokeByName("SpawnMultipleCardsOnBattlefield", spawner,
            new object?[] { (int)Side.Left, false, names, spawner.CardId, true, null, true },
            ctx, out bool handled);

        if (!handled || result is not List<int> ids)
        {
            return $"派发表缺少 SpawnMultipleCardsOnBattlefield 或出参类型错误：handled={handled}, result={result ?? "null"}";
        }

        // 左方 HQ 占半场 1 格，所以 5 张请求只能实际生成 4 张。
        if (ids.Count != 4)
        {
            return $"半场满位前应生成 4 张，实际 {ids.Count} 张：{string.Join(",", ids)}";
        }

        var spawned = ids.Select(state.ById).ToList();
        if (spawned.Any(card => card is null
                || card.Location != CardLocation.BoardHqLeft
                || !card.IsGold
                || !card.Keywords.Contains(Keyword.Blitz)
                || !card.Keywords.Contains(Keyword.Veteran)))
        {
            return "生成卡必须按顺序落在左方半场，并继承金卡、Blitz 和 Veteran 状态";
        }

        if (state.Cards(Side.Left, CardLocation.BoardHqLeft).Count != 5)
        {
            return $"半场应包含 HQ + 4 张生成卡，实际 {state.Cards(Side.Left, CardLocation.BoardHqLeft).Count} 张";
        }

        var noSpawner = engine.Api.InvokeByName("SpawnMultipleCardsOnBattlefield", spawner,
            new object?[] { (int)Side.Left, false, names, 0, false, null, false },
            ctx, out bool noSpawnerHandled);
        if (!noSpawnerHandled || noSpawner is not List<int> empty || empty.Count != 0)
        {
            return "spawnerID<=0 时应返回空数组且不生成卡";
        }

        return null;
    }

    private static string? SpawnNextToCard(CardDatabase db)
    {
        const string spawnerName = "card_unit_arado_ar_196";
        const string spawnedName = "card_unit_brewster_f2a";
        if (db.Find(spawnerName) is null || db.Find(spawnedName) is null)
        {
            return $"卡库里缺 {spawnerName} 或 {spawnedName}";
        }

        var (engine, state) = EmptyBoard(db);
        var spawner = state.CreateWithId(spawnerName, Side.Left, 100,
            CardLocation.BoardFrontline, 2, isGold: true);
        spawner.Attack = 11;
        var existing = state.CreateWithId(spawnerName, Side.Left, 101,
            CardLocation.BoardFrontline, 3);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = spawner, Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("SpawnNextToCard", spawner,
            new object?[] { spawnedName, spawner.CardId, (int)Side.Left, null, true, false, 0, true },
            ctx, out bool handled);
        if (!handled || result is not int spawnedId || spawnedId <= 0)
        {
            return $"SpawnNextToCard 未进入派发表或未返回 spawnedCardID：handled={handled}, result={result ?? "null"}";
        }

        var spawned = state.ById(spawnedId);
        if (spawned is null
            || spawned.Location != CardLocation.BoardFrontline
            || spawned.LocationNumber != 3
            || !spawned.IsGold
            || !spawned.Keywords.Contains(Keyword.Veteran)
            || spawned.Attack != spawner.Attack)
        {
            return $"生成卡应在前线位置 3、继承金卡/老兵并复制攻击力，实际="
                 + $"{spawned?.Location}/{spawned?.LocationNumber}, gold={spawned?.IsGold}, "
                 + $"veteran={spawned?.Keywords.Contains(Keyword.Veteran)}, attack={spawned?.Attack}";
        }

        if (existing.LocationNumber != 4)
        {
            return $"插入相邻卡后原有位置应顺延到 4，实际 {existing.LocationNumber}";
        }

        return null;
    }

    private static string? ResetUnitOperations(CardDatabase db)
    {
        const string furyName = "card_unit_111th_indian_brigade";
        const string plainName = "card_unit_arado_ar_196";
        const string orderName = "card_event_rain2_deluge";
        if (db.Find(furyName) is null || db.Find(plainName) is null || db.Find(orderName) is null)
        {
            return $"卡库里缺 {furyName}、{plainName} 或 {orderName}";
        }

        var (engine, state) = EmptyBoard(db);
        var fury = state.CreateWithId(furyName, Side.Left, 100,
            CardLocation.BoardHqLeft, 1);
        fury.EnteredPlayOnTurn = -1;
        fury.HasMovedThisTurn = true;
        fury.HasAttackedThisTurn = true;
        fury.AttacksThisTurn = 2;
        fury.OperationsUsedThisTurn = 7;

        var plain = state.CreateWithId(plainName, Side.Left, 101,
            CardLocation.BoardHqLeft, 2);
        plain.EnteredPlayOnTurn = -1;
        plain.HasMovedThisTurn = true;
        plain.HasAttackedThisTurn = true;
        plain.AttacksThisTurn = 1;

        var handUnit = state.CreateWithId(plainName, Side.Left, 102,
            CardLocation.HandLeft, 0);
        handUnit.HasMovedThisTurn = true;
        handUnit.HasAttackedThisTurn = true;
        handUnit.AttacksThisTurn = 1;

        var order = state.CreateWithId(orderName, Side.Left, 103,
            CardLocation.BoardHqLeft, 3);
        order.HasMovedThisTurn = true;
        order.HasAttackedThisTurn = true;
        order.AttacksThisTurn = 1;

        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = fury, Controller = Side.Left,
        };

        object? result = engine.Api.InvokeByName("ResetUnitOperations", fury,
            new object?[] { fury.CardId, fury.CardId, null }, ctx, out bool handled);
        if (!handled || result is not null)
        {
            return $"ResetUnitOperations 未正确进入派发表：handled={handled}, result={result ?? "null"}";
        }

        if (fury.HasMovedThisTurn || fury.HasAttackedThisTurn || fury.AttacksThisTurn != 0
            || fury.OperationsUsedThisTurn != 7 || !fury.HasAttackLeft)
        {
            return $"Fury 单位应恢复移动/攻击额度但保留操作计数：move={fury.HasMovedThisTurn}, "
                 + $"attack={fury.HasAttackedThisTurn}, attacks={fury.AttacksThisTurn}, "
                 + $"ops={fury.OperationsUsedThisTurn}, left={fury.HasAttackLeft}";
        }

        engine.Api.InvokeByName("ResetUnitOperations", plain,
            new object?[] { plain.CardId, fury.CardId, null }, ctx, out bool plainHandled);
        if (!plainHandled || plain.HasMovedThisTurn || plain.HasAttackedThisTurn
            || plain.AttacksThisTurn != 0 || !plain.HasAttackLeft)
        {
            return "普通棋盘单位应恢复为一次可攻击且可移动";
        }

        engine.Api.InvokeByName("ResetUnitOperations", handUnit,
            new object?[] { handUnit.CardId, fury.CardId, null }, ctx, out bool handHandled);
        engine.Api.InvokeByName("ResetUnitOperations", order,
            new object?[] { order.CardId, fury.CardId, null }, ctx, out bool orderHandled);
        if (!handHandled || !orderHandled
            || !handUnit.HasMovedThisTurn || !handUnit.HasAttackedThisTurn || handUnit.AttacksThisTurn != 1
            || !order.HasMovedThisTurn || !order.HasAttackedThisTurn || order.AttacksThisTurn != 1)
        {
            return "非棋盘单位或非单位目标不应被 ResetUnitOperations 改写";
        }

        return null;
    }

    private static string? GiveRandomCombatKeyword(CardDatabase db)
    {
        const string unitName = "card_unit_arado_ar_196";
        if (db.Find(unitName) is null)
        {
            return $"卡库里缺 {unitName}";
        }

        // GetCombatKeywords must expose the enum values, not CardInstance objects,
        // and must write both Blueprint out parameters.
        {
            var (engine, state) = EmptyBoard(db);
            var target = PutOnBoard(state, unitName, Side.Left, 100, 1);
            var ctx = new EffectContext { Engine = engine, State = state, Self = target, Controller = Side.Left };
            var raw = engine.Api.InvokeByName("GetCombatKeywords", target,
                new object?[] { null, null }, ctx, out bool handled);
            if (!handled || raw is not object?[] outputs
                || outputs.Length != 2 || outputs[0] is not List<int> keywords
                || outputs[1] is not int count || count != keywords.Count
                || keywords.Count != 0)
            {
                return $"GetCombatKeywords 应返回 [ECombatKeyword[], count]，实际 handled={handled}, "
                     + $"result={raw ?? "null"}";
            }

            var random = engine.Api.InvokeByName("GiveRandomCombatKeyword", target,
                new object?[] { target.CardId, target.CardId, null, null }, ctx, out bool randomHandled);
            if (!randomHandled || random is not object?[] randomOutputs
                || randomOutputs.Length != 2 || Convert.ToInt32(randomOutputs[0] ?? -1) != 0
                || randomOutputs[1] is not false)
            {
                return "GiveRandomCombatKeyword 的成功路径必须保留蓝图 out=0/false 的实际行为";
            }

            string[] combatKeywords =
            [Keyword.Ambush, Keyword.Blitz, Keyword.Fury, Keyword.Guard,
             Keyword.HeavyArmor, Keyword.Shock, Keyword.Smokescreen];
            if (!target.Keywords.Any(combatKeywords.Contains))
            {
                return "GiveRandomCombatKeyword 成功调用没有授予任何战斗关键字";
            }
        }

        // Guard targets must never receive Smokescreen, even when it is the only
        // remaining combat keyword allowed by the random candidate filter.
        {
            var (engine, state) = EmptyBoard(db);
            var target = PutOnBoard(state, unitName, Side.Left, 101, 1);
            engine.Api.GiveKeyword(target, Keyword.Guard);
            var ctx = new EffectContext { Engine = engine, State = state, Self = target, Controller = Side.Left };
            engine.Api.InvokeByName("GiveRandomCombatKeyword", target,
                new object?[] { target.CardId, target.CardId, null, null }, ctx, out bool handled);
            if (!handled || target.Keywords.Contains(Keyword.Smokescreen))
            {
                return "已有 Guard 的目标不应从 GiveRandomCombatKeyword 获得 Smokescreen";
            }
        }

        // With every enum value already present there is no candidate and no
        // state mutation.
        {
            var (engine, state) = EmptyBoard(db);
            var target = PutOnBoard(state, unitName, Side.Left, 102, 1);
            foreach (string keyword in new[]
                     {
                         Keyword.Ambush, Keyword.Blitz, Keyword.Fury, Keyword.Guard,
                         Keyword.HeavyArmor, Keyword.Shock, Keyword.Smokescreen,
                     })
            {
                engine.Api.GiveKeyword(target, keyword);
            }

            var ctx = new EffectContext { Engine = engine, State = state, Self = target, Controller = Side.Left };
            var before = target.Keywords.Count;
            var result = engine.Api.InvokeByName("GiveRandomCombatKeyword", target,
                new object?[] { target.CardId, target.CardId, null, null }, ctx, out bool handled);
            if (!handled || result is not object?[] outputs
                || Convert.ToInt32(outputs[0] ?? -1) != 0 || outputs[1] is not false
                || target.Keywords.Count != before)
            {
                return "无可用战斗关键字时应返回 0/false 且不改变目标";
            }
        }

        return null;
    }

    private static string? BlueprintSetPrimitives(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 103,
            CardLocation.BoardHqLeft, 1);
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };
        var set = new HashSet<object?> { 1 };

        engine.Api.InvokeByName("Set_Add", null, new object?[] { set, 2 }, ctx, out bool addHandled);
        engine.Api.InvokeByName("Set_Add", null, new object?[] { set, 2 }, ctx, out bool duplicateHandled);
        var contains = engine.Api.InvokeByName("Set_Contains", null,
            new object?[] { set, 2 }, ctx, out bool containsHandled);
        var length = engine.Api.InvokeByName("Set_Length", null,
            new object?[] { set }, ctx, out bool lengthHandled);
        var array = engine.Api.InvokeByName("Set_ToArray", null,
            new object?[] { set, null }, ctx, out bool arrayHandled);

        if (!addHandled || !duplicateHandled || !containsHandled || !lengthHandled || !arrayHandled
            || contains is not true || length is not 2
            || array is not List<object?> values || values.Count != 2
            || !values.Contains(1) || !values.Contains(2))
        {
            return "Set_Add/Contains/Length/ToArray 的集合语义不正确";
        }

        var removed = engine.Api.InvokeByName("Set_Remove", null,
            new object?[] { set, 1 }, ctx, out bool removeHandled);
        if (!removeHandled || removed is not true || set.Count != 1 || set.Contains(1))
        {
            return "Set_Remove 应移除指定元素并返回 true";
        }

        var removeItems = new List<object?> { 2 };
        engine.Api.InvokeByName("Set_RemoveItems", null,
            new object?[] { set, removeItems }, ctx, out bool removeItemsHandled);
        if (!removeItemsHandled || set.Count != 0)
        {
            return "Set_RemoveItems 应移除数组中的指定元素";
        }

        // `pinned_units_with_override` is a card member, not a transient local:
        // its Add/Remove helpers run in separate VM frames across events.
        var owner = state.CreateWithId("card_unit_14_panzergrenadier", Side.Left, 104,
            CardLocation.BoardHqLeft, 2);
        var target = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 105,
            CardLocation.BoardHqLeft, 3);
        var local = KismetLibrary.Default?.FindLocalProgram(owner.Name, "AddPinnedOverride");
        if (local is null)
        {
            return "14th Panzergrenadier 的 AddPinnedOverride 局部函数未加载";
        }

        var localCtx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = owner,
            Controller = Side.Left,
            Target = target,
        };
        engine.Api.Vm.RunLocalProgram(local, localCtx,
            new Dictionary<string, object?> { ["pinnedCard"] = target }, "unused");
        if (!owner.BlueprintSets.TryGetValue("pinned_units_with_override", out var persistent)
            || !persistent.Contains(target))
        {
            return "卡对象成员 TSet 未跨局部 VM 帧持久化";
        }

        engine.Api.InvokeByName("Set_Clear", null, new object?[] { set }, ctx, out bool clearHandled);
        if (!clearHandled || set.Count != 0)
        {
            return "Set_Clear 未原地清空目标集合";
        }

        return null;
    }

    private static string? BigThreeCountsFriendlyFactions(CardDatabase db)
    {
        const string orderName = "card_event_the_big_three";
        var groups = db.All
            .Where(card => card.IsUnit && card.FactionId is > 0 and < 11)
            .GroupBy(card => card.FactionId)
            .Where(group => group.Count() >= 2)
            .OrderBy(group => group.Key)
            .ToArray();
        if (groups.Length < 2)
        {
            return "卡库里找不到两个单位同阵营、第三个单位异阵营的测试组合";
        }

        var sameFaction = groups[0].OrderBy(card => card.Name, StringComparer.Ordinal).Take(2).ToArray();
        var otherFaction = groups[1].OrderBy(card => card.Name, StringComparer.Ordinal).First();
        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);
        state.ActiveSide = Side.Left;

        var first = state.CreateWithId(sameFaction[0].Name, Side.Left, 2,
            CardLocation.BoardHqLeft, 1);
        var second = state.CreateWithId(sameFaction[1].Name, Side.Left, 3,
            CardLocation.BoardHqLeft, 2);
        var third = state.CreateWithId(otherFaction.Name, Side.Left, 4,
            CardLocation.BoardHqLeft, 3);
        int[] attackBefore = { first.Attack, second.Attack, third.Attack };
        int[] defenseBefore = { first.Defense, second.Defense, third.Defense };
        var order = state.CreateWithId(orderName, Side.Left, 5, CardLocation.HandLeft, 0);

        if (!engine.PlayCard(order))
        {
            return "Big Three 无法打出";
        }

        if (first.Attack != attackBefore[0] + 1 || first.Defense != defenseBefore[0] + 1
            || second.Attack != attackBefore[1] + 1 || second.Defense != defenseBefore[1] + 1
            || third.Attack != attackBefore[2] + 2 || third.Defense != defenseBefore[2] + 2)
        {
            return $"阵营数量增益错误：同阵营 {first.Attack - attackBefore[0]}/{second.Attack - attackBefore[1]}，"
                 + $"异阵营 {third.Attack - attackBefore[2]}；期望 1/1/2 攻与防"
                 + Dump(state, ("未实现", Unimpl(state)));
        }

        return state.UnimplementedCalls.Keys.Any(key => key.StartsWith("Map_", StringComparison.Ordinal))
            ? "执行期间 Map 原语仍被记为未实现：" + Unimpl(state)
            : null;
    }

    private static string? BlueprintArrayResize(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 106,
            CardLocation.BoardHqLeft, 1);
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        var values = new List<int> { 11, 22, 33, 44, 55 };
        object? result = engine.Api.InvokeByName("Array_Resize", null,
            new object?[] { values, 3 }, ctx, out bool shrinkHandled);
        if (!shrinkHandled || result is not null || !values.SequenceEqual(new[] { 11, 22, 33 }))
        {
            return $"Array_Resize(3) 应保留前 3 项：handled={shrinkHandled}, "
                 + $"result={result ?? "null"}, values=[{string.Join(",", values)}]";
        }

        result = engine.Api.InvokeByName("Array_Resize", null,
            new object?[] { values, 5 }, ctx, out bool growHandled);
        if (!growHandled || result is not null || values.Count != 5
            || !values.Take(3).SequenceEqual(new[] { 11, 22, 33 })
            || values[3] != 0 || values[4] != 0)
        {
            return "Array_Resize(5) 应保留原值并用 0 填充整数数组";
        }

        result = engine.Api.InvokeByName("Array_Resize", null,
            new object?[] { values, -4 }, ctx, out bool negativeHandled);
        return negativeHandled && result is null && values.Count == 0
            ? null
            : "Array_Resize 的负长度必须按 0 处理并清空数组";
    }

    private static string? BlueprintNumericBounds(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId("card_unit_arado_ar_196", Side.Left, 106,
            CardLocation.BoardHqLeft, 1);
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        object? Eval(string function, params int[] values)
        {
            var expression = new KismetExpr
            {
                Math = function,
                Args = values.Select(value => new KismetExpr { Int = value }).ToArray(),
            };
            var steps = new KismetStep[]
            {
                new(0, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    expression, null, -1, "__probe", null),
                new(1, "return", null, Array.Empty<KismetExpr>(), Array.Empty<int>(),
                    null, null, -1, null, null),
            };
            var outputs = engine.Api.Vm.RunLocalProgramMulti(
                new KismetProgram(steps, 0), ctx, null, "__probe");
            return outputs.GetValueOrDefault("__probe");
        }

        var cases = new (string Function, int[] Args, int Expected)[]
        {
            ("Max", new[] { -3, 0 }, 0),
            ("Max", new[] { 4, 0 }, 4),
            ("Min", new[] { 6, 2 }, 2),
            ("Min", new[] { 6, 9 }, 6),
            ("Clamp", new[] { 2, 3, 24 }, 3),
            ("Clamp", new[] { 12, 3, 24 }, 12),
            ("Clamp", new[] { 30, 3, 24 }, 24),
        };

        foreach (var (function, args, expected) in cases)
        {
            object? actual = Eval(function, args);
            if (actual is not int value || value != expected)
            {
                return $"{function}({string.Join(",", args)}) 应为 {expected}，实际 {actual ?? "null"}";
            }
        }

        return null;
    }

    private static string? SturmovikPolishBuff(CardDatabase db)
    {
        const string sourceName = "card_unit_sturmovik_pol";
        const string targetName = "card_unit_t_34_pol";
        if (db.Find(sourceName) is null || db.Find(targetName) is null)
        {
            return $"卡库里缺 {sourceName} / {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 107, CardLocation.BoardHqLeft, 1);
        var target = state.CreateWithId(targetName, Side.Left, 108, CardLocation.BoardHqLeft, 2);
        int sourceAttack = source.Attack;
        int sourceDefense = source.Defense;
        int targetAttack = target.Attack;
        int targetDefense = target.Defense;
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        object? result = engine.Api.InvokeByName("Apply The Buff", source,
            new object?[] { source }, ctx, out bool selfHandled);
        if (!selfHandled || result is not null
            || source.Attack != sourceAttack + 1 || source.Defense != sourceDefense + 1)
        {
            return $"self 目标应永久 +1/+1：handled={selfHandled}, "
                 + $"actual={source.Attack}/{source.Defense}, expected={sourceAttack + 1}/{sourceDefense + 1}";
        }

        result = engine.Api.InvokeByName("Apply The Buff", source,
            new object?[] { target }, ctx, out bool targetHandled);
        if (!targetHandled || result is not null
            || target.Attack != targetAttack + 1 || target.Defense != targetDefense + 1)
        {
            return $"显式 survivor 目标应永久 +1/+1：handled={targetHandled}, "
                 + $"actual={target.Attack}/{target.Defense}, expected={targetAttack + 1}/{targetDefense + 1}";
        }

        return null;
    }

    private static string? L4GrasshopperRemoveBuff(CardDatabase db)
    {
        const string sourceName = "card_unit_l4_grasshopper";
        const string handName = "card_unit_m4a2";
        const string boardName = "card_unit_m4_sherman";
        if (db.Find(sourceName) is null || db.Find(handName) is null || db.Find(boardName) is null)
        {
            return $"卡库里缺 {sourceName} / {handName} / {boardName}";
        }

        var (engine, state) = EmptyBoard(db);
        var source = state.CreateWithId(sourceName, Side.Left, 109, CardLocation.BoardHqLeft, 1);
        var hand = state.CreateWithId(handName, Side.Left, 110, CardLocation.HandLeft, 0);
        var board = state.CreateWithId(boardName, Side.Left, 111, CardLocation.BoardHqLeft, 2);
        int handCost = hand.KreditCost;
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        engine.Api.InvokeByName("ChangeKreditCost", source,
            new object?[] { hand, source.CardId, -1, 0, false, null }, ctx, out bool costHandled);
        engine.Api.InvokeByName("GiveBlitz", source,
            new object?[] { board.CardId, source.CardId }, ctx, out bool blitzHandled);
        if (!costHandled || !blitzHandled || hand.KreditCost != Math.Max(0, handCost - 1)
            || !board.Keywords.Contains(Keyword.Blitz))
        {
            return "L4 光环前置状态未正确施加到 Sherman 手牌/场面";
        }

        object? result = engine.Api.InvokeByName("Remove the Buff", source,
            Array.Empty<object?>(), ctx, out bool handled);
        return handled && result is null && hand.KreditCost == handCost
            && !board.Keywords.Contains(Keyword.Blitz)
            ? null
            : $"Remove the Buff 未撤销 Sherman 光环：handled={handled}, "
              + $"cost={hand.KreditCost}/{handCost}, blitz={board.Keywords.Contains(Keyword.Blitz)}";
    }

    private static string? GetXAndMoreAttackCardsOnBoard(CardDatabase db)
    {
        const string unitName = "card_unit_arado_ar_196";
        if (db.Find(unitName) is null)
        {
            return $"卡库里缺 {unitName}";
        }

        var (engine, state) = EmptyBoard(db);
        var leftKeep = state.CreateWithId(unitName, Side.Left, 100, CardLocation.BoardHqLeft, 1);
        leftKeep.Attack = 5;
        leftKeep.Defense = 3;
        var leftLowAttack = state.CreateWithId(unitName, Side.Left, 101, CardLocation.BoardHqLeft, 2);
        leftLowAttack.Attack = 3;
        leftLowAttack.Defense = 3;
        var leftDestroyed = state.CreateWithId(unitName, Side.Left, 102, CardLocation.BoardHqLeft, 3);
        leftDestroyed.Attack = 8;
        leftDestroyed.Defense = 0;
        var rightKeep = state.CreateWithId(unitName, Side.Right, 103, CardLocation.BoardHqRight, 1);
        rightKeep.Attack = 6;
        rightKeep.Defense = 2;
        var handUnit = state.CreateWithId(unitName, Side.Left, 104, CardLocation.HandLeft, 0);
        handUnit.Attack = 9;
        handUnit.Defense = 9;

        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var leftResult = engine.Api.InvokeByName("Get_X_AndMoreAttackCardsOnBoard", null,
            new object?[] { (int)Side.Left, null, 4, false }, ctx, out bool leftHandled);
        if (!leftHandled || leftResult is not List<int> leftIds
            || !leftIds.SequenceEqual(new[] { leftKeep.CardId }))
        {
            return $"左方应只返回攻击≥4且防御>0的在场单位 ID，实际 handled={leftHandled} "
                 + $"result={leftResult ?? "null"}";
        }

        var rightResult = engine.Api.InvokeByName("Get_X_AndMoreAttackCardsOnBoard", null,
            new object?[] { (int)Side.Right, null, 4, false }, ctx, out bool rightHandled);
        if (!rightHandled || rightResult is not List<int> rightIds
            || !rightIds.SequenceEqual(new[] { rightKeep.CardId }))
        {
            return $"右方应只返回本方符合条件的卡 ID，实际 handled={rightHandled} "
                 + $"result={rightResult ?? "null"}";
        }

        var allThreshold = engine.Api.InvokeByName("Get_X_AndMoreAttackCardsOnBoard", null,
            new object?[] { (int)Side.Left, null, 0, false }, ctx, out bool allHandled);
        if (!allHandled || allThreshold is not List<int> allIds
            || allIds.Contains(state.Hq(Side.Left).CardId)
            || allIds.Contains(handUnit.CardId)
            || allIds.Contains(leftDestroyed.CardId)
            || !allIds.Contains(leftKeep.CardId))
        {
            return $"攻击阈值为 0 时仍须排除 HQ、手牌和防御为 0 的卡，实际 handled={allHandled} "
                 + $"result={allThreshold ?? "null"}";
        }

        return null;
    }

    private static string? GetHighestBomberAttack(CardDatabase db)
    {
        string? bomberName = FindType(db, "bomber");
        string? fighterName = FindType(db, "fighter");
        if (bomberName is null || fighterName is null)
        {
            return "卡库里缺 bomber 或 fighter（无法测）";
        }

        var (engine, state) = EmptyBoard(db);
        var weaker = state.CreateWithId(bomberName, Side.Left, 120, CardLocation.BoardHqLeft, 1);
        weaker.Attack = 3;
        var stronger = state.CreateWithId(bomberName, Side.Left, 121, CardLocation.BoardHqLeft, 2);
        stronger.Attack = 7;
        var enemy = state.CreateWithId(bomberName, Side.Right, 122, CardLocation.BoardHqRight, 1);
        enemy.Attack = 99;
        var nonBomber = state.CreateWithId(fighterName, Side.Left, 123, CardLocation.BoardHqLeft, 3);
        nonBomber.Attack = 99;

        var ctx = new EffectContext { Engine = engine, State = state, Self = weaker, Controller = Side.Left };
        var result = engine.Api.InvokeByName("GetHighestBomberAttack", null,
            new object?[] { null }, ctx, out bool handled);
        if (!handled || Convert.ToInt32(result ?? -1) != 7)
        {
            return $"应返回本方轰炸机最高实时攻击 7，实际 handled={handled}, result={result ?? "null"}";
        }

        stronger.Location = CardLocation.Discard;
        result = engine.Api.InvokeByName("GetHighestBomberAttack", null,
            new object?[] { null }, ctx, out handled);
        if (!handled || Convert.ToInt32(result ?? -1) != 3)
        {
            return $"离场后应只剩攻击 3 的轰炸机，实际 handled={handled}, result={result ?? "null"}";
        }

        weaker.Location = CardLocation.Discard;
        result = engine.Api.InvokeByName("GetHighestBomberAttack", null,
            new object?[] { null }, ctx, out handled);
        return handled && Convert.ToInt32(result ?? -1) == 0
            ? null
            : $"本方没有轰炸机时应返回 0，实际 handled={handled}, result={result ?? "null"}";
    }

    private static string? ConvertCard(CardDatabase db)
    {
        const string sourceName = "card_unit_arado_ar_196";
        const string targetName = "card_unit_routed_troops";
        if (db.Find(sourceName) is null || db.Find(targetName) is null)
        {
            return $"卡库里缺 {sourceName} / {targetName}";
        }

        var (engine, state) = EmptyBoard(db);
        var card = state.CreateWithId(sourceName, Side.Left, 77, CardLocation.BoardHqLeft, 3);
        card.CustomJson["probe"] = "stale";
        card.CustomAbility = "staleAbility";
        card.BuffsBySource[(123, true)] = new CardBuff { SourceCardId = 123, Attack = 4, Defense = 2 };
        card.HasMovedThisTurn = true;
        card.HasAttackedThisTurn = true;
        card.PinnedTurns = 2;

        var ctx = new EffectContext { Engine = engine, State = state, Self = card, Controller = Side.Left };
        var result = engine.Api.InvokeByName("ConvertCard", card,
            new object?[] { new[] { card.CardId }, 9001, targetName, 0, false, null }, ctx, out bool handled);
        if (!handled || result is not System.Collections.IEnumerable ids)
        {
            return $"ConvertCard 应被处理并返回 ID 数组：handled={handled}, result={result ?? "null"}";
        }

        var convertedIds = ids.Cast<object?>().Select(Convert.ToInt32).ToArray();
        if (convertedIds.Length != 1 || convertedIds[0] != 77)
        {
            return $"转换出参应为原 cardID=77，实际 [{string.Join(",", convertedIds)}]";
        }

        if (card.CardId != 77 || card.Owner != Side.Left || card.Location != CardLocation.BoardHqLeft
            || card.LocationNumber != 3 || card.Name != targetName
            || card.Definition.Name != targetName)
        {
            return $"转换应保留 ID/owner/location/locNo 并替换身份，实际 {card.CardId}/{card.Owner}/"
                 + $"{card.Location}/{card.LocationNumber}/{card.Name}";
        }

        var target = db.Require(targetName);
        if (card.Attack != target.Attack || card.Defense != target.Defense
            || card.MaxDefense != target.Defense
            || target.Keywords.Any(keyword => !card.Keywords.Contains(keyword)))
        {
            return $"转换后应使用新卡面攻防/关键字，实际 {card.Attack}/{card.Defense}/{card.MaxDefense}";
        }

        if (card.CustomJson.Count != 0 || card.CustomAbility is not null || card.BuffsBySource.Count != 0
            || card.HasMovedThisTurn || card.HasAttackedThisTurn || card.PinnedTurns != 0)
        {
            return "转换后旧卡的 CustomJson/CustomAbility/buff/行动标记未清理";
        }

        return null;
    }

    private static string? LoseKreditSlotRefillsFromCurrentSlot(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        state.SetMaxKredits(Side.Left, 4);
        state.SetKredits(Side.Left, 4);
        var source = state.CreateWithId("card_event_air_land_sea", Side.Left, 2,
            CardLocation.HandLeft, 0);
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        engine.Api.InvokeByName("LoseKreditSlot", source,
            new object?[] { (int)Side.Left }, ctx, out bool handled);
        if (!handled || state.MaxKredits(Side.Left) != 3 || state.Kredits(Side.Left) != 4)
        {
            return $"降槽应只改上限并保留当前 kredit：handled={handled}, "
                 + $"state={state.Kredits(Side.Left)}/{state.MaxKredits(Side.Left)}";
        }

        engine.StartTurn(Side.Left, draw: false);
        if (state.MaxKredits(Side.Left) != 4 || state.Kredits(Side.Left) != 4)
        {
            return $"下一回合应从当前槽位 3 自然恢复到 4："
                 + $"实际 {state.Kredits(Side.Left)}/{state.MaxKredits(Side.Left)}";
        }

        return null;
    }

    /// <summary>
    /// `DiscardCardFromDeck(cardID, discarderID, skipTriggers, skipVisuals, out success)`
    /// —— 11 调用点 / 11 张卡。
    ///
    /// 直译产物里的门：`cardID > 0` + 卡有效 + **卡必须在牌库里**（location 1/2）。
    /// 这条断言两件事：牌库里的会被弃；手牌里的**不该**被弃（门不能丢）。
    /// </summary>
    private static string? DiscardFromDeck(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        const string self = "card_unit_10_5_cm_lefh";
        if (db.Find(unit) is null || db.Find(self) is null)
        {
            return $"卡库里缺 {unit} / {self}";
        }

        var (engine, state) = DeploymentBoard(db);
        var actor = PutOnBoard(state, self, Side.Left, 20, 1);
        var inDeck = state.CreateWithId(unit, Side.Left, 21, CardLocation.DeckLeft, 1);
        var inHand = state.CreateWithId(unit, Side.Left, 22, CardLocation.HandLeft, 1);
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = actor, Controller = Side.Left,
        };

        var ok = engine.Api.InvokeByName("DiscardCardFromDeck", actor,
            new object?[] { inDeck.CardId, actor.CardId, false, false, null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `DiscardCardFromDeck`（**修复前就是这个状态**）";
        }

        if (inDeck.Location != CardLocation.Discard)
        {
            return $"牌库里的卡必须被弃进弃牌堆，实际 {inDeck.Location}";
        }

        if (ok is not true)
        {
            return $"牌库里的卡被弃时 success 应当是 true，实际 {ok ?? "null"}";
        }

        // 门：不在牌库里的卡不该被弃
        var bad = engine.Api.InvokeByName("DiscardCardFromDeck", actor,
            new object?[] { inHand.CardId, actor.CardId, false, false, null }, ctx, out _);
        if (inHand.Location != CardLocation.HandLeft)
        {
            return $"手牌里的卡**不该**被 `DiscardCardFromDeck` 弃掉（蓝图 L_008B 的门），"
                 + $"实际被移到了 {inHand.Location}";
        }

        if (bad is true)
        {
            return "手牌里的卡被调用时 success 应当是 false";
        }

        return null;
    }

    /// <summary>
    /// `DiscardRandomCardFromHand(side, instigatorID, out discardedCardID)`：
    /// 空候选不应消费随机流；单候选直接弃牌；多候选必须使用同一条 UE 随机流，
    /// 并复用 `DiscardCard` 的 `OnOtherCardDiscarded` 广播。
    /// </summary>
    private static string? DiscardRandomCardFromHand(CardDatabase db)
    {
        const string sourceName = "card_unit_10_5_cm_lefh";
        const string watcherName = "card_unit_nakajima_b5n";
        if (db.Find(sourceName) is null || db.Find(watcherName) is null || db.Find(PlainUnit) is null)
        {
            return $"卡库里缺 {sourceName} / {watcherName} / {PlainUnit}";
        }

        // 空手：不应写出伪造的卡 ID，也不应推进随机游标。
        {
            var (engine, state) = DeploymentBoard(db);
            var source = PutOnBoard(state, sourceName, Side.Left, 20, 1);
            var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };
            long before = state.Random.ConsumedCount;
            var result = engine.Api.InvokeByName("DiscardRandomCardFromHand", source,
                new object?[] { (int)Side.Left, source.CardId, null }, ctx, out bool handled);
            if (!handled)
            {
                return "派发表里没有 `DiscardRandomCardFromHand`（**修复前就是这个状态**）";
            }

            if (result is not 0 || state.Random.ConsumedCount != before)
            {
                return $"空手应返回 discardedCardID=0 且不消费随机流，实际 result={result ?? "null"}、"
                     + $"cursor {before}->{state.Random.ConsumedCount}";
            }
        }

        // 单牌 + 事件：结果 ID、位置、随机消费和旁观订阅都必须正确。
        {
            var (engine, state) = DeploymentBoard(db);
            var watcher = state.CreateWithId(watcherName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
            var enemy = state.CreateWithId(PlainUnit, Side.Right, 60, CardLocation.BoardHqRight, 1);
            var victim = state.CreateWithId(PlainUnit, Side.Left, 61, CardLocation.HandLeft, 0);
            var ctx = new EffectContext { Engine = engine, State = state, Self = watcher, Controller = Side.Left };
            var trace = new List<string>();
            engine.Api.TriggerTrace = trace;
            int enemyDefense = enemy.Defense;

            var result = engine.Api.InvokeByName("DiscardRandomCardFromHand", watcher,
                new object?[] { (int)Side.Left, watcher.CardId, null }, ctx, out bool handled);
            if (!handled)
            {
                return "单牌调用未被派发";
            }

            if ((int)(result ?? 0) != victim.CardId || victim.Location != CardLocation.Discard)
            {
                return $"单牌应弃掉 #{victim.CardId} 并返回该 ID，实际 result={result ?? "null"}、位置={victim.Location}";
            }

            string prefix = $"OnOtherCardDiscarded → {watcher.Name}#{watcher.CardId}";
            if (!trace.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)
                             && t.Contains($"eventCard={victim.Name}#{victim.CardId}", StringComparison.Ordinal)))
            {
                return "随机弃牌没有复用 OnOtherCardDiscarded 事件链"
                     + $"\n       实际派发记录：{string.Join(" | ", trace)}";
            }

            if (enemy.Defense != enemyDefense - 1)
            {
                return $"NAKAJIMA B5N 未收到随机弃牌事件：期望敌方防御 {enemyDefense - 1}，实际 {enemy.Defense}";
            }
        }

        // 多牌：固定种子下必须与独立 UE 流预测的下标一致，并且只弃一张。
        {
            var (engine, state) = DeploymentBoard(db);
            var source = PutOnBoard(state, sourceName, Side.Left, 20, 1);
            var first = state.CreateWithId(PlainUnit, Side.Left, 61, CardLocation.HandLeft, 0);
            var second = state.CreateWithId(PlainUnit, Side.Left, 62, CardLocation.HandLeft, 1);
            var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };
            int expectedIndex = new UeRandomStream(1).Next(2);
            var result = engine.Api.InvokeByName("DiscardRandomCardFromHand", source,
                new object?[] { (int)Side.Left, source.CardId, null }, ctx, out bool handled);
            if (!handled)
            {
                return "多牌调用未被派发";
            }

            var expected = expectedIndex == 0 ? first : second;
            var other = expectedIndex == 0 ? second : first;
            if ((int)(result ?? 0) != expected.CardId || expected.Location != CardLocation.Discard
                || other.Location == CardLocation.Discard || state.Random.ConsumedCount != 1)
            {
                return $"多牌随机结果不符：期望 #{expected.CardId}，实际 result={result ?? "null"}，"
                     + $"位置={first.Location}/{second.Location}，cursor={state.Random.ConsumedCount}";
            }
        }

        return null;
    }

    /// <summary>
    /// ★ `StopDestructionEffect` 门（事件 24）—— 关掉 `CardApi.cs:631` 那条 TODO。
    ///
    /// 出处：`TriggerDestruction` si=905/965
    /// <c>BooleanAND(Not(CustomName1HasAttribute("StopDestructionEffect")), card.hasDestruction)</c>。
    /// 写方是 `card_event_patrol` i=10 / `card_event_usa_promo2` i=414
    /// （`CustomName1Add("StopDestructionEffect")`），读方就是这道门。
    ///
    /// 这条测的是**读写两边用的是同一份存储**：
    /// 用派发表里的 `CustomName1Add` 写，用引擎的 `ShouldTriggerDestructionEffect` 读。
    /// </summary>
    private static string? StopDestructionEffectGate(CardDatabase db)
    {
        const string unit = "card_unit_2nd_parachute";
        if (db.Find(unit) is null)
        {
            return $"卡库里缺 {unit}";
        }

        var (engine, state) = DeploymentBoard(db);
        var card = PutOnBoard(state, unit, Side.Left, 20, 1);
        card.Keywords.Add(Keyword.Destruction);      // 天然带摧毁效果的卡
        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = card, Controller = Side.Left,
        };

        if (!engine.Api.ShouldTriggerDestructionEffect(card))
        {
            return "带 hasDestruction 的卡在没被标记时，摧毁效果**应当**触发";
        }

        engine.Api.InvokeByName("CustomName1Add", card, new object?[] { "StopDestructionEffect" }, ctx,
            out bool handled);
        if (!handled)
        {
            return "派发表里没有 `CustomName1Add`（**修复前就是这个状态**）";
        }

        if (engine.Api.ShouldTriggerDestructionEffect(card))
        {
            return "`CustomName1Add(\"StopDestructionEffect\")` 之后，摧毁效果**不该**触发"
                 + $"（存的是 \"{card.CustomJson.GetValueOrDefault("customName1", "")}\"）"
                 + " —— 写方与读方没共用同一份存储";
        }

        engine.Api.InvokeByName("CustomName1Remove", card, new object?[] { "StopDestructionEffect" }, ctx, out _);
        if (!engine.Api.ShouldTriggerDestructionEffect(card))
        {
            return "`CustomName1Remove(\"StopDestructionEffect\")` 之后，摧毁效果应当恢复触发";
        }

        return null;
    }

    /// <summary>
    /// ★★ 防回归守卫：派发表静态缺口。
    ///
    /// 计算在 `KLink.Bot.Effects.Blueprint.DispatchGap`（库里，审计 ⑥b 共用同一份），
    /// 冻结基线在 <see cref="DispatchGapBaseline"/>。
    /// </summary>
    private static string? DispatchGapGuard(CardDatabase db) => DispatchGapBaseline.Check(db);

    /// <summary>
    /// **UE `FRandomStream` 复刻的逐位验证** —— 用 `Kards_RNG_report` 的
    /// `Weather.md` §4.2.1 给的那组可复算测试向量。
    ///
    /// 那组向量的出处是游戏二进制的 IDA 反编译（`0x143ddce50`），
    /// 不是"照抄引擎源码"，所以它能同时钉住三件事：
    /// 1. LCG 常数 `A=196314165 / C=907633515`（报告说已用字节搜索确认 fork 未改）；
    /// 2. `GetFraction()` 的「高 23 位 → `[1,2)` float → 减 1」变换；
    /// 3. `RandRange(min,max)` 是**闭区间**（除数 `max-min+1`）。
    ///
    /// 向量（`seed = 1000000000 + 10*19390 = 1000193900`，连抽三次 `(0,2)`）：
    /// <code>
    ///   变换前        → 变换后      返回
    ///   1000193900   → 1626977479   1
    ///   1626977479   → 4280773790   2
    ///   4280773790   →  431904801   0
    /// </code>
    ///
    /// ⚠️ **这条用例在换 RNG 之前必然失败**（旧实现是 splitmix64 取模）——
    /// 这正是"修复前红、修复后绿"的那条。
    /// </summary>
    private static string? UeRandomStreamMatchesReportVector(CardDatabase db)
    {
        // 报告 §4.2.1 的重播种值：match_id=1000000000、CurrentActionId=10。
        // ⚠️ 内核**不**做逐动作重播种（那是 2026-08-25 前已失效的机制），
        //    这里只是拿它当"一个确定的种子"来喂 UE 算法。
        var rng = new KLink.Bot.Engine.UeRandomStream(1000000000 + 10 * 19390);

        (uint AfterSeed, int Value)[] want =
        {
            (1626977479u, 1),
            (4280773790u, 2),
            (431904801u, 0),
        };

        for (int i = 0; i < want.Length; i++)
        {
            int got = rng.RandRange(0, 2);
            if (rng.Seed != want[i].AfterSeed)
            {
                return $"第 {i + 1} 次抽签后的 Seed = {rng.Seed}，报告给的是 {want[i].AfterSeed}"
                       + $"（LCG 常数或 GetFraction 变换不一致）";
            }

            if (got != want[i].Value)
            {
                return $"第 {i + 1} 次 RandomIntFromRangeWithStream(0,2) = {got}，报告给的是 {want[i].Value}";
            }
        }

        return null;
    }

    /// <summary>
    /// `UKismetArrayLibrary::Array_ShuffleFromStream` 的**前向** Fisher-Yates
    /// 循环跑满 `n` 次（`i == LastIndex` 时照样消耗一次），所以洗一个 n 元数组
    /// 恰好消耗 **n** 个随机数。写成后向循环（n-1 次）会让游标少走一步，
    /// 后面所有随机取数全部错位。
    /// </summary>
    private static string? UeShuffleConsumesNDraws(CardDatabase db)
    {
        foreach (int n in new[] { 1, 2, 3, 5, 39 })
        {
            var rng = new KLink.Bot.Engine.UeRandomStream(508065);
            var list = Enumerable.Range(0, n).ToList();
            rng.Shuffle(list);

            if (rng.ConsumedCount != n)
            {
                return $"洗 {n} 张牌消耗了 {rng.ConsumedCount} 个随机数，应为 {n}";
            }

            if (list.Count != n || list.Distinct().Count() != n)
            {
                return $"洗 {n} 张牌之后元素集合变了：{string.Join(",", list)}";
            }
        }

        return null;
    }

    /// <summary>
    /// **安全网**：内核自己发号生成的卡，在身份没被动作流确认之前**不许打出去**。
    ///
    /// 为什么（真人玩家实测，2026-10-02）：我们发一条 `PC {"0": X}`，客户端那边
    /// **没有 X 这张卡**（或 X 是别的卡）⇒ 记牌器 +1、场上什么都没有 = **虚空部署**。
    /// **虚空单位会立刻不同步**（客户端根本没有这个单位，之后所有攻击/移动全错位）。
    ///
    /// 判据：`cardID` 是内核分配器（`NextCardId`）发的 ⇒ 客户端未必认得；
    /// 只有动作流用卡组码确认过（`MarkIdentityVerified`）才可信。
    ///
    /// ⚠️ 这条用例在加安全网之前必然失败（`CanPlay` 会返回 true）——
    /// 就是「修复前红、修复后绿」的那条。
    /// </summary>
    private static string? UntrustedGeneratedCardIsNotPlayed(CardDatabase db)
    {
        const string Token = "card_unit_1st_airborne";
        if (db.Find(Token) is null)
        {
            return $"卡库里缺 {Token}";
        }

        var (engine, state) = EmptyBoard(db);
        state.TrackGeneratedCardTrust = true;
        state.ActiveSide = Side.Right;
        state.SetKredits(Side.Right, 10);

        // 效果生成的卡（走 `NextCardId`）⇒ 记进「客户端未必认得」集合
        var gen = state.Create(Token, Side.Right, Side.Right.HandOf(), 0);
        if (!state.GeneratedCardIds.Contains(gen.CardId))
        {
            return $"效果生成的卡 #{gen.CardId} 没有被记进 GeneratedCardIds";
        }

        if (!state.IsIdentityTrusted(gen) == false)
        {
            return "刚生成的卡不该是「已确认」状态";
        }

        // ① 未确认 ⇒ 不许打
        if (engine.CanPlay(gen, out string why))
        {
            return $"身份未确认的生成卡不该能打出去（虚空部署），实际 CanPlay=true（#{gen.CardId} {gen.Name}）";
        }

        if (!why.Contains("身份未核实", StringComparison.Ordinal))
        {
            return $"拒绝原因应是「身份未核实」，实际：{why}";
        }

        string key = $"<unverified-generated-card-not-played:{gen.Name}>";
        if (state.UnimplementedCalls.GetValueOrDefault(key) != 1)
        {
            return $"拒绝时必须在 UnimplementedCalls 里留痕 {key}，实际 " +
                   $"{state.UnimplementedCalls.GetValueOrDefault(key)}";
        }

        // ② 动作流确认之后 ⇒ 放行
        state.MarkIdentityVerified(gen.CardId);
        if (!engine.CanPlay(gen, out string why2))
        {
            return $"动作流确认身份之后必须放行，实际被拒：{why2}";
        }

        // ③ 来自快照的卡（`CreateWithId`）**永远可信**，不受这道门影响
        var seeded = state.CreateWithId(Token, Side.Right, 900001, Side.Right.HandOf(), 1);
        if (!state.IsIdentityTrusted(seeded))
        {
            return "快照/动作流建的卡不该被当成「内核生成的卡」";
        }

        // ④ 开关关掉时（自对弈路径）不拦
        engine.EnforceGeneratedCardTrust = false;
        var gen2 = state.Create(Token, Side.Right, Side.Right.HandOf(), 2);
        if (!engine.CanPlay(gen2, out string why3))
        {
            return $"关掉安全网后不该再拦，实际被拒：{why3}";
        }

        return null;
    }

    /// <summary>
    /// `RandomIntFromRangeWithStream(min, max)` 透传 `RandomIntegerInRangeFromStream`，
    /// 后者按 IDA 反编译是 `Min + floor(GetFraction() * (Max-Min+1))` ⇒ **闭区间**。
    /// 旧实现按半开区间处理，`(0,2)` 永远出不了 2。
    /// </summary>
    private static string? RandomIntFromRangeIsInclusive(CardDatabase db)
    {
        var rng = new KLink.Bot.Engine.UeRandomStream(773639);
        var seen = new HashSet<int>();
        for (int i = 0; i < 60; i++)
        {
            int v = rng.RandRange(0, 2);
            if (v < 0 || v > 2)
            {
                return $"RandRange(0,2) 出了区间外的值 {v}";
            }

            seen.Add(v);
        }

        if (!seen.Contains(2))
        {
            return "60 次 RandRange(0,2) 一次都没出 2 —— 说明还是半开区间";
        }

        if (!seen.Contains(0) || !seen.Contains(1))
        {
            return $"RandRange(0,2) 没覆盖全部取值，只见到 {string.Join(",", seen.OrderBy(x => x))}";
        }

        return null;
    }

    // ==================================================================
    //  ★★ 2026-10-02：`MakeCardsFight`（互斗）
    // ==================================================================
    //
    // ## 背景（真人玩家实测报告）
    //
    // 「**友方单位和敌方单位进行战斗**（不是指定向的战斗，而是有一个函数是使一个单位
    //   与另一个单位进行战斗）。**这个函数好像没实现**，这样子就导致了**对面会出现虚空单位**。」
    //
    // ## 根因（已确认，不是猜）
    //
    // 派发表（`CardApiDispatch.cs`）里**没有 `"MakeCardsFight"` 这个键**，全库只有
    // `CardApi.cs:850` 一行注释。`KismetVm.ExecuteCall`（`KismetVm.cs:606-612`）对认不出的
    // 名字**什么都不做**、只记一笔 `UnimplementedCalls` ⇒「互斗」整段效果静默不发生
    // ⇒ 本该战死的单位没死 ⇒ 我方场上有客户端没有的单位。
    // 影响面：IR **12 个调用点 / 12 张卡**；实测 `replay-773639` 的 ⑥ 里是 `MakeCardsFight ×1`。
    //
    // ## 语义出处（**权威**）
    //
    // `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:25907-26040`。
    // 签名是 **4 个实参**（`_index.g.cs:3994` 的参数名 + IR 12/12 个调用点互证）：
    // <code>
    // MakeCardsFight(unitThisSide, unitOppositeSide, instigatorID, out qqq)
    // </code>
    // 「6 个参数 `(a, b, dmg, False, True, False)`」那句说的是它**内部**那两次
    // `ExecuteOnDealDamageAddDamage(dealer, receiver, dmg, _fromAttack, fromFight, _isDefenderDamage)`
    // （`g.cs:25977` / `26006`），`dmg` 是"这一方向、修正前的伤害"。
    //
    // 执行序（**先算完两个方向，再依次落地**）：
    // <code>
    // si=6A   _damage_to_enemy_unit = getTotalAttack(unitThisSide)      ← 攻击值快照
    // si=AE   _damage_to_my_unit    = getTotalAttack(unitOppositeSide)  ← 也在任何扣血之前
    // si=12F  ExecuteOnDealDamageAddDamage(a→b, …, False, True, False)  ← 方向 1 的修正链
    // si=164  ExecuteOnDealDamageAddDamageAfterCalc(…)
    // si=1E8  ExecuteOnDealDamageAddDamage(b→a, …, False, True, False)  ← 方向 2 的修正链
    // si=21D  ExecuteOnDealDamageAddDamageAfterCalc(…)
    // si=26E  ApplyDamageToCard(b ← a, _final_damage_to_enemy_unit, False, False)   ← 才开始扣血
    // si=299  ApplyDamageToCard(a ← b, _final_damage_to_my_unit,    False, True)
    // </code>

    /// <summary>
    /// 造一个「两个单位面对面」的局面：左方单位在**前线**、右方单位在**半场**，
    /// 两者都 `IsLocatedOnBoard`、都不是 HQ。攻/防由调用方指定。
    /// </summary>
    private static (MatchEngine Engine, CardInstance Mine, CardInstance Foe) FightBoard(
        CardDatabase db, string mineCard, string foeCard,
        int mineAtk, int mineDef, int foeAtk, int foeDef)
    {
        var (engine, state) = EmptyBoard(db);

        var mine = state.CreateWithId(mineCard, Side.Left, 11, CardLocation.BoardFrontline, 0);
        mine.Attack = mineAtk;
        mine.Defense = mineDef;
        mine.MaxDefense = mineDef;
        mine.EnteredPlayOnTurn = -99;

        var foe = state.CreateWithId(foeCard, Side.Right, 51, CardLocation.BoardHqRight, 1);
        foe.Attack = foeAtk;
        foe.Defense = foeDef;
        foe.MaxDefense = foeDef;
        foe.EnteredPlayOnTurn = -99;

        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, 20);
        state.SetMaxKredits(Side.Left, 20);
        return (engine, mine, foe);
    }

    /// <summary>
    /// 按 IR 的实参形状调一次 `MakeCardsFight`（`a[0]`=这一边、`a[1]`=对面、`a[2]`=cardID）。
    /// 返回 false = **派发表里没有这个键**（正是修复前的状态）。
    /// </summary>
    private static bool InvokeMakeCardsFight(MatchEngine engine, CardInstance caster,
                                             CardInstance a, CardInstance b)
    {
        var ctx = new EffectContext
        {
            Engine = engine,
            State = engine.State,
            Self = caster,
            Controller = caster.Owner,
        };

        engine.Api.InvokeByName("MakeCardsFight", caster,
            new object?[] { a, b, caster.CardId }, ctx, out bool handled);
        return handled;
    }

    /// <summary>
    /// 派发表缺键 / 实现抛异常时给出的可读原因。
    /// `InvokeByName` 会把异常吞成 `<名字&lt;fault:类型&gt;` 记进 `UnimplementedCalls`
    /// （`CardApiDispatch.cs:2137-2142`），所以这里两种失败都要看。
    /// </summary>
    private static string? FightFault(GameState state)
    {
        foreach (var kv in state.UnimplementedCalls)
        {
            if (kv.Key.StartsWith("MakeCardsFight", StringComparison.Ordinal))
            {
                return $"`MakeCardsFight` 没跑通（`{kv.Key}` ×{kv.Value}）";
            }
        }

        return null;
    }

    /// <summary>
    /// **互斗的主用例**：双向同时结算。
    ///
    /// 覆盖任务书要求的五种情形：① 双方都活 ② 一方死 ③ **双方都死** ④ 重甲 ⑤ 免疫。
    ///
    /// ⚠️ ③「双方都死」是**最关键**的一条：它同时证明了
    /// <list type="bullet">
    /// <item>两个方向的攻击值都在扣血**之前**取（`si=6A/AE`）；</item>
    /// <item>方向 2 的伤害**没有"防御方死了就不反击"那道门** —— 这与
    /// <see cref="MatchEngine.Attack"/> 的 `defender.IsAlive`（`MatchEngine.cs:1570`）
    /// 正好相反，也正是"3/3 打 3/3 同归于尽"这个 KARDS 常识的来源。</item>
    /// </list>
    /// 一个"先打 A→B、判 B 是否还活着、再打 B→A"的实现会让 ③ 里的我方活下来 ⇒ 本用例红。
    /// </summary>
    private static string? MakeCardsFightMutualStrike(CardDatabase db)
    {
        // 选两张**没有** `OnCardDealDamage_ModifyDamageDealt` 的普通单位，保证算术干净。
        const string Plain = "card_unit_panzer_ii_a";
        const string Armored = "card_unit_kv_ii";      // 天生重甲 2（`HeavyArmorReduction` 用例里已核对过）
        if (db.Find(Plain) is null) return $"卡库里缺 {Plain}";
        if (db.Find(Armored) is null) return $"卡库里缺 {Armored}";

        // ---- ① 双方都活：我方 3/3、敌方 2/5 ⇒ 我方 3→1、敌方 5→2 ----
        {
            var (engine, mine, foe) = FightBoard(db, Plain, Plain, 3, 3, 2, 5);
            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "派发表里**没有** `MakeCardsFight` 这个键 ⇒ 互斗整段效果静默不发生"
                     + "（这正是「虚空单位」的根因）";
            }

            if (FightFault(engine.State) is { } f1) return f1;

            engine.CheckDeaths();
            if (mine.Defense != 1 || foe.Defense != 2)
            {
                return $"① 3/3 与 2/5 互斗后应为 1 与 2，实际 {mine.Defense} 与 {foe.Defense}"
                     + Dump(engine.State, ("未实现", Unimpl(engine.State)));
            }

            if (!mine.AliveOnBoard || !foe.AliveOnBoard)
            {
                return "① 双方都该活下来，实际有单位死了";
            }
        }

        // ---- ② 一方死：我方 5/5、敌方 2/2 ⇒ 敌方吃 5 死，我方吃 2 剩 3 ----
        {
            var (engine, mine, foe) = FightBoard(db, Plain, Plain, 5, 5, 2, 2);
            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "② 派发表里没有 `MakeCardsFight` 键";
            }

            engine.CheckDeaths();
            if (foe.AliveOnBoard)
            {
                return $"② 2/2 挨了 5 点互斗伤害却没死（Defense={foe.Defense}）—— 伤害没落地";
            }

            if (!mine.AliveOnBoard || mine.Defense != 3)
            {
                return $"② 我方 5/5 应吃 2 点剩 3 并活着，实际 Defense={mine.Defense} 活着={mine.AliveOnBoard}";
            }
        }

        // ---- ③ 双方都死：3/3 打 3/3 ⇒ 同归于尽（**反击门必须不存在**）----
        {
            var (engine, mine, foe) = FightBoard(db, Plain, Plain, 3, 3, 3, 3);
            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "③ 派发表里没有 `MakeCardsFight` 键";
            }

            if (mine.Defense != 0 || foe.Defense != 0)
            {
                return $"③ 3/3 打 3/3 应双方各吃 3 点（Defense 都到 0），实际我方 {mine.Defense}、敌方 {foe.Defense}"
                     + " —— 说明有一边的伤害被「对手已死就不打」那道门吃掉了";
            }

            engine.CheckDeaths();
            if (mine.AliveOnBoard || foe.AliveOnBoard)
            {
                return $"③ 3/3 打 3/3 必须**同归于尽**，实际我方活着={mine.AliveOnBoard} 敌方活着={foe.AliveOnBoard}"
                     + $"（我方位置={mine.Location} 敌方位置={foe.Location}）";
            }
        }

        // ---- ④ 重甲**不减免互斗伤害**：敌方 KV-2（重甲 2）6 防、我方 3 攻 ⇒ 敌方掉满 3 ----
        //
        // ✅ 2026-10-02 修正：本条以前断言的是"6→5"（内核当时的**全局过度减免**），
        //    并在注释里标注了「与蓝图不一致」。现在改成了正确的蓝图行为。
        //    蓝图 `MakeCardsFight` 走的是 `ApplyDamageToCard`（`g.cs:26021/26023`），
        //    而 `ApplyDamageToCard` 是**裸减**（`g.cs:1046-1057`：
        //    `setAndEncryptDefense(toCard, getTotalDefense(toCard) - finalDamage)`），
        //    **不扣重甲**。全库 `getTotalHeavyArmor` 的减伤只在 `CalculateDamageDealt`
        //    （`g.cs:5211-5225`），而它**只被攻击链调用**（`g.cs:4657/4667`、
        //    `_deps/BP_Logic.g.cs:2538/2558`）。
        //    规则参考也印证：`KARDS基础规则参考.md:106`「**重甲不减免指令伤害**」。
        //    ⇒ 修法在 `MatchEngine.ApplyDamage` / `CardApi.ApplyCalculatedDamage`
        //      那个**唯一漏斗**里一处改（判据 `isCombatDamage`），
        //      而不是给互斗单开一条绕过重甲的旁路。
        {
            var (engine, mine, foe) = FightBoard(db, Plain, Armored, 3, 4, 2, 6);
            if (foe.HeavyArmor != 2)
            {
                return $"④ {Armored} 的重甲应为 2，实际 {foe.HeavyArmor} —— 本用例前提不成立";
            }

            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "④ 派发表里没有 `MakeCardsFight` 键";
            }

            engine.CheckDeaths();
            if (foe.Defense != 3)
            {
                return $"④ 重甲 2 的 6 防单位挨 3 点互斗伤害应掉满 3（6→3，互斗不是战斗伤害、不扣重甲），"
                     + $"实际 →{foe.Defense}";
            }

            if (mine.Defense != 2)
            {
                return $"④ 我方 4 防吃 2 点应剩 2，实际 →{mine.Defense}";
            }
        }

        // ---- ⑤ 免疫：有 `Immune` 的一方一点伤害都不吃 ----
        //
        // 出处：蓝图 `ExecuteOnDealDamageAddDamageAfterCalc` si=26
        // `getHasImmune(toCard)` ⇒ `finalDamage = 0`（`g.cs:15979-15985`）；
        // 内核对应 `MatchEngine.ApplyDamage` 的 `Keyword.Immune` 早退（`MatchEngine.cs:1801-1804`）。
        {
            var (engine, mine, foe) = FightBoard(db, Plain, Plain, 3, 4, 2, 5);
            engine.Api.GiveKeyword(foe, Keyword.Immune);

            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "⑤ 派发表里没有 `MakeCardsFight` 键";
            }

            engine.CheckDeaths();
            if (foe.Defense != 5)
            {
                return $"⑤ 有免疫的敌方单位不该掉血（5→{foe.Defense}）—— 免疫没生效";
            }

            if (mine.Defense != 2)
            {
                return $"⑤ 免疫只保护挨打那一方；我方仍该吃 2 点（4→2），实际 →{mine.Defense}";
            }
        }

        return null;
    }

    /// <summary>
    /// **互斗不是攻击** —— 这一族差异全是"有没有走 `ExecuteAttackCard` 那一套"。
    ///
    /// 蓝图证据：`MakeCardsFight`（`g.cs:25907-26040`）**全文没有** `CanAttack` /
    /// `SetAttackerHasAttacked` / `OnBeforeAttack` / `OnAfterAttack` /
    /// `ExecuteOnSurvivedCombatEvents` / 扣油费中的任何一个；
    /// `cardsCheckFunctions` 里也没有对应的门（那份规则库只有 `CanAttack` /
    /// `CanSelectAsTarget`，`MakeCardsFight` 两个都不调）。
    /// 它自己只有两道门：`IsValid`（`si=1C/43`）+ `IsLocatedOnBoard`（`si=FC/1B5`，
    /// 且**只决定那一方向的伤害算不算**，两个 `ApplyDamageToCard` 无条件执行）。
    /// </summary>
    private static string? MakeCardsFightIsNotAnAttack(CardDatabase db)
    {
        const string Plain = "card_unit_panzer_ii_a";
        if (db.Find(Plain) is null) return $"卡库里缺 {Plain}";

        // ---- ① 召唤失调 / 不是行动方 / 油费为 0 的单位照样能被强制互斗 ----
        {
            var (engine, mine, foe) = FightBoard(db, Plain, Plain, 3, 5, 2, 5);

            // 把「攻击那一套门」全部堵死：召唤失调 + 行动方是对面 + 油费为 0。
            engine.Api.RemoveKeyword(mine, Keyword.Blitz);   // 选中的样本天生带 Blitz，先摘掉才能制造召唤失调
            mine.EnteredPlayOnTurn = engine.State.Turn;
            engine.State.ActiveSide = Side.Right;
            engine.State.SetKredits(Side.Left, 0);
            mine.OperationCost = 9;

            if (!mine.HasDeploymentSickness(engine.State))
            {
                return "① 用例前提不成立：没能让单位进入召唤失调状态";
            }

            // 控制组：这种局面下**攻击必须被拒**（不然"互斗不受攻击门限制"就没意义了）
            if (engine.Attack(mine, foe, out string whyAttack))
            {
                return "① 用例前提不成立：召唤失调 + 非行动方 + 无油费的单位居然能发起攻击";
            }

            var trace = new List<string>();
            engine.Api.TriggerTrace = trace;

            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "① 派发表里没有 `MakeCardsFight` 键";
            }

            if (FightFault(engine.State) is { } f) return f;

            if (foe.Defense != 2)
            {
                return $"① 召唤失调/非行动方/油费不足**都不该挡住互斗**，敌方应 5→2，实际 →{foe.Defense}"
                     + $" —— 说明互斗被错误地接上了 `CanAttack` 那一族门（攻击被拒的原因：{whyAttack}）";
            }

            if (engine.State.Kredits(Side.Left) != 0)
            {
                return $"① 互斗**不该扣油费**（谁都不是「攻击方」），实际 kredit 变成 {engine.State.Kredits(Side.Left)}";
            }

            if (mine.HasAttackedThisTurn || mine.AttacksThisTurn != 0)
            {
                return $"① 互斗**不该记「已攻击」**（蓝图全文没有 `SetAttackerHasAttacked`），"
                     + $"实际 HasAttackedThisTurn={mine.HasAttackedThisTurn} AttacksThisTurn={mine.AttacksThisTurn}";
            }

            if (trace.Any(t => t.StartsWith("OnSurvivedCombat", StringComparison.Ordinal)
                            || t.StartsWith("OnOtherCardSurvivedCombat", StringComparison.Ordinal)))
            {
                return "① 互斗**不该发「战斗存活」事件**（那一族只挂在 `ExecuteAttackCard` 上，"
                     + "`g.cs:3130/3186/3234`）—— 实际派发了："
                     + string.Join(", ", trace.Where(t => t.Contains("SurvivedCombat")).Take(3));
            }

            if (trace.Any(t => t.StartsWith("OnAfterAttack", StringComparison.Ordinal)
                            || t.StartsWith("OnBeforeAttack", StringComparison.Ordinal)
                            || t.StartsWith("OnAfterOtherCardAttacks", StringComparison.Ordinal)
                            || t.StartsWith("OnBeforeOtherCardAttacks", StringComparison.Ordinal)))
            {
                return "① 互斗**不该发「攻击前后」事件** —— 实际派发了："
                     + string.Join(", ", trace.Where(t => t.Contains("Attack")).Take(3));
            }
        }

        // ---- ② 互斗双方**可以是同一方**：蓝图里一次阵营判定都没有 ----
        //
        // 参数名虽然叫 `unitThisSide` / `unitOppositeSide`，但函数体
        // （`g.cs:25918-26034`）里没有 `side` / `IsSameSide` / `GetPlayingSide` 之类调用。
        // 内核**照抄**，不自己加门（加了反而与客户端不一致）。
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 20);

            // 两个都是**左方**单位（同一阵营、同一半场）
            var mine = state.CreateWithId(Plain, Side.Left, 11, CardLocation.BoardHqLeft, 1);
            mine.Attack = 3; mine.Defense = 5; mine.MaxDefense = 5; mine.EnteredPlayOnTurn = -99;

            var mate = state.CreateWithId(Plain, Side.Left, 12, CardLocation.BoardHqLeft, 2);
            mate.Attack = 2; mate.Defense = 5; mate.MaxDefense = 5; mate.EnteredPlayOnTurn = -99;

            if (!InvokeMakeCardsFight(engine, mine, mine, mate))
            {
                return "② 派发表里没有 `MakeCardsFight` 键";
            }

            if (FightFault(state) is { } f2) return f2;

            if (mine.Defense != 3 || mate.Defense != 2)
            {
                return $"② 两个**同阵营**单位也该互相打（3/5 与 2/5 ⇒ 3 与 2），"
                     + $"实际 {mine.Defense} 与 {mate.Defense} —— 内核自己加了阵营门（蓝图没有）";
            }
        }

        // ---- ③ `IsValid` 门：任一参数不是卡 ⇒ 整段不发生 ----
        {
            var (engine, mine, foe) = FightBoard(db, Plain, Plain, 3, 5, 2, 5);
            var ctx = new EffectContext
            {
                Engine = engine, State = engine.State, Self = mine, Controller = Side.Left,
            };

            engine.Api.InvokeByName("MakeCardsFight", mine,
                new object?[] { mine, null, mine.CardId }, ctx, out bool handled);
            if (!handled)
            {
                return "③ 派发表里没有 `MakeCardsFight` 键";
            }

            if (mine.Defense != 5 || foe.Defense != 5)
            {
                return $"③ `unitOppositeSide` 无效（null）时整段该不发生（双方都不掉血），"
                     + $"实际 {mine.Defense} 与 {foe.Defense}";
            }
        }

        return null;
    }

    /// <summary>
    /// **互斗的伤害带 `fromFight=True`** —— 这条是"有没有复用同一条伤害修正链"的判据。
    ///
    /// `card_unit_m3_stuart`（M3 STUART）的 `OnCardDealDamage_ModifyDamageDealt`
    /// 逐字是（IR `locals` + `ref/kards-sim/.../USA/Base/units/card_unit_m3_stuart.g.cs:35-51`）：
    /// <code>
    /// if (IsInfantry(toCard) &amp;&amp; (fromAttack || fromFight)) newDamage = damage + 4;
    /// else                                                       newDamage = damage;
    /// </code>
    /// `MakeCardsFight` 内部两次 `ExecuteOnDealDamageAddDamage` 都是
    /// `(…, _fromAttack=False, fromFight=True, _isDefenderDamage=False)`（`g.cs:25977/26006`），
    /// 所以 M3 STUART 互斗打步兵时伤害应当是 `攻击 + 4`。
    ///
    /// ⚠️ 这条同时证明**互斗不是战斗伤害**（`fromAttack=False`）：如果实现时图省事
    /// 把 `isCombatDamage`/`fromAttack` 传成 true，`M3Stuart` 一样会 +4（判据是 `||`），
    /// 所以另外用 `card_unit_2_pounder` 那种**只看 `fromAttack`** 的卡来区分是不行的 ——
    /// 见下面第 ② 段的 `OnOtherCardDealDamageAddDamage` 断言。
    /// </summary>
    private static string? MakeCardsFightPassesFromFightFlag(CardDatabase db)
    {
        const string Stuart = "card_unit_m3_stuart";
        if (db.Find(Stuart) is null) return $"卡库里缺 {Stuart}";

        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry（无法测 `IsInfantry(toCard)` 那一支）";

        // ---- ① 对**步兵**互斗 ⇒ 伤害 = 攻击 + 4（`fromFight` 真的进了修正链）----
        {
            var (engine, mine, foe) = FightBoard(db, Stuart, infantry, 2, 5, 1, 9);
            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "派发表里没有 `MakeCardsFight` 键";
            }

            if (FightFault(engine.State) is { } f) return f;

            // M3 STUART 自己 2 攻 ⇒ 修正后 6 点打在步兵身上 ⇒ 9→3
            if (foe.Defense != 3)
            {
                return $"`{Stuart}` 互斗打步兵应造成 2+4=6 点（9→3），实际 →{foe.Defense}"
                     + " —— 说明互斗的伤害**没带 `fromFight=True`**（或压根没走修正链）"
                     + Dump(engine.State, ("未实现", Unimpl(engine.State)));
            }
        }

        // ---- ② 对**非步兵**互斗 ⇒ 不加成（证明判据是"打谁"，不是"无脑 +4"）----
        {
            string? nonInfantry = db.All
                .FirstOrDefault(c => string.Equals(c.Type, "tank", StringComparison.OrdinalIgnoreCase))?.Name;
            if (nonInfantry is null) return "卡库里没有 tank（无法做对照组）";

            var (engine, mine, foe) = FightBoard(db, Stuart, nonInfantry, 2, 5, 1, 9);
            if (!InvokeMakeCardsFight(engine, mine, mine, foe))
            {
                return "派发表里没有 `MakeCardsFight` 键";
            }

            if (foe.Defense != 7)
            {
                return $"`{Stuart}` 互斗打**非步兵**（{nonInfantry}）应只造成 2 点（9→7），实际 →{foe.Defense}"
                     + " —— `IsInfantry(toCard)` 那一支判错了";
            }
        }

        return null;
    }

    // ==================== ★★ 目标合法性门（2026-10-02） ====================
    //
    // 玩家报告（雪雾）：「有一些有指定向指令或部署效果的单位或指令……使一个
    // （敌方/友方）空军撤退……还有一张卡只能指定老兵单位。模拟器里好像没有限制
    // 条件，人机可以随意指定。」
    //
    // 蓝图证据（客户端选目标的主循环 `_deps/BP_Logic.g.cs:1235-1355`）：
    //   遍历 GetAllCardInBattle → `_card.targetOverride = 候选` →
    //   `_card.CanPlayFromHand(...)` → `CanSelectAsTarget(候选, _card, True, ...)`
    // ⇒「目标类型」判据在**每张卡自己的 CanPlayFromHand** 里。
    //
    // 每条用例都**直接断言中间状态**：逐个候选的判定结果 + 候选枚举的结果。

    /// <summary>
    /// 造一个「手牌里有一张需要目标的牌 + 场上有若干候选」的局面。
    ///
    /// 不调 <c>PlayCard</c>（那会真的结算效果）—— 本组用例只测**门**，
    /// 所以直接问 `engine.Api.CanTarget(...)` / `engine.LegalPlayTargets(...)`。
    /// </summary>
    private static (MatchEngine Engine, CardInstance Card, GameState State) TargetBoard(
        CardDatabase db, string handCard, int kredits = 20)
    {
        var (engine, state) = EmptyBoard(db);
        state.ActiveSide = Side.Left;
        state.SetKredits(Side.Left, kredits);
        var card = state.CreateWithId(handCard, Side.Left, 2, CardLocation.HandLeft, 0);
        return (engine, card, state);
    }

    /// <summary>放一个单位在场上（<paramref name="slot"/> = locationNumber）。</summary>
    private static CardInstance PlaceUnit(GameState state, string name, Side side, int id, int slot)
    {
        var loc = side == Side.Left ? CardLocation.BoardHqLeft : CardLocation.BoardHqRight;
        var u = state.CreateWithId(name, side, id, loc, slot);
        u.EnteredPlayOnTurn = -99;
        return u;
    }

    /// <summary>
    /// `card_event_aa_barrage`「Target air unit must retreat」——
    /// 卡自己的门（IR `CanPlayFromHand`，`i=0..491`）只判 `IsAirUnit(目标)`：
    /// <code>
    /// GetTargetedCard(out hasTarget, out card)
    /// JumpIfNot(hasTarget) → canIt=false, reason="air_unit"
    /// IsAirUnit(card)      → JumpIfNot → canIt=false, reason="air_unit"
    /// HasCustomAbility(card,"cantRetreat") → canIt=false, reason="cantRetreat"
    /// 否则 canIt=true
    /// </code>
    /// 判据里**没有阵营**，所以按玩家说的「卡面说'空军'⇒ 敌我都能指定」：
    /// 友方空军也应当被接受（这一点必须与"敌方空军"那张卡区分开）。
    /// </summary>
    private static string? TargetGateAirOnly(CardDatabase db)
    {
        const string Barrage = "card_event_aa_barrage";
        if (db.Find(Barrage) is null) return $"卡库里缺 {Barrage}";

        string? ground = FindType(db, "tank");
        string? bomber = FindType(db, "bomber");
        string? fighter = FindType(db, "fighter");
        if (ground is null || bomber is null || fighter is null)
        {
            return $"卡库里缺 tank/bomber/fighter（{ground}/{bomber}/{fighter}）";
        }

        var (engine, card, state) = TargetBoard(db, Barrage);
        var foeGround = PlaceUnit(state, ground, Side.Right, 51, 1);
        var foeBomber = PlaceUnit(state, bomber, Side.Right, 52, 2);
        var ownFighter = PlaceUnit(state, fighter, Side.Left, 3, 1);

        // ① 地面单位必须被拒，原因就是蓝图自己写的 "air_unit"
        var g = engine.Api.CanTarget(card, foeGround);
        if (g.Can)
        {
            return $"**地面单位（{ground}）被当成合法目标** —— 卡面是「Target air unit must retreat」"
                 + "（旧实现：候选集是「敌方场上 + 敌方 HQ」这个**超集**，没有任何类型判据）";
        }

        if (g.Reason != "air_unit")
        {
            return $"拒绝原因是 `{g.Describe()}`，应当是蓝图写死的 `air_unit`"
                 + "（IR `card_event_aa_barrage` locals.CanPlayFromHand 的 i=309）";
        }

        // ② 轰炸机 / 战斗机都必须被接受（空军 = 战斗机 + 轰炸机，蓝图 IsAirUnit 的定义）
        var b = engine.Api.CanTarget(card, foeBomber);
        if (!b.Can) return $"**轰炸机（{bomber}）被拒**（{b.Describe()}）—— 空军应当包含轰炸机";

        // ③ 友方空军也要被接受（卡面只说「air unit」，没有阵营限制）
        var f = engine.Api.CanTarget(card, ownFighter);
        if (!f.Can)
        {
            return $"**友方空军被拒**（{f.Describe()}）—— `{Barrage}` 的 CanPlayFromHand 里"
                 + "**没有阵营判据**（IR i=0..491 全文无 IsSameSideUnit / side 比较），"
                 + "所以「空军」这张牌敌我都能指定（玩家口径一致）";
        }

        return null;
    }

    /// <summary>
    /// `card_unit_m16_halftrack`「Deployment: An enemy air or infantry unit must retreat.」——
    /// 卡自己的门（IR `i=0..719`）判据是
    /// `IsSameSideUnit(目标, GetOppositeSide()) &amp;&amp; (IsInfantry(目标) || IsAirUnit(目标))`，
    /// 失败时 reason = `enemy_air_or_infantry_unit`。
    /// ⇒ **敌我限制确实写在卡自己的 IR 里**（而不是规则库的 `CanSelectAsTarget`）。
    /// </summary>
    private static string? TargetGateEnemyAirOrInfantry(CardDatabase db)
    {
        const string Halftrack = "card_unit_m16_halftrack";
        if (db.Find(Halftrack) is null) return $"卡库里缺 {Halftrack}";

        string? infantry = FindType(db, "infantry");
        string? fighter = FindType(db, "fighter");
        string? tank = FindType(db, "tank");
        if (infantry is null || fighter is null || tank is null) return "卡库里缺 infantry/fighter/tank";

        var (engine, card, state) = TargetBoard(db, Halftrack);
        var ownFighter = PlaceUnit(state, fighter, Side.Left, 3, 1);
        var foeFighter = PlaceUnit(state, fighter, Side.Right, 51, 1);
        var foeInfantry = PlaceUnit(state, infantry, Side.Right, 52, 2);
        var foeTank = PlaceUnit(state, tank, Side.Right, 53, 3);

        // ① 友方空军必须被拒（"enemy" 那半句）
        var own = engine.Api.CanTarget(card, ownFighter);
        if (own.Can)
        {
            return $"**友方空军被当成合法目标** —— 卡面是「An **enemy** air or infantry unit」；"
                 + "IR 里的判据是 `IsSameSideUnit(目标, GetOppositeSide())`";
        }

        // ② 敌方空军 / 敌方步兵必须被接受
        var ef = engine.Api.CanTarget(card, foeFighter);
        if (!ef.Can) return $"**敌方空军被拒**（{ef.Describe()}）—— 卡面明确包含 air unit";
        var ei = engine.Api.CanTarget(card, foeInfantry);
        if (!ei.Can) return $"**敌方步兵被拒**（{ei.Describe()}）—— 卡面明确包含 infantry";

        // ③ 敌方坦克必须被拒（"air or infantry" 是白名单，不是"任何敌方单位"）
        var et = engine.Api.CanTarget(card, foeTank);
        if (et.Can) return $"**敌方坦克被当成合法目标** —— 卡面是「enemy **air or infantry** unit」";
        if (et.Reason != "enemy_air_or_infantry_unit")
        {
            return $"敌方坦克的拒绝原因是 `{et.Describe()}`，应当是 `enemy_air_or_infantry_unit`";
        }

        return null;
    }

    /// <summary>
    /// `card_event_breakout`「只能指定老兵」—— IR `locals.CanPlayFromHand`（`i=0..322`）：
    /// <code>
    /// GetTargetedCard(out hasTarget, out card)
    /// JumpIfNot(hasTarget) → canIt=false, reason="veteran_unit"
    /// IsVeteran(card)      → JumpIfNot → canIt=false, reason="veteran_unit"
    /// 否则 canIt=true
    /// </code>
    /// </summary>
    private static string? TargetGateVeteranOnly(CardDatabase db)
    {
        const string Breakout = "card_event_breakout";
        if (db.Find(Breakout) is null) return $"卡库里缺 {Breakout}";

        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        var (engine, card, state) = TargetBoard(db, Breakout);
        var plain = PlaceUnit(state, infantry, Side.Right, 51, 1);
        var vet = PlaceUnit(state, infantry, Side.Right, 52, 2);
        vet.Keywords.Add(Keyword.Veteran);

        // ① 非老兵必须被拒
        var no = engine.Api.CanTarget(card, plain);
        if (no.Can)
        {
            return $"**非老兵被当成合法目标** —— `{Breakout}` 的卡面/IR 要求目标必须是老兵";
        }

        if (no.Reason != "veteran_unit")
        {
            return $"拒绝原因是 `{no.Describe()}`，应当是蓝图写死的 `veteran_unit`"
                 + "（IR i=231）";
        }

        // ② 老兵必须被接受（否则上面那条可能是"恒拒"）
        var yes = engine.Api.CanTarget(card, vet);
        if (!yes.Can)
        {
            return $"**老兵被拒**（{yes.Describe()}）—— 上面那条「非老兵被拒」可能是恒拒，"
                 + "不是真的读了 `IsVeteran(目标)`";
        }

        return null;
    }

    /// <summary>
    /// `card_event_air_corps_ferrying`「Give a friendly unit +1+1」——
    /// IR `locals.CanPlayFromHand`：`IsSameSideUnit(目标, side)`，失败 reason = `friendly_unit`。
    /// 这条同时证明：**候选必须含友方** —— 旧实现只枚举「敌方场上 + 敌方 HQ」，
    /// 这张牌在 bot 手里**一个合法目标都没有**，而旧代码不跳过、拿敌方单位硬顶。
    /// </summary>
    private static string? TargetGateFriendlyOnly(CardDatabase db)
    {
        const string Ferrying = "card_event_air_corps_ferrying";
        if (db.Find(Ferrying) is null) return $"卡库里缺 {Ferrying}";

        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        var (engine, card, state) = TargetBoard(db, Ferrying);
        var own = PlaceUnit(state, infantry, Side.Left, 3, 1);
        var foe = PlaceUnit(state, infantry, Side.Right, 51, 1);

        var bad = engine.Api.CanTarget(card, foe);
        if (bad.Can)
        {
            return $"**敌方单位被当成合法目标** —— 卡面是「Give a **friendly** unit +1+1」";
        }

        if (bad.Reason != "friendly_unit")
        {
            return $"拒绝原因是 `{bad.Describe()}`，应当是 `friendly_unit`";
        }

        var good = engine.Api.CanTarget(card, own);
        if (!good.Can) return $"**友方单位被拒**（{good.Describe()}）—— 卡面要求的就是友方";

        // 候选枚举里必须有友方单位（旧实现恒为空 ⇒ 这张牌永远打不出去）
        var legal = engine.LegalPlayTargets(card);
        if (!legal.Contains(own))
        {
            return "`LegalPlayTargets` 里**没有友方单位** —— 候选集仍然只有敌方（旧行为）";
        }

        return null;
    }

    /// <summary>
    /// `card_event_the_commonwealth`「只能指定 HQ」—— IR `locals.CanPlayFromHand`：
    /// `IsLocation(目标)`，失败 reason = `hq`。
    ///
    /// ⚠️ 这条同时守规则库门的一处**极易写错**的地方：
    /// `CanSelectAsTarget` 的第 2 步是 `IsLocatedOnBoard(Targeted)`，而**蓝图语义含 HQ**
    /// （`Loc is Board or Frontline`）。内核里另有一个 `CardApi.IsLocatedOnBoard`
    /// **排除 HQ**（见 `MakeCardsFight` 的注释）—— 用它就会把"指定 HQ"全部拒掉。
    /// </summary>
    private static string? TargetGateHqOnly(CardDatabase db)
    {
        const string Commonwealth = "card_event_the_commonwealth";
        if (db.Find(Commonwealth) is null) return $"卡库里缺 {Commonwealth}";

        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        var (engine, card, state) = TargetBoard(db, Commonwealth);
        var unit = PlaceUnit(state, infantry, Side.Right, 51, 1);
        var foeHq = state.Hq(Side.Right);

        var bad = engine.Api.CanTarget(card, unit);
        if (bad.Can) return "**单位被当成合法目标** —— 这张牌只能指定 HQ";
        if (bad.Reason != "hq") return $"拒绝原因是 `{bad.Describe()}`，应当是 `hq`";

        var good = engine.Api.CanTarget(card, foeHq);
        if (!good.Can)
        {
            return $"**HQ 被拒**（{good.Describe()}）—— `CanSelectAsTarget` 的 "
                 + "`IsLocatedOnBoard` 必须用蓝图语义（含 HQ），"
                 + "不能用内核那个排除 HQ 的 `CardApi.IsLocatedOnBoard`";
        }

        return null;
    }

    /// <summary>
    /// ★★ **候选枚举**里不许出现非法目标 —— 这是"接进目标枚举"这件事本身的断言。
    ///
    /// 直接断言**中间状态**（枚举结果），而不是"连打 N 局看结果"：
    /// 种子固定时后者可能恰好都通过。
    ///
    /// 同时给出**修复前的对照**：旧候选集（敌方场上 + 敌方 HQ）**包含**那个地面单位，
    /// 而新候选集不含 —— 这一条就是"修复前失败"的可复现证据。
    /// </summary>
    private static string? TargetGateCandidateEnumeration(CardDatabase db)
    {
        const string Barrage = "card_event_aa_barrage";
        if (db.Find(Barrage) is null) return $"卡库里缺 {Barrage}";

        string? ground = FindType(db, "tank");
        string? bomber = FindType(db, "bomber");
        string? fighter = FindType(db, "fighter");
        if (ground is null || bomber is null || fighter is null) return "卡库里缺 tank/bomber/fighter";

        var (engine, card, state) = TargetBoard(db, Barrage);
        var foeGround = PlaceUnit(state, ground, Side.Right, 51, 1);
        var foeBomber = PlaceUnit(state, bomber, Side.Right, 52, 2);
        var ownFighter = PlaceUnit(state, fighter, Side.Left, 3, 1);

        var legal = engine.LegalPlayTargets(card);

        // ① 旧候选集（超集）**确实**包含地面单位 —— 证明"只加超集"是不够的
        var legacy = state.Board(Side.Right).Where(c => c.IsAlive)
                          .Concat(new[] { state.Hq(Side.Right) }).ToList();
        if (!legacy.Contains(foeGround))
        {
            return "测试自身失效：旧候选集里居然没有那个地面单位（局面摆错了）";
        }

        // ② 新候选集必须**不含**地面单位
        if (legal.Contains(foeGround))
        {
            return $"**候选枚举里仍有非法目标**：地面单位 {ground}#{foeGround.CardId}"
                 + " —— 客户端会静默不执行（记牌器 +1、场上无变化 = 虚空牌）";
        }

        // ③ 合法目标（敌方轰炸机）必须**在**候选集里（否则可能是"全部拒掉"）
        if (!legal.Contains(foeBomber))
        {
            return $"候选枚举里**没有**敌方轰炸机 {bomber} —— 合法目标被误杀（门写太严）";
        }

        // ④ 友方空军也应当在（卡面只说 air unit，没有阵营限制）
        if (!legal.Contains(ownFighter))
        {
            return $"候选枚举里**没有**友方空军 {fighter} —— `{Barrage}` 的卡面没有阵营限制，"
                 + "敌我空军都该能指定（玩家口径一致）";
        }

        // ⑤ HQ 不该在这张牌的候选里（IsAirUnit(HQ) 恒假）
        if (legal.Contains(state.Hq(Side.Right)))
        {
            return "候选枚举里出现了敌方 HQ —— 这张牌只能指空军，HQ 是位置卡";
        }

        return null;
    }

    /// <summary>
    /// ★ 守卫：`card-ir.json` 里必须真的带每张卡自己的 `CanPlayFromHand`
    /// （`klink bot/tools/gen-kismet-ir.py` 的 `LOCAL_FUNCTIONS`）。
    ///
    /// 为什么需要它：这道门**完全依赖那份 IR** —— 生成器一改回去（或忘了重生成），
    /// `CanPlayFromHandOn` 会找不到 locals、**静默放行所有目标**，
    /// 而其它自测照样全绿（门变成了 no-op，没有任何断言会红）。
    /// </summary>
    private static string? TargetGateIrHasCanPlayFromHand(CardDatabase db)
    {
        string[] probes =
        {
            "card_event_aa_barrage",      // 只能空军
            "card_event_breakout",        // 只能老兵
            "card_event_air_corps_ferrying", // 只能友方
            "card_event_the_commonwealth",   // 只能 HQ
        };

        int withGate = 0;
        foreach (var (name, _) in KismetLibrary.Default!.AllCards)
        {
            if (KismetLibrary.Default.FindLocalProgram(name, "CanPlayFromHand") is not null)
            {
                withGate++;
            }
        }

        // 全卡池实测 438 张（`card-effects.json` 的 `functions.CanPlayFromHand` 计数）。
        // 这里不写死 438（卡池会变），只要求"数量级对得上 + 探针卡都在"。
        if (withGate < 400)
        {
            return $"IR 里只有 {withGate} 张卡带 `CanPlayFromHand`（实测应为 438）"
                 + " —— `card-ir.json` 是否用带 `CanPlayFromHand` 的 `LOCAL_FUNCTIONS` 重新生成过？";
        }

        foreach (string p in probes)
        {
            if (db.Find(p) is null) return $"卡库里缺探针卡 {p}";
            if (KismetLibrary.Default.FindLocalProgram(p, "CanPlayFromHand") is null)
            {
                return $"IR 里没有 `{p}` 的 `CanPlayFromHand` 局部函数"
                     + "（`card-ir.json` 是否重新生成过？）";
            }
        }

        return null;
    }

    /// <summary>
    /// 规则库门 `CanSelectAsTarget` 的三条独立判据（不在场上 / kredit 不足 / 不在场上的卡）。
    ///
    /// 为什么单独测它：`CanPlayFromHand` 与 `CanSelectAsTarget` 是**两道**门，
    /// 上面几条测的是第一道。这一条把第二道单独钉住 —— 将来谁把两道合成一道、
    /// 或者把 `IsLocatedOnBoard` 换成内核那个排除 HQ 的版本，这里会红。
    /// </summary>
    private static string? TargetGateLibraryChecks(CardDatabase db)
    {
        const string Monty = "card_event_monty";
        if (db.Find(Monty) is null) return $"卡库里缺 {Monty}";

        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        // ---- ① 不在场上（手牌里）的卡不能当目标 ----
        {
            var (engine, card, state) = TargetBoard(db, Monty);
            var inHand = state.CreateWithId(infantry, Side.Right, 51, CardLocation.HandRight, 0);
            var r = engine.Api.CanSelectAsTarget(card, inHand, byPlayFromHand: true);
            if (r.Can)
            {
                return "手牌里的卡被 `CanSelectAsTarget` 放行 —— 第 2 步 `IsLocatedOnBoard` 失效了";
            }

            if (r.Reason != "not_on_board") return $"拒绝原因是 `{r.Describe()}`，应当是 `not_on_board`";
        }

        // ---- ② kredit 不足 ⇒ `play_from_hand_not_enough_kredits_to_target` ----
        {
            // 英联邦 12 费，只给 5 点 ⇒ 余下 -7 < 0
            const string Expensive = "card_event_the_commonwealth";
            if (db.Find(Expensive) is null) return $"卡库里缺 {Expensive}";

            var (engine, card, state) = TargetBoard(db, Expensive, kredits: 5);
            var unit = PlaceUnit(state, infantry, Side.Right, 51, 1);
            if (card.KreditCost <= 5)
            {
                return $"测试前提失效：`{Expensive}` 的费用是 {card.KreditCost}，给 5 点 kredit 不足以触发费用门";
            }

            var r = engine.Api.CanSelectAsTarget(card, unit, byPlayFromHand: true);
            if (r.Can) return "kredit 不够却放行了 —— 第 5 步的费用判据失效";
            if (r.Reason != "play_from_hand_not_enough_kredits_to_target")
            {
                return $"拒绝原因是 `{r.Describe()}`，应当是 `play_from_hand_not_enough_kredits_to_target`";
            }
        }

        // ---- ③ 死亡/离场的卡不能当目标（`IsValid` + `IsLocatedOnBoard`）----
        {
            var (engine, card, state) = TargetBoard(db, Monty);
            var unit = PlaceUnit(state, infantry, Side.Right, 51, 1);
            state.Move(unit, CardLocation.Discard);
            var r = engine.Api.CanSelectAsTarget(card, unit, byPlayFromHand: true);
            if (r.Can) return "已经进弃牌堆的卡被 `CanSelectAsTarget` 放行";
        }

        // ---- ④ null 目标必须被拒（不是"无目标合法"）----
        {
            var (engine, card, _) = TargetBoard(db, Monty);
            if (engine.Api.CanTarget(card, null).Can)
            {
                return "`CanTarget(card, null)` 放行了 —— null 必须当成「没有目标」拒绝";
            }
        }

        return null;
    }

    /// <summary>
    /// `PayCardCost` adds an enemy target's tax to the current card cost before
    /// checking and charging kredit. Friendly targets do not add the tax.
    /// </summary>
    private static string? PlayCardEnemyTargetTax(CardDatabase db)
    {
        const string Order = "card_event_aa_barrage";
        const string AirUnit = "card_unit_j2m_raiden";
        const int Tax = 2;

        if (db.Find(Order) is null) return $"卡库里缺 {Order}";
        if (db.Find(AirUnit) is null) return $"卡库里缺 {AirUnit}";

        // Enemy target: the tax is part of the amount actually charged.
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            var order = state.CreateWithId(Order, Side.Left, 2, CardLocation.HandLeft, 0);
            var target = PlaceUnit(state, AirUnit, Side.Right, 42, 1);
            target.KreditsTaxAsEnemyTarget = Tax;
            int total = order.KreditCost + Tax;
            state.SetKredits(Side.Left, total);

            if (!engine.PlayCard(order, target))
            {
                return $"敌方目标税为 {Tax} 时，余额 {total} 的出牌被拒";
            }

            if (state.Kredits(Side.Left) != 0)
            {
                return $"敌方目标税未计入扣费：剩余 {state.Kredits(Side.Left)}，应为 0";
            }
        }

        // Friendly target: the same tax value must not be charged.
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            var order = state.CreateWithId(Order, Side.Left, 2, CardLocation.HandLeft, 0);
            var target = PlaceUnit(state, AirUnit, Side.Left, 42, 1);
            target.KreditsTaxAsEnemyTarget = Tax;
            int baseCost = order.KreditCost;
            state.SetKredits(Side.Left, baseCost);

            if (!engine.PlayCard(order, target))
            {
                return "同阵营目标不应因目标税而被拒";
            }

            if (state.Kredits(Side.Left) != 0)
            {
                return $"同阵营目标错误收税：剩余 {state.Kredits(Side.Left)}，应为 0";
            }
        }

        // One kredit short of the taxed cost: reject without changing state.
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            var order = state.CreateWithId(Order, Side.Left, 2, CardLocation.HandLeft, 0);
            var target = PlaceUnit(state, AirUnit, Side.Right, 42, 1);
            target.KreditsTaxAsEnemyTarget = Tax;
            int available = Math.Max(0, order.KreditCost + Tax - 1);
            state.SetKredits(Side.Left, available);

            if (engine.PlayCard(order, target))
            {
                return "余额不足以支付敌方目标税时仍然出牌成功";
            }

            if (state.Kredits(Side.Left) != available
                || order.Location != CardLocation.HandLeft
                || target.Location != CardLocation.BoardHqRight)
            {
                return "敌方目标税支付失败后修改了 kredit、出牌卡或目标位置";
            }
        }

        return null;
    }

    // ==================== ★★ 攻击路径的目标合法性门（2026-10-02 第三轮） ====================
    //
    // 上一轮把 `CanSelectAsTarget` 只接进了**出牌路径**（`CardApi.CanTarget` /
    // `MatchEngine.LegalPlayTargets`），**攻击路径完全没接**：
    // `MatchEngine.LegalTargets` 只做了「射程 + 烟幕 + 掩护」三条**手写**判据，
    // `MatchEngine.Attack` 同样。而蓝图 `CanAttack` 的末尾就是**直接调规则库**
    //（`ref/kards-sim/KardsSim/Generated/_deps/cardsCheckFunctions.g.cs:902`）：
    // <code>
    // CanSelectAsTarget(self, defenderCard, attackerCard, False, __WorldContext, out can, out Reason, …)
    // if (!can) { canAttack = False; failReason = Reason; }        // :904 / :930
    // </code>
    //
    // ⇒ 两条判据在攻击路径上**从来没有生效过**（这是本次改动真正的收益）：
    //   ⑥ 被敌方指定的额外税 `KreditsTax_AsEnemyTarget`（全池 3 张税卡）
    //   ⑧ 触发点 2 的否决位 `CanOtherCardBeTargetted`（全池唯一实现者 commando）
    // 下面每条都**直接断言中间状态**（门的判定 + 拒绝原因 + 无副作用），
    // 不是"连打 N 局看结果" —— 种子固定时后者可能恰好都通过。

    /// <summary>攻击路径的门 ⑥：被敌方指定的额外税（3 张税卡各一条）。</summary>
    private static string? TargetGateAttackTax(CardDatabase db)
    {
        // 3 张税卡 + 各自税额。出处 `CardInstance.KreditsTaxAsEnemyTarget` 的注释
        //（写方 `BP_CardFunctions.AddKreditsTax`；`card_event_order_of_the_day` +1、
        //  `card_event_grim_day` ±2、`card_unit_tupolev_sb_2` ±2）。
        // 这里直接摆「税已加上」的局面，而不是去跑那三张卡的整套效果 ——
        // 本组测的是**门**，税是怎么加上去的与门无关。
        (string Card, int Tax)[] taxes =
        {
            ("card_event_order_of_the_day", 1),
            ("card_event_grim_day", 2),
            ("card_unit_tupolev_sb_2", 2),
        };

        const string Attacker = "card_unit_a26_invader";   // 4/4 轰炸机，行动费 2，射程 2
        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        foreach (var (taxCard, tax) in taxes)
        {
            var def = db.Find(taxCard);
            if (def is null) return $"卡库里缺税卡 {taxCard}";

            // 守卫：这张卡必须**真的**是税卡（蓝图里调过 `AddKreditsTax`），
            // 否则下面测的就是我们自己编的数字，不是卡池事实。
            if (!def.FunctionCalls.Values.Any(cs => cs.Contains("AddKreditsTax")))
            {
                return $"`{taxCard}` 的蓝图里没有 `AddKreditsTax` —— 测试前提失效（它不是税卡）";
            }

            // ① 余下 kredit 刚好够付行动费（2）、但不够付税 ⇒ 必须拒，原因 `cost_extra_to_target`
            {
                var (engine, _, state) = TargetBoard(db, taxCard, kredits: 2);
                var attacker = PlaceUnit(state, Attacker, Side.Left, 3, 1);
                var foe = PlaceUnit(state, infantry, Side.Right, 51, 1);
                foe.KreditsTaxAsEnemyTarget = tax;

                var r = engine.Api.CanSelectAsTarget(attacker, foe, byPlayFromHand: false);
                if (r.Can)
                {
                    return $"`{taxCard}`（税 {tax}）：余下 kredit 0 < 税 {tax}，目标却**被放行** —— "
                         + "攻击路径的第 ⑥ 条判据没生效（旧行为：门根本没接进攻击路径）";
                }

                if (r.Reason != "cost_extra_to_target")
                {
                    return $"`{taxCard}`：拒绝原因是 `{r.Describe()}`，应当是 `cost_extra_to_target`"
                         + "（蓝图 `cardsCheckFunctions.g.cs:1184`）";
                }
            }

            // ② ★ 反向：余下 kredit 够付税 ⇒ **必须放行**（守「不是恒拒」）
            {
                var (engine, _, state) = TargetBoard(db, taxCard, kredits: 2 + tax);
                var attacker = PlaceUnit(state, Attacker, Side.Left, 3, 1);
                var foe = PlaceUnit(state, infantry, Side.Right, 51, 1);
                foe.KreditsTaxAsEnemyTarget = tax;

                var r = engine.Api.CanSelectAsTarget(attacker, foe, byPlayFromHand: false);
                if (!r.Can)
                {
                    return $"`{taxCard}`（税 {tax}）：余下 kredit {tax} ≥ 税，目标却**被拒**"
                         + $"（{r.Describe()}）—— 上面那条可能是恒拒，而不是真的在算税";
                }
            }

            // ③ ★ 反向：**同阵营不付税**（蓝图 `SelectInt(0, 税, 同阵营)`）
            {
                var (engine, _, state) = TargetBoard(db, taxCard, kredits: 2);
                var attacker = PlaceUnit(state, Attacker, Side.Left, 3, 1);
                var own = PlaceUnit(state, infantry, Side.Left, 4, 2);
                own.KreditsTaxAsEnemyTarget = tax;

                var r = engine.Api.CanSelectAsTarget(attacker, own, byPlayFromHand: false);
                if (!r.Can)
                {
                    return $"`{taxCard}`：**同阵营**目标被收税（{r.Describe()}）—— 蓝图是 "
                         + "`SelectInt(0, KreditsTax_AsEnemyTarget, 同阵营)`，同阵营恒不付税";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 攻击路径的门 ⑧：触发点 2 的否决位（全池唯一实现者 `card_unit_no_3_commando`，
    /// 卡面「Units with 4 or more attack cannot attack.」）。
    /// </summary>
    private static string? TargetGateCommandoVeto(CardDatabase db)
    {
        const string Commando = "card_unit_no_3_commando";
        const string Attacker = "card_unit_a26_invader";   // 总攻 4
        if (db.Find(Commando) is null) return $"卡库里缺 {Commando}";

        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        // ★ 证据：这条自测**实际走的是哪条路**（不许只断言结论）。
        // `CanOtherCardBeTargetted` 的实现是「先跑卡自己的 IR 函数体，拿不到才用转写兜底」，
        // 而 `card-ir.json` 目前**没有**这张卡的函数体（它没有任何事件入口，
        // 生成器对这类卡直接 continue）⇒ 预期走兜底。
        var program = KismetLibrary.Default?.FindLocalProgram(Commando, "CanOtherCardBeTargetted");
        Console.WriteLine("      [commando 判据来源] "
            + (program is null
                ? "兜底转写体（`card-ir.json` 里没有这张卡的 `CanOtherCardBeTargetted` 函数体）"
                : $"通用路径（跑卡自己的 IR 函数体，{program.Steps.Count} 步）"));

        var (engine, _, state) = TargetBoard(db, Commando, kredits: 20);
        var attacker = PlaceUnit(state, Attacker, Side.Left, 3, 1);
        var commando = PlaceUnit(state, Commando, Side.Right, 51, 1);
        var foe = PlaceUnit(state, infantry, Side.Right, 52, 3);

        if (attacker.Attack < 4) return $"测试前提失效：`{Attacker}` 的总攻 {attacker.Attack} < 4";

        // ① 攻方总攻 ≥ 4、commando 在场、**攻击路径** ⇒ 否决，原因逐字 `unit_cant_attack`
        {
            var r = engine.Api.CanSelectAsTarget(attacker, foe, byPlayFromHand: false);
            if (r.Can)
            {
                return $"commando 在场、攻方总攻 {attacker.Attack} ≥ 4，目标却**被放行** —— "
                     + "攻击路径的第 ⑧ 条判据没生效（旧行为：门根本没接进攻击路径）";
            }

            if (r.Reason != "unit_cant_attack")
            {
                return $"拒绝原因是 `{r.Describe()}`，应当是蓝图写死的 `unit_cant_attack`"
                     + "（`card_unit_no_3_commando.g.cs:57`）";
            }
        }

        // ② ★ `self` 是**订阅者**（commando 自己），**不是**被指的目标 ⇒
        //    打「第三张卡」和打 commando 自己都必须被否。这条同时把
        //    「把 `self` 误当成 `targeted`」那种写法钉死。
        {
            var r = engine.Api.CanSelectAsTarget(attacker, commando, byPlayFromHand: false);
            if (r.Can)
            {
                return "打 commando 自己**被放行** —— 蓝图里 `IsLocatedOnBoard(self)` 的 `self` "
                     + "是订阅者，不是 `targetCard`";
            }
        }

        return null;
    }

    /// <summary>
    /// ★★ **反向用例（最重要）**：不满足条件时**必须放行**。
    ///
    /// 这是防「把门做成恒拒」的安全网 —— 恒拒会让合法攻击也被拒、回放侧立刻变差，
    /// 而 4 张受影响卡都不在回放语料里，这种回归**从数字上看不出来**。
    /// 四个不满足条件的分支各一条：总攻 &lt; 4 / commando 不在场 / 出牌路径 / 真的打一次。
    /// </summary>
    private static string? TargetGateAttackPathAllows(CardDatabase db)
    {
        const string Commando = "card_unit_no_3_commando";
        const string Strong = "card_unit_a26_invader";     // 总攻 4
        const string Weak = "card_unit_120mm_m1_gun";      // 总攻 3，炮兵，射程 2

        if (db.Find(Commando) is null) return $"卡库里缺 {Commando}";
        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        // ① 攻方总攻 < 4 + commando 在场 ⇒ 放行
        {
            var (engine, _, state) = TargetBoard(db, Commando, kredits: 20);
            var weak = PlaceUnit(state, Weak, Side.Left, 3, 1);
            PlaceUnit(state, Commando, Side.Right, 51, 1);
            var foe = PlaceUnit(state, infantry, Side.Right, 52, 3);

            if (weak.Attack >= 4)
            {
                return $"测试前提失效：`{Weak}` 的总攻是 {weak.Attack}，不是 < 4";
            }

            var r = engine.Api.CanSelectAsTarget(weak, foe, byPlayFromHand: false);
            if (!r.Can)
            {
                return $"攻方总攻 {weak.Attack} < 4、commando 在场，目标却**被拒**（{r.Describe()}）"
                     + " —— 门写太严（commando 的判据是「4 攻**以上**不能攻击」）";
            }

            if (!engine.LegalTargets(weak).Contains(foe))
            {
                return "`LegalTargets` 里没有这个合法目标 —— 候选枚举把合法攻击误杀了";
            }
        }

        // ② 攻方总攻 ≥ 4、但 commando **不在场** ⇒ 放行
        {
            var (engine, _, state) = TargetBoard(db, Commando, kredits: 20);
            var strong = PlaceUnit(state, Strong, Side.Left, 3, 1);
            var foe = PlaceUnit(state, infantry, Side.Right, 51, 1);

            var r = engine.Api.CanSelectAsTarget(strong, foe, byPlayFromHand: false);
            if (!r.Can)
            {
                return $"commando 不在场、攻方总攻 {strong.Attack} ≥ 4，目标却**被拒**（{r.Describe()}）"
                     + " —— 这条判据要求订阅者自己在场（蓝图 `IsLocatedOnBoard(self)`）";
            }

            if (!engine.LegalTargets(strong).Contains(foe))
            {
                return "`LegalTargets` 里没有这个合法目标 —— 候选枚举把合法攻击误杀了";
            }
        }

        // ③ 攻方总攻 ≥ 4、commando 在场，但 `byPlayFromHand: true`（**出牌路径**）⇒ 放行。
        //    蓝图第一条判据是 `Not_PreBool(byPlayFromHand)`：出牌路径恒不否决。
        {
            var (engine, card, state) = TargetBoard(db, Strong, kredits: 20);
            PlaceUnit(state, Commando, Side.Right, 51, 1);
            var foe = PlaceUnit(state, infantry, Side.Right, 52, 3);

            var r = engine.Api.CanSelectAsTarget(card, foe, byPlayFromHand: true);
            if (!r.Can)
            {
                return $"**出牌路径**被 commando 否决了（{r.Describe()}）—— 蓝图卡版的第一条判据是 "
                     + "`Not_PreBool(byPlayFromHand)`，出牌路径必须完全不受它影响";
            }
        }

        // ④ commando 在**弃牌堆**（订阅表里仍在，但不在场上）⇒ 放行
        {
            var (engine, _, state) = TargetBoard(db, Commando, kredits: 20);
            var strong = PlaceUnit(state, Strong, Side.Left, 3, 1);
            var commando = PlaceUnit(state, Commando, Side.Right, 51, 1);
            var foe = PlaceUnit(state, infantry, Side.Right, 52, 3);
            state.Move(commando, CardLocation.Discard);

            var r = engine.Api.CanSelectAsTarget(strong, foe, byPlayFromHand: false);
            if (!r.Can)
            {
                return $"commando 已经进弃牌堆，目标却**被拒**（{r.Describe()}）—— "
                     + "这条判据要求订阅者**在场上**（蓝图 `IsLocatedOnBoard(self)`）";
            }
        }

        // ⑤ 端到端：commando 不在场时，4 攻单位的攻击必须**真的打得出去**
        //    （前四条都只问门，这一条证明门不会拦下合法攻击）
        {
            var (engine, _, state) = TargetBoard(db, Commando, kredits: 20);
            var strong = PlaceUnit(state, Strong, Side.Left, 3, 1);
            var foe = PlaceUnit(state, infantry, Side.Right, 51, 1);

            if (!engine.Attack(strong, foe, out string why))
            {
                return $"合法攻击被拒（{why}）—— 门把合法攻击也拦了（恒拒回归）";
            }
        }

        return null;
    }

    /// <summary>
    /// ★ **攻击路径与出牌路径用的是同一道门** —— 直接断言三件事：
    /// ① `Attack` 的拒绝原因**逐字来自门**（不是另写一份判据）；
    /// ② 被拒的攻击**没有任何副作用**（门必须在 `State.AddKredits` 之前）；
    /// ③ 候选枚举 `LegalTargets` 与门的结论一致（枚举与结算不可能漂移）。
    /// </summary>
    private static string? TargetGateSharedByBothPaths(CardDatabase db)
    {
        const string Commando = "card_unit_no_3_commando";
        const string Attacker = "card_unit_a26_invader";
        if (db.Find(Commando) is null) return $"卡库里缺 {Commando}";

        string? infantry = FindType(db, "infantry");
        if (infantry is null) return "卡库里没有 infantry";

        var (engine, _, state) = TargetBoard(db, Commando, kredits: 20);
        var attacker = PlaceUnit(state, Attacker, Side.Left, 3, 1);
        PlaceUnit(state, Commando, Side.Right, 51, 1);
        var foe = PlaceUnit(state, infantry, Side.Right, 52, 3);
        state.ActiveSide = Side.Left;

        var gate = engine.Api.CanSelectAsTarget(attacker, foe, byPlayFromHand: false);
        if (gate.Can)
        {
            return "测试前提失效：这道门本应拒绝这次攻击（commando 在场 + 攻方总攻 ≥ 4）";
        }

        // ① 结算路径的拒绝原因必须**逐字**带上门的 `Describe()`
        if (engine.Attack(attacker, foe, out string reason))
        {
            return "`Attack` 竟然放行了 —— 结算路径没有走这道门（这正是本次要修的缺口）";
        }

        if (!reason.Contains(gate.Describe(), StringComparison.Ordinal))
        {
            return $"`Attack` 的拒绝原因是 `{reason}`，里面没有门的 `{gate.Describe()}` —— "
                 + "两条路径的判据不是同一份（枚举一套、结算另一套）";
        }

        // ② 被拒 ⇒ 不许有任何副作用（门在扣油费 / 置已攻击 / 发触发**之前**）
        if (state.Kredits(Side.Left) != 20)
        {
            return $"被拒的攻击扣了油费（kredit {state.Kredits(Side.Left)} ≠ 20）—— "
                 + "门插在 `State.AddKredits` 之后了（门自己会算一次行动费，会变成减两次 ⇒ 误拒合法攻击）";
        }

        if (attacker.HasAttackedThisTurn)
        {
            return "被拒的攻击置了 `HasAttackedThisTurn` —— 门插在副作用之后了";
        }

        if (attacker.AttacksThisTurn != 0)
        {
            return $"被拒的攻击算了攻击额度（{attacker.AttacksThisTurn} ≠ 0）—— 门插在副作用之后了";
        }

        // ③ 候选枚举必须与门同结论
        if (engine.LegalTargets(attacker).Contains(foe))
        {
            return "`LegalTargets` 列出了 `Attack` 会拒的目标 —— 枚举与结算漂移"
                 + "（AI 会反复尝试一个永远失败的动作）";
        }

        // ④ 出牌路径走的也是同一个方法（`CardApi.CanTarget` → `CanSelectAsTarget`），
        //    两条路径的差别**只在实参** `byPlayFromHand`：
        //    出牌路径 True ⇒ commando 的判据恒不生效（见蓝图卡版的第一条）。
        {
            var playCard = state.CreateWithId(Attacker, Side.Left, 9, CardLocation.HandLeft, 0);
            var playGate = engine.Api.CanSelectAsTarget(playCard, foe, byPlayFromHand: true);
            if (!playGate.Can)
            {
                return $"**出牌路径**被 commando 否决了（{playGate.Describe()}）—— 两条路径共用同一个方法，"
                     + "差别只应在 `byPlayFromHand` 这个实参上";
            }
        }

        return null;
    }

    /// <summary>
    /// `CanAttack` 的卡面限制门：
    /// `cantAttack:&lt;type&gt;` 在攻击方，`cantBeAttackedBy:&lt;type&gt;` 在目标方。
    /// 这些标记分别由 `CustomAbilityAdd` 与 `CustomName1Add` 写入，
    /// 所以测试同时走攻击结算和候选枚举，防止两条路径漂移。
    /// </summary>
    private static string? AttackRestrictionAbilities(CardDatabase db)
    {
        string? ground = FindType(db, "infantry");
        string? air = FindType(db, "fighter");
        if (ground is null || air is null)
        {
            return "卡库里没有 infantry/fighter";
        }

        // ① cantAttack:location：不能攻击 HQ；门必须在扣费和攻击计数之前。
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 20);
            var attacker = state.CreateWithId(ground, Side.Left, 3,
                CardLocation.BoardFrontline, 0);
            attacker.EnteredPlayOnTurn = -99;
            attacker.CustomAbility = "cantAttack:location";
            var hq = state.Hq(Side.Right);

            if (engine.Attack(attacker, hq, out string reason))
            {
                return "cantAttack:location 的攻击者不应能攻击敌方 HQ";
            }

            if (reason != "cant_be_attacked_by_unit"
                || state.Kredits(Side.Left) != 20
                || attacker.HasAttackedThisTurn
                || attacker.AttacksThisTurn != 0)
            {
                return $"HQ 限制门状态错误：reason={reason} kredit={state.Kredits(Side.Left)} "
                     + $"hasAttacked={attacker.HasAttackedThisTurn} count={attacker.AttacksThisTurn}";
            }

            if (engine.LegalTargets(attacker).Contains(hq))
            {
                return "LegalTargets 不应列出被 cantAttack:location 排除的 HQ";
            }
        }

        // ② cantAttack:air：攻击方不能攻击空军目标，但仍可选地面单位。
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 20);
            var attacker = state.CreateWithId(ground, Side.Left, 3,
                CardLocation.BoardFrontline, 0);
            attacker.EnteredPlayOnTurn = -99;
            attacker.CustomAbility = "cantAttack:air";
            var foeAir = PlaceUnit(state, air, Side.Right, 51, 1);
            var foeGround = PlaceUnit(state, ground, Side.Right, 52, 2);

            if (engine.Attack(attacker, foeAir, out string airReason)
                || airReason != "cant_be_attacked_by_unit")
            {
                return $"cantAttack:air 未拒绝空军目标：reason={airReason}";
            }

            if (!engine.LegalTargets(attacker).Contains(foeGround)
                || engine.LegalTargets(attacker).Contains(foeAir))
            {
                return "cantAttack:air 的 LegalTargets 未保持地面目标、或仍列出空军目标";
            }
        }

        // ③ cantBeAttackedBy:ground：目标方拒绝地面攻击者；空军攻击者仍可攻击。
        {
            var (engine, state) = EmptyBoard(db);
            state.ActiveSide = Side.Left;
            state.SetKredits(Side.Left, 20);
            var attacker = state.CreateWithId(ground, Side.Left, 3,
                CardLocation.BoardFrontline, 0);
            attacker.EnteredPlayOnTurn = -99;
            var target = PlaceUnit(state, ground, Side.Right, 51, 1);
            var ctx = new EffectContext
            {
                Engine = engine,
                State = state,
                Self = target,
                Controller = Side.Right,
            };
            engine.Api.InvokeByName("CustomName1Add", target,
                new object?[] { "cantBeAttackedBy:ground" }, ctx, out bool handled);
            if (!handled || !CardApi.CustomNameHasAttribute(target, "customName1",
                    "cantBeAttackedBy:ground"))
            {
                return "CustomName1Add 未写入 cantBeAttackedBy:ground";
            }

            if (engine.Attack(attacker, target, out string reason)
                || reason != "unit_cant_be_attack_by_unit")
            {
                return $"cantBeAttackedBy:ground 未拒绝地面攻击者：reason={reason}";
            }

            if (engine.LegalTargets(attacker).Contains(target))
            {
                return "LegalTargets 不应列出被 cantBeAttackedBy:ground 排除的目标";
            }
        }

        return null;
    }

    /// <summary>
    /// `PersistCustomFields` —— 「持久化**哪张卡**」由**第 0 个实参**决定，不是由接收者决定。
    ///
    /// 权威签名（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:27828-27849`）：
    /// <code>
    /// L["cardID"]           = args[0];   // ← 要持久化的那张卡
    /// L["refreshEffectBar"] = args[1];
    /// … GetCardFromID(cardID) … NotifyPersistCustomFields(card.customJson …)
    /// </code>
    /// 而 `recv` 在 IR 里 **489/489 恒为 `{"var":"cardFunction"}`**，即
    /// `KismetVm.Frame` 的 `_locals["cardFunction"] = ctx.Self`（施法的那张牌自己）。
    /// 旧实现 `AsCard(r)` ⇒ **永远持久化施法者**，`a[0]` 指的别人那张卡的
    /// 临时字段（`customJson`）根本没被写进子动作。
    ///
    /// 影响面（`docs/card-ir.json` 扫描，2026-10-03）：489 个调用点里 **30 个**
    /// 的 `a[0]` 不是自己 —— 形状是 `{"var":"cardID","ctx":{"var":"K2Node_Event_targetCard"}}`
    /// 这类**别人卡的 cardID**（`card_event_no_retreat` / `card_event_maginot_line` /
    /// `card_event_seize_the_initiative` 的 `spawnedCard` / `card_unit_obice_da_75_13` 的
    /// `K2Node_Event_cardLeaving` …）。剩下 459 个的 `a[0]` 是裸 `cardID`
    /// （= `ctx.Self.CardId`）⇒ 新旧解析结果**逐位相同**，不许改坏。
    /// </summary>
    private static string? PersistCustomFieldsTargetsArgCard(CardDatabase db)
    {
        string? unitName = FindType(db, "infantry");
        if (unitName is null)
        {
            return "卡库里没有 infantry（无法测）";
        }

        var (engine, state) = EmptyBoard(db);
        var caster = state.CreateWithId(unitName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var other = state.CreateWithId(unitName, Side.Right, 21, CardLocation.BoardHqRight, 2);
        var ctx = new EffectContext { Engine = engine, State = state, Self = caster, Controller = Side.Left };

        // ① 目标形状：`a[0]` 是**别人卡的整数 cardID**（IR 里那 30 个调用点的形状）。
        engine.Api.InvokeByName("PersistCustomFields", caster,
            new object?[] { other.CardId, false }, ctx, out _);

        int persisted = LastPersistedCardId(state);
        if (persisted != other.CardId)
        {
            return $"`a[0]` 是 #{other.CardId}（别人那张卡）的 cardID，子动作里 `cardID` 却是 #{persisted}"
                 + " —— 旧实现 `AsCard(r)` 恒命中接收者（`cardFunction` = 施法者自己）"
                 + Dump(state,
                        ("施法者", $"#{caster.CardId}"),
                        ("a[0]", $"#{other.CardId}"),
                        ("子动作 cardID", $"#{persisted}"));
        }

        // ② 对照：459/489 个调用点的 `a[0]` 是裸 `cardID`（= `ctx.Self.CardId`），
        //    解析结果必须**逐位不变** —— 否则会把本来正确的那 459 处改坏。
        engine.Api.InvokeByName("PersistCustomFields", caster,
            new object?[] { caster.CardId, false }, ctx, out _);

        int persistedSelf = LastPersistedCardId(state);
        if (persistedSelf != caster.CardId)
        {
            return $"`a[0]` = 自己的 cardID（459/489 个调用点的形状）必须仍然持久化自己，实际 #{persistedSelf}";
        }

        return null;
    }

    /// <summary>取 `ActionLog` 里最后一条 `ZActionPersistCustomFields` 子动作的 `cardID`。</summary>
    private static int LastPersistedCardId(GameState state)
    {
        for (int i = state.ActionLog.Count - 1; i >= 0; i--)
        {
            foreach (var s in state.ActionLog[i].SubActions)
            {
                if (s.Name != "ZActionPersistCustomFields")
                {
                    continue;
                }

                foreach (var v in s.Values)
                {
                    if (v.Name == "cardID")
                    {
                        return v.Value;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// `DrawCardsFromDeckBySide` 的**出参** `cardsIDs`（`TArray&lt;int&gt;`）必须真的写出去。
    ///
    /// 权威签名（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:12298-12402`）：
    /// <code>
    /// L["instigatorID"] = args[0]; L["side"] = args[1]; L["numCards"] = args[2];
    /// L["cardSeen"] = args[3];     L["OpponentDraw"] = args[4];
    /// var __out_cardsIDs = args[5].As&lt;Action&lt;Val&gt;&gt;();     // ← 出参
    /// L["drawDelay"] = args[6];
    /// loop: drawnCard = DrawTopCardFromDeck(…);
    ///       if (drawnCard &gt; 0) { Array_Add(drawnCards, drawnCard); … }
    /// L_021B: L["cardsIDs"] = GetLocal(L, "drawnCards");   // g.cs:12386
    /// __halt: __out_cardsIDs?.Invoke(L["cardsIDs"]);       // g.cs:12400
    /// </code>
    /// 派发表旧实现是 `{ DrawCards(...); return null; }` ⇒ `KismetVm.cs:669` 那条
    /// 「`result is not null` 才写 out 槽」直接跳过 ⇒ **出参永远是 null**。
    ///
    /// 影响面（`docs/card-ir.json` 扫描，2026-10-03）：**9 张卡**真的读这个出参 ——
    /// `card_event_detailed_recon`（`Array_Length` + `Array_Get` 的循环）、
    /// `card_event_pact_of_steel`（`Array_Get(cardsIDs, 0)` → `GetCardFromID`）、
    /// `card_event_ijn_akagi`、`card_event_prolonged_siege`、`card_event_spring_offensive`、
    /// `card_event_top_deck_play_test`、`card_unit_289th_gatchina`、
    /// `card_unit_34th_infantry_regiment`、`card_unit_me_bf_109_fin`。
    /// </summary>
    private static string? DrawCardsFromDeckBySideWritesCardsIDs(CardDatabase db)
    {
        const int first = 6001;
        const int second = 6002;

        var (engine, state) = EmptyBoard(db);
        state.CreateWithId(InfRange1, Side.Left, first, CardLocation.DeckLeft, 0);
        state.CreateWithId(InfRange1, Side.Left, second, CardLocation.DeckLeft, 1);

        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        object? raw = engine.Api.InvokeByName("DrawCardsFromDeckBySide", null,
            new object?[] { 20, (int)Side.Left, 2, false, false, null, 0.4f }, ctx, out bool handled);

        if (!handled)
        {
            return "DrawCardsFromDeckBySide 没进派发表";
        }

        if (raw is not System.Collections.IList list)
        {
            return $"返回 {raw?.GetType().Name ?? "null"} —— 蓝图出参 `cardsIDs` 是 TArray<int>，"
                 + "旧实现 `DrawCards(...); return null;` ⇒ `KismetVm.cs:669` 不写出参 ⇒ "
                 + "读它的 9 张卡（`card_event_detailed_recon` 的 `Array_Length(cardsIDs)` 循环、"
                 + "`card_event_pact_of_steel` 的 `Array_Get(cardsIDs, 0)`）整段恒空";
        }

        if (list.Count != 2)
        {
            return $"抽了 2 张，出参里却只有 {list.Count} 个元素";
        }

        var values = list.Cast<object?>().ToList();
        if (values[0] is not int || values[1] is not int)
        {
            return $"出参元素是 {values[0]?.GetType().Name ?? "null"} —— 蓝图 `drawnCards` 是 "
                 + "TArray<int>，元素直接喂 `GetCardFromID`（`card_event_pact_of_steel` i=151）"
                 + "⇒ 必须是**整数 cardID**，不能是卡实例（`AsInt(实例)=0`）";
        }

        var got = values.Select(v => (int)v!).ToHashSet();
        if (!got.SetEquals(new[] { first, second }))
        {
            return $"出参 = {{{string.Join(",", got)}}}，应当是 {{{first},{second}}}";
        }

        foreach (int id in new[] { first, second })
        {
            if (state.ById(id)?.Location != CardLocation.HandLeft)
            {
                return $"#{id} 抽完之后不在左手（{state.ById(id)?.Location}）—— 抽牌本身没生效";
            }
        }

        // 对照：出参必须与「牌库真的少了这两张」一致（不是凭空造一个数组）。
        if (state.Deck(Side.Left).Any())
        {
            return $"牌库里还剩 {state.Deck(Side.Left).Count()} 张，出参却是 2 个";
        }

        return null;
    }

    /// <summary>
    /// 线上 `DrawSpecificCardFromDeckBySide` 的控制流是「RemoveCardFromDeckBySide
    /// → AddCardToDeckBySide(top) → DrawTopCardFromDeck」，不能直接把目标卡移进手牌。
    /// 这个回归同时守住指定目标、手牌上限和抽牌子动作三件事。
    /// </summary>
    private static string? DrawSpecificCardUsesDrawPath(CardDatabase db)
    {
        const int decoyId = 6101;
        const int targetId = 6102;
        const string cardName = "card_event_fog_of_war";
        if (db.Find(cardName) is null)
        {
            return $"卡库里缺 {cardName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.CreateWithId(cardName, Side.Left, decoyId, CardLocation.DeckLeft, 0);
        var target = state.CreateWithId(cardName, Side.Left, targetId, CardLocation.DeckLeft, 1);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };

        object? raw = engine.Api.InvokeByName("DrawSpecificCardFromDeckBySide", null,
            new object?[] { 99, targetId, (int)Side.Left, false }, ctx, out bool handled);
        if (!handled)
        {
            return "DrawSpecificCardFromDeckBySide 没进派发表";
        }

        if (raw is not CardInstance drawn || drawn.CardId != targetId
            || target.Location != CardLocation.HandLeft
            || state.ById(decoyId)?.Location != CardLocation.DeckLeft)
        {
            return $"指定抽牌应只把 #{targetId} 抽入手牌；实际返回 #{(raw as CardInstance)?.CardId}, "
                 + $"target={target.Location}, decoy={state.ById(decoyId)?.Location}";
        }

        if (!state.ActionLog.SelectMany(a => a.SubActions)
                .Any(s => s.Name == "ZActionDrawCardFromDeck"
                    && s.Values.Any(v => v.Name == "cardID" && v.Value == targetId)))
        {
            return "指定抽牌没有记录 ZActionDrawCardFromDeck，说明绕过了 DrawTopCardFromDeck 路径";
        }

        // 手牌填满后，线上 DrawTopCardFromDeck 会把指定卡烧进弃牌堆，仍然完成抽牌动作。
        var (fullEngine, fullState) = EmptyBoard(db);
        for (int i = 0; i < GameState.HandCapacity; i++)
        {
            fullState.CreateWithId(cardName, Side.Left, 6200 + i, CardLocation.HandLeft, i);
        }

        var burned = fullState.CreateWithId(cardName, Side.Left, 6299, CardLocation.DeckLeft, 0);
        var fullCtx = new EffectContext { Engine = fullEngine, State = fullState, Controller = Side.Left };
        fullEngine.Api.InvokeByName("DrawSpecificCardFromDeckBySide", null,
            new object?[] { 99, burned.CardId, (int)Side.Left, false }, fullCtx, out bool fullHandled);
        if (!fullHandled || burned.Location != CardLocation.Discard)
        {
            return $"满手指定抽牌应烧牌；handled={fullHandled}, location={burned.Location}";
        }

        return null;
    }

    /// <summary>
    /// 线上两个改费函数的 changeType=0 写入独立的临时字段；changeType=4
    /// 只撤销该临时字段。验证重复应用会叠加，且不会污染永久来源槽。
    /// </summary>
    private static string? TemporaryResourceCostBuffsExpire(CardDatabase db)
    {
        const string targetName = "card_unit_a26_invader";
        const string sourceName = "card_event_aans";
        if (db.Find(targetName) is null || db.Find(sourceName) is null)
        {
            return $"卡库缺少 {targetName} 或 {sourceName}";
        }

        var (engine, state) = EmptyBoard(db);
        var target = state.CreateWithId(targetName, Side.Left, 6301, CardLocation.HandLeft, 0);
        var source = state.CreateWithId(sourceName, Side.Left, 6302, CardLocation.BoardHqLeft, 1);
        int baseKredit = target.KreditCost;
        int baseOperation = target.OperationCost;
        var ctx = new EffectContext { Engine = engine, State = state, Self = source, Controller = Side.Left };

        engine.Api.InvokeByName("ChangeKreditCost", target,
            new object?[] { target, source.CardId, -1, 0, false, null }, ctx, out bool kredit1);
        engine.Api.InvokeByName("ChangeKreditCost", target,
            new object?[] { target, source.CardId, -1, 0, false, null }, ctx, out bool kredit2);
        if (!kredit1 || !kredit2 || target.KreditCost != Math.Max(0, baseKredit - 2)
            || !target.BuffsBySource.TryGetValue((source.CardId, true), out var kreditBuff)
            || kreditBuff.KreditCost != -2
            || target.BuffsBySource.ContainsKey((source.CardId, false)))
        {
            return $"临时 kredit buff 未正确叠加：base={baseKredit}, actual={target.KreditCost}, "
                 + $"slots={string.Join(",", target.BuffsBySource.Keys.Select(k => $"{k.SourceCardId}/{k.Temporary}"))}";
        }

        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 1, 0, false, false, false }, ctx, out bool operation1);
        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 1, 0, false, false, false }, ctx, out bool operation2);
        if (!operation1 || !operation2 || target.OperationCost != baseOperation + 2)
        {
            return $"临时 operation cost buff 未正确叠加：base={baseOperation}, actual={target.OperationCost}";
        }

        target.BuffsBySource[(source.CardId, true)] = new CardBuff
        {
            SourceCardId = source.CardId,
            Temporary = true,
            Attack = 2,
        };
        var attackTemp = engine.Api.InvokeByName("getAttackTempBuffAmount", target,
            new object?[] { source.CardId, null }, ctx, out bool attackTempHandled);
        if (!attackTempHandled || attackTemp is not int attackAmount || attackAmount != 2)
        {
            return $"getAttackTempBuffAmount 应返回来源 {source.CardId} 的 +2，实际 {attackTemp}";
        }
        target.BuffsBySource.Remove((source.CardId, true));
        target.RecalculateStats();

        engine.Api.RemoveTemporaryBuffs();
        if (target.OperationCost != baseOperation || target.BuffsBySource.Count != 0)
        {
            return $"绝对设值前的临时 operation cost 清理失败：actual={target.OperationCost}, buff槽={target.BuffsBySource.Count}";
        }

        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 0, 2, false, false, false }, ctx, out bool setAbsolute);
        if (!setAbsolute || target.OperationCost != 0
            || !target.BuffsBySource.TryGetValue((source.CardId, false), out var absoluteOpBuff)
            || !absoluteOpBuff.OperationCostSetsAbsoluteValue
            || absoluteOpBuff.OperationCost != -baseOperation)
        {
            return $"绝对 operation cost=0 未正确记录：handled={setAbsolute}, actual={target.OperationCost}";
        }

        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 1, 0, false, false, false }, ctx, out bool relativeAfterSet);
        if (!relativeAfterSet || target.OperationCost != 1)
        {
            return $"绝对 operation cost 后的临时 +1 未叠加：actual={target.OperationCost}";
        }

        engine.Api.RemoveTemporaryBuffs();
        if (target.KreditCost != baseKredit || target.OperationCost != 0
            || target.BuffsBySource.Count != 1
            || !target.BuffsBySource.TryGetValue((source.CardId, false), out var retainedAbsolute)
            || !retainedAbsolute.OperationCostSetsAbsoluteValue)
        {
            return $"临时改费清理后未保留绝对设值：费用 {target.KreditCost}/{baseKredit}，"
                 + $"行动费 {target.OperationCost}/0，buff槽={target.BuffsBySource.Count}";
        }

        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 0, 4, false, false, false }, ctx, out bool removeAbsolute);
        if (!removeAbsolute || target.OperationCost != baseOperation || target.BuffsBySource.Count != 0)
        {
            return $"绝对 operation cost 撤销后未恢复：handled={removeAbsolute}, actual={target.OperationCost}, buff槽={target.BuffsBySource.Count}";
        }

        return null;
    }

    /// <summary>
    /// `card_unit_light_anti_air_battery` 的 +2 行动费是临时来源 buff，
    /// 但光环本身跨回合持续。正版 `ChangeOperationCost(changeType=4)`
    /// 清掉目标的临时槽后，会广播 reset 事件，让电池重新施加 +2。
    /// </summary>
    private static string? LightAntiAirBatteryOperationCostReset(CardDatabase db)
    {
        const string batteryName = "card_unit_light_anti_air_battery";
        const string planeName = "card_unit_arado_ar_196";
        if (db.Find(batteryName) is null || db.Find(planeName) is null)
        {
            return $"卡库缺少 {batteryName} 或 {planeName}";
        }

        var (engine, state) = EmptyBoard(db);
        state.SetKredits(Side.Left, 12);
        state.SetMaxKredits(Side.Left, 12);
        state.ActiveSide = Side.Left;

        var plane = state.CreateWithId(planeName, Side.Right, 6301,
            CardLocation.BoardHqRight, 1);
        int baseOperation = plane.OperationCost;
        var battery = state.CreateWithId(batteryName, Side.Left, 6302,
            CardLocation.HandLeft, 0);
        if (!engine.PlayCard(battery))
        {
            return "Light Anti-Air Battery 打不出来";
        }

        if (plane.OperationCost != baseOperation + 2
            || !plane.BuffsBySource.ContainsKey((battery.CardId, true)))
        {
            return $"电池进场后应给敌方空军 +2 行动费：base={baseOperation}, "
                 + $"actual={plane.OperationCost}, "
                 + $"temp={plane.BuffsBySource.ContainsKey((battery.CardId, true))}";
        }

        // 回合末统一清理临时槽；reset 广播应让电池立即挂回同一个临时来源。
        engine.Api.RemoveTemporaryBuffs();
        if (plane.OperationCost != baseOperation + 2
            || !plane.BuffsBySource.ContainsKey((battery.CardId, true)))
        {
            return $"临时行动费清理后 reset 事件未重施加：base={baseOperation}, "
                 + $"actual={plane.OperationCost}, "
                 + $"temp={plane.BuffsBySource.ContainsKey((battery.CardId, true))}";
        }

        // 显式 ct=4 也必须走同一条 reset 事件链。
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = battery,
            Controller = Side.Left,
        };
        engine.Api.InvokeByName("ChangeOperationCost", plane,
            new object?[] { plane, battery.CardId, -2, 4, true, false, false },
            ctx, out bool handled);
        if (!handled || plane.OperationCost != baseOperation + 2
            || !plane.BuffsBySource.ContainsKey((battery.CardId, true)))
        {
            return $"显式 ct=4 后 reset 事件未重施加：handled={handled}, "
                 + $"actual={plane.OperationCost}";
        }

        // 来源离开棋盘后不在接收快照里，清理不能把光环重新挂回来。
        state.Move(battery, CardLocation.HandLeft);
        engine.Api.InvokeByName("ChangeOperationCost", plane,
            new object?[] { plane, battery.CardId, -2, 4, true, false, false },
            ctx, out handled);
        if (!handled || plane.OperationCost != baseOperation
            || plane.BuffsBySource.ContainsKey((battery.CardId, true)))
        {
            return $"电池离场后 ct=4 应彻底撤销：handled={handled}, "
                 + $"actual={plane.OperationCost}, "
                 + $"temp={plane.BuffsBySource.ContainsKey((battery.CardId, true))}";
        }

        return null;
    }

    private static string? OperationCostChangeEvents(CardDatabase db)
    {
        const string probeName = "card_unit_b_24_d";
        if (db.Find(probeName) is null)
        {
            return $"卡库缺少 {probeName}";
        }

        var (engine, state) = EmptyBoard(db);
        var target = state.CreateWithId(probeName, Side.Left, 6311,
            CardLocation.BoardHqLeft, 1);
        var watcher = state.CreateWithId(probeName, Side.Left, 6312,
            CardLocation.BoardHqLeft, 2);
        var source = state.CreateWithId("card_event_confusion", Side.Left, 6313,
            CardLocation.Discard, 0);
        var trace = new List<string>();
        engine.Api.TriggerTrace = trace;
        var ctx = new EffectContext
        {
            Engine = engine,
            State = state,
            Self = source,
            Controller = Side.Left,
        };

        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 1, 0, false, false, false },
            ctx, out bool handled);
        if (!handled)
        {
            return "ChangeOperationCost 未接入派发表";
        }

        string Prefix(CardInstance card)
            => $"OnAfterOtherCardOperactionCostChanged → {card.Name}#{card.CardId}";
        int firstCount = trace.Count(t => t.StartsWith(Prefix(watcher), StringComparison.Ordinal));
        if (firstCount != 1
            || trace.Any(t => t.StartsWith(Prefix(target), StringComparison.Ordinal)))
        {
            return $"行动费变更广播接收者错误：watcher={firstCount}，"
                 + $"target={trace.Count(t => t.StartsWith(Prefix(target), StringComparison.Ordinal))}";
        }

        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 0, 0, false, false, false },
            ctx, out handled);
        int zeroCount = trace.Count(t => t.StartsWith(Prefix(watcher), StringComparison.Ordinal));
        if (!handled || zeroCount != firstCount)
        {
            return $"行动费 amount=0 不应派发变更事件：handled={handled}, count={zeroCount}";
        }

        watcher.Keywords.Add(Keyword.Suppressed);
        engine.Api.InvokeByName("ChangeOperationCost", target,
            new object?[] { target, source.CardId, 1, 0, false, false, false },
            ctx, out handled);
        int suppressedCount = trace.Count(t => t.StartsWith(Prefix(watcher), StringComparison.Ordinal));
        if (!handled || suppressedCount != zeroCount)
        {
            return $"被抑制的行动费事件接收者不应触发：handled={handled}, count={suppressedCount}";
        }

        return null;
    }

    /// <summary>
    /// `DamageCard` 的**第 3 个实参是整数 `damagerCardID`**，不是卡对象。
    ///
    /// 权威签名（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:11606-11618`）：
    /// <code>
    /// L["card"] = args[0]; L["amount"] = args[1]; L["damagerCardID"] = args[2];  // ← int
    /// L["isRedirected"] = args[3]; L["fromFight"] = args[4]; …
    /// GetCardFromID(damagerCardID) → damageDealer                          // g.cs:11624-11626
    /// ExecuteOnDealDamageAddDamage(card, damageDealer, …)                  // g.cs:11636
    /// </code>
    /// 派发表旧实现 `var source = AsCard(a.ElementAtOrDefault(2)) ?? c.Self;` ——
    /// `AsCard(整数) = null` ⇒ **整数形状一律退回 `c.Self`（施法者自己）**，
    /// 于是「伤害来源」记成施法者。可观测点：`ZActionDamageCard` 子动作的
    /// `attackerCardID`（`CardApi.ApplyCalculatedDamage` → `CardApi.cs:1067`）。
    ///
    /// 影响面（`docs/card-ir.json` 扫描，2026-10-03）：271 个调用点里 **9 个**的 `a[2]`
    /// 不是自己 —— 7 个别人卡的 `{"var":"cardID","ctx":{…}}`
    /// （`card_event_infiltrate` / `card_unit_halifax` / `card_event_yamato` /
    ///  `card_event_jungle_warfare` / `card_event_special_attack` /
    ///  `card_event_sunny2_heatwave3` / `card_event_sunny4_scorching_sun3`）
    /// + 2 个 `spawnedCardID`（`card_event_audacity` / `card_location_soviet_scen4`）。
    /// 剩下 262 个的 `a[2]` 是裸 `cardID`（= `ctx.Self.CardId`）⇒ 新旧解析结果**逐位相同**。
    /// </summary>
    private static string? DamageCardResolvesIntDamagerCardId(CardDatabase db)
    {
        string? unitName = FindType(db, "infantry");
        if (unitName is null)
        {
            return "卡库里没有 infantry（无法测）";
        }

        var (engine, state) = EmptyBoard(db);
        var caster = state.CreateWithId(unitName, Side.Left, 20, CardLocation.BoardHqLeft, 1);
        var victim = state.CreateWithId(unitName, Side.Right, 21, CardLocation.BoardHqRight, 2);
        var damager = state.CreateWithId(unitName, Side.Right, 22, CardLocation.BoardHqRight, 3);
        var ctx = new EffectContext { Engine = engine, State = state, Self = caster, Controller = Side.Left };

        // ① 目标形状：`a[2]` 是**别人卡的整数 cardID**（IR 里那 9 个调用点的形状）。
        engine.Api.InvokeByName("DamageCard", caster,
            new object?[] { victim, 1, damager.CardId, false, false, false, null }, ctx, out _);

        int source = LastDamageSourceId(state);
        if (source != damager.CardId)
        {
            return $"`a[2]` 是 #{damager.CardId}（别人那张卡）的 cardID，"
                 + $"`ZActionDamageCard.attackerCardID` 却是 #{source}"
                 + " —— 旧实现 `AsCard(a[2]) ?? c.Self` 对整数形状恒 null ⇒ 退回施法者"
                 + Dump(state,
                        ("施法者", $"#{caster.CardId}"),
                        ("a[2]", $"#{damager.CardId}"),
                        ("attackerCardID", $"#{source}"));
        }

        if (victim.Defense >= 8)
        {
            return $"前置不成立：伤害没落地（{victim.Name}#{victim.CardId} 防御 {victim.Defense}）";
        }

        // ② 对照：262/271 个调用点的 `a[2]` 是裸 `cardID`（= `ctx.Self.CardId`），
        //    解析结果必须**逐位不变** —— 否则会把本来正确的那 262 处改坏。
        int before = victim.Defense;
        engine.Api.InvokeByName("DamageCard", caster,
            new object?[] { victim, 1, caster.CardId, false, false, false, null }, ctx, out _);

        int sourceSelf = LastDamageSourceId(state);
        if (sourceSelf != caster.CardId)
        {
            return $"`a[2]` = 自己的 cardID（262/271 个调用点的形状）必须仍然解析成自己，"
                 + $"实际 #{sourceSelf}";
        }

        if (victim.Defense != before - 1)
        {
            return $"对照组伤害没落地（{before} → {victim.Defense}）";
        }

        return null;
    }

    /// <summary>取 `ActionLog` 里最后一条 `ZActionDamageCard` 子动作的 `attackerCardID`。</summary>
    private static int LastDamageSourceId(GameState state)
    {
        for (int i = state.ActionLog.Count - 1; i >= 0; i--)
        {
            foreach (var s in state.ActionLog[i].SubActions)
            {
                if (s.Name != "ZActionDamageCard")
                {
                    continue;
                }

                foreach (var v in s.Values)
                {
                    if (v.Name == "attackerCardID")
                    {
                        return v.Value;
                    }
                }
            }
        }

        return -1;
    }

    // ==================== Gotcha（反制卡）子系统（2026-10-03） ====================
    //
    // 权威：`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:23719-23912`
    // （`GotchaTriggered`）、`:18623-18892`（`GetActiveGotchasOrdered`）、
    // `:34036-34220`（`SetCardsSeenByCipher`）、`:3996-4071`（`ApplySetCardsSeenByCipher`）、
    // `:20904-20930`（`GetHandLocationBySide`）、`:29227-29300`（`RearrangeLocation`）、
    // `:28080-28316`（装填）；参考实现 `Bridge/EngineHost.cs:1342-1355`。
    //
    // ⚠️ 下面每条都断言**蓝图的行为**，不断言实现细节：
    //    · 不涉及「每次非 gotcha 出牌后重排对手手牌」这种蓝图里**没有**的副作用；
    //    · 断言用的是**卡自己的 IR 体读到的值**（`card_unit_the_tigers` 读
    //      `countermeasureTriggering.originalSide/cardID/side`），不是内核的内部标志。

    /// <summary>造一张反制卡（默认 `card_event_interception`，52 张 gotcha 之一）。</summary>
    private static CardInstance MakeGotcha(GameState state, string name, Side owner, int cardId,
                                           CardLocation location, int locationNumber)
        => state.CreateWithId(name, owner, cardId, location, locationNumber);

    /// <summary>
    /// Kismet 的布尔语义（`KismetVm.Truthy` 是 `internal`，测试工程看不到）：
    /// `null` / `false` / `0` / 空串为假，其余为真。
    /// </summary>
    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>
    /// `IsGotcha(card, out isIt)` —— 判据是**卡定义的类型**
    /// （52 张 `docs/cards.live.json` 里 `type == "gotcha"` 的卡，与 IR 里调用
    /// `GotchaTriggered` 的 52 张集合完全相同）。
    /// </summary>
    private static string? GotchaIsGotchaByCardType(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);

        if (db.Find("card_event_interception") is null)
        {
            return "找不到 card_event_interception";
        }

        var gotcha = MakeGotcha(state, "card_event_interception", Side.Left, 1500,
            CardLocation.HandLeft, 0);
        var normal = state.CreateWithId(FindType(db, "infantry")!, Side.Left, 1501,
            CardLocation.BoardHqLeft, 0);

        var ctx = new EffectContext { Engine = engine, State = state, Self = normal, Controller = Side.Left };

        if (!engine.Api.IsGotcha(gotcha))
        {
            return "card_event_interception 的类型是 gotcha，IsGotcha 却是假";
        }

        if (engine.Api.IsGotcha(normal))
        {
            return $"普通单位 {normal.Name} 被判成 gotcha";
        }

        // 走派发表（IR 形状：`IsGotcha(card, out)`，接收者才是被查的卡）
        object? r = engine.Api.InvokeByName("IsGotcha", gotcha, new object?[] { null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `IsGotcha` —— IR 里 16 个调用点全部静默失效";
        }

        if (!Truthy(r))
        {
            return $"派发 `IsGotcha` 的接收者是 gotcha 卡，返回值却是 {r ?? "null"}";
        }

        return null;
    }

    /// <summary>
    /// ★★ `ShouldGotchaTrigger` 判的是 **self**，不是实参。
    ///
    /// 蓝图调用点（`Generated/Britain/Base/events/card_event_interception.g.cs:53`）：
    /// <code>
    /// H.Call("ShouldGotchaTrigger", new Val[] { self, K2Node_Event_cardPlayed, Val.Out(…) })
    /// </code>
    /// IR 形状（`docs/card-ir.json`，53 个调用点**全部**）：`args = [触发卡, out]`，
    /// **没有 `recv`** ⇒ self 是隐式的（`KismetVm.Eval` 的 `{self:true}` → `ctx.Self`）。
    ///
    /// ⇒ 本测用**两个方向**把它钉死：
    /// <list type="number">
    /// <item>self = 反制卡、a[0] = 普通卡 ⇒ **真**；</item>
    /// <item>self = 普通卡、a[0] = 反制卡 ⇒ **假**（这就是"判 a[0]"的错法会翻车的那一面）。</item>
    /// </list>
    /// </summary>
    private static string? GotchaShouldTriggerJudgesSelf(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        if (db.Find("card_event_interception") is null)
        {
            return "找不到 card_event_interception";
        }

        var gotcha = MakeGotcha(state, "card_event_interception", Side.Left, 1500,
            CardLocation.HandLeft, 0);
        var normal = state.CreateWithId(FindType(db, "infantry")!, Side.Left, 1501,
            CardLocation.BoardHqLeft, 0);

        // ① self = 反制卡；实参 a[0] = 普通卡（蓝图里那是"触发这件事的卡"）
        var ctx1 = new EffectContext { Engine = engine, State = state, Self = gotcha, Controller = Side.Left };
        object? r1 = engine.Api.InvokeByName("ShouldGotchaTrigger", null,
            new object?[] { normal, null }, ctx1, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `ShouldGotchaTrigger` —— IR 里 53 个调用点全部静默失效";
        }

        if (!Truthy(r1))
        {
            return "self 是反制卡、a[0] 是普通卡，`ShouldGotchaTrigger` 应当为**真**，"
                 + $"实际 {r1 ?? "null"} —— 判据没落在 self 上";
        }

        // ② self = 普通卡；实参 a[0] = 反制卡 ⇒ 必须为假（判 a[0] 的实现会在这里返回真）
        var ctx2 = new EffectContext { Engine = engine, State = state, Self = normal, Controller = Side.Left };
        object? r2 = engine.Api.InvokeByName("ShouldGotchaTrigger", null,
            new object?[] { gotcha, null }, ctx2, out _);
        if (Truthy(r2))
        {
            return "self 是普通卡、a[0] 是反制卡，`ShouldGotchaTrigger` 必须为**假**，"
                 + "实际为真 —— 这是「判 a[0]」的错法（审计 §9 第 9 条）";
        }

        // ③ 反制卡被丢弃（"已销毁"）之后不再响应
        gotcha.Location = CardLocation.Discard;
        object? r3 = engine.Api.InvokeByName("ShouldGotchaTrigger", null,
            new object?[] { normal, null }, ctx1, out _);
        if (Truthy(r3))
        {
            return "反制卡已进弃牌堆，`ShouldGotchaTrigger` 仍为真（蓝图/参考实现要 `!Destroyed`）";
        }

        return null;
    }

    /// <summary>
    /// ★★ `GotchaTriggered` 循环里 `OnCounterMeasureTriggered(item, **TmpGotcha**, out)`
    /// （`BP_CardFunctions.g.cs:23839`）—— 实参必须是 **gotcha 卡**。
    ///
    /// 验证者选 `card_unit_the_tigers`（`docs/card-ir.json` 的
    /// `locals.OnCounterMeasureTriggered`，14 步），它**真的读**那个实参：
    /// <code>
    /// i=33   EqualEqual_ByteByte(originalSide@countermeasureTriggering, side)      ; 同阵营？
    /// i=93   Greater_IntInt(cardID@countermeasureTriggering, 1000)                 ; 生成卡？
    /// i=149  Not_PreBool(同阵营)
    /// i=178  BooleanOR(生成卡, Not(同阵营))
    /// i=216  EqualEqual_ByteByte(side@countermeasureTriggering, side)
    /// i=276  BooleanAND(同阵营实参, 上面那个 OR)
    /// i=314  JumpIfNot → 不加成
    /// i=328  ChangeAttack (self, cardID, +1, changeType=1)
    /// i=391  ChangeDefense(self, cardID, +1, changeType=1)
    /// </code>
    /// ⇒ 把 `TmpGotcha` 换成**触发卡**（审计第 6 条说的错法）时，
    /// `side@countermeasureTriggering` 变成**对面**阵营 ⇒ AND 恒假 ⇒ **+1+1 不发生**。
    /// 这正是本测的判别力所在（把实参改成 instigator 会让它红）。
    /// </summary>
    private static string? GotchaCounterMeasureArgIsGotchaCard(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        if (db.Find("card_unit_the_tigers") is null || db.Find("card_event_interception") is null)
        {
            return "找不到 card_unit_the_tigers / card_event_interception";
        }

        // 订阅者：左方场上的 THE TIGERS
        var tigers = state.CreateWithId("card_unit_the_tigers", Side.Left, 1600,
            CardLocation.BoardHqLeft, 0);
        int atk0 = tigers.Attack, def0 = tigers.Defense;

        // 反制卡：**左方**手牌，cardID > 1000（tigers 的判据之一）
        var gotcha = MakeGotcha(state, "card_event_interception", Side.Left, 1500,
            CardLocation.HandLeft, 0);
        gotcha.GotchaActivated = 1;

        // 触发卡（instigator）：**右方**的一张指令 —— 传错它就会把阵营判成右方
        var instigator = state.CreateWithId("card_event_aans", Side.Right, 2000,
            CardLocation.HandRight, 0);

        var ctx = new EffectContext
        {
            Engine = engine, State = state, Self = gotcha, Controller = Side.Left,
        };

        object? r = engine.Api.InvokeByName("GotchaTriggered", gotcha,
            new object?[] { gotcha, instigator.CardId, false, false, null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `GotchaTriggered` —— IR 里 54 个调用点全部静默失效";
        }

        _ = r;
        if (tigers.Attack != atk0 + 1 || tigers.Defense != def0 + 1)
        {
            return $"`OnCounterMeasureTriggered` 的实参没绑到 gotcha 卡："
                 + $"THE TIGERS 的攻防应当 {atk0}/{def0} → {atk0 + 1}/{def0 + 1}，"
                 + $"实际 {tigers.Attack}/{tigers.Defense}（把实参传成触发卡时 "
                 + "`side@countermeasureTriggering` 变成对面阵营 ⇒ AND 恒假）";
        }

        return null;
    }

    /// <summary>
    /// `GotchaTriggered` 的前 4 步（`BP_CardFunctions.g.cs:23735-23745`）：
    /// <code>
    /// :23735  gotchaActivated = 0
    /// :23739  enterPlayOnTurn = GetTurnNumber()
    /// :23741  location = 8 (Discard)
    /// :23743  GetHandLocationBySide(side) → :23745 RearrangeLocation(handLocation)
    /// </code>
    /// 手牌压紧那一环：拿掉 gotcha 之后，剩下的手牌 `locationNumber` 必须重新变成 0..n-1
    /// （蓝图 `RearrangeLocation` → `SetCardLocationAndLocNumber(cardID, loc, index)`）。
    /// </summary>
    private static string? GotchaTriggeredMovesAndRearranges(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        if (db.Find("card_event_interception") is null)
        {
            return "找不到 card_event_interception";
        }

        // 左方手牌：gotcha（号 0）+ 两张普通卡（号 1、2）
        var gotcha = MakeGotcha(state, "card_event_interception", Side.Left, 1500,
            CardLocation.HandLeft, 0);
        gotcha.GotchaActivated = 1;
        gotcha.EnteredPlayOnTurn = 99;

        var h1 = state.CreateWithId("card_event_aans", Side.Left, 1501, CardLocation.HandLeft, 1);
        var h2 = state.CreateWithId("card_event_aans", Side.Left, 1502, CardLocation.HandLeft, 2);

        // 把 gotcha 手动挪到号 2，制造一个"洞"（0/1/2 里拿掉中间那张）
        gotcha.LocationNumber = 2;
        h1.LocationNumber = 0;
        h2.LocationNumber = 1;

        state.Turn = 7;

        var ctx = new EffectContext { Engine = engine, State = state, Self = gotcha, Controller = Side.Left };
        engine.Api.InvokeByName("GotchaTriggered", gotcha,
            new object?[] { gotcha, 0, false, false, null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `GotchaTriggered`";
        }

        if (gotcha.Location != CardLocation.Discard)
        {
            return $"触发后 gotcha 的位置应当是 Discard(8)，实际 {gotcha.Location}（蓝图 :23741）";
        }

        if (gotcha.EnteredPlayOnTurn != 7)
        {
            return $"触发后 `enterPlayOnTurn` 应当是当前回合 7，实际 {gotcha.EnteredPlayOnTurn}"
                 + "（蓝图 :23737-23739）";
        }

        if (gotcha.GotchaActivated != 0)
        {
            return $"触发后 `gotchaActivated` 应当被置 **0**（蓝图 :23735），实际 {gotcha.GotchaActivated}";
        }

        if (h1.LocationNumber != 0 || h2.LocationNumber != 1)
        {
            return $"手牌没被压紧：两张普通卡的 locationNumber 应当是 0/1，"
                 + $"实际 {h1.LocationNumber}/{h2.LocationNumber}（蓝图 :23743-23745 RearrangeLocation）";
        }

        return null;
    }

    /// <summary>
    /// ★★ `gotchaActivated` 是**从 1 起的激活序号**，不是 bool。
    ///
    /// 三条独立断言：
    /// <list type="number">
    /// <item><b>装填递增</b>：`PlayCardDirectlyFromHand:28310-28316`
    ///   `_nextGotchaActivated = max(同阵营已激活) + 1` —— 第一张 = 1，第二张 = 2；</item>
    /// <item><b>再次正常打出 ⇒ 置 0</b>（`:28092-28096`）；</item>
    /// <item><b>取用</b>：`GetActiveGotchasOrdered` 只收 `gotchaActivated &gt; 0`，
    ///   且输出**按键升序** —— 而 interception / ultra 用的是负数键
    ///   （`:18764` `cardID * -1`、`:18790` `(cardID + 1000000) * -1`），
    ///   所以它们**永远排在普通反制卡之前**。</item>
    /// </list>
    /// 用 bool 的话第 2、3 条都不成立（两张反制卡同号、顺序与客户端不一致）。
    /// </summary>
    private static string? GotchaActivatedIsSequence(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        if (db.Find("card_event_interception") is null || db.Find("card_event_ultra") is null)
        {
            return "找不到 card_event_interception / card_event_ultra";
        }

        // ---- ① 装填递增（左方）----
        var g1 = MakeGotcha(state, "card_event_interception", Side.Left, 1500,
            CardLocation.HandLeft, 0);
        engine.Api.AssignGotchaActivatedOnPlayFromHand(g1);
        if (g1.GotchaActivated != 1)
        {
            return $"第一张反制卡装填后应当是 **1**，实际 {g1.GotchaActivated}（蓝图 :28310-28316）";
        }

        var g2 = MakeGotcha(state, "card_event_ultra", Side.Left, 1501, CardLocation.HandLeft, 1);
        engine.Api.AssignGotchaActivatedOnPlayFromHand(g2);
        if (g2.GotchaActivated != 2)
        {
            return $"第二张反制卡装填后应当是 **2**（= max+1），实际 {g2.GotchaActivated}";
        }

        // 右方独立计数（蓝图 :28261 只数 `item.side == card.side`）
        var gr = MakeGotcha(state, "card_event_interception", Side.Right, 2000,
            CardLocation.HandRight, 0);
        engine.Api.AssignGotchaActivatedOnPlayFromHand(gr);
        if (gr.GotchaActivated != 1)
        {
            return $"右方第一张反制卡应当是 **1**（按阵营独立计数），实际 {gr.GotchaActivated}";
        }

        // ---- ② 再次正常打出 ⇒ 置 0（:28092-28096）----
        engine.Api.AssignGotchaActivatedOnPlayFromHand(g1);
        if (g1.GotchaActivated != 0)
        {
            return $"已激活的反制卡再次装填应当被置 **0**（蓝图 :28096），实际 {g1.GotchaActivated}";
        }

        g1.GotchaActivated = 1;   // 复原，供第 ③ 条用

        // ---- ③ 取用：>0 过滤 + 按键升序（负数键优先）----
        var idle = MakeGotcha(state, "card_event_interception", Side.Left, 1502,
            CardLocation.HandLeft, 2);   // gotchaActivated 保持 0 ⇒ 必须被过滤掉

        var orderCtx = new EffectContext
        {
            Engine = engine, State = state, Self = g1, Controller = Side.Left,
        };
        var ordered = engine.Api.InvokeByName("GetActiveGotchasOrdered", null,
            new object?[] { null }, orderCtx, out bool handledOrdered) as List<int>;
        if (!handledOrdered || ordered is null)
        {
            return "派发表里没有 `GetActiveGotchasOrdered`（蓝图 :18623-18892；"
                 + "`AfterWaitCardPlayFromHand` :626 是它的调用点）";
        }
        // 键的算术（蓝图 :18764 `cardID * -1` / :18790 `(cardID + 1000000) * -1`）：
        //   ultra  #1501 → -(1501+1000000) = -1001501   ← 最小，排第一
        //   interception #2000 → -2000                  ← 次之（**cardID 越大键越小**）
        //   interception #1500 → -1500                  ← 再次
        // ⇒ 期望 [1501, 2000, 1500]；未激活的 #1502 必须被 `:18733` 的 `> 0` 过滤掉。
        var expect = new List<int> { g2.CardId, gr.CardId, g1.CardId };
        if (!ordered.SequenceEqual(expect))
        {
            return "`GetActiveGotchasOrdered` 的顺序应当是 "
                 + $"[ultra #{g2.CardId}, interception #{gr.CardId}, interception #{g1.CardId}]"
                 + "（蓝图 :18764/:18790 用**负数**键 —— ultra 的 -(cardID+1000000) 最小、"
                 + "interception 的 -cardID 次之且 **cardID 越大越靠前**；"
                 + ":18733 过滤 `gotchaActivated > 0`），"
                 + $"实际 [{string.Join(",", ordered)}]"
                 + $"（未激活的 #{idle.CardId} 必须不在结果里）";
        }

        return null;
    }

    /// <summary>
    /// `GotchaTriggered` 尾段（`BP_CardFunctions.g.cs:23857-23904`）：
    /// <code>
    /// :23857  if (!stopFurtherCardActions) → :23902 SetStopFurtherActions(False)
    /// :23859      否则 SetStopFurtherActions(True)
    /// :23863          GetCardFromID(instigatorID).enterPlayOnTurn = 0
    /// :23869      IsOrder(instigator) &amp;&amp; skipDiscardingOrder
    /// :23890          instigator.customJson = JsonMakeField(customJson, "cancelOrderRemove",
    ///                                                       JsonMakeBool(skipDiscardingOrder))
    /// </code>
    /// 两条分支的公共落点都是「`IsOrder &amp;&amp; skipDiscardingOrder` ⇒ 写 `cancelOrderRemove`」；
    /// 差别只有 `SetStopFurtherActions` 的取值与 `enterPlayOnTurn = 0`（只在 True 分支）。
    ///
    /// ⚠️ 蓝图的 `True` 分支**到不了** `SetStopFurtherActions(False)`（执行流栈的
    /// `PopExecutionFlow` 落点是 `:23905` 的 Return）—— 这条**照抄不改**，
    /// 所以第 ① 条断言的是「保持 true」，不是「复位」。
    /// </summary>
    private static string? GotchaStopFurtherActions(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        if (db.Find("card_event_interception") is null)
        {
            return "找不到 card_event_interception";
        }

        var gotcha = MakeGotcha(state, "card_event_interception", Side.Left, 1500,
            CardLocation.HandLeft, 0);
        gotcha.GotchaActivated = 1;

        // instigator 必须是**指令**（`IsOrder`）才会写 customJson
        var order = state.CreateWithId("card_event_aans", Side.Right, 2000,
            CardLocation.HandRight, 0);
        order.EnteredPlayOnTurn = 55;

        var ctx = new EffectContext { Engine = engine, State = state, Self = gotcha, Controller = Side.Left };

        // ---- ① stopFurtherCardActions = true ----
        engine.Api.InvokeByName("GotchaTriggered", gotcha,
            new object?[] { gotcha, order.CardId, true, true, null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `GotchaTriggered`";
        }

        if (!engine.Api.GetStopFurtherActions())
        {
            return "`stopFurtherCardActions=true` 时 `SetStopFurtherActions(True)` 没生效（蓝图 :23859）";
        }

        if (order.EnteredPlayOnTurn != 0)
        {
            return $"触发卡的 `enterPlayOnTurn` 应当被置 0（蓝图 :23863），实际 {order.EnteredPlayOnTurn}";
        }

        if (order.CustomJson.GetValueOrDefault("cancelOrderRemove") != "1")
        {
            return "`IsOrder(instigator) && skipDiscardingOrder` 时应当写 "
                 + "`customJson[\"cancelOrderRemove\"] = true`（蓝图 :23890），"
                 + $"实际 {order.CustomJson.GetValueOrDefault("cancelOrderRemove") ?? "<缺>"}";
        }

        // ---- ② stopFurtherCardActions = false + skipDiscardingOrder = false ----
        engine.Api.SetStopFurtherActions(false);
        var gotcha2 = MakeGotcha(state, "card_event_interception", Side.Left, 1503,
            CardLocation.HandLeft, 1);
        order.CustomJson.Remove("cancelOrderRemove");
        order.EnteredPlayOnTurn = 55;

        engine.Api.InvokeByName("GotchaTriggered", gotcha2,
            new object?[] { gotcha2, order.CardId, false, false, null }, ctx, out _);

        if (engine.Api.GetStopFurtherActions())
        {
            return "`stopFurtherCardActions=false` 时应当 `SetStopFurtherActions(False)`（蓝图 :23902）";
        }

        if (order.CustomJson.ContainsKey("cancelOrderRemove"))
        {
            return "`skipDiscardingOrder=false` 时不该写 `cancelOrderRemove`（蓝图 :23869 的 AND）";
        }

        if (order.EnteredPlayOnTurn != 55)
        {
            return $"`stopFurtherCardActions=false` 时不该动触发卡的 `enterPlayOnTurn`（蓝图只在 :23863 的 True 分支写），"
                 + $"实际被改成 {order.EnteredPlayOnTurn}";
        }

        return null;
    }

    /// <summary>
    /// `SetCardsSeenByCipher(numberOfCardsSeen, instigatorID, side, out qqq)`
    /// （`BP_CardFunctions.g.cs:34036-34220`）：
    /// <code>
    /// :34107  GetCardsInHandBySide(Switch(side, {0→0, 1→2, 2→1}))   ; ★ **对手**手牌
    /// :34137      if (cardSeen) 跳过
    /// :34166  Array_ShuffleFromStream(oppositeSideUnseenCards, cardsRandomStream)
    /// :34170  min = Min(numberOfCardsSeen, 未见面数)
    /// :34174  ApplySetCardsSeenByCipher(…)  → :4048 cardSeen = True
    /// </code>
    /// 三条断言：**只翻对手** / **只翻未见的** / **数量 = min(n, 未见面数)**，
    /// 并且**消耗一次随机流**（洗牌）。
    /// </summary>
    private static string? SetCardsSeenByCipherRevealsOpponentUnseen(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);

        // 左方（施法方）自己手牌里两张 —— 永远不该被翻
        var own1 = state.CreateWithId("card_event_aans", Side.Left, 1501, CardLocation.HandLeft, 0);
        var own2 = state.CreateWithId("card_event_aans", Side.Left, 1502, CardLocation.HandLeft, 1);

        // 右方（对手）手牌三张，其中一张**已经见过**
        var foe1 = state.CreateWithId("card_event_aans", Side.Right, 2001, CardLocation.HandRight, 0);
        var foe2 = state.CreateWithId("card_event_aans", Side.Right, 2002, CardLocation.HandRight, 1);
        var foe3 = state.CreateWithId("card_event_aans", Side.Right, 2003, CardLocation.HandRight, 2);
        foe3.CardSeen = true;

        var ctx = new EffectContext { Engine = engine, State = state, Self = own1, Controller = Side.Left };

        long cursorBefore = state.Random.ConsumedCount;

        // n = 5 > 未见面数(2) ⇒ 应当把两张**未见的**全翻掉，一张不多
        engine.Api.InvokeByName("SetCardsSeenByCipher", own1,
            new object?[] { 5, own1.CardId, (int)Side.Left, null }, ctx, out bool handled);
        if (!handled)
        {
            return "派发表里没有 `SetCardsSeenByCipher` —— IR 里 2 个调用点静默失效";
        }

        if (!foe1.CardSeen || !foe2.CardSeen)
        {
            return $"对手两张未见的牌应当都被翻（n=5 &gt; 未见面数 2），"
                 + $"实际 #{foe1.CardId}.seen={foe1.CardSeen} #{foe2.CardId}.seen={foe2.CardSeen}"
                 + " —— 注意蓝图取的是**对手**手牌（:34107 的 side 取反）";
        }

        if (own1.CardSeen || own2.CardSeen)
        {
            return "**自己的**手牌被翻开了 —— 蓝图 :34107 取的是 `side` 的**对面**";
        }

        if (state.Random.ConsumedCount == cursorBefore)
        {
            return "没有消耗随机流 —— 蓝图 :34166 会对候选做 `Array_ShuffleFromStream`"
                 + "（参考实现洗的是下标数组，那是错的：蓝图洗的是候选卡本身）";
        }

        // 第二轮：n = 1，且未见面数为 0 ⇒ 什么都不做、也**不消耗**随机流
        var (engine2, state2) = EmptyBoard(db);
        var a = state2.CreateWithId("card_event_aans", Side.Right, 3001, CardLocation.HandRight, 0);
        a.CardSeen = true;
        var caster = state2.CreateWithId("card_event_aans", Side.Left, 3002, CardLocation.HandLeft, 0);
        var ctx2 = new EffectContext { Engine = engine2, State = state2, Self = caster, Controller = Side.Left };
        long before2 = state2.Random.ConsumedCount;
        engine2.Api.InvokeByName("SetCardsSeenByCipher", caster,
            new object?[] { 1, caster.CardId, (int)Side.Left, null }, ctx2, out _);

        if (state2.Random.ConsumedCount != before2)
        {
            return "对手手牌**全部已见**时不该消耗随机流（蓝图 :34150 的 `Array_IsNotEmpty` 门）";
        }

        return null;
    }

    private static string Dump(GameState state, params (string Label, string Value)[] extras)
    {        var sb = new System.Text.StringBuilder();
        sb.Append($"\n       左场: {string.Join("  ", state.Board(Side.Left).Select(x => $"{x.Name}#{x.CardId}({x.Attack}/{x.Defense} 费{x.KreditCost})"))}");
        sb.Append($"\n       左手: {string.Join("  ", state.Hand(Side.Left).Select(x => $"{x.Name}#{x.CardId}(费{x.KreditCost})"))}");
        foreach (var (label, value) in extras)
        {
            sb.Append($"\n       {label}: {value}");
        }

        return sb.ToString();
    }
}
