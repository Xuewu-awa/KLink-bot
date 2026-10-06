# BP_CardFunctions 审计报告

生成脚本：klink bot\tools\audit-bp-cardfunctions.py
蓝图 JSON：D:\Codex Workshop\kards-live-extract\BP_CardFunctions.json

## 完整性

- JSON 函数数：294
- names.txt 行数：294（唯一值 294）
- JSON 与 names.txt 顺序一致：**True**
- JSON SHA-256：3032736e120eea085a12c415c0167a46884ad1cc7e2d51a3e729bc5929a949d7
- names.txt SHA-256：f59a7c47ff80f79e5c68be178f772f64d5d319475dec8adbefca52b370cde1de

## 函数统计

- 表达式总数：10663
- Event：192；Pure：40
- Out 参数：198；带默认值：182
- 字节码长度：2 ~ 347，平均 36.27

## 复杂函数 TOP 20

| 函数 | 字节码 | Event | Out | Defaults |
|---|---:|:---:|:---:|:---:|
| ApplyDamageToMultipleCards | 347 | False | True | True |
| SuppressMultipleUnits | 231 | True | True | True |
| CardPlayedFromHand | 227 | False | False | True |
| ChangeDefense | 207 | True | True | True |
| ChangeBuffsFromCards | 205 | False | True | True |
| ExecuteAttackCard | 175 | False | False | True |
| CalculateDamageDealt | 170 | False | True | False |
| AttackCard | 168 | False | True | True |
| GetNewLocationNumbers | 167 | False | True | True |
| ConvertCard | 149 | True | True | True |
| ChangeOperationCost | 128 | True | False | True |
| TriggerDestruction | 123 | True | True | True |
| CreateCard | 118 | False | True | True |
| ApplyDestroyMultipleCards | 117 | False | True | True |
| PlayCardDirectlyFromHand | 116 | True | True | True |
| ChangeKreditCost | 110 | True | True | True |
| ChangeUnitOwnership | 109 | False | False | True |
| SpawnMultipleCardsOnBattlefield | 109 | True | True | True |
| PlayCardFromHand | 105 | False | False | True |
| ChangeAttack | 104 | True | True | False |

## 调用热点

### LocalVirtualFunction

- GetCardFromID：179
- IsActionProcess：141
- FetchAllCardsWithEventTrigger：73
- ChangeBuffsFromCards：51
- DirectClientLogger：51
- GetAllCardInBattle：39
- provideKeysAndFrameCount：24
- ExecuteOnOtherCardsAbilitiesChanged：19
- GetTurnNumber：18
- CanCardBeBuffed：17
- FetchCardsByLocation：17
- GetLocationCardBySide：15
- CardLocationMoved：12
- GetStopFurtherActions：11
- PersistCustomFields：11
- GetClientSide：10
- JSON_Clear：10
- ExecuteOnAfterLeaveBoardOrOwnerEvents：9
- ExecuteOnBeforeLeaveBoardOrOwnerEvents：9
- ExecuteOnBeforeOtherCardDestroyed：9
- GetDeckBySide：9
- RemoveSmokescreen：9
- SetCardLocationAndLocNumber：9
- ApplyRemoveCardFromBoard：8
- ExecuteOnDestructionEffectTriggered：8
- GetFrontlineOwnerSide：8
- CreateCard：7
- ExecuteOnAfterDeckChanged：7
- ExecuteOnCardLocationMoved：7
- ExecuteOnDealDamageAddDamage：7

## 派发关系

- CardApiDispatch + KismetVm 可识别名字：360
- 与 BP_CardFunctions 同名：131
- LocalVirtualFunction 中未出现在派发表的候选名：339

注意：候选名不是自动缺口结论。Blueprint 成员函数、Kismet 数学函数和卡牌局部函数需要结合 IR、事件契约和 CardApi 语义继续分类。

### 候选名 TOP 40

- Achievements_AddEliteCopy
- Achievements_UpdateDealtMoreThanOneToHQ
- Achievements_UpdateFrontlineCaptured
- AddAutoPlayCards
- AddBuffsToRemoveEndOfTurn
- AddCardToAllCardsInBattle
- AddCardToDeckBySide
- AddCustomGameplayTag
- AddGameplayRestrictionEffect
- AddWaitPlayFromHandCards
- AfterWaitCardPlayFromHand
- ApplyDamageToCard
- ApplyDamageToMultipleCards
- ApplyDestroyMultipleCards
- ApplyFatigueDamage
- ApplyMakeCardRetreat
- ApplyPincerEffects
- ApplyRemoveCardFromBoard
- ApplySalvageChanges
- ApplySetCardsSeenByCipher
- CalculateDamageDealt
- CardLocationMoved
- CardPlayedFromHand
- ChangeBuffsFromCards
- ChangeKreditSlotsBySide
- ChangeKreditsBySide
- ChangeUnitOwnership
- ClearBuffsToRemoveEndOfTurn
- ClearWaitPlayFromHandCards
- ConstructAndCopyCard
- CreateCard
- CreateCardObject
- CreateLocationNumberGapForCard
- DirectClientLogger
- DrawTopCardFromDeck
- ExecuteAfterChangeAttackEvents
- ExecuteAttackCard
- ExecuteBeforeReceiveDamage
- ExecuteEndOfTurnQueue
- ExecuteOnAfterAttackEvents
