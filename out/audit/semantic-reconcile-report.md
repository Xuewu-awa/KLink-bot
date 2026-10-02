# 逐卡「卡面 vs 实际行为」语义对账 —— Phase 1 + Phase 2 报告

脚本：`out/audit/semantic-reconcile.py`
产出：`out/audit/semantic-reconcile.tsv`（逐卡 1752 行）、`semantic-reconcile.txt`（分档明细）、
`prim-family-map.tsv`（原语→效果族映射，供人工复核）

---

## 0. 先说结论（含"方法本身行不行"）

1. **「卡面文字 vs 可观测状态」这个对账方法，用在"静态修正"类卡面上是不可行的** ——
   我在 Phase 1 第一版得到 188 个 (A)，逐条看完发现 **全部是假阳性**，根因是
   KARDS 的卡面有两类句子：**主动效果**（"Deal 2 damage to a unit."）和**静态修正**
   （"Deals double damage against tanks."）。后者由引擎级的持续修正实现，
   **根本不在卡自己的 IR `steps` 里**。用"IR 里有没有该族原语"去判它们，必然误报。
   修好判据后 (A) 从 188 → **9**，而这 9 个**仍然全是假阳性**（见 §2）。
2. **真正抓到东西的是"动态"那一侧**（调了写原语却零变化），不是"静态"那一侧。
   我据此定位并修了 **3 个真 bug**，共 **27 个烟雾用例从 `changed=0` 翻成 `changed=1`**
   （16 张卡），**4088 个用例里零回归**，`selftest` **1/116 与对照完全相同**。
3. **对"卡面 vs 实际"的机械化对账，我给出的判据质量评价是"中等偏下"**：
   (A) 档精度 0/9（全是假阳性），(B) 档 147 条里真正值得人看的约 **20 条**。
   更有价值的判据是我在过程中意外发现的**两条通用形状判据**（见 §6.3），
   它们才是可复用的、不依赖卡面文字的机械化检查。

---

## 1. Phase 1 覆盖率

| 口径 | 张数 |
|---|---|
| 卡池 `cards.live.json` | 2021 |
| IR 条目 `card-ir.json` | 1735（含 26 个非卡蓝图） |
| **参与对账** | **1752** |
| SKIP：卡面无文字 | 153 |
| SKIP：战役地图 `location` 卡（`text` 是地点描述，不是效果） | 116 |

分档：

| 档 | 张数 | 含义 |
|---|---|---|
| **(A) 明显不符** | **9** | 无条件主动效果族在 IR（steps+locals+生成的 token）里没有任何原语 |
| **(B) 可疑/歧义** | **147** | 有条件句（106）/ 只缺修正器族（41） |
| **(C) 看起来对** | **875** | 主动效果族都有对应原语 |
| **(P) 只有静态修正** | **332** | 不在本脚本判据范围内（另列） |
| **(N) 事件从不派发** | **23** | 见 §5 |
| **(L) 逻辑全在 `locals`** | **45** | 烟雾测试 0 用例，见 §5 |

动态判据（与 `smoke-all-cards.txt` 同口径的 writeCapable）：

| 档 | 张数 |
|---|---|
| OK（有状态变化） | 1057 |
| **D2（调了写原语却零变化）** | **86** |
| D1（没调写原语 + 卡面有无条件主动效果） | 215 |
| D0（没调写原语 + 卡面只有被动/条件） | 205 |
| NO-CASES（烟雾测试 0 用例） | 189 |

### ★ 顺手纠正了父级数据里的一个口径错
`SmokeAllCards.IsPurePrimitiveName` 用的是 `name.StartsWith("Get")` —— **区分大小写**，
于是内核里小写开头的查询原语（`isBuffedByCard` / `getTotalDefense` / `getHasGameplayTag` /
`getTotalKreditCost` / `getAndDecryptKredit` / `doesSideControlTheFrontline` …）被当成了「写原语」。
**`smoke-all-cards.txt` 里 D2 的「306 用例 / 157 张卡」是高估**；用 `re.I` 重算是
**86 张卡**。差的那 71 张卡基本是「扫了一遍卡池、条件不满足」。
（我**没有**去改 `SmokeAllCards.cs` 的判据 —— 按要求只在脚本里用更严的口径重算。）

---

## 2. (A) 明显不符：9 张 —— **逐条核完，全是假阳性**

| 卡 | 卡面 | 我判"缺"的族 | 实际原因 |
|---|---|---|---|
| `card_event_breaking_point` | Destroy a **Suppressed or Pinned** unit. | SUPPRESS | "Suppressed"是**目标条件**不是效果（IR 有 `IsPinned` + `DestroyCard`） |
| `card_event_fliegerfuhrer_atlantik` | Move two random Navy cards in your deck to the top. | MOVE | IR 调 `AdjustCardPositionInDeck`，是我映射表漏了 |
| `card_event_gambit` | Both players **add** the unit … to their support line. | SPAWN | IR 调 `PlayCardDirectlyFromHand`（**已在烟雾 (B) 未实现清单里**） |
| `card_event_hms_spectre` | Counter an enemy order then **return it to hand**. | BOUNCE | IR 调 `ShouldGotchaTrigger`（**已在未实现清单里**） |
| `card_event_special_assignment` | Choose a unit in hand and add it to the battlefield. | SPAWN | 同 `gambit`（`PlayCardDirectlyFromHand`） |
| `card_event_stretch_the_line` | … then **Intel** equal to friendly LEGIONS. | INTEL | IR 调 `SetCardsSeenByCipher`，映射表漏了 |
| `card_unit_1st_grenadier_regiment` | Enemy units **that damage** anything but this unit are destroyed. | DAMAGE | "damage"是**条件**不是效果 |
| `card_unit_ilyushin_il_2` | … Put it into play. | SPAWN | 同 `gambit` |
| `card_unit_tropic_lightning` | Draw a card the first time you **gain an extra kredit slot**. | KREDIT | "gain an extra kredit slot"是**条件**不是效果 |

**⇒ (A) 档没有发现任何新的硬缺口。** 6 条是我的文本判据把"条件词"读成了"效果词"、
2 条是我的"原语→族"映射表漏了原语名，1 条是已知未实现原语。
**这个结果本身是有价值的**：它说明「卡面承诺了无条件主动效果、IR 里却一条对应原语都没有」
这种硬缺口，在 1570 张有 IR 的卡里**基本不存在** —— 之前那 9 个语义修都是"跑得通但算错"，
不是"缺原语"，与这个结论一致。

---

## 3. (B) 可疑：147 条 —— 整理成可快速裁决的形式

**(B) 分两类：有条件句（106 条）/ 只缺修正器族（41 条）。**
按"值得人花时间"排序，真正需要裁决的是下面这些。

### B★ 一档：**调了已实现的写原语、状态却零变化、卡面无条件**（最可疑）

| 卡名 | 卡面 | 我们做了什么 | 歧义在哪 / 需要判什么 |
|---|---|---|---|
| `card_event_bypass` | Deal damage to target HQ equal to the total cost of your German tanks in the frontline. | 调 `DamageCard`，零变化 | 合成局面里前线没有德国坦克 ⇒ 总额 0。**需确认**：真局里前线有德国坦克时是否真的扣血 |
| `card_event_convoy_attack` | Deal **0-2** damage to any target. | 调 `DamageCard` + `RandomIntFromRangeWithStream`，零变化 | 可能只是掷出 0。**需确认**：多打几局是否偶尔为 0 伤害 |
| `card_event_campaign_alamein1_through_the_wire` | Target enemy unit **loses Guard**. | 调 `RemoveGuard`，零变化 | 合成局面里敌方没有 Guard 单位。**需确认**：对 Guard 单位是否真的摘掉 |
| `card_event_seaborne_invasion` | All enemy units in the frontline retreat. Add two US infantry units there with total attack of 6. | 调 `SpawnCardOnBattlefield`，零变化 | 前半句走 `MakeCardRetreat`（**已在未实现清单里**）；后半句可能因"前线没腾空"而失败 |
| `card_event_soviet_promo3` | Choose one - add 1 OR 3 random Soviet units to your support line. Damage your HQ equal to their defense. | 调 `DamageCard` + `SpawnCardOnBattlefield`，零变化 | 合成局面里支援线可能是满的 |
| `card_unit_kings_own_scottish` | Pincer: Damage dealt to this unit is dealt to its Pincer partner instead. | 调 `CustomAbilityRemove`，零变化 | 属 Pincer 事件链（**内核从不派发**，见 §5） |
| `card_unit_l4_grasshopper` | Your **Sherman** units cost 1 less to deploy and have Blitz. | 调 `ApplyTheBuff`，零变化 | 合成牌库里没有 Sherman ⇒ 无对象可 buff |
| `card_location_british_scen4_ai` | The town of Mateur is one of the main bases for Axis units in Tunisia. | 调 `CustomName2HasAttribute`，零变化 | 战役地图卡，`text` 是地点描述，我的判据本不该收它 |

**⇒ 这一档里"确定是 bug"的 0 条；11 条已经被我在 §6 修掉了（原先也在这个表里）。**
剩下的每一条都能用"合成局面不满足"解释，但**我没有真实对局证据**，所以按"拿不准进 B"处理。

### B 二档：**条件句 + 缺族**（106 条，抽最值得看的）

这类里最需要判的是「**条件门的具体判据**」（读的是哪个 HQ / 哪个阵营 / 哪个时机）：

| 卡名 | 卡面 | 歧义在哪 |
|---|---|---|
| `card_event_spirit_of_rome` | Target friendly infantry gets +1+1 for **every defense your HQ has over the enemy HQ**. | 合成局面双方 HQ 防御相等 ⇒ 0。**判**：真局里差额算法是否一致 |
| `card_event_case_blue` / `brute_force` / `sustained_pressure` | Deal N damage … **for each** <条件> | "每个"的计数口径（含不含自己 / 含不含 HQ） |
| `card_event_focused_attack_ger` | Your infantry and tanks get +1+1 **for each unit type you control**. | "兵种"的定义（5 种？含不含 location？） |
| `card_unit_78th_yongsan` | Has +1 attack **for each kredit slot the enemy has over you**. | 读的是 max slot 还是当前 kredit |
| `card_unit_76th_napoli` | Gets +1 attack **for each defense your HQ has over the enemy HQ**. | 同 `spirit_of_rome` |
| `card_unit_henschel_hs_126` | Give a friendly tank +1+1 **for each non-tank unit type in the same line**. | "same line" 指支援线还是前线 |
| `card_event_committed_crew` | Until end of turn, **Spitfires** cost 0 to deploy and get +3+3 **when deployed**. | "when deployed" 的时机 + Spitfire 的识别口径 |
| `card_unit_katyusha` / `_cam1` | When KATYUSHA attacks, it deals **0-1 additional damage**. | 随机数的消费时机（锁步下会影响 RNG 游标） |
| `card_unit_katzmann` / `card_unit_3rd_kure_snlf` | Increase/Reduce operation cost by 1 **until end of turn, each time …**. | "until end of turn" 的到期时机 |
| `card_unit_yamagata_regiment` | Has Ambush, Blitz and Fury **if you have no cards in hand**. | 判据时机（触发时 vs 结算时） |
| `card_unit_2nd_west_africa` / `arado_ar_196` / `regia_marina` | Draw the cheapest/highest order … **Repeat if it did not start there**. | "did not start there" 的判据（初始牌库归属） |
| `card_event_workers_unite` | **Discard your LIGHT INFANTRY units.** Gain a kredit and draw a card for each. | 合成手牌里没有 LIGHT INFANTRY |

**⚠️ 这一档我按你的要求没有硬判**，因为它们的歧义都在"触发时机 / 条件门判据 / 数值细节 /
目标范围"这四类上 —— 正是你划定的界限。

---

## 4. (C) 看起来对：875 张（不逐条列）

---

## 5. 两个单独分出来的类（**与"原语缺失"分开**）

### (N) 事件从不派发：23 张

判据：卡注册的**全部** `entrypoints` 都不在 `SmokeAllCards.LiveEntrypoints`
（= 内核源码里出现过的入口名字面量，64 个）里，且名字不像 UI/动画。
IR 里注册的入口名共 **449** 个，剔除 UI/动画名后仍有 **53 个玩法相关入口**内核从不派发。

| 死入口 | 卡数 | 卡名 |
|---|---|---|
| `OnPincerEffectApplied` / `OnPincerEffectRemoved` | 7 | `card_unit_2nd_para_c`、`54_jager_regiment`、`cruiser_mk_iii`、`irish_guards`、`panzer_iii_g_desert`、`tupolev_sb_2`、`91st_astrakhan`(OnPincerEffectReceived) |
| `OnIntelTriggered` | 3 | `card_unit_intel_fusiliers`、`legion_pol`、`nakajima_b5n2` |
| `OnOtherCardLoseSmokescreen` | 3 | `card_event_shock_attack`、`card_unit_hirosaki_regiment`、`p1y2_kasho` |
| `OnOtherCardConverted` | 2 | `card_unit_312th_novgorod`、`51st_rifle_brigade` |
| `OnCreateCardApplyCampaignUpgrades` | 2 | `card_unit_33rd_livorno_cam1`、`m3_stuart_cam1` |
| `OnOperationKreditsSpent` / `OnOtherCardOperationKreditsSpent` | 1 | `card_unit_2nd_michigan` |
| `OnOtherCardForecasted` / `OnOtherUnitPinned` / `OnLoseMobilize` / `OnSuccesfulDiscard` / `OnOtherCardSalvaged` | 各 1 | `2_2nd_pioneers`、`cromwell_mk_iv`、`43e_regiment_motorise`、`sally`、`winter_regiment` |

**⇒ 这 23 张卡"没效果"的原因是事件根本没发，不是原语没实现。**
其中 **Pincer 链（7 张）+ Intel 链（3 张）+ Lose Smokescreen（3 张）= 13 张**是三条完整的事件链缺口，
按"一条链一次修"的性价比最高。

### (L) 逻辑全在 `locals`：45 张（**烟雾测试 0 用例**）

`card-ir.json` 里这 45 张卡的 `entrypoints` 是**空的**，逻辑全在 `locals`
（`OnCardDealDamage_ModifyDamageDealt` ×26、`OnOtherCardDealDamageAddDamage` ×16 …）。
`SmokeAllCards` 按 `card.Entrypoints` 枚举用例 ⇒ **这 45 张卡一个用例都没有**。
它们**不是没实现**，是**没被测**。内核确实按名字调它们
（`CardApi.ExecuteOnDealDamageAddDamage` → `RunOwnLocal(dealer, "OnCardDealDamage_ModifyDamageDealt")`）。

**⇒ 这是烟雾测试的一个真实覆盖缺口：98 张卡（entrypoints 空）零覆盖。**
建议：给 `SmokeAllCards` **加一个新模式**（不动原判据），直接按 `locals` 的函数名跑一遍。

---

## 6. 我修了什么（3 个真 bug，全部有前后对照）

三条判据：`dotnet build src\KLink.Bot -c Release` **0 error**；
`selftest` **1/116**，与"把修复关掉"的对照组**完全相同**（那 1 项是已知的
`gordon_highlanders`）；全卡池 4088 用例 **+27 个 0→1、0 个 1→0**。

### 6.1 卡蓝图成员变量的 CDO 默认值没进 IR ⇒ `damageToDeal` 恒为 0（4 张卡）

- **文件**：`src/KLink.Bot/Cards/CardVarDefaults.cs`（新增）、`KismetVm.cs` 的 `Frame.Get`
- **证据**：`gen-kismet-ir.py` 只编字节码、不编 CDO 默认值。`card_event_firestorm_skirm`
  的 `OnPlayedFromHand` 是「攒 `cardsToDamage` → `DamageMultipleCards(cardsToDamage,
  damageToDeal, cardID, out)` @ i=382」的循环，整张卡**没有任何 `set damageToDeal`**
  ⇒ 读成 null ⇒ 0 伤害。**判决性对照**：同族的 `card_event_wave_after_wave` 是唯一
  自己写 `damageToDeal` 的（i=557 设 4 / i=660 设 2），**它是那一族里唯一跑得出变化的**。
  CDO 原文（`decompiled/cards.all.json` → `cdo`）：
  `firestorm_skirm="2"` / `carpet_bombing="3"` / `maelstrom="4"` / `retribution5="2"`，
  **与卡面数字逐个吻合**。
- **效果**：8 个用例 `changed=0,D → changed=1,OK`。
- **全池扫描**：IR 里"裸读但同程序内从没被 set、且 CDO 里有默认值"的卡共 **10 张**；
  其中 `faction`×2、`hasMobilize`×1 已被 `GetMember` 认掉；`buffActive`/`friendlyAttacked`/
  `A6M2Effect`×3 是 `JSON_GetBool` 的**键名**（读写用同一个错键、行为自洽）⇒ **没验证过就不动**，留在 (B)。

### 6.2 `DrawSpecificCardFromDeckBySide` 46 个调用点全部静默 no-op（27 张卡零变化）

- **文件**：`src/KLink.Bot/Effects/CardApiDispatch.cs` 的 `DoDrawSpecific`
- **证据**：46 个调用点的 `a[1]` 形状分布是 `MEMBER(cardID)`×38 / `VAR(cardID)`×5 /
  `VAR(tmpCard|cardFound|cardToDraw)`×3 —— **0 个是 `"card_xxx"` 名字字面量**。
  而旧实现只调 `FindCardNameArg`，判据是「以 `card_` 开头的**字符串**」⇒ **全部返回 null**。
  实测：调用它的 41 张卡里 **27 张状态零变化**，含卡面极明确的
  `card_event_arctic_convoy`「Draw two random units from your deck.」
  （它确实调了 `GetRandomCard`、消耗了 2 次随机数，却什么都没抽上来）。
- **为什么一直没被发现**：它在派发表里 ⇒ **不计入「未实现原语」**，
  烟雾测试只看到「零变化」，而"零变化"被归进了 D1/D2 的启发式里。
- **效果**：16 个用例翻转，10 张卡（`arctic_convoy`/`gathering_storm`/`hidden_plans`/
  `regia_marina`/`second_chance`/`spars`/`2nd_west_africa`/`arado_ar_196`/`dinah_iii`/`yak_9`）。
  另外 17 张没翻是因为合成牌库里没有它要的类别（Commando/Covert/countermeasure）——
  **这也说明"零变化"在合成局面下确实大量是局面问题**。

### 6.3 `setArray`（MakeArray）静默丢掉整数 cardID 元素（2 张卡）

- **文件**：`KismetVm.cs` 的 `case "setArray"`
- **证据**：旧实现 `if (Eval(a,…) is CardInstance c) items.Add(c);` 只收 `CardInstance`。
  而 `card_event_high_altitude_bombing`（「Destroy two random enemy units.」）的
  `i=496 setArray` 两个元素**都是** `{"var":"cardID","ctx":{"var":"…randomCard"}}`
  ⇒ 组出来的数组是**空的** ⇒ `DestroyMultipleCards` 一张都处理不到 ⇒ 零变化。
  全池 182 个 `setArray` 站点里，元素含 `MEMBER(cardID)`/`VAR(cardID)`/`INT` 的有 **20 个**。
- **修法**：**严格增量** —— 有 `CardInstance` 元素（或空数组）时照旧产出
  `List<CardInstance>`（与旧实现逐位一致）；**一个都没有**时原样保留，交给
  `CardApi.EvalList`（认任意 `IList`）+ `AsCardOrId`（实例↔ID 互通）消费。
- **效果**：3 个用例翻转（`high_altitude_bombing` ×2、`35th_infantry_regiment` ×1）。

### ★ 这三条的共同形状（**这才是可复用的判据**）
> **IR 里的实参是"整数 cardID / 卡实例"，而内核的某个原语实现只认另一种形状 ——
> 不报错、静默 no-op。** 它在派发表里 ⇒ 烟雾测试的"未实现原语"计数抓不到，
> 只能靠"调了写原语却零变化"（D2）+ 人工看实参形状抓。
>
> 我建议把它机械化：**对每个原语，统计它所有调用点的实参形状；如果某个原语的实现
> 只认形状 A，而 ≥1 个调用点传的是形状 B，就是嫌疑**。
> `out/audit/argshape-callsites.json`（已存在，6.8 MB）看起来正是这份数据的基础。

---

## 7. 没做成 / 没验证的（如实说）

1. **(A) 档精度 0/9。** 我的"卡面文字→效果族"判据在"条件词当效果词"和"原语名映射不全"
   两处系统性偏保守/偏松，**这一档没有产出任何新发现**。若要继续做，
   应该放弃"文字→族"，改用 §6.3 的"实参形状 vs 实现形状"对账。
2. **(B) 档 147 条我没有硬判**，只按"值得人看"排了序（§3 给了约 20 条）。
   106 条"条件句"里的歧义（时机/门判据/数值/目标范围）**正是你划定的界限**，
   我给不出结论。
3. **`locals`-only 的 45 张卡我完全没有动态验证** —— 烟雾测试 0 用例，
   我只做了静态的"IR 里有没有该族原语"。要真正验证需要给 `SmokeAllCards` 加新模式
   （按 `locals` 函数名跑），我**没有做**（怕动测试台影响你已有的可比性）。
4. **我没有跑真实对局验证这 3 个修复**（按要求不部署）。修复的正确性证据是
   "烟雾用例翻转 + selftest 无回归 + CDO 原文 + 卡面数字吻合"，**不是**回放对拍。
   `damageToDeal` 那 4 张（FIRESTORM / CARPET BOMBING / MAELSTROM / RETRIBUTION）
   和 `DrawSpecificCardFromDeckBySide` 那 10 张是**最容易在真实对局里验证**的：
   打出去看手牌/场上有没有变化即可。
5. **`buffActive` / `friendlyAttacked` / `A6M2Effect`（3 张卡）我没动**：
   它们在 CDO 里的"默认值"就是键名本身（提取器把 Name 型默认值写成了自己的名字），
   而读写用的是同一个（可能错的）键、行为自洽。**改不改我判断不了，留给你**。
6. **`SmokeAllCards.IsPurePrimitiveName` 的大小写 bug 我没有去改源码** ——
   按"别动判据"的要求，我只在自己的脚本里用更严的口径重算并报了差异（157 → 86 张卡）。
   如果你要，改法是给那个方法加 `StringComparison.OrdinalIgnoreCase` 并把 `Does/Did` 加进前缀表。

---

## 8. 建议的下一步（按性价比）

1. **用 §6.3 的"实参形状 vs 实现形状"对账扫一遍全部 924 个原语** ——
   这是唯一一条被证明"能抓到 D2 类真 bug"的机械化判据，而且我已经有 3 个正例。
2. **修 Pincer / Intel / Lose Smokescreen 三条事件链**（13 张卡，一次一条链）。
3. **给 `SmokeAllCards` 加 `locals` 模式**，把 45+53 张零覆盖的卡纳入测试。
4. `SmokeAllCards.IsPurePrimitiveName` 加大小写不敏感（否则 D2 表长期虚高 45%）。
