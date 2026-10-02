# 2026-10-02 本轮「虚空部署 / 随机效果不一致」的结论与证据索引

## 一、RNG 复刻（主修复，已验证）

实现：`src/KLink.Bot/Engine/UeRandomStream.cs`（UE `FRandomStream` 逐位复刻）。
接线：`GameState.Random`（对局路径唯一随机源）、`CardApi.GetRandomCard`、
`CardApiDispatch.ShuffleDeckBySide` / `DoRandomIntFromRange`。

### 逐位验证（最重要的一条证据）

报告 `Kards_RNG_report` 的 `Weather.md` §4.2.1 给了一组**可复算测试向量**
（出自游戏二进制 IDA `0x143ddce50` 的反编译，不是照抄引擎源码）：

| 第几次 `RandomIntFromRangeWithStream(0,2)` | 变换前 Seed | 变换后 Seed | 返回 |
|---|---|---|---|
| 1 | 1000193900 | 1626977479 | 1 |
| 2 | 1626977479 | 4280773790 | 2 |
| 3 | 4280773790 | 431904801 | 0 |

本内核实现**逐位命中**（`tools/BotSim/SelfTest.cs` 的
`UeRandomStreamMatchesReportVector`）—— 同时钉住 LCG 常数、高 23 位变换、闭区间三件事。
旧实现是 splitmix64 + 取模，**算法上不可能**命中这组向量 ⇒ 该用例在修复前必红。

### 同时修掉的第二个独立错

`RandomIntFromRangeWithStream(min,max)` 以前当**半开区间** `[min,max)` 用，
蓝图里它是**一行透传** `RandomIntegerInRangeFromStream(stream, min, max)`，
而 IDA 反编译是 `Min + floor(GetFraction()*(Max-Min+1))` ⇒ **闭区间**。
`(0,2)` 以前永远出不了 2。

## 二、消费点核对（哪些对上、哪些没对上）

已核对并**已修**：
- `GetRandomCard` → `RandomIntegerInRangeFromStream(0, len-1)`，1 次/调用 ✓
  （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:21682-21698`）
- `ShuffleDeckBySide` → `Array_ShuffleFromStream`，**前向** Fisher-Yates、消耗 **n** 次 ✓
- `SpawnCardInDeckBySide` → **每次生成消耗 1 次**
  （`RandomIntegerInRangeFromStream(0, 牌库长度)`，`BP_CardFunctions.g.cs:34856-34867`）。
  以前**完全不消耗** ⇒ 游标落后。实测修好后 773639 `#10 t3` colossus 的第二次抽签
  从 idx 3 变成 idx 1（游标确实动了）。

**仍未对上（如实记录）**：`508065` 的 `atlantic_convoy`（`#36 t9`）两次抽签，
内核落在流位置 #42/#43，而客户端落在 **#88** ⇒ **我们落后 46 次消费**。
候选池本身**已验证正确**（102 张美国费≤3 单位、字典序，
`idx37 = card_unit_506th_airborne`、`idx52 = card_unit_f4f_wildcard` 与内核完全一致；
客户端选中的 `card_unit_fifth_ohio` = idx **55**，而流位置 #88 正好给出 55）。
⇒ 差异**只在流位置**，不在候选集。46 次的来源未定位。

**试过并否决的假设**：`selectCardToDraw` 里 `keepOrder == false` 时的
`Array_ShuffleFromStream(possibleChooseCards)`（`BP_CardFunctions.g.cs:33783`）。
按"游标必须对齐"实现后**整体变差**：人类失败 13 → 22、应用 395/417 → 386/417、
`773639` 从 0 条人类失败退回 5 条 ⇒ 说明我们算出的 `keepOrder` 与客户端不一致。
已回滚，并在 `CardApiDispatch.SelectCardToDraw` 里写明理由。

## 三、生成卡发号规则（虚空部署的根因，权威证据）

`BP_CardFunctions::CreateCard` → `GameStateRef.GenerateNextCardID(turnNumber, out id)`
（`BP_CardFunctions.g.cs:10542`），实现
（`_deps/BP_GameState_Battle.g.cs:1545` / `:1788`）：

```
GenerateNextCardID(turnNumber):
    IncrementCardsCreatedThisTurn()                 # 全局计数器 +1
    mult = SelectInt(500, 1000, turnNumber == 0)    # SelectInt(A,B,pickA)= pickA?A:B
    id   = cardsCreatedCountThisTurn + turnNumber * mult
```

⇒ **分配器没有 side 参数**（规则对双方一致、计数器全局、每回合归零）。
旧实现只让人类那一方走这条规则、bot 走顺序号（81/82/83）⇒ 我们发 `PC {"0":81}`
客户端认不出 ⇒ 记牌器 +1、场上什么都没有。

反向证据：4 局回放里**人类动作引用的所有 >80 的 cardID 全是 `1000×回合+序号` 形状**，
没有一条落在 81..99 的顺序号段里。

### 客户端发号用的"回合号"

`ClientIdTurnOverride` 以前 = 当前动作的 `turn_number`。实测 773639：
bot 的 `#31 t8 XActionStartOfTurn right` 之后它的出牌被记成 `#32..#38 t9` ——
用当前动作的 turn_number 会让 bot 在 `#38 CS` 生成的 `no43_commando` 拿到 **9001**，
把人类 `#46 t9` 引用的 9001（`iron_from_north`）挤成 9002 ⇒ 直接判失败。
改用**「已处理过的 `XActionStartOfTurn` 条数」**（= 客户端 `GetTurnNumber()`）后，
bot 那张发 8001、人类那张仍是 9001，两边都对上。
人类那一侧两条规则恒等（1001/3001-3004/5001/5002/9001/9002/17001/17002/
19001/25001-25003/27001/27002 全部满足），所以这次改动只影响 bot 侧。

## 四、安全网（虚空部署的最后一道防线）

- `GameState.TrackGeneratedCardTrust` / `GeneratedCardIds` / `VerifiedGeneratedCardIds`
- `MatchEngine.EnforceGeneratedCardTrust`（默认开）+ `BlockUntrustedOrders`
- `ReplayRunner` 里**跟踪但关闭拦截**（重放客户端自己发过的动作，拒绝只会更差）
- `BotTurnService.DecideTurn` 在重建之后**重新打开**
- 拒绝时写 `<unverified-generated-card-not-played:{卡名}>` 进 `UnimplementedCalls`（审计 ⑥ 可见）

## 五、四判据前后对比（4 局，2026-10-02）

| 回放 | 应用 前→后 | ⑤b 前 → 后 | ⑥种 前→后 |
|---|---|---|---|
| 214436 | 59/61 → 59/61 | ✅ → ✅ | 7 → 7 |
| 508065 | 124/141 → **127/141** | #46 t11 → **#54 t13** | 9 → **8** |
| 542091 | 75/78 → 75/78 | ✅ → ✅ | 5 → 5 |
| 773639 | 121/137 → **134/137** | #46 t9 → **✅ 无** | 7 → 7 |
| **合计** | 379/417 → **395/417** | — | 28 → **27** |
| 人类失败 | **29 → 13** | | |
| ④HQ（人类动作） | 79 → **79（不变）** | | |
| ④HQ（全部） | 112 → 132（增量全在 bot 侧） | | |

## 六、审计入口

- `out/audit/audit-all-replays.ps1` —— 4 局汇总（应用率 / 人类失败 / 未实现种）
- `out/audit/audit-identity-mismatch.ps1` —— 身份不一致影响面（默认修复后）
- `out/audit/audit-idfix-compare.ps1` —— 身份校正的 A/B（默认关 vs 开）
- `tools/ServerBridgeTest --audit-replay <前缀> [--rng-trace] [--identity-fix] [--identity-only <卡名>]`
  - ⑤c 身份不一致、⑥a 游标失同步、⑦ RNG 游标计数
