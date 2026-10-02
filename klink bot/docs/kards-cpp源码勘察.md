# `E:\peoject\kards` C++ 源码勘察报告

勘察日期：本轮会话
勘察对象：`E:\peoject\kards`（1894 MB / 6631 文件）
本报告只做勘察记录，**没有改动 `src/KLink.Bot/` 或 `ref/kards-sim/` 的任何代码**。

---

## ⚠️ 头号结论（先说，因为它推翻任务书前提）

**那 14 个 `.cpp` 里没有任何一行实现体。整份 `Source/` 是「头文件转储 + 空壳 .cpp」，不是可读的原生实现源码。**

任务书说「现在有了 C++ 源码，很多问题可以一次定案」——**这个前提不成立**。
任务书①里那 7 个符号（`HasMovementLeft` / `HasAttackLeft` / `getHasBlitz` /
`CanMoveAndAttackInTheSameTurn` / `movementLeft` / `attackLeft` / `ResetUnitOperations`）
**一个都没有实现体**，全是声明或空壳。详见 §①。

**我们的仓库里早就有这个结论了**，只是没写进 `内核补全队列.md`：

> `klink bot/Plugins/...` 同源文档 `Plugins/KardsChineseDocs/Resources/kards_functions_zh.md:545`
> ```
> 注意：这些 dump 能确认字段、枚举、函数签名、BlueprintPure/BlueprintImplementableEvent 等接口信息；
> 很多蓝图实现函数体在 dump 的 `.cpp` 里是空壳或默认返回，不能把空函数体当作真实逻辑。
> ```

这份 `kards_functions_zh.md`（2515 行 / 167 KB）就是本轮最大的发现，详见 §⑥。

---

## ① 召唤失调的原生实现 —— **查不到，只有声明，没有实现体**

### 结论

**14 个 `.cpp` 里没有这 7 个符号中任何一个的实现体。** 它们分两类：

| 符号 | 声明处 | `.cpp` 里长什么样 | 性质 |
|---|---|---|---|
| `HasMovementLeft` | `BaseCardObject.h:937` | `BaseCardObject.cpp:383` 空 `{}` | 空壳 |
| `HasAttackLeft` | `BaseCardObject.h:964` | `BaseCardObject.cpp:410` 空 `{}` | 空壳 |
| `getHasBlitz` | `BaseCardObject.h:1042` | `BaseCardObject.cpp:492` 空 `{}` | 空壳 |
| `CanMoveAndAttackInTheSameTurn` | `BaseCardObject.h:1141` | `BaseCardObject.cpp:593` 空 `{}` | 空壳 |
| `movementLeft` | `BaseCardObject.h:230` | `BaseCardObject.cpp:49` `= 0`（**这是真实初值**） | **有信息** |
| `attackLeft` | `BaseCardObject.h:233` | `BaseCardObject.cpp:50` `= 0`（**这是真实初值**） | **有信息** |
| `ResetUnitOperations` | `CardFunctionsStub.h:119` | `CardFunctionsStub.cpp` 里**连函数名都没有** | 完全缺失 |

### 出处

**（a）`BaseCardObject.cpp` 全 678 行，唯一的"实体"是构造函数 + 若干 `return` 常量：**

`E:\peoject\kards\Source\kards\Private\BaseCardObject.cpp:383-384`
```cpp
void UBaseCardObject::HasMovementLeft(bool& doesIt) {
}
```

`E:\peoject\kards\Source\kards\Private\BaseCardObject.cpp:410-411`
```cpp
void UBaseCardObject::HasAttackLeft(bool& doesIt) {
}
```

`E:\peoject\kards\Source\kards\Private\BaseCardObject.cpp:492-493`
```cpp
void UBaseCardObject::getHasBlitz(bool& doesIt) {
}
```

`E:\peoject\kards\Source\kards\Private\BaseCardObject.cpp:593-594`
```cpp
void UBaseCardObject::CanMoveAndAttackInTheSameTurn(bool& canIt) {
}
```

我逐行扫过这 678 行：**非空行只有三类** —— (1) `#include`；(2) 构造函数 `UBaseCardObject::UBaseCardObject()`（第 4-104 行，103 条字段初值）；(3) 第 438/451/455/459/556/578 行的 `return FGameplayTagContainer{};` / `return NULL;` / `return false;`（默认返回值）。**没有任何一条 `if` / 赋值 / 函数调用出现在函数体里。**

**（b）`ResetUnitOperations` 在 `.cpp` 里完全不存在：**

`E:\peoject\kards\Source\kards\Private\CardFunctionsStub.cpp` 全 196 行，内容只有：
```cpp
#include "CardFunctionsStub.h"

ACardFunctionsStub::ACardFunctionsStub(const FObjectInitializer& ObjectInitializer) : Super(ObjectInitializer) {
}
// 第 6-196 行全部是空白行
```
`CardFunctionsStub.h` 里 190 个函数声明，`.cpp` 里 **0 个**。

**（c）为什么 `.cpp` 会生成 `_Implementation` 后缀（一个需要解释的反常现象）：**

4 个头文件里的声明是**普通** `BlueprintCallable, BlueprintPure`，**没有** `BlueprintNativeEvent`：
`E:\peoject\kards\Source\kards\Public\BaseCardObject.h:936-937`
```cpp
    UFUNCTION(BlueprintCallable, BlueprintPure)
    void HasMovementLeft(bool& doesIt);
```
但 `.cpp` 里却出 `UBaseCardObject::HasMovementLeft`（无 `_Implementation`），而 `ShouldGotchaTrigger`（`BaseCardObject.h:115` 声明）在 `.cpp:115` 却是 `UBaseCardObject::ShouldGotchaTrigger_Implementation`。**同一份 dump 里两种命名混用，说明生成器的 `_Implementation` 判定不可靠**，不能据此反推原始 `UFUNCTION` 说明符。顺带：`BaseCardObject.h` 里 256 个 `UFUNCTION`，**没有一个是 `BlueprintNativeEvent`**，但 `.cpp` 里有 100+ 个 `_Implementation`；而 `CardFunctionsStub.h` 里 190 个全是 `BlueprintImplementableEvent`，`.cpp` 里 0 个。→ **这份 dump 的 UFUNCTION 说明符整体不可信。**

**（d）更强的证据：UE4SS 配置强制覆写了 Blueprint 权限：**

`E:\peoject\kards\Plugins\KardsChineseDocs\Resources\kards_functions_zh.md:38`
```
`UE4SS-settings.ini` 当前设置了 `MakeAllFunctionsBlueprintCallable=1`、`MakeAllPropertyBlueprintsReadWrite=1`，
因此 UHT 生成的头文件可能包含提取器强制补出的 Blueprint 权限；不要仅凭这些宏判断原始编辑器权限。
```
→ **任何基于 `BlueprintCallable` / `BlueprintPure` / `BlueprintNativeEvent` 宏的推断都不可用。**

**（e）来源与时间戳（排除"这是重制插件生成的"这条路）：**

- `Source/` 全部 52 个文件的 `LastWriteTime` 是 **2026-07-02 20:04 ~ 21:26**；
- `KardsBlueprintRestorer` 的源码 `LastWriteTime` 是 **2026-07-27 ~ 07-30**；
- → `Source/` **不是** KardsBlueprintRestorer 生成的。

### 对内核/工具的直接影响

1. **`内核补全队列.md` §①.9「本轮读不出来的」第 1 条（第 964-971 行，
   标题在第 962 行）的现状描述要更新**：
   它写的是「jmap 只含 `/Script/*` 的原生对象，给的是签名不是实现」——现在可以补一句：
   **`Source/` 那份 C++ 源码同样只有签名，是同一个头文件转储的另一份拷贝，不能用来定案。**
   结论本身（`HasAttackLeft ≡ attackLeft > 0` 是推断、不是证据）**没有被推翻，仍然只是推断**。

2. **本轮新增了一条可用的硬信息**（这是 §① 唯一能拿走的确定结论）：
   `BaseCardObject.cpp:49-50`
   ```cpp
   this->movementLeft = 0;
   this->attackLeft = 0;
   ```
   → 两个字段的 **CDO 初值都是 0**，不是 1。这与 §①.9 记录的「`ResetUnitOperations` 把
   `movementLeft = 1` / `attackLeft = 1`（有 Fury 时 2）」**不矛盾、且互补**：
   即「新生成的卡两个额度都是 0，必须经过一次 `ResetUnitOperations` 才有行动力」。
   这条对内核有解释力（能解释为什么蓝图要在多个时机主动调 `ResetUnitOperations`），
   **但注意它是 CDO 初值，不等于运行时入场初值**——运行时是不是也走 0 起步，本轮仍然读不出来。

3. **`CanMoveAndAttackInTheSameTurn` 那个「原生函数 vs 同名自定义能力」的疑问（§①.9 第 969-971 行）
   仍然未解**。本轮只能确认：它是一个**原生函数**（`BaseCardObject.h:1141` 有声明），
   且 `card_unit_obice_da_75_13` 之类卡的 `CustomName1Add("CanMoveAndAttackInTheSameTurn")`
   是**另一套机制**（`BaseCardObject.h:1123 CustomName1Add` / `:1120 CustomName1HasAttribute`）。
   两者怎么合成，**读不出来**。

### 不确定的

- 这 4 个函数**到底是不是原生 C++ 实现**。间接证据倾向"是"（`.cpp` 里有 `_Implementation` 体，
  且 `CardFunctionsStub.cpp` 里那些纯 `BlueprintImplementableEvent` 函数连声明都没有），
  但如 §①(c) 所述，**这份 dump 的说明符不可信，所以这只是倾向**。
- `attackLeft` / `movementLeft` 在**运行时**入场时被谁初始化成什么值。CDO 是 0，但如果游戏在
  `SpawnCardOnBattlefield` 里就先调了一次 `ResetUnitOperations`，实际入场值就是 1。**读不出来。**

---

## ② `CardFunctionsStub.h` —— 原生 API 的完整清单

### 结论

**`CardFunctionsStub.h` 一共声明 190 个函数，全部是 `BlueprintImplementableEvent`（无返回值，除了出参），实现体在游戏的 `BP_CardFunctions` 蓝图里，不在这份源码里。** 它**确实是**一份权威的接口对照表，可以直接用来做那 3103 个差集的比对。

### 出处

`E:\peoject\kards\Source\kards\Public\CardFunctionsStub.h` 共 600 行。
- 第 22-23 行：`UCLASS(Blueprintable) class ACardFunctionsStub : public AActor {`
- `UFUNCTION` 说明符统计（我用 `Select-String` + `Group-Object` 数的）：
  - `BlueprintCallable, BlueprintImplementableEvent` = **155**
  - `BlueprintCallable, BlueprintImplementableEvent, BlueprintPure` = **35**
  - 合计 **190**，与 `void` 函数声明数 190 完全吻合，**没有第三个变体**。
- 第 28-29 行是第一个：
  ```cpp
      UFUNCTION(BlueprintCallable, BlueprintImplementableEvent)
      void WhichStrategy(EnumCampaignStrategy& Branches);
  ```
- 第 118-119 行（我们最关心的那个）：
  ```cpp
      UFUNCTION(BlueprintCallable, BlueprintImplementableEvent)
      void ResetUnitOperations(int32 CardID, int32 giverID, bool& qqq);
  ```

**我把 190 个函数连同完整参数表和真实行号导出到了 `_stub_fns.txt`**（格式：`行号|函数名|参数表`）。

### 按功能分类（与对局规则相关的，已排除 UI / 视觉 / 音效 / 战役 / 成就）

> **下表所有行号与签名都是机器从 `CardFunctionsStub.h` 逐行抽出来的，不是我手抄的。**
> 完整 190 条见 `_stub_fns.txt`。

**A. 单位状态与行动额度（直接对口召唤失调、攻击次数、移动）**

| 行 | 签名 |
|---|---|
| 119 | `void ResetUnitOperations(int32 CardID, int32 giverID, bool& qqq);` |
| 332 | `void GiveBlitz(int32 CardID, int32 giverID);` |
| 167 | `void RemoveBlitz(int32 CardID, bool& qqq, int32 giverID, bool skipAction);` |
| 185 | `void PinUnit(int32 CardID, int32 instigatorID);` |
| 134 | `void RemovePin(UBaseCardObject* card, int32& qqq);` |
| 557 | `void ChangedPinnedTurns(int32 CardID, int32 instigatorID, int32 turnsToChange, int32& qqq);` |
| 41 | `void TriggerDestruction(UBaseCardObject* card, int32 instigatorID, bool StealSide, bool RemoveDestruction, int32& qqq);` |
| 44 | `void TriggerDeployment(UBaseCardObject* card, int32 instigatorID, int32& qqq);` |

> ⚠️ **`SetSuppressionException` 在 `CardFunctionsStub.h` 里不存在。**
> 我在 `BaseCardObject.h` 里也没找到它。之前若有文档提到这个名字，**那个名字来源不明，别用**。

**B. 攻防与 Buff（`EChangeType` 的调用方全在这里）**

| 行 | 签名 |
|---|---|
| 563 | `void ChangeAttack(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipAction, bool& qqq);` |
| 560 | `void ChangeDefense(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipAction, bool& qqq);` |
| 545 | `void ChangeOperationCost(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool isBuff, bool skipAction, bool skipAddToBattlelog);` |
| 548 | `void ChangeKreditCost(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipCovertCheck, bool& qqq);` |
| 551 | `void ChangeHeavyArmor(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipAction, bool& qqq);` |
| 581 | `void AddKreditsTax(UBaseCardObject* card, int32 costToAdd, int32 instigatorID, bool& qqq);` |
| 590 | `void AddDefenseToMultipleCards(const TArray<int32>& receiverIDs, int32 amount, int32 giverCardID, int32& qqq);` |
| 596 | `void AddAttackUntilEndOfTurn(UBaseCardObject* card, int32 instigatorID, int32 attackToAdd);` |
| 533 | `void DamageCard(UBaseCardObject* card, int32 amount, int32 damagerCardID, bool isRedirected, bool fromFight, bool isFightDefenderDamage, bool& targetDestroyed);` |
| 530 | `void DamageMultipleCards(const TArray<int32>& receiverIDs, int32 amount, int32 damagerCardID, TArray<int32>& cardsDestroyed);` |
| 485 | `void FullyHealCard(UBaseCardObject* card, int32 healerCardID, int32& HealedAmount);` |

> **两处任务书/文档里的签名是错的，以这里为准：**
> - `DamageMultipleCards` **没有** `isRedirected` / `fromFight` 两个 bool，取而代之的是出参
>   `TArray<int32>& cardsDestroyed` —— 它**直接告诉你哪些卡被打死了**，比我们事后自己判有没有用得多。
> - `AddKreditsTax` **没有** `EChangeType` 参数（只有 `costToAdd`），所以
>   §③ 里的 10 个 `EChangeType` 消费方名单要减掉它。

**C. 关键词给予/移除（`ECombatKeyword` 8 值 → 11 组 Give/Remove）**

`ECombatKeyword` 只有 8 个值（`ECombatKeyword.h:6-15`：`None/Ambush/Blitz/Fury/Guard/HeavyArmor/Shock/Smokescreen`），
但 Stub 里的 Give/Remove **比关键词多**（含 `Bond`/`Alpine`/`Salvage`/`Mobilize`/`Immune`，这些不在 `ECombatKeyword` 里）：

| Give | Remove |
|---|---|
| `GiveSmokescreen(302)` `GiveShock(305)` `GiveSalvage(308)` `GiveRandomCombatKeyword(311)` `GiveMobilize(314)` `GiveImmune(320)` `GiveGuard(323)` `GiveFury(326)` `GiveBond(329)` `GiveBlitz(332)` `GiveAmbush(335)` `GiveAlpine(338)` | `RemoveSmokescreen(125)` `RemoveShock(128)` `RemoveSalvage(131)` `RemoveMobilize(140)` `RemoveImmune(143)` `RemoveGuard(146)` `RemoveFury(155)` `RemoveBond(164)` `RemoveBlitz(167)` `RemoveAmbush(170)` `RemoveAlpine(173)` |

> ⚠️ **`GiveAmbush` 是 335，不是 341**（341 是 `GetUnitTypeCountOnBoard`）；
> **`GiveFury` 是 326、`GiveBond` 是 329**（我在初稿里把这三个串位了，已修正）。
> ⚠️ **`GiveAirunitAmbush` / `GiveAirunitAlpine` / `GiveAirunitIntel` 在 `CardFunctionsStub.h` 里不存在**
> —— `Give` 开头的函数只有上表那 13 个（含 `GiveStarForCampaign(299)` / `GiveKreditsBySide(317)`）。
> 我之前是从别处看来的名字，**已删**。
> ⚠️ 只有一个关键词**没有** Remove 配对：`RemoveHeavyArmor` 不存在（重甲用 `ChangeHeavyArmor` 调）。

**D. 回合 / 计数 / 查询（对局规则最有用的一批）**

| 行 | 签名 |
|---|---|
| 353 | `void GetTurnNumber(int32& TurnNumber);` |
| 491 | `void ForceEndTurn();` |
| 497 | `void EndMatch(ESideEnum winnerSide, float Delay);` |
| 380 | `void GetOperationKreditsSpentThisTurn(int32& kreditsSpentThisTurn);` |
| 407 | `void GetDestroyedCardsIDsThisBattle(TArray<int32>& destroyedCardsIDs);` |
| 410 | `void GetDestroyedCardsIDsByTurn(int32 turnID, TArray<int32>& destroyedCardsIDs);` |
| 413 | `void GetDestroyedCardsCountBySide(ESideEnum SideToGet, int32& count);` |
| 425 | `void GetCardsPlayedThisTurn(TArray<UBaseCardObject*>& Cards);` |
| 428 | `void GetCardsPlayedFromHandThisTurn(TArray<int32>& CardIDsPlayedThisTurn);` |
| 431 | `void GetCardsPlayedFromHandLastTurn(TArray<int32>& CardIDsPlayedLastTurn);` |
| 434 | `void GetCardsPlayedFromHandByTurnNumber(int32 TurnNumber, TArray<int32>& CardIDsPlayed);` |
| 350 | `void GetUnitDestroyedThisTurn(bool& UnitDestroyed);` |
| 398 | `void GetHQ_DamagedAmountThisTurnBySide(ESideEnum SideToGet, int32& damagedAmount);` |
| 479 | `void Get_X_AndMoreAttackCardsOnBoard(ESideEnum side, TArray<int32>& cardsIDs, int32 attack, bool includeCovert);` |

**E. 位置 / 移动 / 前线（对口 §①.9 的前线定案）**

| 行 | 签名 |
|---|---|
| 194 | `void MoveUnitFromSupportToFrontLine(UBaseCardObject* card, int32 instigatorID, bool& qqq);` |
| 494 | `void ForceCardChangeLocation(int32 CardID, int32 instigatorID, ECardLocationEnum NewLocation, int32 newLocationNumber, bool& moved, ECardLocationEnum& OldLocation, int32& oldLocationNumber);` |
| 218 | `void MakeCardRetreat(const TArray<UBaseCardObject*>& Cards, int32 instigatorID);` |
| 554 | `void ChangeFrontlineLimiter(int32 limiter, bool Remove, int32& qqq);` |
| 176 | `void ReleaseControlOfEnemyUnit(UBaseCardObject* card, int32 instigatorID, ECardLocationEnum originalLocation, ESideEnum originalSide);` |
| 47 | `void TakeControlOfEnemyUnit(UBaseCardObject* card, int32 instigatorID);` |
| 56 | `void StealCardFromBoardToDeck(int32 CardID, int32 instigatorID, ESideEnum deckSide, int32& qqq);` |
| 182 | `void PlayCardDirectlyFromHand(UBaseCardObject* card, bool toFrontline, int32 instigatorID, int32& qqq, int32 locationNumber);` |

> **⚠️ 两条与 §①.9 有关的签名更正（会直接影响已有结论的适用范围）：**
> 1. **`MoveUnitFromSupportToFrontLine` 只有 `(card, instigatorID, bool& qqq)` 三个参数，
>    没有 `force` 参数。** §①.9「本轮读不出来的」第 5 条（第 979-982 行）讨论的
>    `MoveCardToFrontline` 的第 7 个实参 / `force` / `PayMovementCost` ——
>    在**这个版本的公开 Stub 里 `force` 已经不在签名上了**。
>    `MoveUnitFromSupportToFrontLine(...)` 的调用方现在看不到 force 这个旋钮。
>    **这条要复核**：可能公开接口被简化过，也可能 §①.9 读的是另一个版本的资产。
> 2. **`ChangeFrontlineLimiter(int32 limiter, bool Remove, int32& qqq)` 的第一个参数是
>    `int32 limiter`，不是 `UBaseCardObject* card`。**
>    §①.9 定案 ①a 说「`FrontlineLimiters` 是 `Set<int>`（限制者卡 ID 集合）」，
>    这里 `int32 limiter` 正好是**卡 ID**，**与定案一致，是加强而不是推翻**。
>    但 §①.9 用的函数名是 `IncreaseFrontlineLimiter`，而 Stub 里叫 `ChangeFrontlineLimiter(554)`
>    —— **两个名字的版本差异要留意**。

**F. 限制 / 副作用（对口 `EGameplayRestrictions`）**

| 行 | 签名 |
|---|---|
| 587 | `void AddGameplayRestriction(ESideEnum side, EGameplayRestrictions Type, int32 CardID, int32 turnsToLast);` |
| 152 | `void RemoveGameplayRestriction(ESideEnum side, EGameplayRestrictions Type, int32 CardID, bool RemoveAll, int32& qqq);` |
| 566 | `void ApplyGameplaySideEffect(ESideEnum SideToApply, FGameplayTag EffectTag, UBaseCardObject* InstigatorCard, EDurationPolicy DurationPolicy, int32 turnsToLast, int32& qqq);` |
| 149 | `void RemoveGameplaySideEffect(ESideEnum SideToRemove, FGameplayTag EffectTag, UBaseCardObject* InstigatorCard, int32& qqq);` |

> ⚠️ **`AddGameplaySideEffect` 不存在，只有 `ApplyGameplaySideEffect`。**
> `EDurationPolicy`（`EDurationPolicy.h:6-12`：`None/Instant/Duration/Ongoing/Static`）+
> `turnsToLast` 是它的时长参数 —— 这正是 `GameplayEffect.h:20-27` 里
> `DurationPolicy` / `RemainingTurns` 的来源（见 §③ 补充）。

**G. 卡牌自定义 JSON（22 个 `JSON_*`）** —— 这是**我们自己设计 buff 结构时最该参考的**：
`JSON_SetStringArray/SetString/SetObject/SetInt/SetFloat/SetBool/SetBoolArray`、
`JSON_Get*`、`JSON_RemoveKey/RemoveFromStringArray/RemoveFromIntArray/RemoveFromBoolArray/RemoveFromFloatArray`、
`JSON_AppendToIntArray/AppendToBoolArray/AppendToFloatArray/AppendToStringArray`、`JSON_Merge`。
统一形如 `(UBaseCardObject* card, const FString& VariableName, ..., bool CreateIfMissing, bool skipAction)`。

**H. 与规则无关（可直接排除）：** 生成/抽牌类里带视觉参数的、`ShowNotification`、`AddToBattleLog`、
`SetObjectiveCounter`、`UpdateCampaignStarStatus`、`WhichStrategy`、`WasLeftMostCardWhenPlayedFromHand`、
`EndMatch`、`ReportTampering`、`AddNumberToText`、`PersistCustomFields`。

### 对内核/工具的直接影响

1. **这 190 个名字可以直接当「基础 API 白名单」用**：凡是在卡牌资产里出现、又落在这 190 个里的调用，
   都属于"引擎给定能力，不是卡自定义逻辑"。**建议把 `_stub_fns.txt` 收进工具链**，
   在算 3103 差集时先减掉这 190 个，剩下的才是真正"我们不认识"的卡内私有函数。
2. **注意这 190 个不能当"运行时一定可用"**：同源文档明确警告
   （`kards_functions_zh.md:68`）：
   > 如果函数只在 Stub 中出现、而当前 `BP_CardFunctions_C` 派生类没有对应覆盖声明，
   > 不能把它当成已验证可用。
   例：文档点名 `SpawnCardOnTopOfDeck` 与 `SpawnCardInDeckBySide` 的区别来源就是这个。
3. **`ResetUnitOperations` 的调用方不在 `BP_Logic` 里**（本轮新查实）：
   我把 `BP_Logic` 全量反汇编（181 个 export）后搜 `ResetUnitOperations` —— **0 命中**。
   只有接口 notifier 里有：
   `E:\bpasm\notifier.bpasm:1486`（源资产 `live/Logic/.../U_CardFunctionsNotifier.uasset`）
   ```
   .export 76 "NotifyResetUnitOperations" {
       context { interfacecontext { instancevariable @path(owner=1) "OnlineMatchReceiver" } }
       32 @path(owner=null) {
           localvirtualfunction name "HandleResetUnitOperations" {
               localvariable @path(owner=77) "CardID"
               localvariable @path(owner=77) "InstigatorID"
               endfunctionparms
           }
       }
   ```
   → **佐证 §①.9 的判断**：「什么时候重置」是服务端权威，客户端侧只有转发的壳。

### 不确定的

- 这 190 个是不是**全部**原生 API。文档 `kards_functions_zh.md:23` 提到
  `UHTHeaderDump\kards\Public` 有 **364 个头文件 / 994 个 `UFUNCTION`** ——
  也就是 `CardFunctionsStub.h` 只是 `ACardFunctionsStub` 这一个类的接口，
  另外 **804 个 UFUNCTION 分布在别的类里**（`BaseCardObject` 256 个、
  `BP_GameState_Battle`、`CombatHelperFunctions` 等）。**要凑齐"权威对照表"，只靠这一份不够。**
- 本轮**没有**去 `C:\Apps\game\kards\Binaries\Win64\ue4ss`（文档第 12 行给的路径）核对
  那 364 个头文件 —— 本轮工作目录在 `E:\`，跨盘去读别人的游戏目录我没有做。
  **如果要把 3103 差集一次做干净，那是必须去的地方。**

---

## ③ `EChangeType.h` + `CardBuffData.h`

### 结论

**`EChangeType` 是 10 个值的 `uint8` 枚举，是 `ChangeAttack/ChangeDefense/ChangeHeavyArmor/ChangeKreditCost/ChangeOperationCost/ChangeBuffsFromCards` 共用的"修改方式"参数。`CardBuffData` 只有 2 个字段** —— 我们之前猜的 buff 结构比它复杂得多。

**`EChangeType` 的消费方（`Select-String 'EChangeType'` 全量扫过，共 5 个）**：

| 行 | 签名 |
|---|---|
| 545 | `void ChangeOperationCost(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool isBuff, bool skipAction, bool skipAddToBattlelog);` |
| 548 | `void ChangeKreditCost(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipCovertCheck, bool& qqq);` |
| 551 | `void ChangeHeavyArmor(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipAction, bool& qqq);` |
| 560 | `void ChangeDefense(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipAction, bool& qqq);` |
| 563 | `void ChangeAttack(UBaseCardObject* card, int32 instigatorID, int32 amount, EChangeType ChangeType, bool skipAction, bool& qqq);` |

（`CardFunctionsStub.h:11` 是 `#include "EChangeType.h"`。）

> **两个需要更正的名单错误（我初稿里写错了，已改）：**
> - **`ChangeBuffsFromCards` 不在公开 `CardFunctionsStub.h` 里**（`Select-String` 命中 0 次）。
>   它是 cooked `BP_CardFunctions_C` 的内部函数，中文文档 `kards_functions_zh.md:863` 明确说了这一点：
>   > 但该函数没有声明在公开的 `CardFunctionsStub.h`，所以它与 `GetActiveGotchasOrdered` 一样
>   > 属于 cooked `BP_CardFunctions_C` 的内部实现，不是稳定公开的 Stub 接口。
> - **`AddKreditsTax(581)` 没有 `EChangeType` 参数**（它签名是 `(card, costToAdd, instigatorID, bool& qqq)`），
>   别把它算进 `EChangeType` 的消费方。

### 出处

`E:\peoject\kards\Source\kards\Public\EChangeType.h:5-17`（全文）
```cpp
UENUM(BlueprintType)
enum class EChangeType : uint8 {
    tempBuffGive,
    permBuff,
    SetValue,
    Suppress,
    tempBuffRemove,
    veteranSet,
    customAdd,
    customRemove,
    combatModify,
    notUsed,
};
```

`E:\peoject\kards\Source\kards\Public\CardBuffData.h:5-16`（全文）
```cpp
USTRUCT(BlueprintType)
struct FCardBuffData {
    GENERATED_BODY()
public:
    UPROPERTY(BlueprintReadWrite, EditAnywhere, meta=(AllowPrivateAccess=true))
    FText cardName;

    UPROPERTY(BlueprintReadWrite, EditAnywhere, meta=(AllowPrivateAccess=true))
    TMap<FString, int32> BuffMap;

    KARDS_API FCardBuffData();
};
```

**`FCardBuffData` 挂在卡上的位置**：`BaseCardObject.h:100-101`
```cpp
    UPROPERTY(BlueprintReadWrite, EditAnywhere, meta=(AllowPrivateAccess=true))
    TMap<int32, FCardBuffData> buffsFromCards;
```
→ 外层 key 是 **`int32`（来源卡的 `CardID`）**，内层是 `TMap<FString, int32>`（buff 名字 → 数值）。
配对的字段还有 `BaseCardObject.h:103-104`
```cpp
    TArray<int32> cardsBuffedByThisCard;
```

### 对内核/工具的直接影响

1. **`EChangeType` 的字节值可以直接定案**（`uint8` 顺序即 0..9）：
   `tempBuffGive=0` `permBuff=1` `SetValue=2` `Suppress=3` `tempBuffRemove=4`
   `veteranSet=5` `customAdd=6` `customRemove=7` `combatModify=8` `notUsed=9`。
   我们的蓝图解码器在解 `ChangeAttack(..., changeType)` 的 **`byteconst N`** 时，
   **可以按这张表直接翻译，不用再猜**。这一条是本次最可直接落地的收获。

2. **本轮的语义说明只到"枚举名"这一层。** 我只在中文文档里找到 pin 级说明
   （`kards_functions_zh.md:823-847`：`ChangeAttack` / `ChangeDefense` 的
   `ChangeType ▶ byte 修改类型(枚举)`），**没有找到"`tempBuffGive` 与 `permBuff` 行为差在哪"
   这种级别的解释**。`ChangeBuffsFromCards` 那段（`:849-863`）提到了
   `buffType=custom` + `instigatorID` + `customDetail` 的组合，但那是 `ECardBuffTypes`，不是 `EChangeType`。

3. **我们的 buff 结构可能设计得过重**：游戏自身用的是
   `buffsFromCards: Map<CardID, {cardName: FText, BuffMap: Map<string,int32>}>` ——
   **一个来源一个 key、buff 名是字符串、值只有一个 int32**。
   如果内核里为每个 buff 存了 duration / policy / 多层结构，那和源头结构对不上。

### 补充：本轮新读到的两个效果结构体（我们之前没建模过）

`E:\peoject\kards\Source\kards\Public\GameplayEffect.h:10-30`（全文）
```cpp
USTRUCT(BlueprintType)
struct FGameplayEffect {
    GENERATED_BODY()
public:
    UPROPERTY(...) FGameplayTag EffectTag;
    UPROPERTY(...) UBaseCardObject* InstigatorCard;
    UPROPERTY(...) EDurationPolicy DurationPolicy;
    UPROPERTY(...) int32 RemainingTurns;
    UPROPERTY(...) int32 EffectValue;
    KARDS_API FGameplayEffect();
};
```

`E:\peoject\kards\Source\kards\Public\GameplayRestrictionEffect.h:7-26`（全文）
```cpp
USTRUCT(BlueprintType)
struct FGameplayRestrictionEffect {
    GENERATED_BODY()
public:
    UPROPERTY(...) ESideEnum AffectedSide;
    UPROPERTY(...) EGameplayRestrictions RestrictionType;
    UPROPERTY(...) int32 CardID;
    UPROPERTY(...) int32 TurnsRemaining;
    KARDS_API FGameplayRestrictionEffect();
};
```

配套枚举 `E:\peoject\kards\Source\kards\Public\EDurationPolicy.h:5-12`（全文）
```cpp
UENUM(BlueprintType)
enum class EDurationPolicy : uint8 {
    None,       // 0
    Instant,    // 1
    Duration,   // 2
    Ongoing,    // 3
    Static,     // 4
};
```

**`EGameplayRestrictions` 也是权威定义（`EGameplayRestrictions.h:5-14` 全文）：**
```cpp
UENUM(BlueprintType)
enum class EGameplayRestrictions : uint8 {
    cannotDrawCardAtTurnStart,        // 0
    cannotKreditSlotAtTurnStart,      // 1
    cannotPlayOrders,                 // 2
    cannotDeployUnits,                // 3
    cannotAttackWithGroundUnits,      // 4  ← ★ 内核已用到
    cannotDiscardAnyCardFromHand,     // 5
    NotAvailable,                     // 6
};
```
→ **§①.9 定案里 `CanAttack` 第 6 条用的 `IsThereGameplayRestriction(myRealSide, 4)`
= `cannotAttackWithGroundUnits`，数值 4 确认无误。** 这条是"确认"，不是"纠正"。

**`FGameplayEffect` 是"按 tag 挂的持续效果"，和我们 `GameplayTagTable.cs` 的定位可能重叠。**
它的 key 是 `FGameplayTag EffectTag`（字符串标签），不是整数 id，
时长是 `(EDurationPolicy, RemainingTurns)` 二元组 ——
**`Ongoing`(3) 和 `Static`(4) 的区别本轮没有证据**，别自己发明。

**`FGameplayRestrictionEffect` 的 `CardID` 是"给出这条限制的卡"**
（配 `AddGameplayRestriction(..., int32 CardID, int32 turnsToLast)` 的参数），
所以限制**可以按来源卡移除** —— `RemoveGameplayRestriction(..., bool RemoveAll, ...)`
的 `RemoveAll` 就是"移除全部来源"。

### 不确定的（③）

- **`EChangeType` 各值的精确语义，本轮没查到。** 本轮只拿到枚举名顺序 + 它是哪 5 个函数的参数。
  想知道 `tempBuffGive` 到底写哪个字段、`SetValue` 是覆盖基础值还是覆盖总值，
  **必须去读 `BP_CardFunctions::ChangeAttack` 的字节码**（那个资产的 uasset/uexp
  **不在本轮勘察范围内**，但见 §⑥ 的工具建议 —— `bpasm` 可以读）。
- `ECardBuffTypes` 本轮**没有找到定义文件**（`Source/` 里没有这个头）。
  它只在中文文档 `kards_functions_zh.md:856` 作为参数类型出现（`ChangeBuffsFromCards` 的 `buffType`），
  文件里标注的类型是 `byte / ECardBuffTypes`。
  → **这是一个 `Source/` 没覆盖到的原生枚举**，反证 §② 的"190 个不是全部"。
- `FGameplayEffect` / `FGameplayRestrictionEffect` 在 `BaseCardObject.h` 里**有没有对应字段**，
  本轮**没有逐个核对**（只读了这两个结构体本身的头文件）。

---

## ④ `ECardLocationEnum.h` + `ChooseOneCardStruct.h`

### 结论

**location 枚举权威定义拿到了，数值和我们的量测完全一致：`Board_HQLeft=5` / `Board_HQRight=6` / `Board_Frontline=7`。但**多出两个我们可能没用到的值：`Deck_Left=1` / `Deck_Right=2`。**
`ChooseOneCardStruct` **只有 2 个字段**，没有"三个选项"的数组 —— 三选一的分支结构不在这里。

### 出处

`E:\peoject\kards\Source\kards\Public\ECardLocationEnum.h:5-17`（全文）
```cpp
UENUM(BlueprintType)
enum class ECardLocationEnum : uint8 {
    NotAvailable,      // 0
    Deck_Left,         // 1
    Deck_Right,        // 2
    Hand_Left,         // 3
    Hand_Right,        // 4
    Board_HQLeft,      // 5
    Board_HQRight,     // 6
    Board_Frontline,   // 7
    Discard,           // 8
    Deck,              // 9
};
```

`E:\peoject\kards\Source\kards\Public\ChooseOneCardStruct.h:5-16`（全文）
```cpp
USTRUCT(BlueprintType)
struct FChooseOneCardStruct {
    GENERATED_BODY()
public:
    UPROPERTY(BlueprintReadWrite, EditAnywhere, meta=(AllowPrivateAccess=true))
    FText cardText;

    UPROPERTY(BlueprintReadWrite, EditAnywhere, meta=(AllowPrivateAccess=true))
    bool selectTargetOnPlayedFromHand;

    KARDS_API FChooseOneCardStruct();
};
```

**配套 enum**：`E:\peoject\kards\Source\kards\Public\EnumChooseOneCardBeingPlayed.h:5-9`
```cpp
UENUM(BlueprintType)
enum class EnumChooseOneCardBeingPlayed : uint8 {
    Card_0,
    Card_1,
};
```

**配套字段**：`BaseCardObject.h:298-299`
```cpp
    UPROPERTY(BlueprintReadWrite, EditAnywhere, meta=(AllowPrivateAccess=true))
    int32 chooseOneIndex;
```
（`BaseCardObject.cpp:70` 构造初值 `this->chooseOneIndex = 0;`）

### 对内核/工具的直接影响

1. **location 5/6/7 的判定确认无误**，内核现有 `Location.IsBoard()` / 前线判断的依据成立。这条是"确认"，不是"纠正"。
2. **`EnumChooseOneCardBeingPlayed` 只有 `Card_0` / `Card_1` 两个值 —— 是"二选一"，不是"三选一"。**
   这一点值得注意：任务书把 `ChooseOneCardStruct` 和"三选一"挂钩，但**枚举本身只支持 2 个分支**。
   要和 §①.11 的结论（三选一的分支在 `PC` / `chooseOneIndex` 里）对上，
   `chooseOneIndex` 是 `int32`（可以 > 1），而 `EnumChooseOneCardBeingPlayed` 是 `uint8` 且只有 2 值 ——
   **这两个是不同的东西，不要混。**
3. **`FChooseOneCardStruct` 没有"选项文本数组"**：它只有一个 `cardText: FText`。
   → 说明每个抉择分支是**一张独立的卡资产**，而不是一张卡里存三个字符串。
   与 `chooseOneIndex`("我选了第几个") + `EnumChooseOneCardBeingPlayed`("正在打出第几个")
   的分工是一致的。
4. **`Deck_Left(1)` / `Deck_Right(2)` 与 `Deck(9)` 并存**，需要留个心：
   我们若把 `1/2/9` 都当"卡组"，可能有行为差异（`1/2` 更像"牌库选牌界面"的位置）。
   **本轮没有证据说明它们的行为差别。**

### 不确定的

- `Deck_Left` / `Deck_Right` / `Deck` 三者的运行时区别，本轮**没有证据**。
- `ChooseOneCardStruct` 数组存在哪个字段上，**本轮没找到**。
  `BaseCardObject.h` 里我搜到 `chooseOneIndex`（第 299 行），但**没搜到 `TArray<FChooseOneCardStruct>` 字段**。
  它在 `ChooseOneCardStruct.h` 里被定义，也在 `BaseCardObject.h:22` 被 `#include`，
  **但 `BaseCardObject.h` 里没有用它声明任何成员** —— 所以它挂在哪张表/哪个资产上，本轮查不到。

---

## ⑤ `Source/BlueprintJson/` —— 是个空壳，不是导出工具

### 结论

**它什么也不做。整个插件只有 4 个文件、合计 891 字节：一个空结构体 `FBlueprintJsonObject`、一个 `IMPLEMENT_MODULE`、一个 `Build.cs`。没有导出逻辑、没有 JSON 序列化、没有产物。**

### 出处

`E:\peoject\kards\Source\BlueprintJson\Public\BlueprintJsonObject.h`（全文 11 行）
```cpp
#pragma once
#include "CoreMinimal.h"
#include "BlueprintJsonObject.generated.h"

USTRUCT(BlueprintType)
struct FBlueprintJsonObject {
    GENERATED_BODY()
public:
    BLUEPRINTJSON_API FBlueprintJsonObject();
};
```

`E:\peoject\kards\Source\BlueprintJson\Private\BlueprintJsonObject.cpp`（全文 5 行）
```cpp
#include "BlueprintJsonObject.h"

FBlueprintJsonObject::FBlueprintJsonObject() {
}
```

`E:\peoject\kards\Source\BlueprintJson\Private\BlueprintJsonModule.cpp`（全文 3 行）
```cpp
#include "Modules/ModuleManager.h"

IMPLEMENT_MODULE(FDefaultGameModuleImpl, BlueprintJson);
```

`E:\peoject\kards\Source\BlueprintJson\BlueprintJson.Build.cs:11-15` 依赖只有
`Core` / `CoreUObject` / `Engine`，**没有 `Json` / `JsonUtilities`** —— 进一步证明它不碰 JSON。

**注意它被谁用了**：`BaseCardObject.h:410`
```cpp
    UPROPERTY(BlueprintReadWrite, EditAnywhere, meta=(AllowPrivateAccess=true))
    FBlueprintJsonObject customJson;
```
→ **`customJson` 的真实类型是 `FBlueprintJsonObject`，而它是个空结构体。**
也就是说：**我们如果把 `customJson` 当"任意 JSON 字典"来建模，方向是对的**（因为游戏侧的 `JSON_*`
系列函数就是往里塞 key/value），但**空结构体意味着类型信息在这里完全丢失** ——
`E:\peoject\kards\Source\kards\Private\BaseCardObject.cpp` 里也**没有 `FBlueprintJsonObject` 的实现**，
只在这个 .cpp 里有构造函数。

### 对内核/工具的直接影响

1. **`BlueprintJson` 这条路对"卡内私有函数没进 IR"的缺口 0 帮助。**
   我们仓库里的 `Effects/Blueprint/KismetIr.cs` + `KismetVm.cs` 是自己在做，
   **不要指望这个插件**。
2. **真正能补这个缺口的东西在别处（本轮新发现，见 §⑥）：**
   - `E:\peoject\kards\Plugins\KardsBlueprintRestorer\`（UE 5.6 编辑器插件，**能反过来**：把 FModel/CUE4Parse 的 JSON 还原成可编辑蓝图）
   - `E:\bpasm\bpasm.exe`（**蓝图字节码汇编/反汇编器**，本报告验证可用）
3. **`customJson` 的建模可以放心**：因为游戏侧 `JSON_*` 22 个函数就是 `(card, VariableName, Value, CreateIfMissing, skipAction)`，
   等价于一个"字符串 key → 任意类型"的字段袋。内核把它建成 `Dictionary<string, object>` 是合理的。

### 不确定的

- 为什么 `FBlueprintJsonObject` 是空的。可能：dump 时成员被裁掉；或者它真的只是个
  UPROPERTY 载体（`customJson` 的容器类型）而所有逻辑都在蓝图的 `JSON_*` 函数里。**本轮无法判定。**

---

## ⑥ 其它

### 6.1 `Tools/` —— 是"改包"工具，不是"读蓝图"工具

`E:\peoject\kards\Tools\pakcook\`：
| 文件 | 大小 | 说明 |
|---|---|---|
| `pakcook.exe` | 3.47 MB | 打包器 |
| `UnrealPak.exe` | 6.10 MB | UE 自带 |
| `mod_P.pak` | 3.02 MB | 产物 |
| `mod_P_backup_0810-0025.pak` | 8.28 MB | 备份 |
| `_filelist.txt` | 18.5 KB | UnrealPak 的文件清单（`"<绝对路径>" "../../../kards/Content/..."` 两列格式） |
| `settings.json` | 615 B | 见下 |

`E:\peoject\kards\Tools\pakcook\settings.json`（全文）
```json
{
	"aeskey": "C257932734957B6D467FA16FCF728D5A16A5BDD07F6E20EE6E3155E898305CC0",
	"unrealpak": "E:/Epic Games/UE_5.6/Engine/Binaries/Win64/UnrealPak.exe",
	"gameName": "kards",
	"contentRoot": "E:/peoject/kards/Saved/Cooked/Windows/kards",
	"copyTo": "E:/klink/kds/kards/Content/Paks",
	"encrypt": false,
	"packAssetRegistry": true,
	"packConfig": true,
	"configRoot": "E:/peoject/kards/Config",
	"excludeFiles": [ "verticalKardsButtonWithText_Widget", "Battle_Settings_Widget",
	                  "BP_EntryPointBaseComponent", "BP_EntryPointActor", "card_event_tfsh",
	                  "BP_CardFunctions", "U_CardFunctionsNotifier" ]
}
```
→ **AES key 明文在这里。** 对内核没直接用处，但记住：`BP_CardFunctions` 和
`U_CardFunctionsNotifier` 在 `excludeFiles` 里，说明这是**做 mod 的工程**，
不是为规则验证准备的。

### 6.2 引擎版本 —— **本轮没在 `Config/DefaultEngine.ini` 里找到**

`E:\peoject\kards\Config\` 只有 4 个文件：`DefaultEditor.ini`(0 B)、`DefaultEngine.ini`(9.18 KB)、
`DefaultGame.ini`(1.37 KB)、`DefaultInput.ini`(9.03 KB)。**`DefaultEditor.ini` 是 0 字节。**
我用 `Select-String -Pattern 'EngineVersion|BuildId|BranchName|CompatibleChangelist'`
扫了 `Config\*.ini`，**0 命中**。

**但引擎版本可以从别处确定，而且是确定的：**
- `E:\peoject\kards\Plugins\KardsBlueprintRestorer\README.md:3`
  > Editor-only **UE5.6** plugin for rebuilding editable KARDS card Blueprints from FModel/CUE4Parse JSON.
- `E:\peoject\kards\Plugins\AssetRegistryTool\README.md:3`
  > **UE 5.6.1** editor plugin for building a new `AssetRegistry.bin` ...
- `Tools/pakcook/settings.json` 的 `unrealpak` 指向 `E:/Epic Games/UE_5.6/...`
- 我们仓库里有两个 usmap：`klink bot/kards-5.6.1-0+++UE5+Release-5.6-Fork-unknown.jmap`
  和 `klink bot/kards1.60_No_UE4SS.jmap`

→ **引擎 = UE 5.6.1**，游戏版本字符串 `5.6.1-0+++UE5+Release-5.6-Fork`。

### 6.3 `.mcp.json` / `.reasonix` —— 是"用 Python 遥控 UE 编辑器"的工作区

`E:\peoject\kards\.mcp.json`（全文 13 行）
```json
{
  "mcpServers": {
    "unreal-mcpython": {
      "command": "<user-home>\\AppData\\Local\\Programs\\uv\\uv.exe",
      "args": [ "--directory", "E:/peoject/kards/.reasonix/unreal-mcp/mcp-server",
                "run", "src/unreal_mcp/main.py" ]
    }
  }
}
```

`.reasonix/`（2410 文件 / 35.5 MB）三层：
1. **一堆一次性小脚本 + 查表产物**：`parse_pak.py`、`cardid_lookup.{py,txt}`、
   `kredits_lookup.{py,txt}`、`repeat_lookup.{py,txt}`、`extra_lookup.{py,txt}`、
   `make_row.py`、`make_mini_reg.py`、`prep_csv.py`、`append_row.py`、`xiangtai_row.json`。
   从 `parse_pak.py` 里硬编码的 `pak_path = r"E:\klink\kds\kards\Content\Paks\mod_P.pak"`
   可以看出这是**做 DIY 卡 mod 时的临时脚本区**。
2. **`unreal-mcp/`**：`mcp-server/`（Python MCP 服务端 + `_catalog.py` 49 KB 工具目录）
   + `repo/`（一个 git clone 的 UnrealMCPython 上游）。
3. `fix_ports.ps1` / `move_dynamic_ports.ps1` / `restart_winnat.ps1` —— 网络端口修复脚本。

**配套的插件是 `Plugins/UnrealMCPython/`**（TCP server，28 KB C++ + 24 个 Python action 模块
`blueprint_actions.py` / `gas_actions.py` / `game_actions.py` / `data_table_actions.py` 等）。
→ **这是一套「让 AI 直接在 UE 编辑器里改蓝图」的完整闭环**，跟我们的规则内核**不是一条路**。

### 6.4 `Content/` 的 62 个 `.uasset` —— 是 **DIY mod 的产物**，不是游戏本体

按路径分三类：
1. **DIY 卡（约占一半）**：`Content/Blueprints/Cards/Britain/DIY/`、`Germany/DIY/`、
   `Japan/DIY/`、`USA/DIY/`、`Soviet/DIY/`，以及 `Britain/Homefront/`、`OceaniaStorm/` 等。
   文件几十~几百 KB（`card_event_forward_observers.uasset` 238 KB 最大）。
2. **DIY 卡用的美术/特效**：`Content/Assets/Textures/Images/Japan/DIY/t_aichi.uasset`(496 KB)、
   `Content/Assets/Textures/Images/China/T_blast.uasset`(1.44 MB)、`bigdog`/`jiao` Niagara 特效、
   `Content/Effects/Niagara/...`。
3. **mod 框架本体**：`Content/Blueprints/XMod/`（`AA_ModBase`、`AC_Mod_Main`、`DT_ModRegistry`、
   `TT_Main/` 科技树 `WBP_Tech*`）、`Content/Blueprints/Cards/BP_CardFunctions.uasset`(28 KB)、
   `Content/Blueprints/Logic/U_CardFunctionsNotifier.uasset`(15.7 KB)。
4. **数据表**：`Content/Structs/deckCodeIDsTable2.uasset`(421 KB)、
   `Content/Structs/DT_CardImages.uasset`(960 KB)、`Content/Structs/Localization/T_buffTextLocalize.uasset`。

**判断依据**：`temp/2/Content/` 是同一批文件的更大快照，里面有
`card_unit_ho229.uasset.codex-backup-20260725-1317` 等 **带日期后缀的备份**（6 个），
还有 `temp/our_modified_card_unit_aichi.uasset`、`temp/original_card_backup.uasset`、
`temp/our_broken_card.uasset`。→ **这是做 DIY 卡的工作目录，不是我们的规则验证素材。**

**`Content/Blueprints/Logic/U_CardFunctionsNotifier.uasset` 是唯一和我们有关的**：
它就是我们仓库 `live/Logic/.../U_CardFunctionsNotifier.uasset` 的**另一个版本**
（`Content/` 的是 15.7 KB，`live/` 的是 24.5 KB —— **不是同一个版本，注意别混用**）。

### 6.5 `Binaries/` —— 只有我们自己编的 3 个 DLL，没有游戏本体

`E:\peoject\kards\Binaries\Win64\`：
`UnrealEditor-BlueprintJson.dll`(58 KB) / `UnrealEditor-kards.dll`(625 KB) /
`UnrealEditor-KardsCore.dll`(60 KB) + 三个 58~62 MB 的 `.pdb` + `kardsEditor.target` + `UnrealEditor.modules`。
→ **是这份工程自己编译的产物**。因为没有对应的 `.cpp` 实现体（§①），
**这些 DLL 里也不会有原生规则逻辑** —— 它们只是 UHT 生成的反射桩。
（旁证：`UnrealEditor-KardsCore.dll` 只有 60 KB，`KardsCore` 模块里只有 4 个枚举头 + 一个空模块 cpp。）

### 6.6 `Saved/`（888.9 MB）—— 大量崩溃报告，`Cooked/` 有可用资产

- `Saved/Config/CrashReportClient/` 下有 **200+ 个 `UECC-Windows-*` 崩溃目录** —— 做 mod 时反复崩过。
- `Saved/Cooked/Windows/kards/Content/` 有 **128 个文件**，包含上面那 62 个 uasset 的 **cooked 版本（.uasset + .uexp）**，
  外加 4 个 GLSL/HLSL shader archive（144 MB 的 `ShaderArchive-Global-PCD3D_SM6`）。
  我们的 `live/` 目录里的资产就是从这类 cooked 包里抽出来的。
- 6 个 json：全是 `ShaderAssetInfo-*.assetinfo.json` / `ShaderTypeInfo-*.stinfo`，**与规则无关**。

### 6.7 ★★★ 本轮真正的金矿：`Plugins/KardsChineseDocs/Resources/`

这个插件（`KardsChineseDocs.uplugin`，附 31 MB DLL）在 `Resources/` 下带出 **4 份 Markdown，合计 283 KB**：

| 文件 | 大小 | 内容 |
|---|---|---|
| **`kards_functions_zh.md`** | **167 KB / 2515 行** | **`CardFunctionsStub` 全函数中英对照 + 每个函数的 pin 级说明 + 枚举表 + BaseCardObject 字段表 + 副作用/限制/关键词/查询全分类** |
| `kards_card_making_summary.md` | 70 KB | 做卡总结 |
| `kards_card_art_vfx_attack_audio_tutorial.md` | 23 KB | 美术/特效（无关） |
| `kards_card_deployment_attack_destruction_audio_vfx_guide.md` | 22.5 KB | 同上（无关） |

`kards_functions_zh.md` 的目录结构（节选，行号是文件内真实行号）：
```
   1  # KARDS CardFunctionsStub 中英对照表（含引脚翻译）
   7  ## 📦 UE4SS 提取文件的用法和证据等级      ← ★ 证据分级表
  42  ## 🧱 KARDS 卡牌逻辑的类分层              ← ★ UBaseCardObject / ACardFunctionsStub / BP_GameState_Battle
 531  ## 🧭 常用枚举/数据含义
 543  ## 🧭 Side / Client Side / Opposite Side / My Side 详细辨析
 821  ## ⚔️ 攻击 / 防御 / 战力                   ← ChangeAttack 引脚说明
1041  ## 🔒 限制 / 副作用                        ← EGameplayRestrictions 逐值
1107  ## 🪖 抑制 / 锁定 / 老兵                   ← SuppressUnit / PinUnit / ResetUnitOperations
1186  ## 🃏 手牌 / 弃牌
1239  ## 🎯 战场 / 前线
1338  ## 🔄 回合 / 对局
1356  ## 🪖 控制权
1387  ## 📋 查询 / 通知 / 战役
1501  ## 🧾 卡牌自定义 JSON (JSON_*)             ← JSON_* 的"逻辑模型"/"什么时候别用"
1615  ## 🧩 BaseCardObject CDO 默认值怎么填        ← ★ 字段级 CDO 填写规范
1823  ### 运行时状态：一般不要在 CDO 手填           ← ★ 明确列出哪些是运行时字段
1886  ## 🔍 判断函数 (Is* / Can* / Has*) — BaseCardObject
1975  ## 📊 数值查询 (Get*) — BaseCardObject
2135  ## 📊 游戏状态查询 (Get*) — CardFunctionsStub
2398  ## 🪝 伤害追加覆盖函数 — BaseCardObject
2438  ## ⚡ 蓝图事件 (Event) — BaseCardObject
```

**其中三条对我们直接有用的原文：**

`kards_functions_zh.md:17-26`（证据分级表 —— 这解释了我们所有"读不出来"的根源）
```
| 目录/文件 | 本次扫描结果 | 能确认什么 | 不能直接确认什么 |
| `CXXHeaderDump` | 3,166 个 `.hpp` | Blueprint 生成类的继承关系、字段布局、方法声明 | 函数体、蓝图节点实际执行顺序 |
| `UHTHeaderDump\kards\Public` | 364 个头文件、994 个 `UFUNCTION` | 原始签名、参数类型和 BlueprintPure/NativeEvent/ImplementableEvent 标记 | 具体资产图表是否接线 |
| `IndividualObjectDumps` | 3 个 JSON | CDO 属性、对象路径、类和大小 | 当前 `Values=null` 的函数执行体 |
| `Mods\KardsReplayProbe` | Lua + Action2 JSONL | 实际运行时外层动作和子动作 | 没有被捕获的服务器内部状态 |
```

`kards_functions_zh.md:1833-1834`（对 §① 的独立佐证 —— 官方文档口径就是"每回合更新"）
```
| `enterPlayOnTurn` | 入场回合 | 运行时记录 |
| `movementLeft` / `attackLeft` / `attackCountThisTurn` | 剩余移动/攻击次数 | 每回合更新 |
```

`kards_functions_zh.md:1182`（`ResetUnitOperations` 的语义说明 —— 注意它自己也是"注意"级，不是定案）
```
- `ResetUnitOperations` 常用于让单位重新获得行动机会，注意这可能影响攻击/移动次数限制。
```

**注意：`kards_functions_zh.md` 里没有 `HasAttackLeft` / `HasMovementLeft`。
它的 `CanMoveAndAttackInTheSameTurn`（第 1907 行）只有一行：
`| CanMoveAndAttackInTheSameTurn | 本回合能否移动并攻击 | canIt◀(bool) |`。**
→ 说明这份文档也是按同一批 dump 写的，**dump 里没有的东西它也没有**。

同目录 `temp/KardsChineseDocs/` 是同一份插件的**旧快照**（Intermediate/binaries 更多），
4 个 md 的内容一致。

### 6.8 ★★★ 第二个金矿：`E:\bpasm\bpasm.exe` —— 蓝图字节码反汇编器（**已验证可用**）

`E:\bpasm\` 内容：
| 文件 | 大小 | 说明 |
|---|---|---|
| `bpasm.exe` | 78.9 MB | **UE 蓝图字节码 assembler/disassembler** |
| `out.bpasm` | 12 KB | 它自己产出的样例（源资产 `card_event_front_and_homeland`） |
| `card_event_front_and_homeland.{uasset,uexp}` | 4.9/6.9 KB | 样例资产 |
| `inspect.py` | 402 B | 一个很原始的 strings 扫描脚本（说明当时还在摸索） |

`bpasm.exe --help` 原文（用法关键部分）：
```
bpasm disasm <asset.uasset> [version] [out.bpasm] [--usmap <file.usmap>]
    Disassemble a .uasset (with its .uexp beside it) into .bpasm text.
bpasm asm    <asset.uasset> <edit.bpasm> [version] [out.uasset] [--usmap <file.usmap>]
bpasm check  <file.bpasm> [asset.uasset] [version] [--usmap <file.usmap>] [--json]
```

**本轮实测（重要：`--usmap` 是必需的，不加就只出 48 字节空壳）：**

| 命令 | 结果 |
|---|---|
| `bpasm disasm <BP_Logic.uasset> VER_UE5_6 out.bpasm`（**无 --usmap**） | `Wrote ... (48 chars)` —— 只有一行 `.objectversion`，**等于没解出来** |
| 同上 **加** `--usmap kards-5.6.1-0+++UE5+Release-5.6-Fork-unknown.jmap` | `Wrote ... (1018481 chars)` —— **1 MB 完整反汇编，181 个 export 全出来了** |

**已产出的可用反汇编（放在 `E:\bpasm\`）：**
- `bp_logic_us.bpasm`（1.02 MB / 28109 行）← `klink bot/live/Logic/kards/Content/Blueprints/Logic/BP_Logic.uasset`
- `notifier.bpasm`（53 KB / 1895 行）← `.../Logic/U_CardFunctionsNotifier.uasset`

**用它交叉验证了 §①.9 的两条既有结论（都对，且这次的证据层级更高）：**

**(1) `HasAttackLeft` / `HasMovementLeft` 的调用点确认在 `CanCardDoAnything` 里，
且它们是原生外部符号（`@import(319)` / `@import(321)`），字节码里没有实现：**

`E:\bpasm\bp_logic_us.bpasm:1494` → `.export 15 "CanCardDoAnything" {`
`E:\bpasm\bp_logic_us.bpasm:1816-1819`（`GetTurnNumber`）
```
    localvirtualfunction name "GetTurnNumber" {
        localvariable @path(owner=16) "CallFunc_GetTurnNumber_TurnNumber"
        endfunctionparms
    }
```
`E:\bpasm\bp_logic_us.bpasm:1867-1877`
```
    context {
        localvariable @path(owner=16) "_card"
    } 19 @path(owner=null) {
        finalfunction @import(319) "HasAttackLeft" {
            localvariable @path(owner=16) "CallFunc_HasAttackLeft_doesIt"
            endfunctionparms
        }
    }
    jumpifnot @L2817 {
        localvariable @path(owner=16) "CallFunc_HasAttackLeft_doesIt"
    }
```
`E:\bpasm\bp_logic_us.bpasm:2142-2169`
```
    context {
        localvariable @path(owner=16) "_card"
    } 19 @path(owner=null) {
        finalfunction @import(321) "HasMovementLeft" {
            localvariable @path(owner=16) "CallFunc_HasMovementLeft_doesIt"
            endfunctionparms
        }
    }
    letbool { ... BooleanAND ... }
    jumpifnot @L3820 {
        localvariable @path(owner=16) "CallFunc_BooleanAND_ReturnValue" #6
    }
```
→ **`HasAttackLeft` 是 `jumpifnot` 的守卫条件，`HasMovementLeft` 参与
`BooleanAND(location != 7, HasMovementLeft)`。这与 §①.9 记录的调用点完全一致**，
并且 `@import` 编号（319/321）**确证它们是原生实现**——所以 §①「读不到实现体」不是我们的失误，
是**真的不存在于任何客户端可读文件里**。

**(2) `HasDeploymentSickness` 的表达式逐句对上：**

`E:\bpasm\bp_logic_us.bpasm:20760` → `.export 82 "HasDeploymentSickness" {`
`E:\bpasm\bp_logic_us.bpasm:20765-20771`
```
    context {
        localvariable @path(owner=83) "Card"
    } 19 @path(owner=null) {
        finalfunction @import(315) "getHasBlitz" {
            localvariable @path(owner=83) "CallFunc_getHasBlitz_doesIt"
            endfunctionparms
        }
    }
```
`E:\bpasm\bp_logic_us.bpasm:20784-20792`
```
        callmath @import(256) "EqualEqual_IntInt" {
            context { localvariable @path(owner=83) "Card" }
            9 @path(owner=-155) "enterPlayOnTurn" {
                instancevariable @path(owner=-155) "enterPlayOnTurn"
            }
            localvariable @path(owner=83) "CallFunc_GetTurnNumber_TurnNumber"
            endfunctionparms
        }
```
`E:\bpasm\bp_logic_us.bpasm:20794-20800`（`IsLocatedOnBoard`，`@import(325)`）
→ **`!getHasBlitz() && (enterPlayOnTurn == GetTurnNumber()) && IsLocatedOnBoard()` 三段全对上。**
§①.9 §②.2 的结论**成立**。

**(3) `BP_Logic` 里没有 `ResetUnitOperations` 调用（0 命中）**，见 §② 第 3 点。

**(4) 顺带拿到 `BP_Logic` 的原生调用面**：`finalfunction @import` 去重后 **90 个原生函数**被 `BP_Logic` 调用，
包括 `getHasBlitz` / `getHasAmbush` / `getHasFury` / `HasAttackLeft` / `HasMovementLeft` /
`HasCustomAbility` / `IsPinned` / `IsLocatedOnBoard` / `IsUnit` / `IsAirUnit` / `IsOrder` / `IsLocation` /
`IsWeatherCard` / `IsUnrevealedCovertCard` / `isKreditBySide`(原文 `getKreditBySide`) /
`getKreditSlotBySide` / `getTotalKreditCost` / `getTotalOperationCost` / `CanEndTurn` / `SetupSidesInBattle` 等。
`localvirtualfunction name`（蓝图自己的函数）去重后 **234 个**。

### 对内核/工具的直接影响

1. **`bpasm` 应该成为我们读卡牌逻辑的主工具**，而不是继续在 Python 里自己解 Kismet 字节码。
   它已经在 `E:\bpasm\`，**不需要安装**，只要 `bpasm.exe disasm <uasset> VER_UE5_6 out.bpasm --usmap <jmap>`。
   **务必带 `--usmap`**，否则静默返回 48 字节空壳（exit code 还是 0，**很容易误判成"这资产没字节码"**）。
2. **对"卡内私有函数没进 IR"那个缺口，`bpasm` 是直接的答案**：
   它按 **export** 输出，`.export N "函数名"`，**卡内私有函数就是普通的 export**，
   不像我们现在的方案只编 `ExecuteUbergraph_*` 入口。**这一条建议优先验证。**
3. **`bpasm asm` 还能写回**——如果哪天真要改蓝图（比如做实验卡），这条路是通的。
4. **注意 usable 资产都在 `klink bot/live/` 下**（Logic 28 / Cards 1000+ / GameState / Library / UIBP），
   `E:\peoject\kards\Content\` 里那 62 个只有 4 个和规则沾边。

### 不确定的

- **`bpasm` 对小型卡牌资产返回 48 字节空壳**：
  `card_unit_panzergrenadier.uasset`(2443 B) + `.uexp`(2197 B) 用同样命令
  （带 `--usmap`、`VER_UE5_6`）得到 `Wrote ... (48 chars)`，只有 `.objectversion`。
  **本轮没有查明原因**（可能是版本号要换、可能是这些 DIY/mod 资产的 export 表结构不同）。
  但 `BP_Logic`(107 KB uasset) 和 `U_CardFunctionsNotifier`(24.5 KB) 都正常 →
  **大资产没问题，小资产要留意。**
- 我**没有**去 `C:\Apps\game\kards\Binaries\Win64\ue4ss`（`kards_functions_zh.md:12` 给的提取目录）
  核对 364 个头文件 / 994 个 UFUNCTION。要去的话得跨盘读别人的游戏目录。
- `bpasm.exe` 的 `asm` 模式本轮**没有实测**。

---

## ★ 最有价值的三个发现

### 🥇 一、那 14 个 `.cpp` 全是空壳 —— 「有源码就能定案」这条路是死的

`BaseCardObject.cpp` 678 行里，函数体全是 `{}`；`CardFunctionsStub.cpp` 196 行里
190 个函数一个都没有。**任务书 §① 想查的 7 个符号，一个都没有实现体。**

而且这份 dump 的 `UFUNCTION` 说明符被 UE4SS 的
`MakeAllFunctionsBlueprintCallable=1`（`kards_functions_zh.md:38`）强制覆写过，
**连"这个函数是不是 `BlueprintNativeEvent`"都不能据此判断**。

**这不只是"查不到"——它改变了工作方式**：`内核补全队列.md` §①.9
第 964-971 行那段"读不出来"，**不应该再指望靠"找源码"来解决**。
唯一有用的副产品是 **`movementLeft` / `attackLeft` 的 CDO 初值都是 0**
（`BaseCardObject.cpp:49-50`），这和「必须经 `ResetUnitOperations` 才有行动力」互补。

**行动**：把 §①.9 第 1 条从「jmap 给的是签名不是实现」升级为
「**jmap、`Source/*.cpp`、`IndividualObjectDumps` 三条路都只有签名，原生实现体在我们能拿到的任何文件里都不存在**」，
然后把精力从"找实现"转到"用调用点约束 + 实测对拍"。

---

### 🥈 二、`E:\bpasm\bpasm.exe` —— 现成的蓝图字节码反汇编器，且已实测可用

`bpasm disasm <uasset> VER_UE5_6 out.bpasm --usmap <jmap>` 一条命令，
把 `BP_Logic.uasset` 解出 **1.02 MB / 28109 行 / 181 个 export 的完整字节码文本**，
格式可读性远高于我们现在 Python 侧的输出：

```
.export 15 "CanCardDoAnything" {
    ...
    } 19 @path(owner=null) {
        finalfunction @import(319) "HasAttackLeft" {
            localvariable @path(owner=16) "CallFunc_HasAttackLeft_doesIt"
            endfunctionparms
        }
    }
    jumpifnot @L2817 { ... }
```

**它直接命中我们最大的两个痛点：**
1. **"卡内私有函数没进 IR"** —— 它按 export 逐个输出，卡内私有函数就是普通 export，
   不受 `ExecuteUbergraph_*` 入口的限制。
2. **`--usmap` 必需，且失败是静默的**（不加 usmap → 只出 48 字节，**exit code 仍是 0**）。
   这条坑必须写进文档，否则下次会误判成"这资产没有字节码"。

本轮已用它交叉验证了 §①.9 的两条既有结论（召唤失调表达式、调用点），**都对**；
并新查实 **`BP_Logic` 里没有 `ResetUnitOperations` 调用**（只有 notifier 的转发壳）。

**行动**：把 `bpasm` 加进工具链，先在"卡内私有函数"那个缺口上做一次可行性验证。
注意大资产（107 KB）正常、小资产（2.4 KB）返回空壳，**原因未查明，用前先确认输出 > 1000 字符**。

---

### 🥉 三、`Plugins/KardsChineseDocs/Resources/kards_functions_zh.md` —— 167 KB 的函数级对照表

**2515 行，覆盖 `CardFunctionsStub` 全函数的中英对照 + pin 级说明 + 枚举表 +
`BaseCardObject` 的字段级 CDO 填写规范 + 按功能分的全分类（攻击/防御、关键词、限制、抑制/锁定、
手牌/弃牌、战场/前线、回合、控制权、查询、JSON、判断函数、数值查询、蓝图事件）。**

它直接回答了任务书 ③④ 两节（`ChangeAttack` 的 pin 说明、
`EGameplayRestrictions` 逐值、`ResetUnitOperations` 的位置），
而且**它的证据分级表（`:17-26`）自己就标注了"函数体读不出来"** ——
这解释了为什么我们所有"读不出来"的条目都集中在原生实现上。

**它同样缺 `HasAttackLeft` / `HasMovementLeft`** ——
证明这份文档和我们的 dump 是同一批材料，**不能用来补 dump 的洞**。

**行动**：这 4 个 md（尤其这一个）应该收进 `klink bot/docs/` 或直接当工具链的查询表。
§③ 的 `EChangeType` 字节值（`tempBuffGive=0 … notUsed=9`）**已经可以直接落地到蓝图解码器的
`byteconst` 翻译表里**。

---

## 附：本轮产出的可复用文件

| 文件 | 内容 |
|---|---|
| `_stub_fns.txt` | `CardFunctionsStub.h` 全部 190 个函数，格式 `行号\|函数名\|完整参数表` |
| `E:\bpasm\bp_logic_us.bpasm` | `BP_Logic` 完整反汇编（1.02 MB / 28109 行 / 181 export） |
| `E:\bpasm\notifier.bpasm` | `U_CardFunctionsNotifier` 完整反汇编（53 KB / 1895 行 / 99 export） |
| `E:\bpasm\bp_logic.bpasm` / `card_test.bpasm` / `card_pg.bpasm` | **48 字节空壳，是"忘了带 `--usmap`"的反面样例，留着当对照** |

## 附：本轮**没有**覆盖的地方（别当成已查过）

1. `C:\Apps\game\kards\Binaries\Win64\ue4ss`（`kards_functions_zh.md:12` 给的提取目录）
   —— 364 个头文件 / 994 个 UFUNCTION 没核对。**这是补齐 §② 那 3103 差集必须去的地方。**
2. `BP_CardFunctions.uasset`（`GetCardFromID` / `ChangeAttack` 的真实实现）
   —— 本轮只反汇编了 `BP_Logic` 和 `U_CardFunctionsNotifier`，**没碰它**。
   §③ 的 `EChangeType` 语义、§① 的"谁在什么时候重置"都在这里面。
3. `BP_GameState_Battle.uasset` —— 资产在 `klink bot/live/GameState/`（30 KB + 76 KB），
   **本轮没反汇编**。§①.9 的前线定案是从它来的，如果要复核应该对它跑一次 `bpasm`。
4. `bpasm asm` 模式、`bpasm check --json` —— **没实测。**
5. `E:\peoject\kards\temp\1\` —— 是 `Source/` + `Config/` + `Content/` 的**旧快照**
   （`BaseCardObject.h` 的 SHA256 与 `Source/` 那份完全相同：`F4133F388EFEDC30...`），
   **没有额外信息**，不用再看。
6. `temp/3/`（`AssetRegistry.bin` 6.66 MB + `main.go` 10 KB + `pakcook.exe`）——
   是个 pak 打包工具工程，**与规则无关，没细看**。
