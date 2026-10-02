# 维度 2：合法性函数审计（蓝图 ⇄ 内核）

**只读审计，未改 `src/KLink.Bot/` 任何一行。**

- 内核快照时间：**2026-09-27 12:04:59**（`MatchEngine.cs` 的 mtime）。
  ⚠️ 审计期间有另一个代理在改这个目录 —— 我读到的 `MatchEngine.cs` 从 914 行变成 995 行，
  中途新增了 `OutOfRangeAttacksRejected`（`:73`）和 `CanReachAcrossFrontline`（`:866`）。
  **本报告的内核行号对应 995 行那一版。** 表里凡是写「已实现」的，请以行号复核。
- 蓝图来源：`out/bp-logic.json` / `out/bp-gamestate.json` / `out/bp-cardscheck.json` / `out/bp-cardfn.json`。
- 枚举权威：`<kards-src>\Source\kards\Public\*.h`。
- 内核派发表：`src/KLink.Bot/Effects/CardApiDispatch.cs` 的 `BuildDispatch()`（177 个键）。
  **键名不在表里 = 内核没实现这个蓝图原语。**
- 卡量统计：`klink bot/docs/card-ir.json`（1636 张，`entrypoints` + `steps[].fn`）、
  `out/cards-full.json`（2019 张，`usedTriggers` 直接给触发名 + CDO 字段）。
- 已定案项（容量 / 召唤失调 / 前线互斥 / 落点 / T1 不摸牌）**直接引用 `klink bot/docs/内核补全队列.md` §①.9 与 §①.10，未重复解码**。

### 读字节码时确认的两条控制流语义（本报告全程按这个读）

- `JumpIfNot(cond) -> N`：**cond 为假**时跳到语句 N。
  验证：`CanPlayCardFromHand` i=890 `JumpIfNot(_card.IsUnit()) -> 1259`，而 1259 正是 `_card.IsOrder()`。
- `PushExecutionFlow(Offset=N)` + 配对的无参 `PopExecutionFlowIfNot(cond)`：
  `cond` 为假 → 跳到 N；为真 → 落下去执行块体。
  验证：`CanCardDoAnything` i=1543/1557 与 §①.9 的既有结论一致。
- `PushExecutionFlow(Offset=N)` + `PopExecutionFlow`（无参）：无条件跳到 N（循环 continue）。
  验证：`DecrementPinnedTurnsEndTurn` i=241(→440) / i=439 / i=716 / i=817 的嵌套只有这一种读法能自洽
  （见下面该函数小节）。

---

## 1. 总表

| # | 函数 | 蓝图检查的条件（i=） | 内核实现位置 | 缺的条件 | 影响面 |
|---|---|---|---|---|---|
| **BP_Logic** |
| 1 | `CanCardDoAnything` | 按 location 分派：i=495 switch；i=1352-1568 召唤失调一刀切；i=1573 `HasAttackLeft` 才查攻击；i=2817 `loc!=7 && HasMovementLeft && !HasAbility("CantMove")`；i=3029 `kredits >= getTotalOperationCost` | **未实现**（无同名入口；规则被拆进 `CardInstance.CanOperateThisTurn`/`CanMoveThisTurn`/`MatchEngine.CanPlay`） | 见下「逐函数」 | 全卡池 |
| 2 | `CanIDoAnything` | i=16 枚举全部 `BP_BaseCard`；i=293 读每张卡的缓存位 `lastCanDoAnythingCheck` | **未实现** | 纯 UI（"我这回合还有没有事可做"），对局行为无影响 | 0 张卡直接调 |
| 3 | `CanPlayCardFromHand` | 9 条，见 §2.1.3 | `MatchEngine.CanPlay`（`:358-386`）**只实现 3 条** | 天气卡闸门 / 主基地自定义能力 / `BlockCardFromBeingPlayedFromHand` / `cannotDeployUnits` / `cannotPlayOrders` / 卡自己的 `CanPlayFromHand` / 指向牌的目标搜索 | 51 张天气卡 + 10 张非战役限制卡 + 417 张 `selectTargetOnPlayedFromHand` |
| 4 | `CanSideDrawCards` | i=52/86/124 `(turn==1 && !isTutorialGame) ⇒ false`；否则 i=267/305/343 `!((startingTurnForOpponent && turn==1) \|\| IsThereGameplayRestriction(side, 0))` | `MatchEngine.StartTurn`（`:217` `doDraw = State.Turn != 1`）**只实现第一条** | `startingTurnForOpponent` 分支、`cannotDrawCardAtTurnStart`（restriction 0） | 3 张非战役卡 + 战役地点 |
| 5 | `CanSideGainKreditSlots` | i=0/56/85 `!IsThereGameplayRestriction(side, 1)` | **未实现**（`StartTurn` 无条件 `MaxKredits+1`） | `cannotKreditSlotAtTurnStart`（restriction 1） | 1 张非战役卡（`card_event_admiral_hipper`） |
| 6 | `HasDeploymentSickness` | i=0/23/64/93/194/232 `IsLocatedOnBoard && enterPlayOnTurn==turn && !getHasBlitz` | `CardInstance.HasDeploymentSickness`（`:88-91`），用于 `CanOperateThisTurn`（`:104`）/`CanMoveThisTurn`（`:109`） | **无**（已定案） | 205 张 Blitz 是例外 |
| 7 | `IsLocationFull`（logic 版） | i=0/46 纯转发 `FetchCardsByLocation` | `GameState.HalfBoardCapacity=5`（`:95`）/`HandCapacity=9`（`:98`）+ `MatchEngine.HalfBoardFull`（`:403`） | **无**（已定案） | — |
| 8 | `GiveMobilizeBonus` | i=397/469/510/570 `IsSideActive(card.side) && IsLocatedOnBoard && hasMobilize` → `ChangeAttack(+1,type=1)` + `ChangeDefense(+1,type=1)` | **未实现**（`Keyword.Mobilize` 有，但没有任何地方结算） | 整条 | **9 张** `hasMobilize` 单位（`1st_airborne`/`2nd_parachute`/`5th_parachute_brigade`/`48e_regiment*`/`1er_marins_commandos`/`2e_brigade`/`43e_regiment_motorise`/`73e_regiment_infanterie`） |
| 9 | `DecrementPinnedTurnsEndTurn` | i=369/425 `pinnedTurns<1` 跳过；i=514/570 `pinnedTurns>1` → `ChangedPinnedTurns(-1)`；否则 i=717 `RemovePin` | **未实现**（`PinUnit` 派发成永久 `Keyword.Pinned`，没有任何地方解除） | 整条 | **58 张**调 `PinUnit` / 9 张调 `RemovePin` / 1 张调 `ChangedPinnedTurns` |
| 10 | `GetCardsPinnedThisTurn` | i=953 扫 `OnlineMatch.AllMatchActions` 与 `subActions` 找 `ZActionPinUnit` 的 `cardID` | **未实现** | 纯 UI 查询，无对局影响 | 0 |
| 11 | `DoOnStartOfTurn` | i=400/433/466 重置 `hasBeenAttackedThisTurn`/`hasAttackedThisTurn`/`attackCountThisTurn`；i=641 `gotchaActivated=0`；i=976 `DecrementTurnGameplayRestrictions()`；i=4528/4628/4674 `movementLeft=1`、`attackLeft=1`（Fury 2） | `MatchEngine.StartTurn`（`:216-251`）重置 `HasAttackedThisTurn`/`HasMovedThisTurn`/`OperationsUsedThisTurn` | `attackCountThisTurn`、`hasBeenAttackedThisTurn`、`gotchaActivated`、`DecrementTurnGameplayRestrictions`；且蓝图重置**双方**所有在场卡，内核只重置行动方 | Ambush（`hasAmbush` 46 张）、gotcha、限制卡 |
| 12 | `StartTurnBySide` | i=387 `CanSideGainKreditSlots`；i=469/507 `slot<Max && canGain` → `setKreditSlotBySide`；i=933 `SetKreditsAndKreditSlots`；i=1047 `ExecuteBeforeStartOfTurnEvents`；i=1083/1115 `CanSideDrawCards`；i=1247 `GiveMobilizeBonus`；i=1330 `ExecuteStartOfTurnEvents`；i=1422 `KreditCheckAndAutoBanIfNeeded` | `MatchEngine.StartTurn`（`:216`）—— 骨架对，但缺 5 个闸门/钩子 | `CanSideGainKreditSlots` 闸门、`CanSideDrawCards` 完整判据、`GiveMobilizeBonus`、`ExecuteBeforeStartOfTurnEvents`、`KreditCheckAndAutoBanIfNeeded`、`SetActiveBondsAtStartOfTurn`/`UpdateBondVisuals`（Bond 未建模） | 每回合 |
| 13 | `RunStartOfGameEffects` | i=46 `CreateAction_StartOfGame_Start` → i=82 `StartOfGameEffects` → i=142 `CreateAction_StartOfGame_End` | `MatchEngine.Start`（`:154-155`）直接 `FireTrigger("OnStartOfGame")` 两侧各一次 | action 信封（对拍用），规则等价 | 43 张 `OnStartOfGame` |
| 14 | `StartOfGameEffects` | i=5 `FetchAllCardsWithEventTrigger(63)`；i=296 逐张 `OnStartOfGame()` | `CardApi.FireTrigger("OnStartOfGame", null, side)`（按程序名派发，语义等价） | 无（触发号 63 未建模，但按名字派发覆盖同一批卡） | 43 张 |
| 15 | `isFirstPlayerToAct` | i=0/45 `GetStartingSide() == mySide` | `GameState.StartingSide`（`:32`）存在，无同名判定函数 | 无实际缺口 | — |
| 16 | `autoPickCardToDraw` | i=51 `selectCardToDrawPending` 非空 → 按键 2/3/1 取；i=2416/2445/2499 兜底 `GetDeckBySide(GetPlayingSide()).DeckCardIDs[0]` | `MatchEngine.PickCardToDraw`（`:99`）+ `CardApi` 兜底取 `DeckCardIDs[0]` | 无（近似正确；`effectSelected:` 族按键未实现） | 11 张 `selectCardToDraw` 系卡 |
| **BP_GameState_Battle** |
| 17 | `IsFrontlineLimited` | i=0/51 `Set_IsNotEmpty(FrontlineLimiters)` | `GameState.IsFrontlineLimited`（`:71`） | **无**（已定案） | 1 张（黑王子） |
| 18 | `IsThereGameplayRestriction` | i=283/342/389/436 遍历 `GameplayRestrictionEffects`，`RestrictionType==restriction && AffectedSide==side` | **未实现** | 整条 | 19 处 `AddGameplayRestriction`（**10 张非战役卡**），6 种类型全用到 |
| 19 | `IsCheatGameplayRestrictionActive` | i=283/342/389/436/521 同上 + `CardID==cheatCardID` | **未实现** | 整条 | 卡池 IR 里 **0 次**调用（只有作弊/测试路径用） |
| 20 | `CanPlayWeatherCard` | i=0/29 `!hasPlayedWeatherCardThisTurn` | **未实现** | 整条 | **51 张**天气卡（rain/storm/sunny 系） |
| 21 | `GetStopAttack` | i=0 直读字段 `stopAttack` | **未实现** | 整条 | 无卡 IR 调用；`XActionCheat`/服务端用 |
| 22 | `GetStopFurtherActions` | i=0 直读字段 `stopFurtherActions` | **未实现** | 整条 | 同上 |
| 23 | `DecrementTurnGameplayRestrictions` | i=280/331/375/561 `TurnsRemaining-1 >= 0` 才保留并写回 `-1` | **未实现** | 整条 | 同 #18 |
| 24 | `GetFatigueDamageBySide` | i=140/168/200 switch：0→0、1→`FatigueDamageLeft`、2→`FatigueDamageRight` | `GameState.Fatigue(side)`（`:41`）—— **语义等价** | 无 | 每局牌库抽空后 |
| 25 | `IncrementFatigueDamageBySide` | i=95/196 `Left/Right += 1` | `MatchEngine.DrawCard`（`:304-305` `State.SetFatigue(side, State.Fatigue(side)+1)`）—— **语义等价** | 无 | 同上 |
| 26 | `getKreditSlotsLostBySide` | i=95/164 `Abs(KreditSlotsLost_Left/Right)`，0 与 3 返回 0 | **未实现** | 整条 | 卡池 IR 里 **0 次**调用 |
| 27 | `UpdateFrontlineLimiter` | i=14 `Set_Remove` / i=79 `Set_Add` | `GameState.FrontlineLimiters`（`:66`），但**没有任何卡代码写它**（`ChangeFrontlineLimiter` 不在派发表） | 卡侧调用 | 1 张（黑王子） |
| 28 | `FetchCardsByLocation`（容量表） | i=1675 手牌 9 / i=1699 半场 5 / i=1746 `SelectInt(2,5,IsFrontlineLimited)` | `GameState.HandCapacity=9`/`HalfBoardCapacity=5`/`FrontlineCapacity`（`:79-98`） | **无（已定案）** | — |
| **cardsCheckFunctions** |
| 29 | `CanAttack` | 18 条，见 §2.3.1 | `MatchEngine.Attack`（`:680-785`）+ `LegalTargets`（`:788`）**只覆盖 5.5 条** | 见 §2.3.1 的逐条表 | 全卡池 |
| 30 | `CanSelectAsTarget` | i=0/43 有效性+在场；i=185/226/264/302 未揭示隐蔽卡；i=537/606/644/682 `cantBeTargetedByEnemyOrder`；i=1089/1135/1292/1365 费用与 `KreditsTax_AsEnemyTarget`；i=1417/1507/1567 `isSuppressed \|\| CanBeTargetted`；i=1581 `CanOtherCardBeTargetted` | **未实现** | 整条 | 全部指向性效果 |
| 31 | `CanOtherCardBeTargetted` | i=59 `FetchAllCardsWithEventTrigger(2)`；i=354 逐张调卡自己的重载，任一为真即真 | **未实现**（内核从不问"别的卡"） | 整条 | **1 张**卡订阅（`usedTriggers` 计数 1） |
| 32 | `getActiveEffects` | 23 个分支，见 §2.3.4 | **未实现** | 纯 UI 图标集合（`Set<byte>`），对局行为无影响 | 0 |
| **BP_CardFunctions** |
| 33 | `IsLocationFull` | i=0/68 转发 `FetchCardsByLocation` | 同 #7 | **无**（已定案） | — |
| 34 | `IsCardReserved` | i=0/54 `CardFunctionsNotifier.NotifyCheckCardReserved(InCardName)` | `CardApiDispatch.cs:65` **恒 `false` 的桩** | 整条 | **11 张**调 `IsCardReserved` + 1 处 UI 调 `isCardReserved` |
| 35 | `getFrontlineLimit` | i=0/45 转发 `IsFrontlineLimited` | `GameState.IsFrontlineLimited`（`:71`） | **无**（已定案） | 1 张 |
| 36 | `DoesSideControlTheFrontline` | i=0/45 `side == GetFrontlineOwnerSide()` | `CardApi.DoesSideControlTheFrontline`（`:693`）+ 派发表 `:70` | **无** | 已定案 |
| 37 | `CanCardBeBuffed` | i=0/41 `IsUnrevealedCovertCard ⇒ true`；否则 switch(location)：1/2/3/4/9→true，0/5/6/7/8→false | **未实现**（`ChangeAttack`/`ChangeDefense`/`ApplyTheBuff` 都不查） | 整条 | 所有 buff 效果；**在场卡（5/6/7）与弃牌堆（8）不该被 buff** |
| 38 | `PayCardCost` | i=20 `KreditCheckAndAutoBanIfNeeded`；i=134/230/341 敌方目标且 `KreditsTax_AsEnemyTarget>0` ⇒ 费用加税；i=578 `kredits > 拥有 ⇒ 失败` | `MatchEngine.PlayCard`（`:410` 扣 `card.KreditCost`）+ `CanPlay`（`:373`） | `KreditsTax_AsEnemyTarget` 加税、`KreditCheckAndAutoBanIfNeeded` | **3 张**：`stug_iii_g_fin`(2)、`pb4y_2_privateer`(2)、`red_devils`(1) |
| 39 | `PayMovementCost` | i=51/191 `kredits >= getTotalOperationCost` ⇒ 扣费 + `addOperationKreditsSpentThisTurn`，否则记 "possible cheat" 并返回 false | `MatchEngine.MoveUnit`（`:556` 判 `OperationCost > Kredits`，`:581` 扣费） | `addOperationKreditsSpentThisTurn`（`OnOperationKreditsSpent` 3 张卡订阅） | 3 张 |
| 40 | `CalculateDamageDealt` | 见 §2.4.1（免疫 / 先攻伤害 / 反击 / Ambush / HeavyArmor / passive defense / lethal / clamp 0-99） | **未实现**（`Attack` 直接 `attacker.Attack` 与 `defender.Attack`，`ApplyDamage` 只做减法） | 整条 | 52 张 `heavyArmor`、46 张 `hasAmbush`、32 张 `hasShock`、11 张 `hasCovert` |
| 41 | `ExecuteBeforeReceiveDamage` | i=38 `isSuppressed` 分流；i=74/198/232/270 对 HQ 打 >1 点 → 成就；i=321 广播触发号 52 | 部分：`CardApi.DealDamage`（`:366`）发 `OnReceiveDamage`/`OnOtherCardReceiveDamage` | 触发号 52 的"只发订阅者"语义、成就分支 | 15 + 8 张 |
| 42 | `ExecuteOnDealDamageAddDamage` | i=5 `!isSuppressed ⇒ OnCardDealDamage_ModifyDamageDealt`；i=109 遍历触发号 37；i=554 `reRunAtEnd` 二趟；i=1300 `Clamp(0,99)` | **未实现** | 整条 | **34 张**订阅 `OnOtherCardDealDamageAddDamage` |
| 43 | `ExecuteOnDealDamageAddDamageAfterCalc` | i=38 `toCard.getHasImmune ⇒ 0`；i=121 `damageDealer.isSuppressed` 分流；i=225 遍历触发号 38；i=595 `card_event_national_fire_service` 延后；i=1508 `Max(tmpDamage,0)` | **未实现** | 整条 | **15 张**订阅 `OnOtherCardDealDamageAddDamageAfterCalc` |
| 44 | `ExecuteOnSurvivedCombatEvents` | i=38 `!cardSurviving.isSuppressed`；i=74 遍历触发号 59；i=343 `card.cardID != cardSurviving.cardID` | **未实现** | 整条 | **9 张** `OnSurvivedCombat` + **6 张** `OnOtherCardSurvivedCombat` |
| 45 | `ExecuteStoppedAttack` | i=0 `SetAttackerHasAttacked(attacker)`（**停手也吃掉攻击**）；i=46 `IsActionProcess` → i=60 `attacker.OnAttackStopped()` + i=96 `NotifyStoppedAttack` | **未实现** | 整条 | **2 张** `OnAttackStopped` |
| 46 | `ResetUnitOperations` | i=32/73/114 `IsUnit && IsLocatedOnBoard`；i=198 `movementLeft=1`；i=362/535 `attackLeft=1`（Fury 2） | 部分：`StartTurn`（`:236-240`）重置 `HasAttackedThisTurn`/`HasMovedThisTurn`；**无 `attackLeft`（Fury 双攻）** | `attackLeft` 数值模型、Fury 例外 | **49 张** `hasFury` 全部少一次攻击 |
| 47 | `SetAttackerHasAttacked` | i=41 `CanMoveAndAttackInTheSameTurn` 才不扣移动；i=55 `attackLeft-=1`；i=168/201/234 三个标记；i=379 `movementLeft-=1` | 部分：`Attack`（`:727`）只置 `HasAttackedThisTurn`，**不扣移动额度** | `attackLeft`、`hasEverAttacked`、`attackCountThisTurn`、`CanMoveAndAttackInTheSameTurn` | 全卡池（"攻击吃掉移动"未建模） |
| 48 | `MoveUnitFromSupportToFrontLine` | i=41 在场；i=98/163 `cantMove`；i=281/333 对面占着⇒拒；i=468/543 我方满⇒拒 | `MatchEngine.MoveUnit`（`:544-617`）**逐条实现** | **无**（已定案） | — |
| 49 | `UpdateFrontlineIfNeeded` | i=138 空⇒`NotAvailable`；i=367 非空⇒第一张的 side；i=915 广播 `OnFrontlineOwnershipChange` | `MatchEngine.UpdateFrontlineOwner`（`:618`）+ `MoveUnit`（`:602`） | **无**（已定案） | 28 张订阅 |
| 50 | `UpdateGuarded` | i=5/36/67/174 只对 location ∈ {5,6,7}；i=523/634 有 Guard 且非未揭示隐蔽⇒`isBeingGuarded=false`；i=836/1517 相邻有 Guard⇒`isBeingGuarded=true` 否则 false | **未实现**（`LegalTargets` 用的是"全场有 Guard 就必须先打 Guard"） | 整条 —— **语义错**，见 §3.2 | **128 张** `hasGuard`；`CanAttack` #12 依赖 `isBeingGuarded` |
| 51 | `WhichStrategy` | i=0/45 `CardFunctionsNotifier.GetCampaignStrategy()` | **未实现** | 战役模式专用 | **16 张** |
| 52 | `IsUsingStrategy` | i=0/45/83 `strategy == GetCampaignStrategy()` | **未实现** | 同上 | **2 张** |

---

## 2. 逐函数证据

### 2.1 BP_Logic

#### 2.1.3 `CanPlayCardFromHand`（97 条语句，`out/bp-logic.json`）

按控制流顺序，**9 条判据**：

| # | i= | 原文片段 | 条件 |
|---|---|---|---|
| 1 | i=5/37 | `LocalVirtualFunction:GetCardFromID((cardID, …))` → `LetObj _card` | 取卡 |
| 2 | i=56/101/130/171/209 | `CanPlayWeatherCard(out can)`；`Not_PreBool(can)`；`_card.IsWeatherCard()`；`BooleanAND`；`JumpIfNot -> 239`；i=223 `yes=False` | **天气卡 且 本回合已打过天气卡 ⇒ 不可打** |
| 3 | i=239/284/344/400/438 | `GetClientSideLocationCard()`；`HasCustomAbility("blockplayfromhand")`；`HasCustomAbility("CantPlayCards")`；`BooleanOR`；`JumpIfNot -> 468`；i=452 `yes=False` | **己方 HQ 带这两个自定义能力之一 ⇒ 不可打** |
| 4 | i=468/509/581/619/679/717/833 | `_card.getTotalKreditCost()`；`getKreditBySide(_card.side)`；`GreaterEqual_IntInt`；`EqualEqual_ByteByte(_card.side, mySide)`；`BooleanAND`；`JumpIfNot -> 833`；i=833 `yes=False` | **费用不足 或 不是自己那侧 ⇒ 不可打** |
| 5 | i=731/803/817 | `_card.BlockCardFromBeingPlayedFromHand(out blocked, out reason, …)`；`JumpIfNot(blocked) -> 849`；i=817 `yes=False` | **卡自己的 `BlockCardFromBeingPlayedFromHand` ⇒ 不可打** |
| 6 | i=849/890/959/991 | `_card.IsUnit()`；`JumpIfNot -> 1259`；`SupplyLineLocationFromSide(mySide)`；`IsLocationFull(loc, out isFull)`；`JumpIfNot(isFull) -> 1021`；i=1005 `yes=False` | **单位且半场满（5 含 HQ）⇒ 不可打**（已定案） |
| 7 | i=1021/1057/1113/1127 | `JumpIfNot(_card.selectTargetOnPlayedFromHand) -> 1143`；`IsThereGameplayRestriction(mySide, Byte(3))`；`JumpIfNot(isRestricted) -> 2602`；i=1127 `yes=False` | **单位 且 `cannotDeployUnits` ⇒ 不可打** |
| 8 | i=1143/1224/1238/1243 | `_card.CanPlayFromHand(out canIt, …, out targetedCard)`；`JumpIfNot(canIt) -> 1243`；i=1238 `Jump -> 1057`（回到 #7 复核限制）；i=1243 `yes=False` | **卡自己的 `CanPlayFromHand` 必须为真**（指令牌会被同一段复核 #7 的限制） |
| 9 | i=1259/1300/1314/1370/1384 | `_card.IsOrder()`；`JumpIfNot -> 2586`；`IsThereGameplayRestriction(mySide, Byte(2))`；`JumpIfNot(isRestricted) -> 1400`；i=1384 `yes=False` | **指令 且 `cannotPlayOrders` ⇒ 不可打** |
| 10 | i=1400..2618 | `JumpIfNot(selectTargetOnPlayedFromHand) -> 2570`；遍历 `GetAllCardInBattle()`：i=1815 `CustomName1HasAttribute("cantBeTargetedByEnemyOrder")`、i=1774 `IsOwnedByClientSide`、i=1884 `IsLocatedOnBoard`、i=1925/1954/1992 `(!cantBeTargetedByEnemyOrder \|\| IsOwnedByClientSide) && IsLocatedOnBoard`；i=2144 `_card.targetOverride = card`；i=2185 `CanPlayFromHand(...)`；i=2276 `IsValid(targetedCard)`；i=2452 `cardsCheckFunctions.CanSelectAsTarget(item, _card, True, …)`；i=2554 `yes=True`；i=2618 `yes=False` | **需要选目标的指令：必须存在一张合法目标卡** |

**内核 `MatchEngine.CanPlay`（`:358-386`）**：
```csharp
if (card.Owner != State.ActiveSide)            // ≈ #4 的 EqualEqual_ByteByte
if (card.Location != State.ActiveSide.HandOf()) // 蓝图没有这条（多余但无害）
if (card.KreditCost > State.Kredits(...))       // ≈ #4 的 GreaterEqual
if (card.Definition.IsUnit && HalfBoardFull(...)) // ≈ #6
```
**缺 #2 #3 #5 #7 #8 #9 #10。**
（`IsWeatherCard` / `BlockCardFromBeingPlayedFromHand` / `CanPlayFromHand` 三个原语**都不在派发表**；
`IsThereGameplayRestriction` 也没有。）

#### 2.1.4 `CanSideDrawCards`（16 条语句）

```
i=0   LetBool Not_PreBool(isTutorialGame)
i=29  GetTurnNumber → CallFunc_GetTurnNumber_TurnNumber_1
i=52  LetBool EqualEqual_IntInt(TurnNumber_1, Int(1))
i=86  LetBool BooleanAND(EqualEqual_ReturnValue_1, Not_PreBool_ReturnValue)
i=124 JumpIfNot(BooleanAND_ReturnValue -> 154)
i=138     LetBool (False canDraw)
i=149     Jump -> 391
i=154 GetTurnNumber → CallFunc_GetTurnNumber_TurnNumber
i=177 LetBool EqualEqual_IntInt(TurnNumber, Int(1))
i=211 GameStateRef.IsThereGameplayRestriction(sideToCheck, Byte(0), out isRestricted)
i=267 LetBool BooleanAND(startingTurnForOpponent, EqualEqual_ReturnValue)
i=305 LetBool BooleanOR(BooleanAND_ReturnValue_1, IsThereGameplayRestriction_isRestricted)
i=343 LetBool Not_PreBool(BooleanOR_ReturnValue)
i=372 LetBool canDraw = Not_PreBool_ReturnValue_1
```
即：`canDraw = (turn==1 && !isTutorialGame) ? false : !((startingTurnForOpponent && turn==1) || IsThereGameplayRestriction(side, cannotDrawCardAtTurnStart))`。

`startingTurnForOpponent` 在 16 条语句里**没有赋值点**，只能是函数入参（dump 不含形参表 → **读不出来它到底是谁**）。
内核 `MatchEngine.StartTurn:217` `bool doDraw = draw ?? (State.Turn != 1);` 只等价于第一条分支。

#### 2.1.8 `GiveMobilizeBonus`（29 条语句）

```
i=51  GameStateRef.GetAllCardInBattle(...)
i=397 localCardToCheck.IsSideActive(localCardToCheck.side, out active)
i=469 localCardToCheck.IsLocatedOnBoard(out isIt)
i=510 BooleanAND(isIt, localCardToCheck.hasMobilize)
i=570 BooleanAND(BooleanAND_ReturnValue, IsSideActive_active)
i=608 PopExecutionFlowIfNot(BooleanAND_ReturnValue_1)
i=664 CardFunctions.ChangeAttack(localCardToCheck, localCardToCheck.cardID, Int(1), Byte(1), False, out qqq)
i=803 CardFunctions.ChangeDefense(localCardToCheck, localCardToCheck.cardID, Int(1), Byte(1), False, out qqq)
```
**每个回合开始**，给「在场上 且 `hasMobilize` 且 属于当前行动方」的单位 **+1/+1**（`changeType=Byte(1)`）。
调用点：`StartTurnBySide` i=1247。
内核：`Keyword.Mobilize` 在 `CardInstance.cs:340` 有常量、`CardInnateTable` 也灌了，但**没有任何地方结算**，
`GiveMobilizeBonus` 不在 `CardApiDispatch` 的 177 个键里。**9 张卡**（`out/cards-full.json` `hasMobilize` 真值计数 = 9）。

#### 2.1.9 `DecrementPinnedTurnsEndTurn`（29 条语句）—— 控制流逐条还原

```
i=0    PushExecutionFlow(Offset=818)          ← 外层循环，出口 818
i=5    LetBool localSkip = False
i=16..62   for card in GameStateRef.GetAllCardInBattle():
i=204  PopExecutionFlowIfNot(loopcond)        ← 假 ⇒ 818（退出）
i=241  PushExecutionFlow(Offset=440)          ← 内层作用域，continue 点 440
i=369  LetBool Less_IntInt(card.pinnedTurns, 1)
i=425  JumpIfNot(cond -> 514)                 ← pinnedTurns >= 1 ⇒ 514
i=439      PopExecutionFlow                   ← ⇒ 跳到 440（循环 continue）
i=514  LetBool Greater_IntInt(card.pinnedTurns, 1)
i=570  JumpIfNot(cond -> 717)                 ← pinnedTurns <= 1 ⇒ 717
i=584/630  CardFunctions.ChangedPinnedTurns(card.cardID, Int(0), Int(-1), out qqq)
i=716      PopExecutionFlow                   ← ⇒ 跳到 440（循环 continue）
i=717  CardFunctions.RemovePin(_card, out qqq)   ← pinnedTurns == 1 才走这里
i=763  …（另一处调用）
i=817  PopExecutionFlow                       ← ⇒ 跳到 440
i=818  Return
```
**结论**：`pinnedTurns < 1` → 跳过；`> 1` → 减 1；`== 1` → `RemovePin`。
`PinUnit`（`bp-cardfn.json`，55 条语句）里 pin 的时长是：
```
i=883 _card.IsSideActive(_card.side, out active)
i=955 SelectInt(Int(3), Int(2), active)          ← 自己那侧行动时 3，否则 2
i=1002 Max(_card.pinnedTurns, SelectInt_ReturnValue)
i=1070 _card.pinnedTurns = Max_ReturnValue
```
且 `PinUnit` 的守卫是：i=56 `IsValid`；i=99 `HasCustomAbility("cantBePinned") ⇒ 直接返回`；
i=215 `IsLocatedOnBoard`；i=266 `IsUnit`；i=1206 广播触发号 61 → `OnOtherUnitPinned`。
`RemovePin`（47 条语句）：i=48 `card.IsPinned()`；i=628 `ChangeBuffsFromCards(card, -1, instigator, 6, 8, "pinned")`；
i=715 `card.pinnedTurns = 0`；i=860 广播触发号 62 → `OnOtherUnitUnpinned`。
`ChangedPinnedTurns`：i=272 `Clamp(pinnedTurns + turnsToChange, 0, 5)`。

**内核**：`CardApiDispatch.cs:232` `["PinUnit"] = DoGiveKeyword(…, Keyword.Pinned)` —— 永久关键字，
**没有 `pinnedTurns`、没有 `cantBePinned`、没有 `IsLocatedOnBoard`/`IsUnit` 守卫、没有回合末递减**；
`RemovePin` **不在派发表**（卡调它 → 记 `UnimplementedCalls`）；`ChangedPinnedTurns` 也不在。
`CanAttack` #9 判 `attackerCard.IsPinned()`，内核用 `Keywords.Contains(Pinned)` 代 —— 在"永不解 pin"的前提下**会永久封住这张卡的攻击**。

#### 2.1.11 `DoOnStartOfTurn`（137 条语句）

```
i=56   GameStateRef.GetAllCardInBattle(...)
i=344  LetObj _card = item
i=363/386  IsLocalClientTurn ⇒ i=400 hasBeenAttackedThisTurn=False；i=433 hasAttackedThisTurn=False；i=466 attackCountThisTurn=0
i=526/567/627  GameStateRef.GetPlayingSide ⇒ (card.side == playingSide) ⇒ i=641 gotchaActivated=0
i=976  GameStateRef.DecrementTurnGameplayRestrictions()
i=4495 IsLocalClientTurn ⇒ i=4528 movementLeft=1；i=4573 getHasFury ⇒ i=4628 attackLeft=2 / i=4674 attackLeft=1
```
（i=235/511/516/521/1087/2566 那几层是视觉 `effectBar.SetEffectActive/Inactive`，
`GetVisualBoardCardCardFromID` —— 纯 UI，与规则无关。
⚠️ i=976 与循环体的嵌套我只还原到"每回合开始会被调用"，
**"是否在每张卡的迭代里被重复调用"我读不出来** —— 两种读法都能自洽，标注为不确定。）

内核 `MatchEngine.StartTurn:236-240` 只重置**行动方自己**的 `HasAttackedThisTurn`/`HasMovedThisTurn`/`OperationsUsedThisTurn`；
蓝图那段重置的是 `movementLeft`/`attackLeft`，且（在 `IsLocalClientTurn` 前提下）遍历**双方所有在场卡**。
`attackCountThisTurn` / `hasBeenAttackedThisTurn` / `gotchaActivated` 三个字段内核**完全没有** ——
`hasBeenAttackedThisTurn` 正是 `CalculateDamageDealt` 里 Ambush 分支的判据（i=1011）。

#### 2.1.12 `StartTurnBySide`（49 条语句）

```
i=20/43/85/127   CallMulticastDelegate((GetTurnNumber()+1)/2)        ← 回合号广播
i=176            GetMatchController().IsReconnectMatch(...)
i=250            GameStateRef.SetActiveBondsAtStartOfTurn(sideStartTurn)
i=341            CardFunctions.UpdateBondVisuals(sideStartTurn)
i=387            CanSideGainKreditSlots(sideStartTurn, out canGain)
i=419/469/507    getKreditSlotBySide(side) < MaxKreditsConst && canGain
i=760            GameStateRef.setKreditSlotBySide(side, slot+1, key1, key2, frameCount)
i=933            CardFunctions.SetKreditsAndKreditSlots(side, slot, slot, Int(0))
i=1047           CardFunctions.ExecuteBeforeStartOfTurnEvents()
i=1083/1115      CanSideDrawCards(sideStartTurn, out canDraw) ⇒ DrawTopCardFromDeck(side, 0, False, False, True, DoubleConst:, False, out drawnCard)
i=1247           GiveMobilizeBonus()
i=1330           CardFunctions.ExecuteStartOfTurnEvents(GetTurnNumber())
i=1422           CardFunctions.KreditCheckAndAutoBanIfNeeded()
i=1459/1497      (sideStartTurn == mySide) ⇒ NotifyPlayerAsYourTurn()
```
内核 `StartTurn`（`:216`）的骨架与之对应，但缺 #4/#5/#8 三个闸门 + `ExecuteBeforeStartOfTurnEvents`（14 张 `OnBeforeStartOfTurn` 订阅）
+ `KreditCheckAndAutoBanIfNeeded`。`OnBeforeStartOfTurn` **不在内核 28 个 `FireTrigger` 名字里**。

#### 2.1.16 `autoPickCardToDraw`（70 条语句）

```
i=51  Map_IsNotEmpty(OnlineMatch.selectCardToDrawPending)
i=284/353  Map_Find(map, Int(2))  ⇒ i=618 _chooseOneCardNames；i=645 Split(name, ":", …)；i=896 SelectString("cardToDrawSelected:", "effectSelected:", …)
i=1400/1446 Map_Find(map, Int(3))
i=1766/1812 Map_Find(map, Int(1))
i=2416/2445/2499  else: GetDeckBySide(GetPlayingSide()).DeckCardIDs[0]      ← 兜底 = 牌库第一张
```
内核 `MatchEngine.PickCardToDraw`（`:99`）+ `CardApi` 兜底取 `DeckCardIDs[0]` —— **兜底一致**。
`effectSelected:` 那条分支（i=896）内核没有。

---

### 2.2 BP_GameState_Battle

#### 2.2.18 `IsThereGameplayRestriction`（27 条语句）

```
i=73/102/161/199/237   循环条件（Not(break) && counter < len）
i=283  Array_Get(GameplayRestrictionEffects, index) → CallFunc_Array_Get_Item
i=342  EqualEqual_ByteByte(item.RestrictionType, restriction)
i=389  EqualEqual_ByteByte(item.AffectedSide, side)
i=436  BooleanAND(EqualEqual_ByteByte_ReturnValue_1, EqualEqual_ByteByte_ReturnValue)
i=474  PopExecutionFlowIfNot(AND) ⇒ i=484 localIsRestricted = True；i=495 break = True
i=507  isRestricted = localIsRestricted
```
结构体（`<kards-src>\Source\kards\Public\GameplayRestrictionEffect.h`）：
```
ESideEnum AffectedSide;
EGameplayRestrictions RestrictionType;
int32 CardID;
int32 TurnsRemaining;
```
枚举（`EGameplayRestrictions.h`）：
```
0 cannotDrawCardAtTurnStart   1 cannotKreditSlotAtTurnStart   2 cannotPlayOrders
3 cannotDeployUnits           4 cannotAttackWithGroundUnits   5 cannotDiscardAnyCardFromHand
6 NotAvailable
```
`IsCheatGameplayRestrictionActive` 是同构 + i=342 `EqualEqual_IntInt(item.CardID, cheatCardID)` + i=521 三个 AND。
`DecrementTurnGameplayRestrictions`：i=280 `TurnsRemaining - 1`；i=331 `>= 0` 才保留；
i=426/471/516/561 重建结构体（`AffectedSide`/`RestrictionType`/`CardID`/`TurnsRemaining-1`）。
`AddGameplayRestriction(side, type, cardID, turnsToLast)`（10 条语句，`bp-cardfn.json`）→
`GameStateRef.AddGameplayRestrictionEffect(struct)`；`RemoveGameplayRestriction(side, type, cardID, removeAll)` → `RemoveGameplayRestrictionEffect(...)`。

**内核**：`grep -i "GameplayRestriction" src/KLink.Bot` **零命中**。`GameState` 里没有这个数组，`GameState.cs` 里没有对应字段。
**影响**（`card-ir.json` 的 `AddGameplayRestriction` 第 2 个实参）：

| 类型 | 调用次数 | 非战役卡 |
|---|---|---|
| 0 `cannotDrawCardAtTurnStart` | 10 | `card_event_admiral_hipper`、`card_event_campaign_tunis3_supply_intercepted`、`card_unit_panther_a` |
| 1 `cannotKreditSlotAtTurnStart` | 4 | `card_event_admiral_hipper` |
| 2 `cannotPlayOrders` | 4 | `card_event_elusive_force`、`card_event_reichsbank`、`card_unit_60th_cavalry_regiment` |
| 3 `cannotDeployUnits` | 2 | `card_event_tirpitz`、`card_event_u_99` |
| 4 `cannotAttackWithGroundUnits` | 1 | `card_event_mud` |
| 5 `cannotDiscardAnyCardFromHand` | 1 | `card_unit_finnish_boys` |
| **合计** | 22 | **10 张非战役卡**（其余是 `card_location_*` 战役地点） |

`CanAttack` #6（i=1126 `IsThereGameplayRestriction(myRealSide, 4)`）也依赖它 —— 所以 `card_event_mud` 在内核里**完全无效**。

#### 2.2.20 `CanPlayWeatherCard`（4 条语句）

```
i=0  LetBool Not_PreBool(hasPlayedWeatherCardThisTurn)
i=29 LetBool can = Not_PreBool_ReturnValue
```
即 **每回合最多打一张天气卡**（全局一个 bool，不分侧）。
`CanPlayCardFromHand` #2（i=56/101/130/171/209）用它。
**内核零实现**。天气卡按 `cards-full.json` 文本含 "rain/storm/sunny/weather" 计数 = **51 张**
（`card_event_rain1_mist` … `card_event_storm4_cyclone3`、`card_event_sunny4_scorching_sun2` 等）。
`IsWeatherCard` 是 `BaseCardObject` 的原生函数，**不在 `bp-cardfn.json`**（`MISS`），具体判据**读不出来**。

#### 2.2.24/25 疲劳

```
GetFatigueDamageBySide:   i=140 side0→0；i=168 side1→FatigueDamageLeft；i=200 side2→FatigueDamageRight
IncrementFatigueDamageBySide: i=95 side1→Left+1；i=196 side2→Right+1
```
内核 `GameState.Fatigue(side)`（`:41`）+ `MatchEngine.DrawCard`（`:304-305`）—— **语义等价**。

#### 2.2.26 `getKreditSlotsLostBySide`（12 条语句）

```
i=0/31/45/76  switch(sideToGet)：1 → i=95 Abs(KreditSlotsLost_Left)；2 → i=164 Abs(KreditSlotsLost_Right)；其余 → 0
```
`card-ir.json` 里**没有任何卡调用它**（`steps[].fn` 计数 0）→ 无对局影响，纯服务端/UI。

---

### 2.3 cardsCheckFunctions

#### 2.3.1 `CanAttack` —— 内核逐条覆盖

（18 条判据清单来自任务书 + §①.9，**未重新解码字节码**，只核对内核。）

| # | 语句 | 蓝图条件 | 内核 | 判定 |
|---|---|---|---|---|
| 1 | i=116 | `!attacker.IsUnit()` | `Attack:682` `attacker.IsHq` 拒；调用方只传单位 | 近似 |
| 2 | i=172 | `HasCantAttack()` | 无 | **缺** |
| 3 | i=549 | `HasCantAttackType(防守方type) && !HasCustomAbility("ignoreCantAttack_"+type)` | 无 | **缺** |
| 4 | i=684/744 | 防守方空军 且 `HasCantAttackType("air")` | 无 | **缺** |
| 5 | i=800/859 | 防守方陆军 且 `HasCantAttackType("ground")` | 无 | **缺** |
| 6 | i=1126 | `IsThereGameplayRestriction(myRealSide,4) && attackerIsGroundUnit` | 无（限制系统整体缺） | **缺** |
| 7 | i=1339 | `defender.HasCantBeAttackedBy(攻击方type)` | 无 | **缺** |
| 8 | i=1477/1537/1552/1652 | 空/陆军变体 | 无 | **缺** |
| 9 | i=1839 | `attacker.IsPinned() && !HasCustomAbility("canOperateWhilePinned")` | `Attack:712` 查 `Keyword.Pinned`（**没有豁免能力**，且**多加了 `Keyword.Suppressed`**） | **语义错** |
| 10 | i=2087 | `(currentTurn == enterPlayOnTurn) && !getHasBlitz()` | `CardInstance.HasDeploymentSickness:88` + `CanOperateThisTurn:104` | **✔**（已定案） |
| 11 | i=2410 | `(攻方loc != 7) && (守方loc != 7) && (攻方 range < 2)` | `MatchEngine.CanReachAcrossFrontline:866`（**12:04 那次改动新增**）+ `Attack:720` + `LegalTargets:797` | **✔**（新加） |
| 12 | i=2627 | 非轰炸/火炮 且 `defender.isBeingGuarded` | `LegalTargets:794` "全场有 Guard 就必须先打 Guard" | **语义错**（见 §3.2） |
| 13 | i=3370 | 轰炸机 且 防守位有未揭示的隐蔽卡（type==4） | 无（`hasCovert` 11 张，`IsUnrevealedCovertCard` 未建模） | **缺** |
| 14 | i=3637 | `defender.getHasSmokescreen()` | 无（`Keyword.Smokescreen` 有、`CardInnateTable` 灌了 **54 张**，但 `Attack`/`LegalTargets` 从不读） | **缺** |
| 15 | i=3903 | `!attacker.HasAttackLeft()` | `Attack:687` `CanOperateThisTurn` 查 `HasAttackedThisTurn`（**单次；Fury 双攻没建模**） | **部分**（49 张 Fury 少一次攻击） |
| 16 | i=4208 | `attackerKredits < attacker.getTotalOperationCost()` | `Attack:707` `attacker.OperationCost > State.Kredits(...)` | **✔** |
| 17 | i=4292 | 攻击方 location ∉ {5,6,7} | `Attack:682` `attacker.Location.IsBoard()` | **✔** |
| 18 | i=4896 | `CanSelectAsTarget(...)` | 无（`LegalTargets` 是近似） | **缺** |

**统计：18 条里内核完整实现 4 条（#10 #11 #16 #17）、近似 2 条（#1 #15）、语义错 2 条（#9 #12）、完全缺 10 条。**

#### 2.3.2 `CanSelectAsTarget(Targeted, Targeting, byPlayFromHand, __WorldContext)`（72 条语句，static）

```
i=0/29     IsValid(Targeted)            为假 ⇒ 直接返回（can 默认 false）
i=43/84    Targeted.IsLocatedOnBoard()  为假 ⇒ 直接返回
i=98/156   Targeting.CustomName1HasAttribute("canTargetCovert") → Not
i=185/226  Targeted.IsUnrevealedCovertCard() && byPlayFromHand && !canTargetCovert
i=302/316     can=False；Reason="cant_target_unrevealed"；返回
i=414/455/537/606/644/682  Targeting.IsOrder() && Targeted.side != Targeting.side
                            && Targeted.CustomName1HasAttribute("cantBeTargetedByEnemyOrder")
i=696         can=False；Reason="cant_be_targeted_by_enemy_orders"；返回
i=858/952/993/1034/1089/1135
           tmpRemainingKredits = getKreditBySide(Targeting.side)
                               - SelectInt(getTotalKreditCost(Targeting), getTotalOperationCost(Targeting), byPlayFromHand)
i=1162/1196/1757  tmpRemainingKredits < 0 ⇒ Reason = SelectString("play_from_hand_not_enough_kredits_to_target",
                                                                  "not_enough_kredits_to_target", byPlayFromHand)
i=1210/1292/1365  extra = SelectInt(Int(0), Targeted.KreditsTax_AsEnemyTarget, Targeting.side == Targeted.side)
                  tmpRemainingKredits < extra ⇒ Reason="cost_extra_to_target"，Param1 = str(KreditsTax_AsEnemyTarget)
i=1417/1507/1567  Targeted.isSuppressed || Targeted.CanBeTargetted(out canIt, …, Targeting, byPlayFromHand)
i=1581/1667       ⇒ CanOtherCardBeTargetted(Targeting, Targeted, byPlayFromHand, out CanIt, …)
                      CanIt ⇒ can=True；否则 Reason = CanOtherCardBeTargetted 的 Reason
i=2114            can=False；Reason = CanBeTargetted 的 Reason
i=2211            can=False；Reason = CanOtherCardBeTargetted 的 Reason
```
注意 i=1507：**`Targeted.isSuppressed` 为真时直接短路成"可选中"**（`BooleanOR`）。
`SelectInt(A,B,bPickA) = bPickA ? A : B`（jmap `/Script/Engine.KismetMathLibrary:SelectInt`）。
**内核零实现**；`CanBeTargetted` / `CanOtherCardBeTargetted` / `IsUnrevealedCovertCard` / `KreditsTax_AsEnemyTarget`
四个原语都不在派发表。

#### 2.3.3 `CanOtherCardBeTargetted(cardTargetting, targetCard, byPlayFromHand)`（28 条语句）

```
i=5/59   GetGameState().FetchAllCardsWithEventTrigger(Byte(2))
i=354    item.CanOtherCardBeTargetted(out canIt, out reason, …, cardTargetting, targetCard, byPlayFromHand)
i=453/467  任一为真 ⇒ CanIt=True、Reason=""、返回
i=618      全假 ⇒ CanIt=False，Reason 取最后一次的
```
即"问所有订阅了触发号 2 的卡"。**内核从不做这件事**（`FetchAllCardsWithEventTrigger` 整个机制不存在，
内核是按程序名遍历棋盘+弃牌堆派发，见 `CardApi.FireTrigger:122-249`）。
订阅者数量（`out/cards-full.json` 的 `usedTriggers`）＝ **1 张**。

#### 2.3.4 `getActiveEffects(card, out effects)`（171 条语句，static）

构造一个 `Set<byte>` 的**图标集合**，23 个分支：

| i= | 条件 | 图标 |
|---|---|---|
| 146 | `card.hasMobilize` | 14 |
| 281 | `getHasAmbush()` | 1 |
| 403 | `getHasFury()` | 10 |
| 525/595 | `card.hasGuard && !IsUnrevealedCovertCard()` | 3 |
| 740/772/1171/1262/1344/1440 | `card.isBeingGuarded`，取 `GetAdjacentCards(card, False)` 里第一个 `hasGuard` 的：`adj.locationNumber > card.locationNumber` ⇒ 21，否则 20 | 21/20 |
| 1589 | `getHasSmokescreen()` | 5 |
| 1711 | `card.underEnemyControl` | 11 |
| 1814/1855/1903/1951 | `getTotalHeavyArmor()` switch 1/2/3 | 6/7/8 |
| 2213 | `effectType == 9 \|\| HasCustomAbility("passive")` | 9 |
| 2435 | `hasDestruction \|\| (CustomName1HasAttribute("AddDestructionIcon") && !…("StopDestructionEffect")) \|\| HasCustomAbility("destruction")` | 12 |
| 2860 | `hasActivePincerEffect()` | 16 |
| 2982 | `effectType == 13 \|\| HasCustomAbility("trigger")` | 13 |
| 3204 | `IsVeteran(False)` | 18 |
| 3327 | `hasActiveCountdownEffect()` | 19 |
| 3449 | `IsPinned()` | 4 |
| 3571 | `getHasImmune()` | 15 |
| 3693 | `card.isSuppressed` | 22 |
| 3796 | `card.hasSalvage` | 23 |
| 3899 | `card.isSalvaged` | 24 |
| 4002 | `card.hasCovert` | 25 |
| 4105 | `getHasShock()` | 26 |

**纯客户端图标行**，对 NN/引擎行为无影响。但它顺带证明了一串内核**没有建模的状态谓词**：
`underEnemyControl` / `hasActivePincerEffect` / `hasActiveCountdownEffect` / `isSalvaged` / `isRevealed` / `gotchaActivated`。
内核零实现。

---

### 2.4 BP_CardFunctions

#### 2.4.1 `CalculateDamageDealt`（170 条语句）

签名（按实参位置还原，`bp-cardfn.json`）：
`CalculateDamageDealt(damageDealerCard, damageRecieverCard, dealingDamageIsAttacker, ignoreAmbush, ignoreHeavyArmor, applyBeforeAttackBuffs, out damage, out doesDamageRecieverDie, out damageRecieverKilledBeforeAattack, out wasShockAttack)`

按 `PushExecutionFlow` 的 Offset 还原出的分支：

```
i=155/196    _damageRecieverCard.getHasImmune() ⇒ 真时落 i=210..266：
             damage=0；doesDamageRecieverDie=False；damageRecieverKilledBeforeAattack=False；wasShockAttack=False；返回
i=4031       PopExecutionFlowIfNot(applyBeforeAttackBuffs)   ← 为假 ⇒ 跳到主路径 i=271
  i=4041/4095  AttackerBeforeAttackAttackBuff  = dealer.GetBeforeAttackAttackBuff(dealingDamageIsAttacker)
  i=4122/4176  AttackerBeforeAttackDefenseBuff = dealer.GetBeforeAttackDefenseBuff(dealingDamageIsAttacker)
  i=4232/4286  DefenderBeforeAttackAttackBuff  = receiver.GetBeforeAttackAttackBuff(!dealingDamageIsAttacker)
  i=4342/4396  DefenderBeforeAttackDefenseBuff = receiver.GetBeforeAttackDefenseBuff(!dealingDamageIsAttacker)
  i=4423/4477  BeforeAttackDamage = dealer.GetBeforeAttackDamage(dealingDamageIsAttacker)
  i=4504/4538  BeforeAttackDamage > 0 ⇒ i=4552 ExecuteOnDealDamageAddDamage(dealer, receiver, BeforeAttackDamage,
                                                                           fromAttack=False, isDefenderDamage=False, ignoreAmbush=False)
  i=4632/4673  BeforeAttackDamage >= receiver.getTotalDefense()
  i=4711/4721     damage = BeforeAttackDamage；doesDamageRecieverDie=True；
                  damageRecieverKilledBeforeAattack=True；wasShockAttack=False
i=271..822   主路径：
  i=342/383/473  _dealerCalculatedDamage = ExecuteOnDealDamageAddDamage(
                     dealer, receiver, dealer.getTotalAttack() + SwitchValue(applyBeforeAttackBuffs),
                     fromAttack=True, isDefenderDamage=False, ignoreAmbush = !dealingDamageIsAttacker)
  i=603/644/734  _recieverCalculatedDamage = ExecuteOnDealDamageAddDamage(
                     receiver, dealer, receiver.getTotalAttack() + SwitchValue(applyBeforeAttackBuffs),
                     fromAttack=True, isDefenderDamage=False, ignoreAmbush = dealingDamageIsAttacker)
i=823/931     receiver.getHasAmbush() && !ignoreAmbush ⇒
  i=1011/1062   dealingDamageIsAttacker && !receiver.hasBeenAttackedThisTurn && !dealer.getHasImmune()
  i=1189        dealer.IsArtillery() ⇒ 跳过整块
  i=1461        dealer.IsBomber() && !receiver.IsFighter() && !receiver.IsAntiAir() ⇒ 跳过
  i=1596        receiver.IsBomber() || dealer.getHasShock() ⇒ 跳过
  i=1971/2009   _recieverCalculatedDamage >= dealer.getTotalDefense() + dealer.getTotalHeavyArmor()
                                              + dealer.GetPassiveDefenseBuff(_recieverCalculatedDamage)
                                              + SwitchValue(applyBeforeAttackBuffs)
                ⇒ i=2019 _dealerCalculatedDamage = 0        ← 伏击反杀
i=2043/2151   !dealingDamageIsAttacker && receiver.IsArtillery() ⇒ _dealerCalculatedDamage = 0
i=2185/2293   !dealingDamageIsAttacker && dealer.IsBomber()     ⇒ _dealerCalculatedDamage = 0
i=2327/2622   !dealingDamageIsAttacker && receiver.IsBomber() && !(dealer.IsFighter() || dealer.IsAntiAir())
                ⇒ _dealerCalculatedDamage = 0
i=2656/2843   !dealingDamageIsAttacker && receiver.getHasShock() && dealer.IsUnit()
                ⇒ _dealerCalculatedDamage = 0；i=2876 shockAttack = True
i=2888/2922   _dealerCalculatedDamage > 0 ⇒
  i=2936/2977   SelectInt(Int(0), receiver.getTotalHeavyArmor(), ignoreHeavyArmor)      ← 重甲减免
  i=3028/3082   SelectInt(receiver.GetPassiveDefenseBuff(_dealerCalculatedDamage), Int(0), applyBeforeAttackBuffs)
  i=3133/3179/3225/3267  _dealerCalculatedDamage = Max(0, dmg - (重甲 + 被动防御))
i=3294/3426   _dealerCalculatedDamage += (applyBeforeAttackBuffs ? AttackerBeforeAttackAttackBuff : 0)
i=3453/3536   receiverDefense = receiver.getTotalDefense() + (applyBeforeAttackBuffs ? DefenderBeforeAttackDefenseBuff : 0)
i=3626/3664/3678  _dealerCalculatedDamage >= receiverDefense ⇒ localDoesDamageRecieverDie = True
i=3690/3919/3933  dealer.HasCustomAbility("lethal") && !receiver.IsLocation() && _dealerCalculatedDamage > 0 ⇒ Jump -> 3678
i=3938        localDoesDamageRecieverDie = False
i=3949/3950   damage = _dealerCalculatedDamage；doesDamageRecieverDie = localDoesDamageRecieverDie；
              damageRecieverKilledBeforeAattack = False；wasShockAttack = shockAttack
i=4781        Return
```
⚠️ **i=3664 与 i=3938 的精确嵌套我没能定案**：`JumpIfNot(GreaterEqual) -> 3690` 为假时落到 i=3938 `localDoesDamageRecieverDie = False`，
按字面读会把 i=3678 刚设的 `True` 覆盖掉。两种读法（"防御比较决定死亡" / "只有 lethal 决定死亡"）都能自洽，
**标注为不确定**，需要另做一次带作用域栈的控制流还原。

**内核**：`MatchEngine.Attack:738-739`
```csharp
int attackerDamage = attacker.Attack;
int defenderDamage = defender.IsHq ? 0 : defender.Attack;   // 反击
```
→ `Api.DealDamage` → `MatchEngine.ApplyDamage:802` 只做 `target.Defense -= amount`（+ `Keyword.Immune` 直接返回）。
**整条管线缺**：免疫以外的重甲减免（**52 张 `heavyArmor`**）、伏击（**46 张 `hasAmbush`**）、
Shock（**32 张 `hasShock`**）、passive defense、`GetBeforeAttack*` 先攻伤害、
`OnCardDealDamage_ModifyDamageDealt` / `OnOtherCardDealDamageAddDamage`（**34 张**订阅）、
`OnOtherCardDealDamageAddDamageAfterCalc`（**15 张**）、`Clamp(0,99)`、`lethal`、`hasBeenAttackedThisTurn`。
另外内核**从不发 `OnOtherCardDealDamage`**（`out/cards-full.json` 里 **28 张**卡订阅它）。

#### 2.4.2 `ExecuteBeforeReceiveDamage`（33 条语句）

```
i=5/28   IsActionProcess 守卫
i=38     JumpIfNot(cardToReceiveDamage.isSuppressed -> 687)
i=687        cardToReceiveDamage.OnReceiveDamage(cardToDealDamage, damageAmount)
i=741        Jump -> 74
i=74/138/198/232/270  cardToReceiveDamage.cardID == GetLocationCardBySide(GetOpponentSide()).locationCardID
                      && damageAmount > 1 ⇒ Achievements_UpdateDealtMoreThanOneToHQ(True)
i=321    FetchAllCardsWithEventTrigger(Byte(52))
i=590/672    item.cardID == cardToReceiveDamage.cardID ⇒ i=687 OnReceiveDamage
            否则 i=820 item.OnOtherCardReceiveDamage(cardToDealDamage, cardToReceiveDamage, isCombatDamage, damageAmount)
```
⚠️ `i=38 JumpIfNot(...) -> 687` 与 `i=741 Jump -> 74` 组合起来，在"接收者自己也在触发号 52 列表里"时会形成回边，
我无法排除它其实是"先广播、再调自己"的另一种编译形态 —— **这段的精确语义标注为不确定**。
能确定的是：**触发号 52 的广播存在**（`OnOtherCardReceiveDamage`，**9 张**订阅；`OnReceiveDamage` **15 张**）。
内核 `CardApi.DealDamage:366` 发 `OnReceiveDamage` + `OnOtherCardReceiveDamage`（名字对），但**没有 `isCombatDamage` 参数**
（内核的 `FireTrigger` 只在 `ZActionDamageCard` 里带了 `damage`，触发程序拿不到 `isCombatDamage`）。

#### 2.4.3 `ExecuteOnDealDamageAddDamage`（46 条语句）

```
i=5      JumpIfNot(_damageDealerCard.isSuppressed -> 692)
i=692        _damageDealerCard.OnCardDealDamage_ModifyDamageDealt(_damageRecieverCard, damage, _fromAttack, fromFight,
                                                                  out newDamage)
i=773/800    _dealerCalculatedDamage = newDamage；Jump -> 68
i=41     （被抑制路径）_dealerCalculatedDamage = damage
i=68/109 Array_Clear(addDamageToReRun)；FetchAllCardsWithEventTrigger(Byte(37))
i=382/481/527  item.OnOtherCardDealDamageAddDamage(_damageDealerCard, _damageRecieverCard, _dealerCalculatedDamage,
                                                   _fromAttack, _isDefenderDamage, out damageToAdd, out reRunAtEnd)
               _dealerCalculatedDamage += damageToAdd
i=554/564/623  reRunAtEnd ⇒ 把 item 记进 addDamageToReRun
i=805/851..1295 对 addDamageToReRun 里的卡再跑一遍，再累加
i=1300/1347  calculatedDamage = Clamp(_dealerCalculatedDamage, Int(0), Int(99))     ← ★ 上限 99
```
**内核零实现**。订阅者 **34 张**。

#### 2.4.4 `ExecuteOnDealDamageAddDamageAfterCalc`（66 条语句）

```
i=5/28   IsActionProcess 守卫
i=38/79/93   toCard.getHasImmune() ⇒ finalDamage = 0；返回
i=121/742    JumpIfNot(damageDealer.isSuppressed -> 742)
i=742..947   （未抑制路径）damageToAdd = damageDealer.OnDealDamageAddDamageAfterCalc(toCard, damageAmount,
                                     isCombatDamage, isAttackingDamage, isRedirected, out …)
             tmpDamage = Max(damageAmount + damageToAdd, 0)；Jump -> 184
i=157..      （抑制路径）tmpDamage = damageAmount
i=225        FetchAllCardsWithEventTrigger(Byte(38))
i=595/659    item.name == Name("card_event_national_fire_service") ⇒ 记进 runAfter（延后到最后一趟）
i=1656/1738  item.cardID != damageDealer.cardID ⇒ i=1748 item.OnOtherCardDealDamageAddDamageAfterCalc(...)
                                                  tmpDamage = Max(tmpDamage + damageToAdd, 0)
i=1962/1972  stopAdding ⇒ break
i=952..1508  对 runAfter 里的卡再跑一遍
i=1508/1550  finalDamage = Max(tmpDamage, 0)
```
**内核零实现**。订阅者 **15 张**。`card_event_national_fire_service` 的"必须最后结算"语义也没有。

#### 2.4.5 `ExecuteOnSurvivedCombatEvents`（25 条语句）

```
i=5/28   IsActionProcess 守卫
i=38     JumpIfNot(cardSurviving.isSuppressed -> 549)
i=549        cardSurviving.OnSurvivedCombat(cardCombatted)      ← 未抑制：只发自己
i=74     FetchAllCardsWithEventTrigger(Byte(59))
i=343/425/435/494  item.cardID != cardSurviving.cardID ⇒ item.OnOtherCardSurvivedCombat(cardSurviving, cardCombatted)
```
**内核零实现**（`OnSurvivedCombat` **9 张** + `OnOtherCardSurvivedCombat` **6 张**，都不在内核 28 个 `FireTrigger` 名字里）。
"哪张卡算存活"（`cardSurviving`）的判据在调用方，**读不出来**。

#### 2.4.6 `ExecuteStoppedAttack`（7 条语句）

```
i=0    SetAttackerHasAttacked(attacker)          ← ★ 停手也消耗攻击
i=23/46  IsActionProcess ⇒ i=60 attacker.OnAttackStopped()；i=96 CardFunctionsNotifier.NotifyStoppedAttack(attacker.cardID)
```
**内核零实现**。订阅者 **2 张**。

#### 2.4.7 `CanCardBeBuffed(Card)`（30 条语句）

`K2Node_SwitchEnum` 的 `CmpSuccess = (Card.location != K)`，`JumpIfNot(CmpSuccess)` 命中即跳 case 体：

| i= | location | 跳转 | 结果 |
|---|---|---|---|
| 55/108 | 0 | 746 | **false** |
| 122/175 | 1 | 762 | true |
| 189/242 | 2 | 762 | true |
| 256/309 | 3 | 762 | true |
| 323/376 | 4 | 762 | true |
| 390/443 | 5 | 746 | **false** |
| 457/510 | 6 | 746 | **false** |
| 524/577 | 7 | 746 | **false** |
| 591/644 | 8 | 746 | **false** |
| 658/711 | 9 | 762 | true |
| — | default | i=725 `Jump -> 773` | 保持默认（false） |

外加最外层 `i=0/41 JumpIfNot(Card.IsUnrevealedCovertCard() -> 730)`：**未揭示的隐蔽卡恒可被 buff**。
即 **在场（5/6/7）、弃牌堆（8）、NotAvailable（0）不可被 buff**；牌库（1/2/9）、手牌（3/4）可以。
**内核零实现**：`CardApi.ChangeAttack:400` / `ChangeDefense:423` / `ApplyTheBuff` 都不查，
`CanCardBeBuffed` 不在派发表。→ **内核允许给场上/弃牌堆的卡加 buff**。

#### 2.4.8 `PayCardCost(cardID, card, targetCardID)`（36 条语句）

```
i=20     KreditCheckAndAutoBanIfNeeded()
i=35/69  targetCardID > 0 ⇒ targetCard = GetCardFromID(targetCardID)
i=134/216  card.side != targetCard.side ⇒
i=230/286    targetCard.KreditsTax_AsEnemyTarget > 0 ⇒
i=341/409      kredits = targetCard.KreditsTax_AsEnemyTarget + card.getTotalKreditCost()
i=437/478  else kredits = card.getTotalKreditCost()
i=578/616  kredits > getKreditBySide(card.side) ⇒ errorText = "…"
i=689/727  errorText == MakeLiteralText("") ⇒ ChangeKreditsBySide(card.side, -kredits, cardID)；success=True
            否则 success=False
```
**`KreditsTax_AsEnemyTarget`**：`out/cards-full.json` 里只有 **3 张**（`stug_iii_g_fin`=2、`pb4y_2_privateer`=2、`red_devils`=1）。
内核 `MatchEngine.PlayCard:410` 无条件扣 `card.KreditCost`，**没有敌方目标加税**，也没有 `KreditCheckAndAutoBanIfNeeded`。

#### 2.4.9 `PayMovementCost(cardID)`（16 条语句）

```
i=0/32   _card = GetCardFromID(cardID)
i=51/92  operationCost = _card.getTotalOperationCost()
i=119/191  getKreditBySide(_card.side) >= operationCost ⇒
i=243/285     ChangeKreditsBySide(_card.side, -operationCost, Int(0))
i=344         GameStateRef.addOperationKreditsSpentThisTurn(operationCost)
i=389         success = True
i=405/532  else DirectClientLogger("possible cheat: pay movement cost is called when the player doesn't have enough kredits.")
              success = False
```
**没有移动额度检查、没有位置检查** —— 这条只负责扣钱。
内核 `MatchEngine.MoveUnit:556/581` 等价；缺 `addOperationKreditsSpentThisTurn`（订阅者 **3 张**，触发名 `OnOtherCardOperationKreditSpent`）。

#### 2.4.10 `UpdateGuarded(location)`（62 条语句）

```
i=5/36/67/174  仅 location ∈ {5, 6, 7} 才做（BooleanOR 三个 EqualEqual_ByteByte）
i=188          FetchCardsByLocation(location, …)
i=523/564/634  对每张 _currentCard：cond = getHasGuard() && !IsUnrevealedCovertCard()
i=672          JumpIfNot(cond -> 836)
i=686/718          cond 为真 ⇒ isBeingGuarded = False      ← Guard 单位自己不被守护
i=836          cond 为假 ⇒ _removeGuarded = True
i=847/1206/1276   GetAdjacentCards(_currentCard, True) 里若有 hasGuard && !covert 的邻卡：
i=1346/1382/1393      isBeingGuarded = True；_removeGuarded = False；break
i=1517/1587    _removeGuarded && isBeingGuarded ⇒ isBeingGuarded = False
```
**语义**：`isBeingGuarded` = "**相邻**（`GetAdjacentCards`，`locationNumber` 相邻）有一张已揭示的 Guard 卡"。
`CanAttack` #12（i=2627）只在攻击者**不是轰炸/火炮**时看这一条。

**内核**：`MatchEngine.LegalTargets:793-798`
```csharp
var guards = enemyUnits.Where(u => u.Keywords.Contains(Keyword.Guard)).ToList();
if (guards.Count > 0) return guards.Where(t => CanReachAcrossFrontline(attacker, t));
```
→ **全局嘲讽**：只要敌方场上**任何位置**有 Guard，就必须打它。蓝图是**相邻格子**的守护，
且**轰炸机与火炮可以无视守护**（直接打被守护者）。
**128 张 `hasGuard`** 全部受影响。`UpdateGuarded` 不在派发表，`isBeingGuarded` 字段内核没有。

#### 2.4.11 `IsCardReserved(InCardName)`（4 条语句）

```
i=0  CardFunctionsNotifier.NotifyCheckCardReserved(InCardName, out IsReserved)
i=54 LetBool IsReserved = CallFunc_NotifyCheckCardReserved_IsReserved
```
转发到通知器（`bp-notifier.json` 的 `NotifyCheckCardReserved` 只有 4 条语句，走 `I_BP_Logic_Receiver` 接口），
**真正判据在实现该接口的卡/Logic 上，我 dump 的资产里读不出来**。
内核 `CardApiDispatch.cs:65`：`["IsCardReserved"] = (c, r, a) => false,   // TODO 待 BalancedCards 解出` —— **恒 false 的桩**。
调用方：**11 张**卡 + `createCard_NUI_Widget`（`isCardReserved`）。
`cards-full.json` 的 CDO 里 `isReserved` 字段出现在 **563 张**卡上（值需另查真值分布，本报告未展开）。

#### 2.4.12 `WhichStrategy` / `IsUsingStrategy`（4 / 5 条语句）

```
WhichStrategy:  i=0/45  CardFunctionsNotifier.GetCampaignStrategy(out Strategy)；Branches = Strategy
IsUsingStrategy: i=0/45/83  EqualEqual_ByteByte(strategy, GetCampaignStrategy()) → usingIt
```
**战役模式专用**（`EnumCampaignStrategy.h`）。内核零实现。
调用方：`WhichStrategy` **16 张**、`IsUsingStrategy` **2 张**（全是 `card_*_scen*` / campaign 卡）。

#### 2.4.13 `IsLocationFull` / `getFrontlineLimit` / `DoesSideControlTheFrontline`

已定案（§①.9）。内核对应：`GameState.HalfBoardCapacity:95` / `HandCapacity:98` / `FrontlineCapacity:79` /
`IsFrontlineLimited:71`；`CardApi.DoesSideControlTheFrontline:693` + 派发表 `:70`。

---

## 3. 「未实现」与「实现了但语义错」的区分

### 3.1 未实现（内核里连字段/入口都没有）

`CanCardDoAnything`、`CanIDoAnything`、`CanSideGainKreditSlots`、`GiveMobilizeBonus`、
`DecrementPinnedTurnsEndTurn`、`GetCardsPinnedThisTurn`、`IsThereGameplayRestriction`、
`IsCheatGameplayRestrictionActive`、`CanPlayWeatherCard`、`GetStopAttack`、`GetStopFurtherActions`、
`DecrementTurnGameplayRestrictions`、`getKreditSlotsLostBySide`、`CanSelectAsTarget`、`CanOtherCardBeTargetted`、
`getActiveEffects`、`CanCardBeBuffed`、`CalculateDamageDealt`、`ExecuteBeforeReceiveDamage`、
`ExecuteOnDealDamageAddDamage`、`ExecuteOnDealDamageAddDamageAfterCalc`、`ExecuteOnSurvivedCombatEvents`、
`ExecuteStoppedAttack`、`UpdateGuarded`、`WhichStrategy`、`IsUsingStrategy`。

以及**内核整块缺失的子系统**（不在任务清单但被上面这些函数依赖）：

| 子系统 | 蓝图出处 | 内核 |
|---|---|---|
| 触发号位图 `FetchAllCardsWithEventTrigger(byte)` | `CanOtherCardBeTargetted` i=59、`ExecuteOnDealDamageAddDamage` i=109、`PinUnit` i=1206、`ExecuteOnSurvivedCombatEvents` i=74 等 | 不存在。内核改成"按程序名遍历棋盘+弃牌堆"（`CardApi.FireTrigger:122-249`），**无法表达"只有订阅者才收到"** |
| `GameplayRestrictionEffect` 数组 | `BP_GameState_Battle` 的 27/29/27 条语句三个函数 | `GameState` 无字段 |
| 卡自己的虚拟函数重载 | `CanPlayFromHand` / `BlockCardFromBeingPlayedFromHand` / `CanBeTargetted` / `CanOtherCardBeTargetted` / `OnCardDealDamage_ModifyDamageDealt` / `OnDealDamageAddDamageAfterCalc` / `OnOtherCardDealDamageAddDamage` / `OnOtherCardDealDamageAddDamageAfterCalc` | 派发表 177 个键里**一个都没有**（这 8 个是 `BaseCardObject` 的虚函数，按卡重载） |
| 数值字段 | `pinnedTurns` / `attackLeft` / `movementLeft` / `attackCountThisTurn` / `hasBeenAttackedThisTurn` / `hasEverAttacked` / `gotchaActivated` / `isBeingGuarded` / `underEnemyControl` / `isRevealed` / `isSalvaged` / `isSuppressed` / `hasCovert` / `KreditsTax_AsEnemyTarget` / `isReserved` | `CardInstance` 只有 `EnteredPlayOnTurn`/`OperationsUsedThisTurn`/`HasAttackedThisTurn`/`HasMovedThisTurn`/`Keywords`/`CustomAbility`/`CustomJson`/`BuffsBySource` |

### 3.2 实现了但语义错

| # | 位置 | 内核现在 | 蓝图要求 | 错在哪 |
|---|---|---|---|---|
| 1 | `MatchEngine.LegalTargets:793-798` | 敌方场上**任何**位置有 Guard ⇒ 只能打 Guard | `UpdateGuarded`：`isBeingGuarded` = **相邻格**有已揭示 Guard；且 `CanAttack` #12（i=2627）**只在攻击者不是轰炸/火炮时**才看它 | **范围错**（全局 vs 相邻）+ **漏豁免**（轰炸/火炮该无视守护）。**128 张 `hasGuard`** |
| 2 | `MatchEngine.Attack:712` | `Keywords.Contains(Pinned) \|\| Keywords.Contains(Suppressed)` ⇒ 拒 | `CanAttack` #9：`IsPinned() && !HasCustomAbility("canOperateWhilePinned")`；**抑制不是攻击禁令** | **漏豁免能力**；**多加了抑制**（抑制的语义在 `CalculateDamageDealt` 的 i=38/121 里是"不触发伤害修正"，不是"不能攻击"） |
| 3 | `CardApiDispatch.cs:232` `PinUnit` | 加永久 `Keyword.Pinned` | `PinUnit`：`cantBePinned` 豁免、`IsLocatedOnBoard`/`IsUnit` 守卫、`pinnedTurns = Max(现在, 自己那侧行动 ? 3 : 2)`、触发号 61 广播 | **永久化**（`DecrementPinnedTurnsEndTurn` 没实现）+ 三个守卫全缺。**58 张调 `PinUnit`** |
| 4 | `MatchEngine.Attack:738` | `attackerDamage = attacker.Attack` | `CalculateDamageDealt` 的完整管线（重甲减免 / 伏击 / Shock / 先攻伤害 / `Clamp(0,99)` / `lethal`） | **重甲完全不减伤**（**52 张 `heavyArmor`**）、**伏击无反击**（**46 张**）、**Shock 不生效**（**32 张**） |
| 5 | `MatchEngine.Attack:687` | `CanOperateThisTurn` = `!HasAttackedThisTurn` | `attackLeft`（Fury ⇒ 2，`SetAttackerHasAttacked` i=55）+ `attackCountThisTurn` | **Fury 少一次攻击**（**49 张 `hasFury`**）；`OnAttackStopped` 不消耗攻击 |
| 6 | `MatchEngine.StartTurn:236-240` | 只重置**行动方自己**的 `HasAttackedThisTurn`/`HasMovedThisTurn` | `DoOnStartOfTurn` i=4495-4674 重置 `movementLeft`/`attackLeft`，遍历**双方所有在场卡** | 单侧 vs 双侧；`attackCountThisTurn`/`hasBeenAttackedThisTurn` 未建模（Ambush 依赖它） |
| 7 | `MatchEngine.CanPlay:367` | `card.Location != State.ActiveSide.HandOf()` ⇒ 拒 | `CanPlayCardFromHand` 里**没有位置判据**（只按 cardID 取卡） | 多余判据。**不影响正确性**（重放路径靠它挡掉非法动作），但和蓝图不是同一套判据 |
| 8 | `GameState.NextLocationNumber:342` | `existing.Max(locationNumber)+1` | `GetNextCardLocationNumber` i=551 `locationNumber = QtyInLocation`（= 张数） | 有洞时两者不同；活区无洞时等价（§①.9 快照已证） |

---

## 4. 影响面量化

统计口径：
- 卡量 = `out/cards-full.json`（2019 张 CDO）的真值字段计数，或
  `klink bot/docs/card-ir.json`（1636 张）里 `steps[].fn` / `entrypoints` 的出现卡数。
- 两个来源**不一致是正常的**：`card-ir.json` 只抽了事件型入口点，`BaseCardObject` 的虚函数重载不在里面。

### 4.1 按关键字 / 字段（`out/cards-full.json`）

| 字段 | 张数 | 关联的缺失判据 |
|---|---|---|
| `hasBlitz` | 205 | 召唤失调的例外（**已实现**） |
| `hasGuard` | 128 | `UpdateGuarded` 的 `isBeingGuarded`、`CanAttack` #12 —— **语义错** |
| `hasSmokescreen` | 54 | `CanAttack` #14 —— **完全缺**（`Attack` 从不读 `Keyword.Smokescreen`） |
| `heavyArmor` | 52 | `CalculateDamageDealt` i=2936-3267 重甲减免 —— **完全缺** |
| `hasFury` | 49 | `ResetUnitOperations` i=362 `attackLeft=2` —— **完全缺** |
| `hasAmbush` | 46 | `CalculateDamageDealt` i=823-2019 伏击反杀 —— **完全缺** |
| `hasShock` | 32 | `CalculateDamageDealt` i=2656-2876 —— **完全缺** |
| `hasCovert` | 11 | `IsUnrevealedCovertCard` / `CanAttack` #13 / `CanCardBeBuffed` i=0 —— **完全缺** |
| `hasMobilize` | 9 | `GiveMobilizeBonus` —— **完全缺** |
| `KreditsTax_AsEnemyTarget` | 3 | `PayCardCost` i=230-409 / `CanSelectAsTarget` i=1210-1365 —— **完全缺** |
| `selectTargetOnPlayedFromHand` | 417 | `CanPlayCardFromHand` #10 的目标搜索 —— **完全缺** |
| `isReserved`（CDO 字段） | 563 | `IsCardReserved` 是恒 false 桩；**真值分布未展开** |

### 4.2 按 IR 调用卡数（`card-ir.json`）

| 蓝图函数 | 调用它的卡数 | 内核状态 |
|---|---|---|
| `PinUnit` | **58** | 永久 pin，无解除 |
| `AddGameplayRestriction` | 19（**10 张非战役**） | 无 |
| `IsLocationFull` | 17 | 已实现 |
| `WhichStrategy` | 16 | 无 |
| `IsCardReserved` / `isCardReserved` | 11 + 1 | 恒 false 桩 |
| `RemovePin` | 9 | **不在派发表** |
| `MoveUnitFromSupportToFrontLine` | 7 | 已实现 |
| `RemoveGameplayRestriction` | 3 | 无 |
| `ResetUnitOperations` | 3 | 部分 |
| `ChangedPinnedTurns` | 1 | 无 |
| `ChangeFrontlineLimiter` | 1 | 无（`card_unit_black_prince`） |
| `IsUsingStrategy` | 2 | 无 |
| `CalculateDamageDealt` / `ExecuteOnDealDamage*` / `PayCardCost` / `PayMovementCost` / `CanCardBeBuffed` / `UpdateGuarded` / `CanSelectAsTarget` / `CanOtherCardBeTargetted` / `CanPlayWeatherCard` / `IsThereGameplayRestriction` / `CanSideDrawCards` / `CanSideGainKreditSlots` | **0** | 这些是**引擎内部调用**，不是卡直接调 —— 卡通过事件/重载间接依赖 |

### 4.3 按触发订阅卡数（`out/cards-full.json` 的 `usedTriggers`）

内核 28 个 `FireTrigger` 名字**覆盖不到**、但蓝图有订阅者的（只列与本次审计函数相关的）：

| 触发名 | 订阅卡数 | 与哪个函数相关 |
|---|---|---|
| `OnOtherCardDealDamageAddDamage` | **34** | `ExecuteOnDealDamageAddDamage` |
| `OnOtherCardDealDamage` | **28** | `CalculateDamageDealt` 的伤害广播（内核从不发） |
| `OnOtherCardDealDamageAddDamageAfterCalc` | **15** | `ExecuteOnDealDamageAddDamageAfterCalc` |
| `OnBeforeStartOfTurn` | **14** | `StartTurnBySide` i=1047 `ExecuteBeforeStartOfTurnEvents` |
| `OnOtherCardReceiveDamage` | 9 | `ExecuteBeforeReceiveDamage`（内核**发**这个名字，但拿不到 `isCombatDamage`） |
| `OnSurvivedCombat` | 9 | `ExecuteOnSurvivedCombatEvents` |
| `OnOtherCardSurvivedCombat` | 6 | 同上 |
| `OnOtherUnitPinned` / `OnOtherUnitUnPinned` | 2 / 2 | `PinUnit` i=1206 / `RemovePin` i=860 |
| `OnAttackStopped` | 2 | `ExecuteStoppedAttack` |
| `OnOtherCardOperationKreditSpent` | 3 | `PayMovementCost` i=344 |
| `CanOtherCardBeTargetted` | 1 | `CanOtherCardBeTargetted` |

（另有 `OnCardReset` 64 / `OnOtherCardReset` 41 / `OnBeforeOtherCardPlayedFromHand` 40 / `OnOtherCardAttacks` 22 /
`OnOtherCardSuppressed` 18 / `OnOtherCovertCardPlayedFromHand` 15 / `OnCounterMeasureTriggered` 13 等
与本次审计函数无直接关系的事件也不在内核派发名单里 —— 属维度 1/3 的范围，本报告不展开。）

### 4.4 一句话影响面

**「每局都会错」的**：`CalculateDamageDealt` 缺重甲/伏击/Shock（**52+46+32 张**）、
`LegalTargets` 的全局嘲讽（**128 张 Guard**）、`PinUnit` 永久化（**58 张**）、
`CanAttack` #14 隐蔽/烟幕（**54 张烟幕 + 11 张隐蔽**）、Fury 双攻（**49 张**）、
`GiveMobilizeBonus`（**9 张**）。
**「特定卡才错」的**：10 张限制卡、3 张加税卡、51 张天气卡、11 张 `IsCardReserved`、
16 张战役策略卡、1 张 `CanOtherCardBeTargetted`。

---

## 5. 读不出来 / 不确定（明确记录）

1. **`startingTurnForOpponent`（`CanSideDrawCards` i=267）没有赋值点** —— dump 不含形参表，
   它是入参还是未初始化局部变量**读不出来**。所以 `CanSideDrawCards` 第二条分支的**触发条件**我只能写"存在这样一条分支"。
2. **`CanAttack` 的 18 条判据我没有重新解码**（按任务书给定清单核对内核）。
3. **`CalculateDamageDealt` i=3664 与 i=3938 的精确嵌套** —— `JumpIfNot(GreaterEqual) -> 3690` 与
   i=3938 `localDoesDamageRecieverDie = False` 按字面读会覆盖 i=3678 的 `True`。
   两种读法都能自洽，**需要一次带作用域栈的控制流还原才能定案**。
4. **`ExecuteBeforeReceiveDamage` i=38 / i=741 / i=74 的回边** —— 在"接收者自己也在触发号 52 列表里"时按字面读会死循环，
   说明我对 `JumpIfNot`+`Jump` 的组合还原还缺一层。**语义标注为不确定**，但"触发号 52 广播存在"是确定的。
5. **`DoOnStartOfTurn` i=976 `DecrementTurnGameplayRestrictions()` 的位置** ——
   "每回合开始至少调一次"确定；"是否在每张卡的迭代里重复调用"**读不出来**。
6. **`IsWeatherCard` 的判据读不出来** —— 它是 `BaseCardObject` 的原生函数，不在 `bp-cardfn.json`（`MISS`），
   jmap 只有签名。天气卡的张数（51）是我按 `cards-full.json` 的文本关键字统计的**近似值**。
7. **`IsPinned()` 的原生实现读不出来** —— 我按 `RemovePin` i=715 `pinnedTurns = 0` 推断 `IsPinned() ⟺ pinnedTurns > 0`，
   这是推断不是字节码证据。
8. **`IsCardReserved` 的真判据读不出来** —— `NotifyCheckCardReserved` 走 `I_BP_Logic_Receiver` 接口（4 条语句），
   实现方不在我 dump 的 8 个资产里。CDO 里 563 张卡带 `isReserved` 字段，但**我没查真值分布**。
9. **`ExecuteOnSurvivedCombatEvents` 的"存活"判据读不出来** —— `cardSurviving` 由调用方决定，调用方在 `BP_OnlineMatch` 里（未逐段展开）。
10. **`GetStopAttack` / `GetStopFurtherActions` 的写入方读不出来** —— 只有 3 条直读语句，
    写字段的应该是服务端 action；卡池 IR 里 0 次调用。
11. **`FetchAllCardsWithEventTrigger(byte)` 的完整触发号→程序名映射读不出来** ——
    我只从调用点反推出 4 个：`63→OnStartOfGame`、`52→OnReceiveDamage/OnOtherCardReceiveDamage`、
    `59→OnSurvivedCombat/OnOtherCardSurvivedCombat`、`61/62→OnOtherUnitPinned/OnOtherUnitUnPinned`。
    `37` / `38` / `2` / `27` 我是按"函数语义 + `cards-full.json` 的 `usedTriggers` 名字"配的，**属推断**。
