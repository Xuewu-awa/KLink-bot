# 维度 3：关键字 / 机制审计

调查日期：2026-09-27。只做只读调查，未改 `src/KLink.Bot/` 任何代码。

**行号漂移警告**：`src/KLink.Bot/Engine/MatchEngine.cs` 在本轮调查期间被另一个代理改动过
（`Immune` 判据从 L827 漂到 L889，文件从 932 行涨到 994 行）。下表所有 `文件:行号`
都同时给出**该行的原文**，以原文为准；行号可能已经不准。

## 数据来源

| 文件 | 用途 |
|---|---|
| `out/cards-full2.json` | 2023 条卡 CDO。字段在 `raw` 里**缺席 = 默认 false/0**（已逐字段核过：所有 `has*` 字段出现时恒为 `true`，`heavyArmor` 出现时恒为 1/2/3） |
| `out/bp-cardfn.json` | `BP_CardFunctions` 294 函数，判据所在 |
| `out/bp-cardscheck.json` | `CanAttack`(210 语句) / `CanSelectAsTarget` / `CanOtherCardBeTargetted` / `getActiveEffects` |
| `out/bp-gamestate.json` | `BP_GameState_Battle` 95 函数 |
| `out/bp-logic.json` | `BP_Logic` 181 函数（`HasDeploymentSickness` / `GiveMobilizeBonus` / `DoOnStartOfTurn`） |
| `out/bp-onlinematch.json` | `BP_OnlineMatch` 412 函数（子动作接收端） |
| `klink bot/docs/card-ir.json` | 1636 张卡 IR：`entrypoints` + `steps[].fn` |
| `src/KLink.Bot/**` | 内核实现 |

`raw` 字段缺席即默认值的核验方式：对每个 `has*` 字段统计出现值，全部只有 `True`：

```
hasGuard         present=  128 vals={True: 128}
hasAmbush        present=   46 vals={True: 46}
hasSmokescreen   present=   54 vals={True: 54}
hasAlpine        present=   18 vals={True: 18}
hasCovert        present=   11 vals={True: 11}
hasScrying       present=    1 vals={True: 1}
heavyArmor       present=   52 vals={1: 40, 2: 11, 3: 1}
hasFury          present=   49 vals={True: 49}
hasMobilize      present=    9 vals={True: 9}
hasBlitz         present=  205 vals={True: 205}
hasShock         present=   32 vals={True: 32}
hasDeployment    present=  249 vals={True: 249}
hasDestruction   present=   73 vals={True: 73}
hasPincer        present=   15 vals={True: 15}
hasSalvage       present=    9 vals={True: 9}
cipher           present=   25 vals={9: 1, 2: 9, 3: 4, 1: 11}
hasCipher/hasBond/bond/hasSuppress/hasPinned/hasImmune   present=0
```

**Bond 在 CDO 里根本没有字段**（`hasBond` / `bond` 出现 0 次），`gameplayTags` 在 2023 条里
全为 `null`。所以 Bond 的天生卡数 = 0，只能靠效果授予。

---

## 1. 总表

「卡数」列 = CDO 天生带该字段的卡数；「联动卡数」= `card-ir.json` 里调用该机制原语的卡数
（授予 / 移除 / 查询 / 订阅触发点），这是**行为会错的卡数下界**。

| 机制 | 卡数（CDO） | 蓝图规则（带 i=） | 内核状态 | 影响面 |
|---|---|---|---|---|
| **掩护 Guard** | 128 | 一张卡 `isBeingGuarded` ⟺ **同一条线上 locationNumber±1 的邻卡**有 `getHasGuard()` 且邻卡不是未揭示隐蔽卡（`UpdateGuarded` i=174/634/1276/1405/1517/1587）。`CanAttack` i=2627 `if !(defenderCard.isBeingGuarded) -> pop`，i=2714/2725 `canAttack=false; failReason="hq_is_being_garded"`，i=2788/2799 `"is_being_guarded"`；轰炸机/炮兵跳过该判定（i=2492-2626）。**掩护卡自己不因此免疫** | **实现但语义错**：`MatchEngine.cs:794` `var guards = enemyUnits.Where(u => u.Keywords.Contains(Keyword.Guard)).ToList();` 把它做成「必须优先打掩护单位」的嘲讽模型 | 128 天生 + 21 张 `GiveGuard` + 9 张 `RemoveGuard` + 4 张 `getHasGuard` + 2 张 `hasGuardAdjacentUnit` + 1 张 `SetGuardOnTop`。**全部 128 张的嘲讽目标集都错** |
| **伏击 Ambush** | 46 | `CalculateDamageDealt` 的 ambush 分支 i=823 `_damageRecieverCard.getHasAmbush()` && i=864 `!ignoreAmbush`，i=1138 要求 `_dealingDamageIsAttacker && !receiver.hasBeenAttackedThisTurn && !dealer.getHasImmune()`；i=1148/1245/1476/1558 排除炮兵、轰炸机、Shock；i=1971-2019 `if (_recieverCalculatedDamage >= dealer.getTotalDefense()+dealer.getTotalHeavyArmor()+GetPassiveDefenseBuff+BeforeAttackDefenseBuff) _dealerCalculatedDamage = 0`（伏击方的伤害先落地，够致命则攻击方 0 伤害）；i=2043-2876 反向分支把伏击方伤害归零 | **未实现**（关键字能存，无任何行为读取） | 46 天生 + 10 张 `GiveAmbush` + 5 张 `RemoveAmbush` + 1 张 `getHasAmbush` |
| **烟幕 Smokescreen** | 54 | `CanAttack` i=3637 `if !(defenderCard.getHasSmokescreen()) -> pop` → i=3713 `"location_has_smokescreen"` / i=3793 `"defender_has_smokescreen"`（**不能被攻击**）；`AttackCard` i=3511 `RemoveSmokescreen(attackerCardID, attackerCardID, true, false)`（**自己攻击后消失**）；`CardLocationMoved` i=643/684/735 移动到前线(7) 时 `RemoveSmokescreen`（**上前线后消失**） | **未实现** | 54 天生 + 9 张 `GiveSmokescreen` + 8 张 `RemoveSmokescreen` + 3 张 `OnOtherCardLoseSmokescreen` |
| **山地 Alpine** | 18 | `GiveAlpineBonus` i=47/88/129/242 前置：`getHasAlpine() && IsLocatedOnBoard() && getTotalDefense()>0`；i=562-878 `bonus = 场上其他友方 getHasAlpine() 单位的个数`；i=950 `ChangeAttack(card, cardID, bonus, 1, false)`，i=1025 `ChangeDefense(card, cardID, bonus, 1, false)` —— **每个其他友方山地单位给 +1/+1**。由 `PlayCardFromHand` / `SpawnCardToBoard` / `AfterWaitCardPlayFromHand` / `SpawnMultipleCardsOnBattlefield` 调 | **未实现** | 18 天生 + 7 张 `getHasAlpine` + 1 张 `GiveAlpine` + 1 张 `RemoveAlpine` |
| **隐蔽 Covert** | 11 | `IsUnrevealedCovertCard()`（原生）是全局前置：`CanSelectAsTarget` i=185-327 `if (Targeted.IsUnrevealedCovertCard() && byPlayFromHand && !Targeting.HasCustomName1("canTargetCovert")) { can=false; Reason="cant_target_unrevealed" }`；`AttackCard` i=643/698 与 i=783/838 攻击时 `RevealCard` 双方；`GetAdjacentCards` i=969/1326 隐蔽卡不算邻卡；`UpdateGuarded` i=564/1206 隐蔽卡不算掩护；`SetActiveBondsAtStartOfTurn` i=336 隐蔽卡不进 bond 阵营集 | **未实现**（`Keyword` 类里没有 `Covert` 常量；`KismetVm.cs:762` 注释明写「这里**不列** `hasCovert`」→ 读成 null→假） | 11 天生 + 18 张卡调 `IsUnrevealedCovertCard` + 15 张 `OnOtherCovertCardPlayedFromHand` + 1 张 `OnOtherCovertCardSpawned` + 8 张 `OnCardRevealed` + 5 张 `OnOtherCardRevealed` |
| **占卜 Scrying** | 1 | `ExecuteScryingEffectBySide` i=5 取牌库，i=508 `FilterCardsToScry` 过滤，i=1145 `Min(len,3)` 只把前 3 张留在顶，i=1345 比对玩家选中的 `cardToDrawID`，i=1646/1765 其余移到牌库底，i=1839 `SetDeckBySide`，i=2005 `DrawTopCardFromDeck`。调用方是 `BP_OnlineMatch::OpponentActionsCardToDrawSelected`（客户端/服务端握手，**不在卡蓝图里**） | **未实现** | 1（`card_event_exploit_the_gap`） |
| **重甲 HeavyArmor** | 52 | `CalculateDamageDealt` i=1694/1735 `dealer.getTotalDefense() + dealer.getTotalHeavyArmor()` 参与「能不能活」的判定；i=2936-3267 `_dealerCalculatedDamage = Max(_dealerCalculatedDamage - SelectInt(0, receiver.getTotalHeavyArmor(), ignoreHeavyArmor) - SelectInt(passiveDefenseBuff, 0, applyBeforeAttackBuffs), 0)` —— **减伤**。`ChangeHeavyArmor` i=1068-1183 基础值 `Clamp(sum,0,3)`，i=1304-1421 `heavyArmorBuff` 无上限累加 | **有字段有查询、减伤未实现**：`CardInstance.cs:148-160` 算点数、`CardApiDispatch.cs:553` `["getTotalHeavyArmor"] = (c,r,a) => AsCard(r)?.HeavyArmor ?? 0`、`CardApiDispatch.cs:1590` `DoChangeHeavyArmor`；但 `MatchEngine.cs:882-903 ApplyDamage` 只做 `target.Defense -= amount`，**从不减重甲** | 52 天生 + 5 张 `ChangeHeavyArmor` + 1 张 `CampaignIncreaseHeavyArmor` + 2 张 `getTotalHeavyArmor` + 1 张 `Pincer: +1 Heavy Armor` |
| **狂怒 Fury** | 49 | `ResetUnitOperations` i=275 `getHasFury()` → i=362 `attackLeft = 2`，否则 i=535 `attackLeft = 1`；`BP_Logic::DoOnStartOfTurn` i=4518 `movementLeft=1`、i=4573/4628/4674 `getHasFury() ? attackLeft=2 : attackLeft=1` | **未实现**（`Keyword.Fury` 只被授予/移除，没有任何行为读取；`CardInstance.cs:104-105 CanOperateThisTurn => Location.IsBoard() && !HasAttackedThisTurn && !HasDeploymentSickness(state)` 是**每回合一次**） | 49 天生 + 10 张 `GiveFury` + 4 张 `RemoveFury` |
| **动员 Mobilize** | 9 | `BP_Logic::GiveMobilizeBonus` i=510 `IsLocatedOnBoard && hasMobilize && IsSideActive(side)` → i=664 `ChangeAttack(+1, changeType=1)`、i=803 `ChangeDefense(+1, changeType=1)`；**受伤即失去**：`ApplyDamageToCard` i=2193/2269 `if (toCard.hasMobilize && finalDamage>0) RemoveMobilize(...)`、`ExecuteAttackCard` i=3762/3838 与 i=4675/4751 同判据 | **未实现** | 9 天生 + 3 张 `GiveMobilize` + 2 张 `OnGainMobilize` + 3 张 `OnLoseMobilize` |
| **闪电战 Blitz** | 205 | `BP_Logic::HasDeploymentSickness` i=23 `getHasBlitz()` → i=64 `Not` → i=232 `IsLocatedOnBoard && (enterPlayOnTurn==GetTurnNumber()) && !getHasBlitz()`；`CanAttack` i=1960-2108 `failReason="deployment_sickness"` | **正确（已定案）**：`CardInstance.cs:88-91` `Location.IsBoard() && EnteredPlayOnTurn == state.Turn && !Keywords.Contains(Keyword.Blitz)` | — |
| **冲击 Shock** | 32 | `ExecuteAttackCard` i=405-704：`attacker.getHasShock() && !attacker.HasCustomAbility("CantLoseShock") && !defender.IsLocation()` → i=617 `RemoveShock(attacker...)`、i=704 `wasShockAttack = true`（**攻击后自己失去 Shock**）；`CalculateDamageDealt` i=1476-1610 与 i=2656-2887 里 Shock 参与 ambush 分支的归零判定，i=2876 `shockAttack = true` | **未实现** | 32 天生 + 18 张 `GiveShock` + 3 张 `getHasShock` + 1 张 `ShockRecoil` |
| **部署 Deployment** | 249 | `TriggerDeployment` i=59 `if (!card.hasDeployment) -> return`；i=173-724 遍历事件 14（`OnBeforeOtherCardDeploymentTrigger`，4 张卡）可 `cancelDeploymentEffect`；i=956 `ExecuteOnDeploymentTriggered`（事件 23，1 张卡）；i=1020-1256 `card.OnPlayedFromHand(...)` 循环 `triggerMultiple+1` 次。`CardPlayedFromHand` i=3640-5813 同一条链 | **部分实现**：`MatchEngine.cs:488-489` 打出牌时跑 `Api.RunCardEffect(card, target)`（= 卡自己的 `OnPlayedFromHand`，所以卡面「Deployment: …」文本能生效）；但**没有** `hasDeployment` 门、**不派发** `OnBeforeOtherCardDeploymentTrigger` / `OnDeploymentEffectTriggered`、**没有** `TriggerDeployment` 原语、**不支持** `triggerMultiple` / `SetExtraPlayTriggers` | 249 天生；4 张 `OnBeforeOtherCardDeploymentTrigger` 无法取消部署效果，1 张 `OnDeploymentEffectTriggered` 永不触发 |
| **摧毁 Destruction** | 73 | `TriggerDestruction` i=381-626 门：`!isSuppressed && (hasDestruction \|\| HasCustomAbility("destruction")) && !CustomName1HasAttribute("StopDestructionEffect")` → i=1237/1466 `card.OnDestroyed(...)`；`ExecuteOnDestructionEffectTriggered`（事件 24，4 张卡）i=325/710 里 `if (!skipSuppressCheck && cardTriggered.isSuppressed) -> 跳过`；i=3359-3594 `RemoveDestruction` 时 `CustomAbilityRemove("destruction")` + `CustomName1Add("StopDestructionEffect")`；`ExecuteOnCardDestroyedFunction` i=343-999 同一门 | **部分实现**：`MatchEngine.cs:944 Destroy(...)` 派发 `OnBeforeDestroyed` 和 `OnDestroyed`/`OnOtherCardDestroyed`（`MatchEngine.cs:953` / `:970`），所以卡自己的 `OnDestroyed` 会跑；但**没有** `hasDestruction` 门、**不派发** `OnDestructionEffectTriggered`、**没有** `TriggerDestruction` 原语、**没有** `StopDestructionEffect` / `StealSide` / `RemoveDestruction` | 73 天生；4 张 `OnDestructionEffectTriggered` 永不触发；5 张卡直接调 `TriggerDestruction` 无效果 |
| **钳击 Pincer** | 15 | `CardPlayedFromHand` i=4542 `if (cardPlayed.hasPincer && IsValid(target))` → i=4648 `ApplyPincerEffects(cardPlayed, target)`；`ApplyPincerEffects` i=118 `JSON_SetInt(cardPlayed, "pincer_receiver", target.cardID)`、i=335 `JSON_AddToIntArray(target, "pincer_givers", cardPlayed.cardID)`、i=73/244/290/459 派发 `OnPincerEffectApplied`/`OnPincerEffectReceived`；`RemovePincerEffects` i=10-1675 双向清 JSON 并派发 `OnPincerEffectRemoved`（由 `ExecuteOnBefore/AfterLeaveBoardOrOwnerEvents`、`SuppressMultipleUnits` 调） | **未实现**（`ApplyPincerEffects`/`RemovePincerEffects` 都不在 `CardApiDispatch.BuildDispatch()` 的 173 个键里；`OnPincerEffect*` 三个触发点内核从不派发） | 15 天生 + 15 张 `OnPincerEffectApplied` + 13 张 `OnPincerEffectRemoved` + 2 张 `hasActivePincerEffect` |
| **打捞 Salvage** | 9 | `ExecuteOnCardDestroyedFunction` i=2188-2572 门：`!destroyed.HasCustomAbility("cantBeSalvaged") && killer.hasSalvage && killer.IsSideActive() && destroyed.side == killer 对面` → i=2572 `SalvageMultipleUnits([cardID], killer.cardID)`；`SalvageMultipleUnits` i=566-1792 若打捞方手牌未满则 `CreateCard(salvagingSide, destroyed.name, handLocation, …, S_SalvagedCardInfo{isSalvaged=true, salvageFaction=salvager.faction}, …)`；`ApplySalvageChanges` i=0-345 `isSalvaged=true`、`faction=salvageFaction`、`attack=1`、`defense=1`、`kredits = min(kredits,3)`（`isVeteran` 时跳过最后三条） | **未实现** | 9 天生 + 3 张 `GiveSalvage` + 2 张 `SalvageMultipleUnits` + 1 张 `OnOtherCardSalvaged` + 1 张 `RemoveSalvage` |
| **密码 / 情报 Cipher** | 25（值 1×11、2×9、3×4、9×1） | `CardPlayedFromHand` i=1955 `if (cardPlayed.cipher > 0)` → i=3380 `SetCardsSeenByCipher(cardPlayed.cipher, cardPlayed.cardID, cardPlayed.side)`；`SetCardsSeenByCipher` i=10-311 对事件 28 的订阅者（`OnIntelTriggered`，7 张卡）派发 `OnIntelTriggered(instigator, numberOfCardsSeen)`，i=431-1272 收集对方**未见过**的手牌、`Array_ShuffleFromStream` 后取 `Min(numberOfCardsSeen, len)` 张，i=1098 `ApplySetCardsSeenByCipher(...)`；`ApplySetCardsSeenByCipher` i=591 `cardSeen = true`、i=658 `NotifyCardsSeen(...)` | **未实现** | 25 天生 + 2 张 `SetCardsSeenByCipher` + 1 张 `AddIntelToCard` + 7 张 `OnIntelTriggered` |
| **羁绊 Bond** | **0**（CDO 无字段） | `GiveBond` i=696 `AddCustomGameplayTag("ability.bond", card)`、i=901 `NotifyGiveBond`、i=977 `ExecuteOnOtherCardsAbilitiesChanged`；`BP_GameState_Battle::SetActiveBondsAtStartOfTurn` i=92-896 重建 `activeBondFactions` = 该方**在场、是单位、非未揭示隐蔽**的卡的 faction 集合；`CardPlayedFromHand` i=1539-1917：`if (cardPlayed.HasBond() && !Set_Contains(activeBondFactions, cardPlayed.faction))` → i=1765 `ApplyFatigueDamage(cardPlayed.side, true, destroyed)`，i=1820 若 HQ 被打死 → i=1894 `HQ_DestroyedEndMatch(对面)` | **未实现**（`HasBond` 不在 dispatch 表；`SetActiveBondsAtStartOfTurn` 内核没有） | 0 天生 + 6 张卡调 `HasBond` + 2 张 `GiveBond` + 1 张 `RemoveBond`。卡面文字（`card_unit_kings_african_rifles`「Give it +1+1 and Bond」、`card_event_way_of_subjects`「All Japanese cards in your deck get Bond」）说明 Bond 主要靠效果授予 |

---

## 2. 逐机制证据

### 2.1 掩护 Guard —— 「实现但语义错」

**卡数**：`hasGuard` 128 张。

**蓝图规则**（`out/bp-cardfn.json` → `UpdateGuarded`）：

```
i=5     CallFunc_EqualEqual_ByteByte_ReturnValue   = EqualEqual_ByteByte(location,7)
i=36    CallFunc_EqualEqual_ByteByte_ReturnValue_1 = EqualEqual_ByteByte(location,6)
i=67    CallFunc_EqualEqual_ByteByte_ReturnValue_2 = EqualEqual_ByteByte(location,5)
i=136   CallFunc_BooleanOR_ReturnValue_1 = BooleanOR(BooleanOR(loc==5,loc==6),loc==7)
i=174   IFNOT(CallFunc_BooleanOR_ReturnValue_1) -> goto 752          ; 只处理 5/6/7
i=523   _currentCard.<- getHasGuard(CallFunc_getHasGuard_doesIt)
i=564   _currentCard.<- IsUnrevealedCovertCard(CallFunc_IsUnrevealedCovertCard_isIt)
i=634   CallFunc_BooleanAND_ReturnValue = BooleanAND(getHasGuard, Not(IsUnrevealedCovertCard))
i=672   IFNOT(CallFunc_BooleanAND_ReturnValue) -> goto 836           ; 自己带掩护 ⇒ 走 836
i=836   _removeGuarded = true
i=847   GetAdjacentCards(_currentCard, true, CallFunc_GetAdjacentCards_adjacentCards)
i=1276  CallFunc_BooleanAND_ReturnValue_2 = BooleanAND(adj.hasGuard, Not(adj.IsUnrevealedCovertCard))
i=1336  IFNOT(CallFunc_BooleanAND_ReturnValue_2) -> pop
i=1405  _currentCard.isBeingGuarded <- isBeingGuarded = true
i=1517  CallFunc_BooleanAND_ReturnValue_1 = BooleanAND(_removeGuarded, _currentCard.isBeingGuarded)
i=1587  _currentCard.isBeingGuarded <- isBeingGuarded = false
```

`GetAdjacentCards`（同文件）i=799/841 `Subtract_IntInt(_locationNumber,1)` 与 i=1156/1198
`Add_IntInt(_locationNumber,1)`，即**同一 location 内 locationNumber ±1 的两张邻卡**；
i=969-1077 / i=1326-1434 把 `IsUnrevealedCovertCard` 的邻卡排除（除非 `includeCovertCards`）。

所以：**掩护不是嘲讽。掩护单位保护它左右紧邻的卡（含 HQ 那一格）；掩护单位本身可以被攻击。**

判据侧（`out/bp-cardscheck.json` → `CanAttack`）：

```
i=2492  attackerCard.IsBomber(...)      ; i=2533 IsArtillery
i=2574  CallFunc_BooleanOR_ReturnValue = BooleanOR(IsBomber, IsArtillery)
i=2612  JumpIfNot(..) -> 2627
i=2626  PopExecutionFlow                 ; 轰炸机/炮兵跳过掩护判定
i=2627  IFNOT(defenderCard.isBeingGuarded) -> pop
i=2700  JumpIfNot(defenderCard.IsLocation()) -> 2788
i=2714  canAttack = false ; i=2725 failReason = "hq_is_being_garded"
i=2788  canAttack = false ; i=2799 failReason = "is_being_guarded"
```

**内核**：`src/KLink.Bot/Engine/MatchEngine.cs:794`

```csharp
// Guard 单位必须优先被攻击
var guards = enemyUnits.Where(u => u.Keywords.Contains(Keyword.Guard)).ToList();
if (guards.Count > 0) { return guards.Where(t => CanReachAcrossFrontline(attacker, t)); }
```

**判定：实现但语义错。** 内核用的是「有掩护就必须打掩护」的嘲讽模型，与蓝图相反：
- 蓝图里**孤立的一张非掩护敌方单位可以被攻击**，内核在存在任一掩护单位时会把它从合法目标里删掉；
- 蓝图里**掩护单位自己可以被攻击**（它不带 `isBeingGuarded`，除非邻卡也是掩护），内核把它变成唯一合法目标；
- 蓝图里 HQ 只在「被邻卡掩护」时不可攻击（`hq_is_being_garded`），内核在 `guards.Count > 0` 时把 HQ 整个从列表里去掉。

`GetAdjacentCards` / `UpdateGuarded` 在内核里**完全没有对应实现**（`CardApiDispatch` 无
`UpdateGuarded`、`GetAdjacentCards` 键）。

### 2.2 伏击 Ambush —— 未实现

**卡数**：`hasAmbush` 46 张。

**蓝图规则**：`out/bp-cardfn.json` → `CalculateDamageDealt`（170 语句）。

```
i=96    _ignoreAmbush = ignoreAmbush
i=823   _damageRecieverCard.<- getHasAmbush(CallFunc_getHasAmbush_doesIt)
i=864   CallFunc_Not_PreBool_ReturnValue_9 = Not_PreBool(_ignoreAmbush)
i=893   CallFunc_BooleanAND_ReturnValue_7 = BooleanAND(getHasAmbush, Not(_ignoreAmbush))
i=931   IFNOT(CallFunc_BooleanAND_ReturnValue_7) -> pop
i=941   _damageDealerCard.<- getHasImmune(...)
i=1011  CallFunc_Not_PreBool_ReturnValue_6 = Not_PreBool(damageRecieverCard.hasBeenAttackedThisTurn)
i=1100  BooleanAND(dealingDamageIsAttacker, Not(hasBeenAttackedThisTurn), Not(dealer.getHasImmune))
i=1148  _damageDealerCard.<- IsArtillery(...)      ; i=1189 IFNOT -> 1204 ; i=1203 PopExecutionFlow
i=1245  _damageDealerCard.<- IsBomber(...)         ; i=1423 AND(IsBomber, Not(receiver.IsFighter), Not(receiver.IsAntiAir))
i=1476  _damageDealerCard.<- getHasShock(...)      ; i=1558 BooleanOR(receiver.IsBomber, dealer.getHasShock)
i=1653  _damageDealerCard.<- getTotalDefense(...)
i=1694  _damageDealerCard.<- getTotalHeavyArmor(...)
i=1735  CallFunc_Add_IntInt_ReturnValue_4 = Add_IntInt(getTotalDefense_2, getTotalHeavyArmor_1)
i=1781  _damageDealerCard.<- GetPassiveDefenseBuff(_recieverCalculatedDamage, ...)
i=1971  CallFunc_GreaterEqual_IntInt_ReturnValue_2 = GreaterEqual_IntInt(_recieverCalculatedDamage, <上面那串和>)
i=2009  IFNOT(..) -> pop
i=2019  _dealerCalculatedDamage = 0
i=2043  _damageRecieverCard.<- IsArtillery(...)    ; i=2113 BooleanAND(Not(dealingDamageIsAttacker), IsArtillery)
i=2161  _dealerCalculatedDamage = 0
i=2185  _damageDealerCard.<- IsBomber(...)         ; i=2255 BooleanAND(Not(dealingDamageIsAttacker), IsBomber)
i=2303  _dealerCalculatedDamage = 0
i=2327  ... i=2584 BooleanAND(Not(dealingDamageIsAttacker), receiver.IsBomber, Not(OR(dealer.IsFighter, dealer.IsAntiAir)))
i=2632  _dealerCalculatedDamage = 0
i=2656  _damageDealerCard.<- IsUnit(...) ; i=2697 _damageRecieverCard.<- getHasShock(...)
i=2805  BooleanAND(Not(dealingDamageIsAttacker), receiver.getHasShock, dealer.IsUnit)
i=2853  _dealerCalculatedDamage = 0
i=2876  shockAttack = true
i=3950  damage = _dealerCalculatedDamage
```

调用点（`AttackCard` i=3807 / i=4004）：

```
i=3807  CalculateDamageDealt(_attackerCard,_defenderCard,true,false,false,false, out damage, ...)
i=4004  CalculateDamageDealt(_defenderCard,_attackerCard,false,false,false,false, out damage_1, ..., out wasShockAttack_1)
i=4076  shockAttack = CallFunc_CalculateDamageDealt_wasShockAttack_1
```

**不确定的地方**：这段的 `PushExecutionFlow` / `PopExecutionFlow` 嵌套在字节码里的发射顺序
不是线性的（`PushExecutionFlow` 集中在 i=115-150，`PopExecutionFlow` 散在 i=822/1057/2042/2161/2303/2632/2655），
所以**哪个 `_dealerCalculatedDamage = 0` 属于哪一层条件我没有 100% 还原**。
能确定的是：ambush 分支整体由 `receiver.getHasAmbush() && !ignoreAmbush` 门控，
且分支内出现「把某一方算好的伤害归零」这个动作 —— 这是伏击「先手打，够致命则对方 0 伤害」的实现方式。
**结论的不确定度就在这里，不要把它当逐字定案。**

**内核**：`Keyword.Ambush` 只在两处出现 —— `CardApiDispatch.cs:216` `["GiveAmbush"]`（授予）
和 `KismetVm.cs:768` `"hasAmbush" => card.Keywords.Contains(Keyword.Ambush)`（读给卡脚本用）。
`MatchEngine.Attack`（`MatchEngine.cs:680`）里 `int defenderDamage = defender.IsHq ? 0 : defender.Attack;`
—— 无条件反击，没有任何 Ambush 判定。

**判定：未实现。**

### 2.3 烟幕 Smokescreen —— 未实现

**卡数**：`hasSmokescreen` 54 张。

**蓝图规则**：

`out/bp-cardscheck.json` → `CanAttack`：

```
i=3596  ? = defenderCard.^^^^^  (getHasSmokescreen)
i=3637  IFNOT(CallFunc_getHasSmokescreen_doesIt) -> pop
i=3647  ? = defenderCard.^^^^^ (IsLocation)
i=3688  IFNOT(CallFunc_IsLocation_isIt) -> goto 3782
i=3702  canAttack = false ; i=3713 failReason = "location_has_smokescreen"
i=3782  canAttack = false ; i=3793 failReason = "defender_has_smokescreen"
```

`out/bp-cardfn.json` → `AttackCard`：

```
i=2911  _attackerCard.<- IsLocatedOnBoard(CallFunc_IsLocatedOnBoard_isIt_2)
i=2952  IFNOT(..) -> goto 3640
i=2966  IFNOT(_attackerCard.isSuppressed) -> goto 3590
...
i=3511  _attackerCard.cardFunction <- cardFunction.<- RemoveSmokescreen(attackerCardID,attackerCardID,true,false)
i=3590  _attackerCard.<- OnBeforeAttack(_defenderCard)
```

`out/bp-cardfn.json` → `CardLocationMoved`：

```
i=638   PushExecutionFlow
i=643   CallFunc_EqualEqual_ByteByte_ReturnValue_2 = EqualEqual_ByteByte(newLocation,7)
i=674   IFNOT(..) -> pop
i=684   tmpCard.<- getHasSmokescreen(CallFunc_getHasSmokescreen_doesIt)
i=725   IFNOT(..) -> pop
i=735   RemoveSmokescreen(cardID,cardID,true,false)
```

**规则一句话**：带烟幕的单位**不能被攻击**；它一旦**自己攻击**或**移动到前线(7)**，烟幕消失。

**内核**：`Keyword.Smokescreen` 只有 `CardApiDispatch.cs:220`（Give）/ `:231`（Remove）和
`KismetVm.cs:769`（读）。`MatchEngine.LegalTargets` / `Attack` / `ApplyDamage` 都不看它。

**判定：未实现。**

### 2.4 山地 Alpine —— 未实现

**卡数**：`hasAlpine` 18 张。

**蓝图规则**：`out/bp-cardfn.json` → `GiveAlpineBonus`（39 语句）

```
i=5     CanCardBeBuffed(card, ...)            ; i=37 IFNOT -> pop
i=47    card.<- getHasAlpine(...)
i=88    card.<- IsLocatedOnBoard(...)
i=129   card.<- getTotalDefense(...)
i=170   CallFunc_Greater_IntInt_ReturnValue = Greater_IntInt(getTotalDefense,0)
i=242   BooleanAND(BooleanAND(IsLocatedOnBoard, def>0), getHasAlpine)   ; i=280 IFNOT -> pop
i=290   GetAllUnitsOnBoard(false, CallFunc_GetAllUnitsOnBoard_cards)
i=562   CallFunc_Array_Get_Item.<- getHasAlpine(...)
i=603   EqualEqual_ByteByte(unit.side, card.side)
i=685   NotEqual_ObjectObject(unit, card)
i=761   BooleanAND(BooleanAND(unit.getHasAlpine, unit != card), unit.side == card.side)
i=799   IFNOT(..) -> pop
i=809   CallFunc_Add_IntInt_ReturnValue_1 = Add_IntInt(bonus,1)
i=950   ChangeAttack(card, cardID, bonus, 1, false, ...)
i=1025  ChangeDefense(card, cardID, bonus, 1, false, ...)
```

**规则一句话**：`+bonus/+bonus`，`bonus` = 场上**其他**同阵营 `hasAlpine` 单位的个数。
调用点：`PlayCardFromHand` / `PlayCardDirectlyFromHand` / `AfterWaitCardPlayFromHand` /
`SpawnCardToBoard` / `SpawnMultipleCardsOnBattlefield`。

**内核**：`Keyword.Alpine` 只有 `CardApiDispatch.cs:221`（Give）和 `KismetVm.cs:762` 附近
的注释（`GetMember` 表里**没有** `hasAlpine` → 卡脚本读它得到 null → 判假）。
`GiveAlpineBonus` 不在 `BuildDispatch()` 的 173 个键里。

**判定：未实现。** 7 张卡自己读 `getHasAlpine`，这 7 张的判据会静默走假分支。

### 2.5 隐蔽 Covert —— 未实现

**卡数**：`hasCovert` 11 张。

**蓝图规则**：`IsUnrevealedCovertCard` 是原生函数（`bp-cardfn.json` 里 20 个函数引用它），
它作为**全局前置**出现在：

```
CanSelectAsTarget   i=185  Targeted.<- IsUnrevealedCovertCard(...)
                    i=226  BooleanAND(IsUnrevealedCovertCard, byPlayFromHand)
                    i=327  Reason = "cant_target_unrevealed"
AttackCard          i=643/684/698   if (attacker.IsUnrevealedCovertCard()) RevealCard(attacker)
                    i=783/824/838   if (defender.IsUnrevealedCovertCard()) RevealCard(defender)
GetAdjacentCards    i=969/1010/1039 BooleanOR(Not(IsUnrevealedCovertCard), includeCovertCards)
UpdateGuarded       i=564/1206      IsUnrevealedCovertCard 排除掩护
SetActiveBondsAtStartOfTurn i=336/377 排除隐蔽卡
ChangeHeavyArmor    i=96/137/151/162  未揭示 ⇒ qqq=false 直接返回
CanCardBeBuffed / ChangeOperationCost / GetAllUnitsOnBoard / GetAllCardsOnBoard /
GetCardsInFrontlineBySide / GetCardsInSupportLineBySide / SpawnCardToBoard …
```

**规则一句话**：未揭示的隐蔽卡**不能成为目标**，不算邻卡、不算掩护、不算 bond 阵营，
并且**攻击或被攻击时双方一起揭示**（`RevealCard`）。

**内核**：`src/KLink.Bot/Engine/CardInstance.cs:331-348` 的 `Keyword` 类**没有 `Covert` 常量**；
`KismetVm.cs:762` 注释原文：

```
// ⚠️ 这里**不列** `hasCovert` / `covert`：本内核的 `Keyword` 里没有 Covert
//    （关键字集只覆盖了 CDO 的 has* 字段里已建模的那些）。
```

`IsUnrevealedCovertCard` / `RevealCard` 都不在 `CardApiDispatch` 的键里。
IR 统计：`hasCovert` 以成员读形式出现 10 次、`IsUnrevealedCovertCard` 被 **18 张卡**调用。

**判定：未实现。**

### 2.6 占卜 Scrying —— 未实现

**卡数**：`hasScrying` 1 张 —— `card_event_exploit_the_gap`，卡面
「If you control the frontline, draw 1 of 3 top units of your deck. Give it Blitz and set cost to 0. Put rest on bottom.」

**蓝图规则**：`out/bp-cardfn.json` → `ExecuteScryingEffectBySide`（84 语句）

```
i=5     GameStateRef.<- GetDeckBySide(sideScrying, CallFunc_GetDeckBySide_DeckCardIDs)
i=59    IFNOT(Array_IsNotEmpty(..)) -> pop
i=508   cardPlayed.<- FilterCardsToScry(card, CallFunc_FilterCardsToScry_isValid)
i=562   CallFunc_Greater_IntInt_ReturnValue = Greater_IntInt(3, _totalFilteredCards)
i=761   Array_Insert(_deckCardIDs, <卡>, _totalFilteredCards)     ; 有效的往前提
i=917   if (_totalFilteredCards == 0) DirectClientLogger("Tried to execute scrying effect with 0 valid cards in deck")
i=1145  CallFunc_Min_ReturnValue = Min(Array_Length(_deckCardIDs), 3)   ; 只看前 3 张
i=1345  EqualEqual_IntInt(Array_Get(_deckCardIDs, i), cardToDrawID)     ; 玩家选中的那张
i=1646/1765 Array_RemoveItem + Array_Add  → 其余移到牌库底
i=1839  GameStateRef.<- SetDeckBySide(sideScrying, _deckCardIDs)
i=1937  CardFunctionsNotifier.<- NotifyNewDeck(sideScrying, _deckCardIDs, ...)
i=2005  DrawTopCardFromDeck(sideScrying, 0, false, false, false, 0.4, true, ...)
i=2143  cardPlayed.<- OnHandTargetSelected(Array_Get(_deckCardIDs,0), cardPlayed.cardID)
```

调用方在 `out/bp-onlinematch.json` → `OpponentActionsCardToDrawSelected`
（**客户端/服务端动作握手，不在卡蓝图里**）。

**内核**：`ExecuteScryingEffectBySide` 不在 dispatch 表；`FilterCardsToScry` /
`GetDeckBySide` / `SetDeckBySide` 也都不在。

**判定：未实现。** 影响 1 张卡。补充：`CardApiDispatch.cs` 里已有
「卡池挑牌 → `OpponentActionsCardToDrawSelected`」这条链的实现痕迹
（`DevelopChosenCard`，注释在 L843-860 引用同一段蓝图），所以占卜**可以复用那条链**，
只是 `Min(len,3)` / 移到底 / `FilterCardsToScry` 三段没接。

### 2.7 重甲 HeavyArmor —— 有字段有查询，减伤未实现

**卡数**：`heavyArmor` 52 张（1×40、2×11、3×1）。`CardInnateTable` 里同分布
（`Counter({0: 445, 1: 40, 2: 11, 3: 1})`，非零 52）。

**蓝图规则**：

`out/bp-cardfn.json` → `CalculateDamageDealt`

```
i=1653  _damageDealerCard.<- getTotalDefense(...)         ; 攻击方"有效防御"
i=1694  _damageDealerCard.<- getTotalHeavyArmor(...)
i=1735  Add_IntInt(getTotalDefense_2, getTotalHeavyArmor_1)
i=1781  _damageDealerCard.<- GetPassiveDefenseBuff(_recieverCalculatedDamage, ...)
i=1971  GreaterEqual_IntInt(_recieverCalculatedDamage, <defense+heavyArmor+passive+beforeAttackBuff>)
...
i=2888  CallFunc_Greater_IntInt_ReturnValue_1 = Greater_IntInt(_dealerCalculatedDamage, 0)
i=2922  IFNOT(..) -> goto 3294
i=2936  _damageRecieverCard.<- getTotalHeavyArmor(CallFunc_getTotalHeavyArmor_totalHeavyArmor)
i=2977  SelectInt(0, getTotalHeavyArmor, ignoreHeavyArmor)
i=3028  _damageRecieverCard.<- GetPassiveDefenseBuff(_dealerCalculatedDamage, ...)
i=3082  SelectInt(GetPassiveDefenseBuff_amount_1, 0, applyBeforeAttackBuffs)
i=3133  Add_IntInt(SelectInt_ReturnValue, SelectInt_ReturnValue_1)
i=3179  Subtract_IntInt(_dealerCalculatedDamage, <上面那个和>)
i=3225  Max(.., 0)
i=3267  _dealerCalculatedDamage = CallFunc_Max_ReturnValue
```

`ChangeHeavyArmor`：

```
i=1068  Add_IntInt(cardToChangeRef.heavyArmor, localInputAmount)
i=1136  Clamp(.., 0, 3)
i=1183  cardToChangeRef.heavyArmor <- heavyArmor = Clamp_ReturnValue      ; changeType 分支
i=1304  Add_IntInt(cardToChangeRef.heavyArmorBuff, localInputAmount)
i=1372  cardToChangeRef.heavyArmorBuff <- heavyArmorBuff = ...            ; 无上限
```

**规则一句话**：`getTotalHeavyArmor()` 直接从每一次受到的伤害里扣掉（下限 0），
基础 `heavyArmor` 夹在 0..3，`heavyArmorBuff` 无限叠加。

**内核**：
- 值算得对：`src/KLink.Bot/Engine/CardInstance.cs:148-160`

  ```csharp
  public int HeavyArmor
  { get { int total = Definition.HeavyArmor;
          foreach (var buff in BuffsBySource.Values) total += buff.HeavyArmor;
          return total; } }
  ```
- 查询原语有：`CardApiDispatch.cs:553` `["getTotalHeavyArmor"] = (c, r, a) => AsCard(r)?.HeavyArmor ?? 0,`
- 修改原语有：`CardApiDispatch.cs:193` `["ChangeHeavyArmor"] = ... DoChangeHeavyArmor`（`CardApiDispatch.cs:1590`）
- **但伤害结算完全不减重甲**：`src/KLink.Bot/Engine/MatchEngine.cs:882-903`

  ```csharp
  internal void ApplyDamage(CardInstance target, int amount, CardInstance? source)
  {
      if (amount <= 0 || !target.IsAlive) return;
      if (target.Keywords.Contains(Keyword.Immune)) return;   // L889
      target.Defense -= amount;                                // L894  ← 没有任何 heavyArmor 扣减
  ```

`CardInstance.HeavyArmor` 的全部读取点（grep 结果）只有 `CardApiDispatch.cs:553` 和
`CardInstance.cs:197`（`RecalculateStats` 里同步关键字）。**没有任何伤害路径读它。**

**判定：字段/查询有、减伤未实现。** 属于「有字段但没实现」的典型。
影响 52 张天生重甲卡，每张每次受击都少减 `heavyArmor` 点。

### 2.8 狂怒 Fury —— 未实现

**卡数**：`hasFury` 49 张。

**蓝图规则**：`out/bp-cardfn.json` → `ResetUnitOperations`（21 语句）

```
i=73    CallFunc_GetCardFromID_card.<- IsUnit(...)
i=114   BooleanAND(IsUnit, IsLocatedOnBoard)          ; i=152 IFNOT -> goto 585
i=198   CallFunc_GetCardFromID_card.movementLeft <- movementLeft = 1
i=275   CallFunc_GetCardFromID_card.<- getHasFury(CallFunc_getHasFury_doesIt)
i=316   IFNOT(CallFunc_getHasFury_doesIt) -> goto 503
i=362   CallFunc_GetCardFromID_card.attackLeft <- attackLeft = 2
i=503   GetCardFromID(cardID, ...)
i=535   CallFunc_GetCardFromID_card.attackLeft <- attackLeft = 1
```

`out/bp-logic.json` → `DoOnStartOfTurn` 同一判据：

```
i=4518  IFNOT(IsLocalClientTurn) -> pop
i=4528  _card.movementLeft <- movementLeft = 1
i=4573  _card.<- getHasFury(...)
i=4614  IFNOT(..) -> goto 4674
i=4628  _card.attackLeft <- attackLeft = 2
i=4674  _card.attackLeft <- attackLeft = 1
```

（`DoOnStartOfTurn` i=2566 / i=3781 的 `getHasAmbush` / `getHasFury` 是 UI 的
`effectBar.SetEffectActive(26)`，**纯视觉**，别误当成规则。）

**规则一句话**：狂怒单位每回合 `attackLeft = 2`（普通单位 1），即**一回合能攻击两次**。

**内核**：`Keyword.Fury` 只被 `CardApiDispatch.cs:217`（Give）/ `:228`（Remove）和
`KismetVm.cs:770` 使用。攻击次数判据在 `src/KLink.Bot/Engine/CardInstance.cs:104-105`：

```csharp
public bool CanOperateThisTurn(GameState state)
    => Location.IsBoard() && !HasAttackedThisTurn && !HasDeploymentSickness(state);
```

`MatchEngine.Attack`（`MatchEngine.cs:687`）调它，`MatchEngine.cs:727` 置
`attacker.HasAttackedThisTurn = true;` —— **一次攻击后本回合就再也动不了**。

**判定：未实现。** 49 张天生狂怒卡每回合少一次攻击。

### 2.9 动员 Mobilize —— 未实现

**卡数**：`hasMobilize` 9 张。

**蓝图规则**：`out/bp-logic.json` → `GiveMobilizeBonus`

```
i=397   localCardToCheck.<- IsSideActive(localCardToCheck.side, ...)
i=469   localCardToCheck.<- IsLocatedOnBoard(...)
i=510   BooleanAND(IsLocatedOnBoard, localCardToCheck.hasMobilize)
i=570   BooleanAND(.., IsSideActive)
i=608   IFNOT(..) -> pop
i=664   CallFunc_GetCardFunctions_cardFunctions_1.<- ChangeAttack(localCardToCheck, cardID, 1, 1, false, ...)
i=803   CallFunc_GetCardFunctions_cardFunctions.<- ChangeDefense(localCardToCheck, cardID, 1, 1, false, ...)
```

调用方：`BP_Logic::StartTurnBySide`（与 `SetActiveBondsAtStartOfTurn` 同处）。

失去条件 —— `out/bp-cardfn.json` → `ApplyDamageToCard`：

```
i=2193  IFNOT(toCard.hasMobilize) -> pop
i=2225  CallFunc_Greater_IntInt_ReturnValue_2 = Greater_IntInt(finalDamage, 0)
i=2259  IFNOT(..) -> pop
i=2269  RemoveMobilize(toCardID, 0, false, false, ...)
```

`ExecuteAttackCard` i=3762-3838（防守方）/ i=4675-4751（攻击方）同一判据。

**规则一句话**：动员单位在自己回合 `+1/+1`（`changeType=1`），**一旦受到任何伤害就失去动员**。

**内核**：`Keyword.Mobilize` 只有 `CardApiDispatch.cs:222`（Give）/ `KismetVm.cs:762` 附近注释
（`GetMember` 表里没有 `hasMobilize`）。`GiveMobilizeBonus` / `RemoveMobilize` /
`ApplyDamageToCard` 都不在 dispatch 表。

**判定：未实现。** 9 张天生 + 3 张授予。

### 2.10 闪电战 Blitz —— 已定案，正确

**卡数**：`hasBlitz` 205 张。

**蓝图规则**：`out/bp-logic.json` → `HasDeploymentSickness`

```
i=23    Card.<- getHasBlitz(CallFunc_getHasBlitz_doesIt)
i=64    CallFunc_Not_PreBool_ReturnValue = Not_PreBool(getHasBlitz)
i=93    EqualEqual_IntInt(Card.enterPlayOnTurn, CallFunc_GetTurnNumber_TurnNumber)
i=153   Card.<- IsLocatedOnBoard(...)
i=232   BooleanAND(BooleanAND(IsLocatedOnBoard, enterPlayOnTurn==turn), Not(getHasBlitz))
```

`out/bp-cardscheck.json` → `CanAttack` i=1960-2108：`failReason = "deployment_sickness"`。

**内核**：`src/KLink.Bot/Engine/CardInstance.cs:88-91`

```csharp
public bool HasDeploymentSickness(GameState state)
    => Location.IsBoard()
       && EnteredPlayOnTurn == state.Turn
       && !Keywords.Contains(Keyword.Blitz);
```

数据来源 `CardInnateTable.cs`（497 张，其中 Blitz 205 张）。**判定：正确，已定案。**

### 2.11 冲击 Shock —— 未实现

**卡数**：`hasShock` 32 张。

**蓝图规则**：`out/bp-cardfn.json` → `ExecuteAttackCard`

```
i=335   defender.<- IsLocation(...)      ; i=376 Not_PreBool -> i=405
i=405   attacker.<- HasCustomAbility("CantLoseShock", ...)
i=490   attacker.<- getHasShock(...)
i=531   BooleanAND(getHasShock, Not(HasCustomAbility("CantLoseShock")))
i=569   BooleanAND(.., Not(defender.IsLocation()))
i=607   IFNOT(..) -> pop
i=617   RemoveShock(attacker.cardID, attacker.cardID, true, false, ...)
i=704   wasShockAttack = true
```

`CalculateDamageDealt` 里 Shock 参与 ambush 分支：

```
i=1476  _damageDealerCard.<- getHasShock(...)
i=1517  _damageRecieverCard.<- IsBomber(...)
i=1558  BooleanOR(IsBomber, getHasShock)          ; i=1596 IFNOT -> 1611 ; i=1610 PopExecutionFlow
i=2697  _damageRecieverCard.<- getHasShock(...)
i=2805  BooleanAND(Not(dealingDamageIsAttacker), receiver.getHasShock, dealer.IsUnit())
i=2853  _dealerCalculatedDamage = 0
i=2876  shockAttack = true
```

**规则一句话**：冲击单位攻击（非地点卡）后**失去冲击**并标记 `wasShockAttack`；
在伏击结算里 Shock 与轰炸机同属一类例外。

**内核**：`Keyword.Shock` 只有 `CardApiDispatch.cs:224`（Give）/ `KismetVm.cs:762` 附近注释
（`GetMember` 表里没有 `hasShock`）。`RemoveShock` / `ExecuteAttackCard` / `ShockRecoil` /
`getHasShock` 都不在 dispatch 表。

**判定：未实现。** 32 张天生 + 18 张授予 + 3 张卡读 `getHasShock`（判据会静默假）。

### 2.12 部署 Deployment —— 部分实现（卡自己的战吼能跑，观察者/取消/多次触发全缺）

**卡数**：`hasDeployment` 249 张。

**蓝图规则**：`out/bp-cardfn.json` → `TriggerDeployment`（52 语句）

```
i=16    CallFunc_IsValid_ReturnValue = IsValid(card)
i=45    IFNOT(..) -> goto 725          ; 无效卡：DirectClientLogger("[Trigger Destruction] on an invalid card.")
i=59    IFNOT(card.hasDeployment) -> pop
i=173   FetchAllCardsWithEventTrigger(14, ...)      ; 14 = OnBeforeOtherCardDeploymentTrigger
i=524   OnBeforeOtherCardDeploymentTrigger(card, out cancelDeploymentEffect)
i=578   DeployEffectStopped = cancelDeploymentEffect
i=597   IFNOT(..) -> pop
i=666   CardFunctionsNotifier.<- NotifyForceCardEffectTrigger(<卡>, 9)
i=713   Temp_bool_True_if_break_was_hit_Variable = true
i=833   IFNOT(DeployEffectStopped) -> goto 956
i=847   NotifySideEffectTrigger(side, GameplayTag 'sideeffect.blockdeployment', ...)
i=956   ExecuteOnDeploymentTriggered(card, instigatorID, out triggerMultiple)   ; 23 = OnDeploymentEffectTriggered
i=1020  Add_IntInt(1, triggerMultiple)
i=1100  IFNOT(LessEqual_IntInt(i, 1+triggerMultiple)) -> goto 1261
i=1119  card.<- OnPlayedFromHand(card.currentTarget)
```

`CardPlayedFromHand` i=3640-5813 是同一条链的重复实现（`hasDeployment` 门 + 事件 14 + 事件 23 +
`SetExtraPlayTriggers(_triggerMultiple)`）。

**内核**：
- **有**：`src/KLink.Bot/Engine/MatchEngine.cs:488-489`

  ```csharp
  // ---- ④ 战吼（卡自己的 OnPlayedFromHand）----
  Api.RunCardEffect(card, target);
  ```
  `CardApi.cs:71` `var program = library?.FindProgram(card.Definition.Name, "OnPlayedFromHand");`
  → 卡面「Deployment: …」文本对应的效果**能跑**。
- **缺**：`hasDeployment` 门（`CardDatabase` 里没有这个字段）、
  `OnBeforeOtherCardDeploymentTrigger`（4 张卡，**无法取消部署效果**）、
  `OnDeploymentEffectTriggered`（1 张卡，**永不触发**）、
  `TriggerDeployment` / `SetExtraPlayTriggers` / `triggerMultiple`（**不支持一次部署触发多次**）。

**判定：部分实现。** 严格说不是「未实现」，但 249 张卡的部署效果在
「被别人拦掉」「被多次触发」「被别人观察」这三个维度上全部走错。

### 2.13 摧毁 Destruction —— 部分实现（同 2.12 的形状）

**卡数**：`hasDestruction` 73 张。

**蓝图规则**：`out/bp-cardfn.json` → `TriggerDestruction`（123 语句）

```
i=381   card.<- HasCustomAbility("destruction", ...)
i=435   card.<- CustomName1HasAttribute("StopDestructionEffect", ...)
i=499   CallFunc_Not_PreBool_ReturnValue = Not_PreBool(CustomName1HasAttribute)
i=528   BooleanAND(Not(..), card.hasDestruction)
i=588   BooleanOR(BooleanAND(..), HasCustomAbility("destruction"))
i=626   IFNOT(..) -> pop
i=636   ExecuteOnBeforeOtherCardDestroyed(CardID, instigatorID, true, false)
i=1237  localCardUsedForTriggeringDestructionEffect.<- OnDestroyed(NoObject{}, true)
i=1286  ExecuteOnDestructionEffectTriggered(..., localDestructionEffectTriggerCards, false, ...)
i=3359  IFNOT(RemoveDestruction) -> pop
i=3437  CustomAbilityRemove("destruction", CardID, 0, true, ...)
i=3539  card.<- CustomName1Add("StopDestructionEffect")
```

`ExecuteOnDestructionEffectTriggered` i=325/710：

```
i=325   IFNOT(skipSuppressCheck) -> goto 710
i=710   IFNOT(cardTriggered.isSuppressed) -> goto 339
```

`ExecuteOnCardDestroyedFunction` i=343-999 是同一门的另一份实现（多一个 `isSuppressed` 前置）。

**内核**：`src/KLink.Bot/Engine/MatchEngine.cs:944` `Destroy(...)`

```csharp
Say($"{card} 被摧毁");
Api.FireTrigger("OnBeforeDestroyed", card, card.Owner);          // L953
FireSubAction("ZActionDestroyUnit", ...);
if (card.Location.IsBoard() && !card.IsHq) FireLeaveTrigger(card, CardLocation.Discard);
State.Move(card, CardLocation.Discard);
Api.FireTrigger("OnDestroyed", card, card.Owner, "OnOtherCardDestroyed");   // L970
```

- **有**：卡自己的 `OnDestroyed` 程序会跑（`FireTrigger` 的 subject 是那张卡）。
- **缺**：`hasDestruction` 门（内核没有这个字段）、`StopDestructionEffect`、
  `ExecuteOnDestructionEffectTriggered`（事件 24，4 张卡**永不触发**）、
  `TriggerDestruction` 原语（5 张卡直接调它）、`StealSide` / `RemoveDestruction` 两个参数语义。

**判定：部分实现。**

### 2.14 钳击 Pincer —— 未实现

**卡数**：`hasPincer` 15 张。卡面形态：「Pincer: +2 attack.」/「Pincer: Ambush.」/
「Pincer: Damage dealt to this unit is dealt to its Pincer partner instead.」

**蓝图规则**：

`CardPlayedFromHand`（`out/bp-cardfn.json`）

```
i=4513  CallFunc_IsValid_ReturnValue = IsValid(CallFunc_GetCardFromID_card_4)
i=4542  CallFunc_BooleanAND_ReturnValue_3 = BooleanAND(cardPlayed.hasPincer, IsValid_ReturnValue)
i=4602  IFNOT(..) -> goto 4680
i=4616  GetCardFromID(targetCardID, CallFunc_GetCardFromID_card_5)
i=4648  ApplyPincerEffects(cardPlayed, CallFunc_GetCardFromID_card_5)
```

`ApplyPincerEffects`（16 语句）

```
i=5     NotEqual_ByteByte(cardPlayed.location, 8)     ; 进弃牌堆就不挂
i=73    cardPlayed.<- OnPincerEffectApplied(cardPlayed)
i=118   JSON_SetInt(cardPlayed, "pincer_receiver", cardTargeted.cardID)
i=198   PersistCustomFields(cardPlayed.cardID, true)
i=244   cardPlayed.<- OnPincerEffectReceived(cardPlayed)
i=290   cardPlayed.<- OnPincerEffectApplied(cardTargeted)
i=335   JSON_AddToIntArray(cardTargeted, "pincer_givers", cardPlayed.cardID)
i=459   cardTargeted.<- OnPincerEffectReceived(cardPlayed)
```

`RemovePincerEffects`（49 语句）i=10/78/110 读 `pincer_receiver` 反向清，
i=766-1507 读 `pincer_givers` 逐个清并派发 `OnPincerEffectRemoved`，
i=584/1507 `JSON_Clear` 两个键。

**规则一句话**：打出带钳击的单位时选一个目标，两者互相登记为钳击伙伴
（`pincer_receiver` / `pincer_givers`）；伙伴在场时该卡的「Pincer: …」文本生效，
任一方离场/被压制时解链。

**内核**：`ApplyPincerEffects` / `RemovePincerEffects` / `hasActivePincerEffect` /
`hasGuardAdjacentUnit` **都不在 `CardApiDispatch.BuildDispatch()` 的 173 个键里**；
`OnPincerEffectApplied` / `OnPincerEffectReceived` / `OnPincerEffectRemoved` 三个触发点
内核从不 `FireTrigger`（`FireTrigger` 全部调用点见 §4）。

**判定：未实现。** 15 张天生钳击卡 + 15 张订阅 `OnPincerEffectApplied` 的卡。

### 2.15 打捞 Salvage —— 未实现

**卡数**：`hasSalvage` 9 张。

**蓝图规则**：

`ExecuteOnCardDestroyedFunction`（`out/bp-cardfn.json`）

```
i=2147  localKiller.<- GetOppositeSide(...)
i=2188  localDestroyedCard.<- HasCustomAbility("cantBeSalvaged", ...)
i=2245  EqualEqual_ByteByte(localDestroyedCard.side, <killer 的对面>)
i=2334  localKiller.<- IsSideActive(localKiller.side, ...)
i=2406  BooleanAND(localKiller.hasSalvage, IsSideActive)
i=2466  BooleanAND(.., destroyedCard.side == 对面)
i=2504  BooleanAND(.., Not(HasCustomAbility("cantBeSalvaged")))
i=2542  IFNOT(..) -> pop
i=2572  SalvageMultipleUnits(K2Node_MakeArray_Array, localKiller.cardID, ...)
```

`SalvageMultipleUnits`（104 语句）

```
i=566   IsLocationFull(salvaging hand location, ...)   ; i=598 满则跳过
i=1678  S_SalvagedCardInfo.isSalvaged = true
i=1698  S_SalvagedCardInfo.salvageFaction = killer.faction
i=1756  S_SalvagedCardInfo.salvagedCardID = tmpCardID
i=1792  CreateCard(salvaging side, _destroyedCard.name, salvaging hand location, ..., K2Node_MakeStruct_S_SalvagedCardInfo, ...)
i=2796  CallFunc_Array_Get_Item.<- OnOtherCardSalvaged(tmpCardID, createdCardID, instigatorID)
```

`ApplySalvageChanges`（12 语句）

```
i=0     card.isSalvaged <- isSalvaged = salvageInfo.isSalvaged
i=50    card.faction <- faction = salvageInfo.salvageFaction
i=108   card.salvageFaction <- salvageFaction = salvageInfo.salvageFaction
i=166   IFNOT(isVeteran) -> goto 185 ; i=180 goto 390
i=185   card.attack <- attack = 1
i=230   card.defense <- defense = 1
i=275   Greater_IntInt(card.kredits, 3)
i=345   card.kredits <- kredits = 3
```

**规则一句话**：带打捞的单位在自己回合**打死敌方单位**时，把那张卡以
**己方阵营、1/1、费用≤3、`isSalvaged=true`** 的形式复制一份进自己手牌
（`isVeteran` 时保留原数值）。

**内核**：`SalvageMultipleUnits` / `ApplySalvageChanges` / `CreateCard`（作为原语）/
`OnOtherCardSalvaged` 都不在 dispatch 表 / 从不派发。

**判定：未实现。** 9 张天生 + 3 张授予。

### 2.16 密码 / 情报 Cipher —— 未实现

**卡数**：`cipher` 字段 25 张（值 1×11、2×9、3×4、9×1）。

**蓝图规则**：

`CardPlayedFromHand`

```
i=1955  CallFunc_Greater_IntInt_ReturnValue_2 = Greater_IntInt(cardPlayed.cipher, 0)
...
i=3380  SetCardsSeenByCipher(cardPlayed.cipher, cardPlayed.cardID, cardPlayed.side, ...)
```

`SetCardsSeenByCipher`（49 语句）

```
i=10    FetchAllCardsWithEventTrigger(28, ...)     ; 28 = OnIntelTriggered
i=311   CallFunc_Array_Get_Item.<- OnIntelTriggered(GetCardFromID(instigatorID), numberOfCardsSeen)
i=431   GetCardsInHandBySide(side, ...)
i=795   IFNOT(CallFunc_Array_Get_Item_1.cardSeen) -> goto 1272
i=893   Array_ShuffleFromStream(oppositeSideUnseenCards, cardsRandomStream)
i=1002  CallFunc_Min_ReturnValue = Min(numberOfCardsSeen, Array_Length(oppositeSideUnseenCards))
i=1098  ApplySetCardsSeenByCipher(oppositeSideUnseenCards, false, true)
```

`ApplySetCardsSeenByCipher`（27 语句）i=591 `CallFunc_Array_Get_Item.cardSeen <- cardSeen = true`，
i=658 `CardFunctionsNotifier.<- NotifyCardsSeen(IDs, seen, showAnimation, enemyTurn)`。

**规则一句话**：`cipher = N` 的卡打出时，从对手手牌里**随机抽 N 张未见过**的牌标记为
「已知」（`cardSeen`），并向 `OnIntelTriggered` 的 7 张订阅卡广播。

**内核**：`SetCardsSeenByCipher` / `ApplySetCardsSeenByCipher` 不在 dispatch 表；
`OnIntelTriggered` 从不派发。`CardApiDispatch` 里 `HasIntel` 在表里
（`["HasIntel"]` 存在），但**只读不写** —— 没有任何地方置 `cardSeen`。

**判定：未实现。** 25 张天生密码卡 + 7 张 `OnIntelTriggered` 订阅卡。

### 2.17 羁绊 Bond —— 未实现（CDO 无字段）

**卡数**：**0**。`hasBond` / `bond` 在 2023 条 CDO 里出现 0 次；`gameplayTags` 全为 `null`。
Bond 只能由效果授予 —— IR 里 2 张卡调 `GiveBond`：
`card_unit_kings_african_rifles`（「Deployment: Choose a unit in hand. Give it +1+1 and Bond.」）、
`card_event_way_of_subjects`（「All Japanese cards in your deck get Bond and …」）。

**蓝图规则**：

`GiveBond`（37 语句）

```
i=636   _card.<- HasBond(CallFunc_HasBond_hasIt)
i=677   IFNOT(CallFunc_HasBond_hasIt) -> goto 696 ; i=691 goto 1291
i=696   AddCustomGameplayTag(GameplayTag 'ability.bond', _card.cardID, instigatorID, ...)
i=823   CustomAbilityRemove("bond_removed", _card.cardID, instigatorID, true, ...)
i=901   CardFunctionsNotifier.<- NotifyGiveBond(_card.cardID, instigatorID)
i=977   ExecuteOnOtherCardsAbilitiesChanged(_card)
```

`BP_GameState_Battle::SetActiveBondsAtStartOfTurn`（`out/bp-gamestate.json`）

```
i=5     Set_Clear(activeBondFactions)
i=92    GetAllCardInBattle(CallFunc_GetAllCardInBattle_AllCardsInBattle)
i=336   CallFunc_Array_Get_Item.<- IsUnrevealedCovertCard(...)
i=377   CallFunc_Not_PreBool_ReturnValue = Not_PreBool(IsUnrevealedCovertCard)
i=406   CallFunc_Array_Get_Item.<- IsUnit(...)
i=447   EqualEqual_ByteByte(CallFunc_Array_Get_Item.side, side)
i=507   CallFunc_Array_Get_Item.<- IsLocatedOnBoard(...)
i=662   IFNOT(..) -> pop
i=754   Set_Add(activeBondFactions, CallFunc_Array_Get_Item.faction)
```

`CardPlayedFromHand`

```
i=1539  cardPlayed.<- HasBond(CallFunc_HasBond_hasIt)
i=1580  Set_Contains(GameStateRef.activeBondFactions, cardPlayed.faction)
i=1684  CallFunc_Not_PreBool_ReturnValue_1 = Not_PreBool(Set_Contains_ReturnValue_1)
i=1713  BooleanAND(HasBond, Not(Set_Contains))
i=1751  IFNOT(..) -> goto 1918
i=1765  ApplyFatigueDamage(cardPlayed.side, true, CallFunc_ApplyFatigueDamage_destroyed)
i=1820  IFNOT(destroyed) -> goto 1918
i=1834  CallFunc_GetTheOtherSide_ReturnValue = GetTheOtherSide(cardPlayed.side, self)
i=1894  HQ_DestroyedEndMatch(CallFunc_GetTheOtherSide_ReturnValue)
```

**规则一句话（按代码读）**：每回合开始时重算 `activeBondFactions` = 己方在场、
非隐蔽单位的阵营集合；**打出一张有 Bond 的卡，而它的阵营不在这个集合里 ⇒ 自己吃疲劳伤害**，
疲劳打穿 HQ 就直接判负。

> ⚠️ **不确定**：仓库里没有本地化/卡牌关键词说明文件（`raw` 里 `Text` 只有卡面正文，
> 关键词说明是图标 tooltip，不在这份 dump 里），所以这条「惩罚式」语义**无法用玩家可见文案交叉验证**，
> 只能说「蓝图代码是这么走的」。`card_unit_kings_african_rifles` 的文案「Give it +1+1 and Bond」
> 说明 Bond 是授予型关键词，与上面那条惩罚判据并不矛盾（授予在前、打出时结算）。

**内核**：`Keyword.Bond` 常量存在（`CardInstance.cs:336`），`CardApiDispatch.cs:225`
`["GiveBond"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Bond),`（**只是往 `Keywords` 集合里加字符串**）。
`HasBond` / `RemoveBond` / `SetActiveBondsAtStartOfTurn` / `ApplyFatigueDamage` 都不在 dispatch 表。

**判定：未实现。** 影响 6 张读 `HasBond` 的卡 + 2 张授予。

---

## 3. 「未实现」vs「实现了但语义错」的明确区分

| 分类 | 机制 |
|---|---|
| **实现但语义错** | **掩护 Guard** —— 内核做成嘲讽（必须优先打掩护），蓝图是「掩护保护左右邻卡、掩护自己可被打」。 |
| **部分实现**（卡自己的效果能跑，但机制层缺） | **部署 Deployment**、**摧毁 Destruction** —— 卡的 `OnPlayedFromHand` / `OnDestroyed` 会跑，但 `hasDeployment` / `hasDestruction` 门、观察者触发点（`OnDeploymentEffectTriggered` / `OnDestructionEffectTriggered`）、取消/多次触发全部缺失。 |
| **有字段有查询、关键行为未实现** | **重甲 HeavyArmor** —— 点数算得对、`getTotalHeavyArmor` 原语在表里，但 `ApplyDamage` 不减伤。 |
| **完全未实现**（关键字能存能读，但没有任何规则读取） | 伏击 Ambush、烟幕 Smokescreen、山地 Alpine、隐蔽 Covert、占卜 Scrying、狂怒 Fury、动员 Mobilize、冲击 Shock、钳击 Pincer、打捞 Salvage、密码 Cipher、羁绊 Bond |
| **正确** | 闪电战 Blitz（已定案） |

---

## 4. 支撑证据：内核缺什么

### 4.1 `CardApiDispatch.BuildDispatch()` 的 173 个键里**没有**这些名字

（用 `grep '^\s*\["..."\]\s*='` 抽键名后逐个比对）

```
RemoveAlpine  RemoveMobilize  RemoveSalvage  RemoveShock  RemoveBond  RemoveHeavyArmor
GiveRandomCombatKeyword  ApplyPincerEffects  RemovePincerEffects
TriggerDeployment  TriggerDestruction  SalvageMultipleUnits  ApplySalvageChanges
SetCardsSeenByCipher  ApplySetCardsSeenByCipher  ExecuteScryingEffectBySide
GiveMobilizeBonus  GiveAlpineBonus  SetActiveBondsAtStartOfTurn  HasBond
UpdateGuarded  IsUnrevealedCovertCard  RevealCard  IsPinned  SuppressMultipleUnits
ChangedPinnedTurns  ApplyFatigueDamage  FilterCardsToScry  GetDeckBySide  SetDeckBySide
hasActivePincerEffect  hasGuardAdjacentUnit
getHasGuard  getHasAlpine  getHasShock  getHasBlitz  getHasFury  getHasAmbush  getHasSmokescreen  getHasImmune
HasAttackLeft  ResetUnitOperations  SetAttackerHasAttacked
ExecuteBeforeReceiveDamage  ExecuteOnSurvivedCombatEvents
CalculateDamageDealt  ExecuteAttackCard  AttackCard
```

### 4.2 `KismetVm.GetMember` 的关键字成员表只覆盖 7 个

`src/KLink.Bot/Effects/Blueprint/KismetVm.cs:765-771`（原文）

```csharp
"hasGuard" => card.Keywords.Contains(Keyword.Guard),
"hasBlitz" => card.Keywords.Contains(Keyword.Blitz),
"hasAmbush" => card.Keywords.Contains(Keyword.Ambush),
"hasFury" => card.Keywords.Contains(Keyword.Fury),
"hasSmokescreen" => card.Keywords.Contains(Keyword.Smokescreen),
"hasHeavyArmor" => card.Keywords.Contains(Keyword.HeavyArmor),
"pinned" => card.Keywords.Contains(Keyword.Pinned),
```

**不在表里**（IR 里以 `{"var":"X","ctx":…}` 成员读形式出现的次数，从 `card-ir.json` 统计）：

| 成员 | IR 出现次数 | 后果 |
|---|---|---|
| `hasCovert` | 10 | 读成 null → 判假 |
| `hasDestruction` | 4 | 同上 |
| `hasAlpine` | 2 | 同上 |
| `hasMobilize` | 2 | 同上 |
| `hasDeployment` | 1 | 同上 |
| `hasGuard`（无 ctx 形式） | 2 | 依赖别处兜底，需单独核 |

另外 `getXxx()` **函数调用**形式（IR 里是 `{"op":"call","fn":"getHasXxx"}`）全部走
`_api.InvokeByName`（`CardApiDispatch.cs:1350`），而它**只查 dispatch 表、没有成员读兜底**：

```csharp
public object? InvokeByName(string name, object? receiver, object?[] args, EffectContext ctx, out bool handled)
{
    if (_dispatch.TryGetValue(name, out var handler)) { handled = true; ... }
    handled = false;
    return null;
}
```

所以 IR 里以 `call fn=getHasAlpine` 形式调用的 **7 张卡**、`getHasShock` **3 张卡**、
`getHasBlitz` **6 张卡**、`getHasGuard` **4 张卡**、`getHasFury` **1 张卡**、
`getHasAmbush` **1 张卡**、`getHasSmokescreen` **1 张卡**，判据全部静默取假。
（`KismetVm.GetMember` 里的 `"hasGuard"` 等条目只对**成员读**形式生效，对 `call` 形式无效。）

### 4.3 内核派发的触发点（`FireTrigger` 全部调用点）

```
CardApi.cs:366       OnReceiveDamage / OnOtherCardReceiveDamage
CardApi.cs:498       OnOtherCardSpawnedInHand
CardApi.cs:563       OnBecomingVeteran
MatchEngine.cs:163   OnStartOfGame
MatchEngine.cs:244   OnStartOfTurn / OnOtherStartOfTurn
MatchEngine.cs:255   OnEndOfTurn / OnOtherEndOfTurn
MatchEngine.cs:351   OnOtherCardDrawnFromDeck
MatchEngine.cs:486   OnEnterPlay / OnOtherCardEnterPlay
MatchEngine.cs:495   OnOtherCardPlayedFromHand
MatchEngine.cs:600   OnMoveToFrontline / OnOtherCardMoveToFrontline
MatchEngine.cs:604   OnFrontlineOwnershipChange / OnOtherFrontlineOwnershipChange
MatchEngine.cs:664   OnLeaveBoardOrOwner / OnOtherCardLeaveBoardOrOwner
MatchEngine.cs:666   OnAfterOtherCardLeaveBoardOrOwner
MatchEngine.cs:735   OnBeforeAttack / OnBeforeOtherCardAttacks
MatchEngine.cs:736   OnBeforeAttack（防守方）
MatchEngine.cs:782   OnAfterAttack / OnAfterOtherCardAttacks
MatchEngine.cs:953   OnBeforeDestroyed
MatchEngine.cs:970   OnDestroyed / OnOtherCardDestroyed
CardApiDispatch.cs:713/1296  OnHandTargetSelected
```

**从不派发**（`out/cards-full2.json` 的 `usedTriggers` 订阅卡数）：

| 触发点 | 订阅卡数 | 关联机制 |
|---|---|---|
| `OnDestructionEffectTriggered` | 4 | 摧毁 |
| `OnDeploymentEffectTriggered` | 1 | 部署 |
| `OnBeforeOtherCardDeploymentTrigger` | 4 | 部署（取消） |
| `OnOtherCardLoseSmokescreen` | 3 | 烟幕 |
| `OnOtherCardSalvaged` | 1 | 打捞 |
| `OnOtherCovertCardSpawned` | 1 | 隐蔽 |
| `OnIntelTriggered` | 7 | 密码 |
| `OnOtherCardSurvivedCombat` | 6 | 战斗存活（伏击/冲击相关） |
| `OnOtherCardAttackSwitchTarget` | 2 | 攻击改目标 |
| `OnOtherCardAttacks` | 22 | 攻击宣告（含 `SetStopAttack`） |
| `OnOtherUnitPinned` / `OnOtherUnitUnPinned` | 2 / 2 | 压制/钉死 |
| `OnOtherCardRevealed` | 6 | 隐蔽揭示 |
| `OnOtherCardBecomingVeteran` | 9 | 老兵 |
| `OnOtherCardGainDefense` | 8 | — |
| `OnOtherCardDealDamageAddDamage` | 34 | 伤害加成 |
| `OnOtherCardDealDamageAddDamageAfterCalc` | 15 | 伤害加成（结算后） |
| `OnOtherCardDealDamage` | 28 | 造成伤害 |
| `OnOtherCardReceiveDamage` | 9 | 受到伤害（内核有派发 `OnOtherCardReceiveDamage`，见 `CardApi.cs:366`） |

> 上表只是「内核从不派发」的名单，订阅卡数取自 CDO `usedTriggers`。
> 其中一部分不属于维度 3（例如 `OnOtherCardDealDamageAddDamage`），列在这里是因为
> 它们和关键词机制（伏击/冲击的伤害归零、老兵）耦合。

### 4.4 内核 `Keyword` 类（`src/KLink.Bot/Engine/CardInstance.cs:331-348`）

```csharp
public static class Keyword
{
    public const string Alpine = "Alpine";
    public const string Ambush = "Ambush";
    public const string Blitz = "Blitz";
    public const string Bond = "Bond";
    public const string Fury = "Fury";
    public const string Guard = "Guard";
    public const string Immune = "Immune";
    public const string Mobilize = "Mobilize";
    public const string Salvage = "Salvage";
    public const string Shock = "Shock";
    public const string Smokescreen = "Smokescreen";
    public const string HeavyArmor = "HeavyArmor";
    public const string Veteran = "Veteran";
    public const string Suppressed = "Suppressed";
    public const string Pinned = "Pinned";
}
```

没有 `Covert`、没有 `Cipher`、没有 `Scrying`、没有 `Pincer`、没有 `Deployment`、没有 `Destruction`。

### 4.5 `CardInnateTable` 覆盖情况

`src/KLink.Bot/Cards/CardInnateTable.cs`：497 条。逐关键字计数：

```
Guard 128 / Blitz 205 / Fury 49 / Smokescreen 54 / Ambush 46 /
Mobilize 9 / Shock 32 / Salvage 9 / Alpine 18        （合计 550 个关键字实例）
HeavyArmor: Counter({0: 445, 1: 40, 2: 11, 3: 1})    （非零 52，与 CDO 完全一致）
```

**表里没有**：`Covert`(11)、`Deployment`(249)、`Destruction`(73)、`Pincer`(15)、
`Scrying`(1)、`Cipher`(25)、`Immune`、`Bond`(0)。
`src/KLink.Bot/Cards/CardDatabase.cs:46-47` 的注释也确认表只含
「Blitz / Guard / Ambush / Fury / Smokescreen / Alpine / Mobilize / Salvage / Shock」。

---

## 5. 读不出来 / 不确定

1. **Ambush 分支的嵌套结构**。`CalculateDamageDealt` 的 `PushExecutionFlow` 集中在
   i=115-150、`PopExecutionFlow` 散落在 i=822/1057/2042/2161/2303/2632/2655，
   发射顺序不是嵌套顺序，所以**哪条 `_dealerCalculatedDamage = 0` 属于哪一层条件我没有完全还原**。
   见 §2.2。要定案需要 `bpasm` 的 `.export` 结构化输出（`out/xr-calcdamage.json` 是同一份字节码，
   没有额外信息）。
2. **Bond 的玩家可见语义**。仓库里没有本地化 / 关键词 tooltip dump（`raw.Text` 只有卡面正文）。
   §2.17 的「惩罚式」结论只来自 `CardPlayedFromHand` i=1539-1917 的代码路径，
   **无法用文案交叉验证**。
3. **Pincer 的伙伴选择约束**。`CardPlayedFromHand` i=4513 只判 `IsValid(target)`，
   我没有找到「必须相邻 / 必须同线」的判据；但 `card_event_protect_the_pocket` 与
   `card_unit_5_panzergrenadier` 用 `hasActivePincerEffect`，那个函数的定义不在我读的
   四份 bp JSON 里（可能在 `bp-onlinematch.json` 或卡蓝图内部）。**不确定**。
4. **`hasScrying` 的 1 张卡之外还有没有别的入口**。`ExecuteScryingEffectBySide` 的唯一调用方是
   `BP_OnlineMatch::OpponentActionsCardToDrawSelected`，属于服务端/客户端握手；
   如果内核不回放这条握手，占卜永远走不到。**内核有没有这条链没查**（超出维度 3）。
5. **`Keyword.Immune`**：`MatchEngine.cs:889` 有实现（`if (target.Keywords.Contains(Keyword.Immune)) return;`），
   与蓝图 `CalculateDamageDealt` i=155/196（`getHasImmune` ⇒ `damage = 0`）、
   `ExecuteOnDealDamageAddDamageAfterCalc` i=38/79（`finalDamage = 0`）、
   `ApplyDamageToMultipleCards` i 也判 immune —— 语义一致。9 张卡授予免疫。
   **这一条看起来是对的**，但不在任务清单里，只作旁注。
6. **`Keyword.Pinned` / `Keyword.Suppressed`**：`MatchEngine.cs:712` 实现
   （`if (attacker.Keywords.Contains(Keyword.Pinned) || attacker.Keywords.Contains(Keyword.Suppressed)) return false;`），
   对应 `CanAttack` i=1801 `BooleanAND(IsPinned, Not(HasCustomAbility))` → `failReason = "unit_is_pinned"`。
   但 `IsPinned` 不在 dispatch 表（16 张卡读它）、`SuppressMultipleUnits`（2 张卡调）、
   `ChangedPinnedTurns`（1 张卡调）也没有。**部分实现**，同样只作旁注。
