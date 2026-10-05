# PR #1 审查与**选择性移植**记录

> 对象：**`Xuewu-awa/KLink-bot` PR #1**（作者 `DinkyPuffer`，分支 `codex/dispatch-gotcha-convertcard`，
> head `775275b`，base `0264a22`，2026-10-03 提交；标题 *Implement blueprint dispatch gaps and regression coverage*）。
> 本地已 fetch 到分支 **`pr-1`**，所以下面所有复核都可以用 `git diff main...pr-1` 重放。
>
> 审查时间：2026-10-05；对照基线 `main = 5d2896b`（PR 的 base 是 `main` 的**祖先**，中间隔 49 个提交）。

## 一、结论（一句话）

**不要整体合入。** PR head 的树**不是**「`0264a22` + 新功能」，而是
**「更老的树（`DispatchGap` 冻结基线还是 538）+ Codex 新写的原语」**的混合 ——
于是 `git diff main...pr-1` 里除了新功能，还夹着**对 main 已经定案修复的回退**。
按 README §7.1 / §11.3，**没有任何一条改动附 22 局回放 A/B 或判死记录**，
而它同时改了约 20 处运行期行为与一个**冻结基线常量** ⇒ 不具备验收条件。

分类计数（子代理逐 hunk 过了一遍，报告见 `temp/pr1-review.md`）：
**UNSAFE/WRONG 14 · ALREADY-IN-MAIN 17 · DIFFERENT-SHAPE 3 · VALUABLE 6 · TEST-ONLY 3 · 纯噪声 5 组**。

## 二、本次独立复核的三条硬事实

| # | 事实 | 复核方式 |
|---|---|---|
| 1 | PR 的两条"头条功能" **`ConvertCard` 与 `GotchaTriggered` 在 main 里早就实现了** | `git grep`：`CardApiDispatch.cs:698 ["ConvertCard"]` + `:4862 DoConvertCard`；`CardApi.cs:3759 GotchaTriggered` |
| 2 | PR 的冻结基线是**过期值**，合入即自测失败 | PR `511/A0E2688CFE47DA08` vs main `tools/BotSim/DispatchGap.cs:149`=`510`、`:152`=`BC44670AFCD9720C`；`Check()` 逐位比指纹 |
| 3 | PR 加的两条 VM 运算里，`Format` 的**形状不对**；`Conv_IntToInt64` 本身对但需要配套 | IR 实测（`temp/scan-math-ops.py`）：`Conv_IntToInt64` 70 点、`Format` 67 点，且 `Format` 的真实形状是 `Format(格式串, MakeArray(FFormatArgumentData))` + UE 具名 `{ArgName}` 占位符，`string.Format({0})` 会把数组对象 `ToString` 进占位符 ⇒ 纯表现层，**不移植** |

## 三、本轮**已移植**（1 条）

### ★ 卡内私有函数的出参：必须优先取**裸名**

- **落点**：`src/KLink.Bot/Effects/Blueprint/KismetVm.cs`（`ExecuteCall` 的 locals 兜底分支）。
- **判据（全 IR 实测，`temp/scan-local-outslots.py`）**：
  - 函数体**只写裸出参名**（例：`{"op":"set","dst":"found3op"}`）、而调用点 out 槽叫
    `CallFunc_<函数>_<出参>` 的调用点 = **170 个 / 163 个 (卡,函数) 对**；
  - 函数体写全名的 = **0 个**。
  - 而旧实现 `result = bag[outNames[0]]` 里的 `outNames[0]` 正是那个**全名**
    ⇒ `Frame.Get(全名)` 找不到槽（既不是本地槽、也不是实例变量、CDO 里也没有）
    ⇒ **result 恒为 null** ⇒ 下游 `jumpIfNot(那个槽)` 恒走假分支。
- **决定性实例（有语料消费者）**：`card_unit_b_24_d`（「Costs 3 less to deploy if you control a unit with
  3 or more operation cost.」）的 `OnCardSpawnedInHand`：`i=43` 调
  `doIControl3opCostUnit(out found3op)`（函数体 `i=507/:523` 写 `found3op`）→ `i=80 jumpIfNot(found3op)`
  → `i=279 ChangeKreditCost(-3)`。旧实现下这条判断恒假 ⇒ **降费分支是死代码**。
- **自测**：`SelfTest.LocalFunctionOutParamReachesCallSite`（① 空场不降费 ② 场上有行动费 3 的单位必须降 3
  ③ 重复触发不得重复降费）。判死验证：把取回值改回 `outNames[0]` ⇒ ② 立刻失败。
- ⚠️ **如实标注**：**22 局逐位不变**（与 `docs/内核补全队列.md:9375-9387` 那次
  "试过出参双名兜底、零效果、已回退"的记录**一致**）。本轮的增量是
  **全 IR 量化 + 可判死的端到端用例**，**不是**回放侧证据 —— 与 §8.13–§8.31 那 14 轮同类。

## 四、**可移但本轮没做**（按性价比排序，留作后续）

| # | 项 | 落点（main 行号为 5d2896b） | 说明 |
|---|---|---|---|
| 1 | `Conv_IntToInt64` | `KismetVm.cs` 的 `EvalMath` + `ToInt`/`Truthy` | **必须同时**给 `ToInt`/`Truthy` 加 `long` 分支，否则 `ToInt(long)` 落 `_ => 0`、`Truthy(long 0)` 为真。IR 70 点、4 张真卡（其余是战役 location 与 UI 蓝图） |
| 2 | `DiscardRandomCardFromHand` | `CardApiDispatch.cs` 的 `DiscardCardFromHand` 旁 | 蓝图 `BP_CardFunctions.g.cs:12152-12237`；20 点、语料消费者 3 张。⚠️ 依赖 `GetHandLocationBySide`（§8.16 被 A/B **否决**过的那个键）⇒ 要连它一起重新评估，**必须 A/B**（消耗随机数） |
| 3 | `stopAction` 取消门 | `CardApiDispatch.cs:2892-2941`（`DoMakeCardRetreat` 内） | main 现在只广播、**不取出参**；蓝图 `:3611/:3613`、`:3718/:3720` 有取消门，且 `:3611` 的第一实参是"别的卡"（要逐张跑别人的程序）⇒ A/B + 判死必需 |
| 4 | `SetCountdown` / `JSON_RemoveFromIntArray` / `getKreditTempBuffAmount` / `getAttackTempBuffAmount` | `CardApiDispatch.cs` 相应族 | 小项，逐个按蓝图核对；PR 的返回/出参语义有几处反了（例：`SetCountdown` 出参应恒 `false`） |

## 五、**明确不要移植**

| 项 | 理由 |
|---|---|
| Gotcha 全族（`IsGotcha` 判据 / `ShouldGotchaTrigger` / `GotchaTriggered` 四实参 + T21 广播） | main 已按蓝图逐行实现并有 6 条判死自测；PR 用近似**覆盖**忠实实现且无 A/B。另：PR 把 `CardInstance.GotchaActivated` 写成 `bool`，而 `CardInstance.cs:68-82` 专门记了"**必须是 int**（审计：某个 PR 用了 bool）" |
| `DoConvertCard` | main 已落地（§8.31），且 PR 的形状与蓝图（"造新卡 + T34"）不符 |
| `IsUnrevealedCovertCard` 真实现 | 缺 Covert 揭示状态机 ⇒ 会把**所有** Covert 卡恒过滤（比不实现更糟，§9.5 P4） |
| `Format`（含 `PoolPureOps`） | 形状错 + 纯表现层（见上表第 3 条） |
| `BuildLocalSeed`（按位置猜私有函数入参） | 文档已记「入参 seed **零效果**、已回退」（`docs/内核补全队列.md:9383`）：对 `side` 这类名字，调用方的值与帧默认值本来就相同 |
| `HasCampaignUpgrade` / `SetObjectiveCounter` / `ReportError` / `getKreditSlotsLostBySide` | 战役/教程族（§8.24：离线内核没有战役模式）。注册 `HasCampaignUpgrade=false` **不改任何行为**，只把 247 个调用点从判据 ⑥ 里抹掉 ⇒ 稀释信号 |
| `tools/BotSim/DispatchGap.cs` 的两个常量 | 见"硬事实 2" |
| `E3–E7` 全部回退 | 见下节风险 1 |

## 六、Top 3 风险（整体合入会踩的）

1. **★★★ 回退 main 已定案的蓝图修复**：`BoardInBattleOrder`/`BattleCardsInOrder`（`GameState.cs`，`0264a22` 定案）、
   `CardsPlayedFromHandByTurn` + `getCardsPlayedFromHandByTurn` + `hasPlayedOrderThisTurn`、
   T51 广播时机（`MatchEngine.cs:1129` vs `:1152`，`854099` 证据链）、两处 `GetCardsOnBoardBySide`
   ⇒ 会把 README §9.1 的"随机候选集顺序"第三成因**重新引回来**。
2. **★★★ 冻结基线被换成过期值** ⇒ 合入即自测失败；而"顺手改常量"正是该守卫要防的"修一个 + 坏一个"。
3. **★★ 用近似覆盖忠实实现且零 A/B**（Gotcha / `ConvertCard` / `IsUnrevealedCovertCard` / `SetCardsSeenByCipher`）。

## 七、复核方法（可重放）

```powershell
cd "E:\项目\klink-dotnet\klink bot"
git merge-base main pr-1                       # ⇒ 0264a22
git diff main...pr-1 --stat                     # 8 文件 / +2181 −307
git diff main...pr-1 -- src/KLink.Bot/Effects/Blueprint/KismetVm.cs
python temp\scan-local-outslots.py              # locals 出参名对账（170 个调用点）
python temp\scan-math-ops.py                    # math 运算名与 EvalMath 对账
python temp\map-suppression-exceptions.py       # ref 的 11 条抑制例外 → 内核卡名
```
