# 派发表缺口：分类 / 修复 / 守卫（2026-10-02）

> 这一轮的题目：「IR 里被调用的函数 760 种，派发表只有 197 个键 ⇒ 608 种没人处理」。
> 下面把 608 种**重新分类**（旧分类脚本的结论是错的，见 §0），修掉其中最划算的 25 种，
> 加一道**能自动报警的守卫**，并给出四条判据的前后对比。

---

## 0. 先纠正题面里的两个判断（都有证据）

### 0.1 「608 种缺失」这个分母偏大：其中 64 种**已经被 locals 兜底处理**

`card-ir.json` 的每张卡可以带一个 `locals` 字典（**卡自己蓝图里的私有函数体**）。
`KismetVm.ExecuteCall` 在派发表查不到时，会回退执行**调用方那张卡自己的**函数体
（`src/KLink.Bot/Effects/Blueprint/KismetVm.cs:583` 起；守这条的自测是
`tools/BotSim/SelfTest.cs` 的 `LocalFunctionActuallyRuns`）。

⇒ 「不在派发表里」**不等于**「没被处理」。必须**按调用点**判：
调用它的那张卡的 `locals` 里有这个名字 ⇒ 这个调用点已经执行了。

实测：**64 种 / 154 个调用点**属于这一类（例：`RemoveBuff`、`ApplyBuff`、
`didPlayBritishInfantryLastTurn`、`GetPlayFromHandDamage`）。

### 0.2 「DiscardCardFromHand / getAndDecryptAttack / FullyHealCard / MakeCardRetreat 已有实现」——**只有第一个是对的**

| 键 | 题面给的位置 | 那个位置实际是什么 | 结论 |
|---|---|---|---|
| `DiscardCardFromHand` | `CardApi.cs:1653` | **真方法** `CardApi.DiscardCard`（`CardApi.cs:1659`） | ✅ 真的是 (B) |
| `getAndDecryptAttack` | `CardApi.cs:1136` | **一行注释**（`/// ChangeAttack si=934 …`），全仓库**没有**同名/同义方法 | ❌ 不是 (B) |
| `FullyHealCard` | `CardApi.cs:1097` | **一行注释**（`// 「被完全修复」—— 出处 … FullyHealCard`） | ❌ 不是 (B) |
| `MakeCardRetreat` | `MatchEngine.cs:184` | **一行注释**（列举经过 `FireLocationMoved` 的路径） | ❌ 不是 (B) |

取证（全仓库 grep，只匹配到注释行）：

```
CardApi.cs:1113      // 「被完全修复」—— 出处 `out/bp-cardfn.json` 函数 `FullyHealCard`
MatchEngine.cs:184   // `Destroy` / `DiscardCard` / 效果退回手牌 / 洗回牌库 / `MakeCardRetreat`
MatchEngine.cs:972   /// `card_unit_black_prince` 的打出效果 `MakeCardRetreat(GetAllCardsInFrontline)`。
```

⇒ **真正的 (B) 只有 3 种**（见 §1），不是题面暗示的 4 种。
`FullyHealCard` / `MakeCardRetreat` / `getAndDecryptAttack` 都是 **(C)**，
只是「语义出处好找、成本低」而已 —— 它们在本轮里按 (C) 处理（前两个没做，第三个做了）。

---

## 1. 608 种的完整分类（A / B / C）

### 1.1 判据（脚本：`out/audit/missing-keys-classify2.py`）

脚本对每个缺失键给出 `class` + `rule` + `evidence`（每条都可复核）：

**(B) 实现已经在我们代码里、只差注册** —— 三档，**全部需要人工复核参数形状后才可注册**：
* `B1` 显式别名表（从源码文档注释读出的「IR 名 → C# 方法」，逐条带出处）
* `B2` 方法名**完全相同**（大小写不敏感）
* （`B3` 反引号引用 / `B4` 参考实现有 —— **都不算 B**：前者会误配，后者是"语义已查明"，
  仍要写一层实现。它们归 C，用 `C-easy-ref` / `C-easy-wrap` 标出来供排序。）

**(A) 纯 UI / 战役 / 表现层**（对局无影响）：
* `A1` 直译产物里**只**定义在 UI/战役蓝图，且名字不含玩法动词
* `A2` 名字命中 UI/战役词表且不含玩法动词（`ShowTutorialMessage` / `GiveStarForCampaign`
  这类**定义在玩法蓝图 `BP_CardFunctions` 里的 UI/战役辅助函数**，只看定义处会误判成 C）

**(C) 其余**（玩法关键但没实现，或保守归类）：
* `C1` 直译产物里有定义（玩法蓝图）
* `C2` 直译产物里找不到定义（保守归 C）
* `C-easy-ref` 参考实现 `ref/kards-sim/KardsSim/Bridge/EngineHost.cs` 里有 ⇒ 语义已查明
* `C-easy-wrap` 直译函数体只调 1~3 个**已注册**原语 ⇒ 写层适配即可

### 1.2 数字

口径说明：
* 「**入口口径**」= 只扫事件入口的 `steps`（题面那个 608 是这个口径）；
* 「**修正口径**」= 再加上 `locals` 函数体（locals 体是**真会被执行**的，
  里面调一个没实现的名字同样是缺口）。**修正口径才是运行时的事实**，
  与守卫 `tools/BotSim/DispatchGap.cs` 对账一致（`out/audit/dispatch-gap-parity.py`）。

| | 入口口径 | 修正口径 |
|---|---|---|
| IR 里被调用的函数 | 760 种 | **784 种** |
| 派发表键 | 195 | 195 |
| 不在派发表里 | **608 种** | **629 种** |
| 其中 locals 兜底已处理 | — | 64 种 / 154 调用点（**不是缺口**） |
| ⇒ **真缺口** | 545 种 | **565 种 / 3424 个调用点 / 666 张卡** |

修复前的 A/B/C 分布（修正口径，完整表在
`out/audit/missing-keys-classify2-prefix.txt`）：

| 类 | 种数 | 真缺口调用点 | 涉及卡 |
|---|---|---|---|
| **(A)** 纯 UI / 战役 / 表现层 | 149 | 801 | 109 |
| **(B)** 实现已有、只差注册 | **3** | 95 | 76 |
| **(C)** 玩法关键但没实现 | 413 | 2528 | 626 |

修复后（完整表在 `out/audit/missing-keys-classify2-after.txt`）：

| 类 | 种数 | 真缺口调用点 | 涉及卡 |
|---|---|---|---|
| (A) | 149 | 801 | 109 |
| **(B)** | **0** | 0 | 0 |
| (C) | 391 | 1951 | 523 |
| 合计真缺口 | **540** | **2752** | 534 |

### 1.3 人工抽查（14 个，逐个给证据）

| # | 键 | 脚本判 | 人工核过的事实 | 结论 |
|---|---|---|---|---|
| 1 | `CampaignSetText` (211/46卡) | A | 46 张调用者**全部**是 `*_cam1` 战役卡；入口只有 `OnCreateCardApplyCampaignUpgrades` / `OnCardSpawnedInHand` | ✅ A |
| 2 | `AddToVerticalBox` (41/1卡) | A | IR 里只有 `BP_CardHelp` 一个调用者（`i=300`） | ✅ A |
| 3 | `CreateHelpBubbleEntry` (39/1卡) | A | 同上（`BP_CardHelp` `i=121`） | ✅ A |
| 4 | `GetIsGoldCard` (24/1卡) | A | 直译产物里只定义在 `BP_CardHelp`（UI 蓝图），调用者也只有它 | ✅ A |
| 5 | `SetActorHiddenInGame` (16/7卡) | A | 调用者 `BP_BaseCard`/`BP_DraftingCard`/`BP_EffectBar`；入口是 `ReceiveBeginPlay`/`AnimateIn__FinishedFunc`/`FlyOnScreen` 等纯表现 | ✅ A |
| 6 | `ShowTutorialMessage` (29/12卡) | ~~C~~→**A** | 定义在 `BP_CardFunctions`（玩法蓝图）⇒ 旧 A1 判据误判；实际调用者全是 `*_tut` 教学卡 | ✅ 修判据后 A |
| 7 | `GiveStarForCampaign` (75/25卡) | ~~C~~→**A** | 53/75 个调用点在 `CampaignOnEndOfGame` 入口 | ✅ 修判据后 A |
| 8 | `HasCampaignUpgrade` (217/40卡) | C-easy-ref | 参考实现 `EngineHost.cs:1319` 就是 **无条件 false**（无头模拟里战役升级恒未升级） | ✅ 归类正确（成本极低但确实要写） |
| 9 | `DiscardCardFromHand` (39/37卡) | **B1** | `CardApi.cs:1659` 有真方法 `DiscardCard`；39 个调用点有 14 种形状（卡对象 26 / 整数 cardID 13） | ✅ B（已修） |
| 10 | `IsUnrevealedCovertCard` (28/18卡) | **B2** | `CardApi.cs:1194` 同名静态方法（有意的恒 false 桩，注释已说明理由） | ✅ B（已修） |
| 11 | `IsBomber` (19/13卡) | **B2** | `MatchEngine.cs:1529` 同名静态方法 | ✅ B（已修） |
| 12 | `RemoveBuff` (12/5卡) | **locals 兜底** | IR 的 `card_unit_1st_london_brigade` 等 5 张卡自己在 `locals` 里带了这个函数体 ⇒ 已经会执行 | ✅ 不是缺口 |
| 13 | `IsLocationFull` (20/17卡) | C | 直译函数体**唯一一步**是 `FetchCardsByLocation(location, out …, out isLocationFull, …)` | ✅ C（已修） |
| 14 | `SpawnCardInFrontline` (108/29卡) | C | 直译函数体（`_deps/BP_CardFunctions.g.cs:35201`）**唯一一步**是 `SpawnCardToBoard(…, 7 /*BoardFrontline*/, …)` | ✅ C（已修） |

**判据被抽查推翻过两次**（#6 / #7），说明"只靠正则/词表"确实不行 ——
两处都改成了"名字形态 + 不含玩法动词"，并把 `A2` 档整档列出来人工过了一遍
（`out/audit/missing-keys-classify2-after.txt` 的 (A) 段）。

---

## 2. 修了什么（25 个键）

全部改动在两个文件：`src/KLink.Bot/Effects/CardApiDispatch.cs`（派发表 + 辅助实现）、
`src/KLink.Bot/Effects/CardApi.cs`（`StopDestructionEffect` 门 + 共用的后缀读法）。
每个键的**证据出处 → 修法 → 自测**如下。

### 2.1 (B)：实现已有，只差注册（3 个 / 95 调用点）

| 键 | 真缺口调用点 | 证据 | 修法 | 自测 |
|---|---|---|---|---|
| `DiscardCardFromHand` | 39 / 37卡 | `CardApi.cs:1653` 文档注释「对应 `BP_CardFunctions::DiscardCardFromHand` / `DiscardCard`」 | `DoDiscardCardFromHand` 适配器 → `CardApi.DiscardCard`；**`a[0]` 两种形状都认**（卡对象 / 整数 cardID，IR 实测 26:13） | 「DiscardCardFromHand：卡对象 / 整数 cardID 两种形状都要真的弃掉」 |
| `IsUnrevealedCovertCard` | 28 / 18卡 | `CardApi.cs:1194` 同名方法（恒 false 桩，理由在它的注释里） | 一行注册 | （并入 `IsBomber/IsFighter` 那条的形状断言；行为中性） |
| `IsBomber` | 19 / 13卡 | `MatchEngine.cs:1529` 同名方法 | 一行注册 | 「IsBomber / IsFighter：轰炸机 / 战斗机判据必须为真」 |

### 2.2 语义取自参考实现 `ref/kards-sim/.../EngineHost.cs`（10 个）

形状统一：`recv` = 被查的卡，`a[0]` = out 槽（逐个用 `out/audit/ir-callsites.py <名字>` 核过）。

| 键 | 调用点 | 参考实现出处 | 修法 |
|---|---|---|---|
| `IsFighter` | 15 | `EngineHost.cs:1015` | `Definition.Type == "fighter"` |
| `getAndDecryptAttack` | 33 | `EngineHost.cs:1098` | `card.Attack` |
| `getAndDecryptDefense` | 1 | `EngineHost.cs:1361` | `card.Defense` |
| `IsPinned` | 16 | `EngineHost.cs:1341` | `Keywords.Contains(Keyword.Pinned)` |
| `HasBond` | 7 | `EngineHost.cs:1107` | `Keywords.Contains(Keyword.Bond)` |
| `hasActivePincerEffect` | 2 | `EngineHost.cs:1336` | `Keywords.Contains(Keyword.Pincer)` |
| `HasAttackLeft` | 2 | `EngineHost.cs:1288` | `!HasAttackedThisTurn` |
| `IsExile` | 13 | `EngineHost.cs:1106` | 无条件 `false`（参考实现也是） |
| `getKreditBySide` | 1 | `EngineHost.cs:1862` | `State.Kredits(side)` |
| `GetFrontlineOwnerSide` | 1 | `EngineHost.cs:1962` | `State.FrontlineOwner` |

**为什么这些是真修复而不是"刷计数器"**：VM 在未处理时**什么都不写回 out 槽**
（`KismetVm.cs:606-612`），布尔槽保持 null ⇒ **恒假**、整数槽保持 null ⇒ **恒 0**。
对「这张卡是战斗机吗」「它攻击力多少」这种问题，恒假 / 恒 0 是**错的**。
`getAndDecryptAttack` 33 个调用点里就有 4 次真的在回放里触发（见 §4）。

### 2.3 (C) 真实现（12 个）

| 键 | 调用点 | 语义出处 | 修法 |
|---|---|---|---|
| ★ `SpawnCardInFrontline` | 108 / 29卡 | `_deps/BP_CardFunctions.g.cs:35201`：唯一一步 = `SpawnCardToBoard(card_name, side, **7**, locationNumber, 0, gold, giveBlitz, spawnerID, 0, makeVeteran, out)` | `DoSpawnInFrontline`：复用已验证的 `SpawnOnBattlefield(frontline: true, …)`，`gold` 取 spawner 的 `IsGold`，`a[8]` 走 `MakeVeteran` |
| `CustomName1Add/HasAttribute/Remove` | 14/8/8 | `EngineHost.cs:1498-1500` + `:2160-2182`（`SuffixAdd`/`SuffixRemove`，**幂等**、逗号分隔） | 存进 `CardInstance.CustomJson["customName1"]` |
| `CustomName2Add/HasAttribute/Remove` | 88/108/24 | 同上（`EngineHost.cs:1088/1307`） | 键 `customName2` |
| `GetCustomName2Attributes` | 17 | `EngineHost.cs:1639` | 返回标记数组 |
| `GetCardsInSupportLineBySide` | 41 / 34卡 | `_deps/BP_CardFunctions.g.cs` 同名函数体（四道过滤：side / location=半场 / covert / unitsOnly） | 半场 = `side.HqOf()`；`unitsOnly` 按参数过滤 |
| `IsLocationFull` | 20 / 17卡 | `_deps` 同名函数体（唯一一步 `FetchCardsByLocation` → `isLocationFull`） | 容量：前线 `FrontlineCapacity`(5)、半场 `HalfBoardCapacity`(5，**含 HQ**)、手牌 9、牌库/弃牌堆永不"满" |
| `DestroyMultipleCards` | 20 / 19卡 | `_deps` 同名函数体（→ `ApplyDestroyMultipleCards`，语义即逐张 Destroy） | 逐张 `DestroyCard`；**两种元素形状都认**（卡对象 / 整数 cardID） |
| `DiscardCardFromDeck` | 11 / 11卡 | `_deps` 同名函数体（门：`cardID>0` + 有效 + **必须在牌库** 1/2） | 复用 `DiscardCard`；**保留"必须在牌库"这道门** |

**额外关掉的一条 TODO**：`CardApi.cs:631` 早就记着
「没做 `StopDestructionEffect` 那一半：它是 `CustomName1` 属性，而 `CustomName1*` 三件套内核一个都没进派发表」。
本轮 `CustomName1*` 有了**写方**（`card_event_patrol` i=10 / `card_event_usa_promo2` i=414），
于是把**读方**一起补上：`ShouldTriggerDestructionEffect` 现在等于
`!CustomName1HasAttribute("StopDestructionEffect") && (hasDestruction || HasCustomAbility("destruction"))`
（出处 `TriggerDestruction` si=905/965）。写读共用同一个静态读法 `CardApi.CustomNameHasAttribute`，
并有自测守着。

### 2.4 自测：**修复前失败、修复后通过**（实测）

新增 **11 条**自测（10 条原语 + 1 条门 + 1 条守卫，其中 `IsUnrevealedCovertCard` 并入形状断言）。
每条都用 `engine.Api.InvokeByName(名字, …, out handled)` **直接断言中间状态**
（`handled == false` 就是修复前的状态），不写成"连打 N 局看结果"。

**修复前**（把 §2 的注册块用 `#if FALSE_TEMP_BEFORE_FIX_PROBE` 关掉后重跑）：

```
11/82 项失败     ← 10 条新用例全部失败 + 1 条已知基线失败
  ❌ SpawnCardInFrontline  → 派发表里没有 `SpawnCardInFrontline`（**修复前就是这个状态**）
  ❌ DiscardCardFromHand   → 派发表里没有 `DiscardCardFromHand`
  ❌ getAndDecryptAttack   → 派发表里缺 getAndDecryptAttack=True / getAndDecryptDefense=True
  ❌ IsBomber/IsFighter    → 派发表里没有 `IsBomber`
  ❌ IsPinned/HasBond      → 派发表里没有 `IsPinned`
  ❌ CustomName1/2 三件套  → 派发表里没有 `CustomName1Add`
  ❌ GetCardsInSupportLineBySide → 派发表里没有
  ❌ IsLocationFull        → IsLocationFull 没进派发表
  ❌ DestroyMultipleCards  → 派发表里没有
  ❌ DiscardCardFromDeck   → 派发表里没有
```

**修复后**：`1/84 项失败` —— 只有那条**已知基线失败**
（`手牌目标：gordon_highlanders…`，按题面要求没动），新增 12 条全绿。

---

## 3. 防回归守卫

### 3.1 设计：**自测 + 冻结指纹**（不是"接进审计打印一个数"）

我选的是**自测**，理由是：审计是**人工跑**的、看的人可能只看汇总表；
而这个 bug 类的特点是**不报错**（动作照样"应用成功"），
所以必须由**每次 CI/自测都会跑**的东西来挡。审计那一路我**也加了**（⑥b，见 §3.4），
但它是**信息**，守卫是**门禁**。

计算放在库里：`src/KLink.Bot/Effects/Blueprint/DispatchGap.cs`
（放库里是因为自测和审计 ⑥b 要**共用同一份数字**，两处各写一遍迟早会不一致）。

判据三层（缺一不可）：

1. **不在派发表里**（`CardApi.ImplementedNames`）；
2. **locals 也兜不住** —— `KismetLibrary.FindLocalProgram(调用卡, 名字)` 命中就不算缺口
   （自带 `ResolveBaseName` 变体后缀回退，与 VM 的 `LocalProgramFor` 同源）；
3. **事件入口的 `steps` 和 `locals` 的函数体都要扫**（locals 体是真会被执行的）。

冻结值在 `tools/BotSim/DispatchGap.cs`：**种类数 + 集合指纹**（排序后 `名字:调用点数`
行的 SHA-256 前 16 位）。

### 3.2 为什么用**指纹**而不是只比总数

只比总数会漏掉「**修一个 + 坏一个**」——两个方向的改动互相抵消，总数不变，
但缺口集合已经变了。指纹对**任何**集合变化都敏感。

### 3.3 实测：**它真的会报警**

实验：故意把 `["IsBomber"]` 注释掉（缺口 +1）**同时**多注册一个 `["IsGotcha"] = false`
（缺口 −1），制造净零变化：

```
❌ ★★ 派发表静态缺口守卫：缺口集合的指纹必须与冻结基线一致（只降不升）
   ⚠️ 缺口**总数没变但集合变了** —— 典型的「修一个 + 坏一个」，正是只比总数会漏掉的那种。
   基线：540 种 / 指纹 290650B7225728D2
   现在：540 种 / 2761 个真缺口调用点 / 指纹 E255E806047C6C16
```

**总数一模一样（540），但守卫照样报警** —— 这正是设计目标。
（实验后已恢复，当前 `1/84`。）

**更新方式**（也是"只降不升"的执行机制）：缺口变少时守卫**也会失败**，
提示你去跑 `dotnet run --project tools\BotSim -c Release -- dispatch-gap` 拿新的两个常量。
故意让它失败（而不是"自动接受变小"）就是为了逼出「修一个 + 坏一个」这种情况。

### 3.4 审计 ⑥b（信息层）

`tools/ServerBridgeTest/ReplayAudit.cs` 的 ⑥ 段之后加了 **⑥b**：
打印静态缺口全集（种类数 + 调用点 + 指纹 + top 15）。
与 ⑥ 的区别：⑥ 是「这一局撞到的」（受卡组影响），⑥b 是「全集」（与卡组无关）。
**两个数现在是同一份计算**，不会再出现"审计说 8 种、自测说 540 种"这种各说各话。

---

## 4. 四条判据的前后对比

方法：同一份回放、同一台机器，跑两次 ——
一次带本轮的 25 个注册，一次用 `#if FALSE_TEMP_BEFORE_FIX_PROBE` 把它们关掉。
原始输出在 `out/audit/audit-before/` 与 `out/audit/audit-after/`。

| 回放 | ① ⑤b 首个人类失败 | ② ⑥ 未实现原语种数 | ③ ④ HQ 对不上 | ④ 人类失败 | 应用 |
|---|---|---|---|---|---|
| 214436 | 无 → 无 | **8 → 7** | 8 → 8 | 0 → 0 | 59/61 → 59/61 |
| 508065 | `#46 t11 PC：kredit 不足` → 同 | **10 → 9** | 60 → 60 | 16 → 16 | 124/141 → 同 |
| 542091 | 无 → 无 | **6 → 5** | 10 → 10 | 0 → 0 | 75/78 → 同 |
| 773639 | `#45 t9 ML：移动被拒` → 同 | **9 → 8** | 34 → 34 | 6 → 6 | 130/137 → 同 |
| **合计** | 无变化 | **33 → 29** | 112 → 112 | **22 → 22** | 388/417 |

**②（未实现原语）是唯一改善的判据，方向正确。** 消失的具体键：

* 214436：`IsPinned ×2` 消失
* 508065：`IsPinned ×1` 消失
* 542091：`IsPinned ×4` 消失
* 773639：`getAndDecryptAttack ×4` 消失

**①/③/④ 完全没动** —— 这是**符合预期**的，不是失败：
本轮修的 25 个键里，回放里真正被触发的只有 `IsPinned` 与 `getAndDecryptAttack` 两个**查询**；
它们不影响棋盘状态（只影响蓝图里读它们的判据），所以 HQ / 人类失败数不会变。
**其余 23 个键在这 4 局回放里一次都没触发** —— 所以这 4 局**测不出**它们，
必须靠 §2.4 的最小断言兜底（这正是题面提醒的"别只断言连打 N 局"）。

**关于④人类失败数**：本轮 22 → 22 未变，所以不存在"变差"的问题，
不需要用"强制与客户端同随机"或"单卡门控"去归因。

### 4.1 本轮**没有**改善 ⑤b 的原因（如实说）

两局有人类失败的 ⑤b 根因都**不是**派发表缺口：

* `508065 #46 t11 PC：打不出：kredit 不足（kredits=2，费用=3）` —— 费用/资源算错；
* `773639 #45 t9 ML：移动被拒（当前 BoardHqLeft）` —— 位置模型对不上。

⇒ 要改善这两条，得去查**费用结算**与**移动/位置**，不是继续补派发表键。
**这是本轮结论里最有用的负面信息。**

---

## 5. 没修的（尤其是 (C) 里没做的）

完整清单见 `out/audit/没修的.md`。这里给**为什么**：

### 5.1 (C) 里按调用点数最高的几个

| 键 | 真缺口调用点 | 为什么没做 |
|---|---|---|
| `GotchaTriggered` + `ShouldGotchaTrigger` | 54 + 53（**回放里每局都真的触发 29~58 次**，是当前**最活跃**的缺口） | Gotcha（盖着的反制卡）是一整个**子系统**：`GotchaTriggered` 的函数体（`_deps/BP_CardFunctions.g.cs`）里有 `gotchaActivated` 状态位、`RearrangeLocation`、`SetCardsSeenByCipher`、`GetHandLocationBySide`、`ApplyRemoveCardFromBoard`… 还要建模 Covert 的「已揭示/未揭示」位与 cipher/intel。**本内核目前一个都没建模**（`IsUnrevealedCovertCard` 是恒 false 的桩）。只注册 `ShouldGotchaTrigger = false` 只会把 ⑥ 计数刷绿而行为不变 —— **故意不注册**（代码里留了注释指到本文件）。 |
| `MakeCardRetreat` | 36 / 35卡 | 需要「把单位退回所有者手牌 + 重算前线归属 + 触发 `OnAfterLeaveBoard`」整条链。本内核有 `MoveUnitFromSupportToFrontLine` 的语义注释但**没有** `MakeCardRetreat` 实现；`GetAllCardsInFrontline`（8 点）也缺。**可以照 `DestroyCard` 的结构做，但要新增一个"退手牌"原语并接好触发点**，本轮时间不够，宁可不做。 |
| `FullyHealCard` | 33 / 31卡 | 相对简单（`HealCard(card, MaxDefense - Defense)` + out `HealedAmount`），但 `MaxDefense` 的语义（是否含 buff）要先确认；**下一轮最容易补的一个**。 |
| `ConvertCard` | 26 / 25卡 | 函数体 400+ 行：要 `CreateCard` 新卡、处理牌库/手牌/场上三种来源、`RemoveCardFromDeckBySide`/`AddCardToDeckBySide`、`ExecuteOnEnterPlayEvents`、`OnOtherCardConverted` 广播、salvaged 结构体。**这是本轮 (C) 里最大的一块，风险高。** |
| `AddGameplayRestriction` / `GetCardsPlayedFromHandThisTurn` / `WhichStrategy` / `GetSupportLineLocationBySide` / `LoseKreditSlot` / `EndMatch` / `IncrementObjectiveCounter` / `SetCountdown` / `RemovePin` / `getCardsBuffedByThisCard` … | 8~22 各 | 大多属于 `C-easy-ref`（参考实现里有）⇒ **下一轮批量做的首选**：`getCardsBuffedByThisCard`(25) / `RemovePin`(10) / `getKreditTempBuffAmount`(11) / `getAttackTempBuffAmount`(9) / `SetCountdown`(15，纯包装) / `WasLeftMostCardWhenPlayedFromHand`(6) / `WasRightMostCardWhenPlayedFromHand`(4) 等，都是「读一个字段 / 写一个 JSON 键」级别。 |
| `Campaign*` 一族（`CampaignAddKreditCost` 79 / `CampaignAddAttack` 59 / `CampaignAddDefense` 59 / `CampaignAddBlitz` 23 / …） | ~300 | **分类是 (A) 更合适**（战役模式，天梯对局里不会走），但脚本因为名字含 `Kredit`/`Defense` 这些玩法动词把它们归了 C。它们**不影响天梯对局**，所以没做；要刷 ⑥ 的计数可以一行 `= no-op`，但那是刷指标，没做。 |

### 5.2 明确**不做**的（行为中性、只影响计数）

* `IsExile` 做了（参考实现就是无条件 false，语义等价）。
* `HasCampaignUpgrade`、`ShouldGotchaTrigger`、`IsGotcha`、`CustomName*` 之外的
  「注册成常量 false 就能刷绿」的键**一律没做** ——
  不注册至少有 ⑥ 的 `UnimplementedCalls` 报警，注册成假是**静默**的。

### 5.3 已知的近似（做了但语义不完整，别当成复刻）

| 位置 | 近似 | 影响 |
|---|---|---|
| `DiscardCardFromDeck` | **没有** `OnAttemptedDiscard` 的取消门（本内核没这个事件） | 少一次"取消弃牌"的机会，**不会多弃牌** |
| `DiscardCardFromHand` | `skipTriggers`（`a[2]`）不生效 | 传 true 的调用点会多广播一次 `OnOtherCardDiscarded`（全卡池该位都是 false，暂无影响） |
| `HasAttackLeft` | 没有 `maxAttack` 字段，按"本回合没攻击过"近似（参考实现同） | Fury 的多次攻击未建模 |
| `GetCardsInSupportLineBySide` | `includeCovertCards` 恒真（`IsUnrevealedCovertCard` 是恒 false 桩） | 与蓝图在本内核下的行为一致 |
| `SpawnCardInFrontline` | `campaignName`（`a[3]`）、`salvageFaction`（`a[6]`）不实现 | 战役命名 / Salvage 未建模 |
| `CustomName2*` | 参考实现把 `CustomName2` 存进 `customName`，本内核用独立的 `customName2` 键 | 只要**读写同源**就等价；已用 `CardApi.CustomNameHasAttribute` 强制同源 |
| `IsLocationFull` | 精确槽号受 `GameState.NormalizeLocationNumbers` 压缩影响 | 自测因此只断言"两张占不同槽位"，不断言精确槽号 |

---

## 6. 复现命令

```powershell
# 分类表（修复前 / 修复后，同一判据）
python out\audit\missing-keys-classify2.py --prefix     # → missing-keys-classify2-prefix.txt
python out\audit\missing-keys-classify2.py              # → missing-keys-classify2-after.txt

# 与 C# 守卫对账（两边结果必须一致）
python out\audit\dispatch-gap-parity.py

# 某个键的直译函数体 / IR 调用点 / 参考实现
python out\audit\gap-bodies.py SpawnCardInFrontline
python out\audit\ir-callsites.py SpawnCardInFrontline
python out\audit\ref-sim-coverage.py ShouldGotchaTrigger

# 守卫
dotnet run --project tools\BotSim -c Release -- dispatch-gap
dotnet run --project tools\BotSim -c Release -- selftest

# 回放审计（4 局）
& out\audit\audit-all-replays.ps1 -Dir "$PWD\out\_server-replays" -Proj "$PWD\tools\ServerBridgeTest"
```
