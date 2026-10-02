# `CardDispatch.Fire` 只传 2 个实参：参数 >2 的事件效果被**静默跳过**（附真实对局复现）

先说明：这套「Kismet AST → C# 直译」的路线比我自己的 IR 解释器强得多
（1670 资产 / 0 空体 / 0 未实现宿主调用），下面的问题不影响这个判断 ——
正因为直译产物本身是**忠实**的，才能把这类缺口精确地定位到引擎这一侧。

我用 **5 局真实客户端对局**（从 fyserver 的只读回放接口导出，含每步动作）
拿来做黑盒验收，逐步比对动**作流里自带的状态量**。
下面两条都是这样抓出来的。

---

## Bug 1（主要）：触发载荷被截断，效果静默失效

### 复现

对局 `310284`，第 10 回合，右方：

| 动作 | 内容 |
|---|---|
| A34 `PC` | 右方打出 `card_unit_7_schutzen`（7. SCHÜTZEN，4/5） |
| A35 `AC` | 它攻击左方的 `card_unit_m16_halftrack`（2/2），**摧毁** |
| A36 `EndOfTurn` | 动作流里的状态量显示**左方 HQ 18 → 15（-3）** |

KardsSim 算出的左方 HQ 停在 **18**，缺这 3 点。
（我自己的内核同样缺这 3 点 —— 所以这不是"谁抄谁"，而是两边共有的架构缺口。）

### 根因

`card_unit_7_schutzen` 自己直译出来的逻辑
（`Generated/Germany/BrothersInArms/units/card_unit_7_schutzen.g.cs`）是：

```csharp
// 入口 10 = OnOtherCardDestroyed(cardDestroyed, killer, TriggerNotDestroyed,
//                               destroyedLocation, selfIsAlsoGettingDestroyed, destroyedInCombat)
### 根因：**三个缺陷叠在同一条链上**

我用一个定点探针（直接按真实签名调那张卡的直译产物，绕过引擎派发）
把这条链逐步验证了一遍，结论比一开始想的更深：

| # | 缺陷 | 位置 | 验证方式 |
|---|---|---|---|
| **a** | 触发载荷被截断到 2 个实参 | `Bridge/CardDispatch.cs:42-44` | 传 2 参 → 无效果（符合预期） |
| **b** | **事件桩写的「持久帧」进不到 ubergraph** | 直译产物 / 发射器 | 传 6 参后**宿主全局变量是对的**（`destroyedInCombat=true`、`killer=#61`），但效果仍被跳过 |
| **c** | `getHasVeteranUpgrade` 语义反了，`MakeVeteran` 恒不可达 | `Bridge/EngineHost.cs:210` | 守卫自相矛盾，见下 |

#### (b) 是最根本的一个

事件桩（`OnOtherCardDestroyed`）把形参写进**宿主全局变量**：

```csharp
L["destroyedInCombat"] = args.Length > 5 ? args[5] : Val.Nothing;
H.SetVar("K2Node_Event_destroyedInCombat", GetLocal(L, "destroyedInCombat"));   // → Host._vars
_ = H.Call("ExecuteUbergraph_card_unit_7_schutzen", new Val[] { self, Val.Of(10) });
```

但 ubergraph 读的是**它自己的函数局部变量**：

```csharp
L_001D:
    if (!(GetLocal(L, "K2Node_Event_destroyedInCombat")).AsBool()) goto L_01F9;   // L 里没有 → Nothing → 短路
```

`GetLocal` 是纯局部的：

```csharp
private static Val GetLocal(Dictionary<string, Val> L, string n)
    => L.TryGetValue(n, out var v) ? v : Val.Nothing;
```

**所以 `H.SetVar` 写进去的值没有任何人去读。**
我把那张卡的直译产物手工改成 `H.GetVar("K2Node_Event_destroyedInCombat")` 之后，
宿主全局确实被读到了（值正确），效果才继续往下走 —— 桥确实不在。

这一条比 (a) 严重得多：**它对全部 172 个带参事件都成立，跟参数个数无关。**
(a) 只是让第 3 个参数起变成 `Nothing`；(b) 让**所有**参数都到不了效果体。
两个叠起来，凡是"读事件参数做判断"的效果都会静默走空分支。

> 这也解释了为什么自对弈报告「未实现调用 0%」却仍有大量效果不生效：
> 参数读不到不是"未实现"，是"读到了空值"，不报错、不进 `Unhandled`。

**修法**：发射器把 `LetValueOnPersistentFrame` 的目标名（`K2Node_Event_*`）
在 ubergraph 里的**读取**发射成 `H.GetVar("...")` 而不是 `GetLocal(L, "...")`。
改动很小，但要重跑一遍转译器生成 1670 个文件。

#### (c) `MakeVeteran` 恒不可达

`BP_CardFunctions.g.cs` 里生成的 `MakeVeteran` 守卫是：

```csharp
Not_PreBool(IsVeteran(card))                     // !IsVeteran
AND( Not_PreBool_ReturnValue, getHasVeteranUpgrade(card) )
```

而宿主的实现是（`Bridge/EngineHost.cs:210`）：

```csharp
["getHasVeteranUpgrade"] = (h, a) => Out(a, h.Card_(a[0])?.Veteran ?? false),
```

它返回的是「**是否已经是老兵**」，不是「**是否有老兵升级可用**」。
于是守卫变成 `!Veteran && Veteran` —— **恒为 false，`MakeVeteran` 永远不会生效**。

影响：任何卡都变不成老兵；`Veteran` 关键字、`MakeVeteran`、
以及直译产物里 **20 个注册了 `OnBecomingVeteran` 的资产**全部失效。
（`card_unit_7_schutzen` 的 3 点伤害就在这条链的末端。）

**建议**：改成读 `card.Def` 的 `spawnCardName` / 老兵升级是否可用
（`cards.json` 里 `card_unit_7_schutzen` 的 `spawnCardName` 是
`card_unit_7_schutzen_vet`，可以在加载时预先标记成 `HasVeteranUpgrade`）。



卡面只写了「Becomes Veteran when it attacks and destroys a unit」，
**变成老兵带来的伤害文本里根本没写** —— 所以光看卡面永远推不出来。

完整的调用链是：

```csharp
// 入口 10 = OnOtherCardDestroyed(cardDestroyed, killer, TriggerNotDestroyed,
//                               destroyedLocation, selfIsAlsoGettingDestroyed, destroyedInCombat)
if (selfIsAlsoGettingDestroyed) return;
if (!destroyedInCombat) return;                 // ← 缺陷 (b) 在这里短路
if (!IsSideActive(self.side)) return;
if (!IsUnit(cardDestroyed)) return;
if (killer.cardID != self.cardID) return;       // 击杀者必须是我
if (IsVeteran(self)) return;
MakeVeteran(self);                              // ← 缺陷 (c) 在这里恒不可达

// 入口 352 = OnBecomingVeteran
side = GetOppositeSide();
hq   = GetLocationCardBySide(side);             // 敌方 HQ
DamageCard(hq, 3, self.cardID, ...);            // ← 这 3 点
```

另外 `Engine/GameEngine.Effects.cs:26` 的
`public void FireTrigger(Trigger t, Card subject)` 只接受**一个**载荷卡，
`killer` / `destroyedInCombat` / `destroyedLocation` 根本没有传递通道 ——
这是缺陷 (a) 的引擎侧根源。

而 `Bridge/CardDispatch.cs:42-44` 只传 2 个实参：

```csharp
// 事件函数的形参约定（按 ERegisteredCardFunction 的实测调用点）：
// 大多数是 (self, 相关卡) 两个；少数只有 self。多传的参数在直译产物里
// 由 args.Length 守卫，不会越界。
var args = subject is null
    ? new[] { self }
    : new[] { self, Val.Ref(host.Obj(subject)) };
```

所以 `destroyedInCombat` 落到 `Val.Nothing` → `AsBool()` 为 false
→ `if (!destroyedInCombat) return;` **直接短路，整段效果跳过**。

`args.Length` 守卫确实防住了越界，但代价是**缺的参数恒为 `Nothing`**，
而这类 `if (!xxx)` 前置守卫遍布直译产物 —— 于是效果**静默消失，不报错、不进 Unhandled**。

链条上还有两处：

1. `Engine/GameEngine.Effects.cs:26` — `public void FireTrigger(Trigger t, Card subject)`
   只接受**一个**载荷卡，`killer` / `destroyedInCombat` / `destroyedLocation` …
   这些根本没有传递通道。
2. `Core/Trigger.g.cs` 里只有 `OnOtherCardBecomingVeteran = 32`，
   **没有 `OnBecomingVeteran`**。就算 `MakeVeteran` 被调到，
   那 3 点伤害也没有触发点可用。
   （直译产物里注册了 `OnBecomingVeteran` 的资产有 **20 个**。）

### 爆炸半径

事件签名可以从事件桩里的 `LetValueOnPersistentFrame` 抽出来
（形如 `LetValueOnPersistentFrame K2Node_Event_killer = <局部 killer>`，
后面紧跟 `LocalFinalFunction ExecuteUbergraph_<资产> <入口偏移>`）。
我在 2053 个资产上扫了一遍，得到 **172 个事件**的完整签名。

按「参数个数 > 2」筛选：

```
参数 > 2 的事件：        33 个
覆盖的卡牌注册数：       383
```

| 事件 | 参数个数 | 注册卡数 |
|---|---|---|
| `OnOtherCardDestroyed` | 6 | **69** |
| `OnOtherCardDrawnFromDeck` | 3 | 43 |
| `OnAfterAttack` | 3 | 40 |
| `OnOtherCardLeaveBoardOrOwner` | 3 | 40 |
| `OnFrontlineOwnershipChange` | 3 | 28 |
| `OnOtherCardDealDamage` | 6 | 27 |
| `OnAfterOtherCardAttacks` | 4 | 23 |
| `OnCardLocationMoved` | 4 | 23 |
| `OnOtherCardMoveToFrontline` | 3 | 19 |
| `OnBeforeOtherCardDestroyed` | 4 | 6 |
| …（共 33 个） | | |

也就是说：**凡是注册了这些事件的卡，效果会按「第 3 个参数起全是 Nothing」运行**，
而带前置守卫的效果会被整个跳过。

### 建议修法

不要用「固定 2 个」这个约定，改成**按签名传载荷**：

1. 从每个事件桩抽出 `参数名 → 持久帧槽位`（就是前面那个
   `LetValueOnPersistentFrame` 序列），做成一张 `(卡, 事件) → 形参表` 的静态表。
   放在 `Generated/` 里跟着直译产物一起生成最自然。
2. `FireTrigger` 改成接收**具名载荷**（`killer` / `destroyedInCombat` / …），
   `CardDispatch.Fire` 按形参表把值填到对应位置，缺的才填 `Nothing`。
3. `Trigger` 枚举补上不带 `Other` 的自触发点
   （`OnBecomingVeteran`、`OnReceiveDamage`、`OnDealDamage`、`OnDestroyed` …），
   并在 `MakeVeteran` / `DamageCard` / `DestroyCard` 里派发。

一个低成本的过渡方案：既然直译产物已经能跑，可以在
**测试/自对弈模式下把「某个 `if (!X)` 守卫因为 `X` 为 Nothing 而短路」记进
`Unhandled`** —— 现在是完全静默的，靠自对弈的「未实现调用 0%」看不出来。

---

## Bug 2：`Blitz` 被 `SummonedThisTurn` 挡住，豁免无效

### 复现

同一局 `310284`：

| 动作 | 内容 |
|---|---|
| A24 `PC` | 右方打出 `card_unit_m20_scout_car` |
| A25 `ML` | 同一回合把它移入前线 |
| A26 `AC` | 同一回合用它攻击 |

KardsSim 拒绝了 A25 和 A26。另外两个同型例子：
A34 `PC card_unit_7_schutzen` → A35 `AC`；
A70 `PC card_unit_sd_kfz_10_38` → A71 `ML`。

### 根因

`Engine/GameEngine.Actions.cs:162-163`：

```csharp
c.SummonedThisTurn = true;
if (!c.Has(Kw.Blitz)) c.AttackedThisTurn = true;   // 非 Blitz 当回合不能打
```

意图很清楚：Blitz 单位当回合可以行动。但 `CanAttack` 又无条件挡了召唤失调：

```csharp
// Actions.cs:63
if (u.AttackedThisTurn || u.SummonedThisTurn) return false;
```

Blitz 单位 `AttackedThisTurn` 是 false，却仍然被 `SummonedThisTurn` 拒绝
→ **163 行那个 `if (!c.Has(Kw.Blitz))` 实际上是死逻辑**。
`CanMoveAtAll`（`Actions.cs:91`）同样：
`return !u.MovedThisTurn && !u.SummonedThisTurn;`

### 这条规则我做过严格验证

「召唤失调 + Blitz 例外」在 5 局真实回放里：

- **同回合「出牌 → 行动」共 6 例**：4 例的卡带 `hasBlitz`，**0 例不带**，
  2 例是作弊生成的卡（查不到卡名）。**零反例。**
- **反向**：所有**不带** Blitz 的单位，出牌后第一次行动最早也在 **+2 回合**
  （`turn_number` 按玩家回合计）—— 14 例，最小间隔 2，无一例外。

```
310284  card_unit_stug_iii        T4 出牌 → T6 ML / T6 AC        （无 Blitz）
310284  card_unit_m16_halftrack   T7 出牌 → T9 ML               （无 Blitz）
310284  card_unit_ju_87_stuka     T9 出牌 → T11 AC              （无 Blitz）
310284  card_unit_m4a1            T12 出牌 → T14 ML / T14 AC    （无 Blitz）
…
```

### 建议修法

```csharp
// CanAttack
if (u.AttackedThisTurn) return false;
if (u.SummonedThisTurn && !u.Has(Kw.Blitz)) return false;

// CanMoveAtAll
if (u.MovedThisTurn) return false;
if (u.SummonedThisTurn && !u.Has(Kw.Blitz)) return false;
```

---

## 附：我已经在本地把 (b)(c) 改掉并验证了编译

为了确认结论不是纸上推演，我在本地拉了一份 `main` 改了两处，`dotnet build` 0 错误：

**(b) 的机械修法** —— 905 个生成文件、2579 处读取：

```python
# GetLocal(L, "K2Node_Event_X")  →  H.GetVar("K2Node_Event_X")
READ = re.compile(r'GetLocal\(L, "(K2Node_Event_[A-Za-z0-9_]+)"\)')
```

模式无歧义（事件桩只写 `H.SetVar`，ubergraph 只读 `GetLocal`，
且每个生成方法都有 `IHost H` 形参），所以可以安全地脚本化，
不必重跑转译器。抽查结果：

```csharp
// ubergraph 侧（读）—— 已改
if (!(H.GetVar("K2Node_Event_destroyedInCombat")).AsBool()) goto L_01F9;
// 事件桩侧（写）—— 保持不变
H.SetVar("K2Node_Event_destroyedInCombat", GetLocal(L, "destroyedInCombat"));
```

**(c)**：`EngineHost.cs` 的 `getHasVeteranUpgrade` 改成读
`card.Def.SpawnCardNames` 里有没有 `_vet` 结尾的项。

### 但是改完这两处，那个 3 点伤害**还是没有出现**

说明这条链上还有第四环。用探针把已知的都排除之后，剩下的嫌疑集中在：

1. **`H.GetMember(card, "side")` 可能没实现。**
   `OnOtherCardDestroyed` 里第一道判定是
   `IsSideActive(self.side)`，而 `self.side` 走的就是 `GetMember`。
   如果它返回 `Nothing`，`AsInt()` 得 0 → `IsSideActive(0)` 恒假 → 整段跳过。
   （同一个 `GetMember(..., "side")` 在读 `killer.cardID` 时也用到。）
2. **`getStaticVeteranUpgrade` 全仓没有实现**（`EngineHost` / `Host` 里都搜不到）——
   `BP_CardFunctions.MakeVeteran` 会调它拿老兵卡，拿到 `Nothing` 可能中途退出。
3. `MakeVeteran` 的接收者剥离（`a[1..]`）看起来是对的，
   但它是否真的被调到，我还没直接观测到。

我在这条链上**每修一层就会露出下一层**，所以：

- 我这边会继续往下剥（探针已经能精确定位断点，成本不高）
- 但**更划算的是你那边一次性修这一类**：发射器的持久帧 + 宿主 `GetMember` 的成员表，
  这两处都是「一个约定错、整类效果静默失效」的类型，逐个 bug 修不如按约定修

如果你愿意，我可以把本地这两处改动整理成 diff / PR 发过去。
（另外那个 905 文件的脚本是**可重跑、可回滚**的，所以就算你重新生成一遍也不会丢。）



为了定位上面第一条，我扫了全部资产的事件桩，生成了一张事件签名表：

```json
"OnOtherCardDestroyed": {
  "shapes": [{ "types": ["BaseCardObject","BaseCardObject","Bool",
                         "ECardLocationEnum","Bool","Bool"], "cards": 69 }],
  "slots": {
    "K2Node_Event_cardDestroyed":             {"local":"cardDestroyed",            "type":"BaseCardObject"},
    "K2Node_Event_killer":                    {"local":"killer",                   "type":"BaseCardObject"},
    "K2Node_Event_TriggerNotDestroyed":       {"local":"TriggerNotDestroyed",      "type":"Bool"},
    "K2Node_Event_destroyedLocation":         {"local":"destroyedLocation",        "type":"ECardLocationEnum"},
    "K2Node_Event_selfIsAlsoGettingDestroyed":{"local":"selfIsAlsoGettingDestroyed","type":"Bool"},
    "K2Node_Event_destroyedInCombat":         {"local":"destroyedInCombat",        "type":"Bool"}
  }
}
```

（172 个事件全都有，含参数名、类型、目标槽位。）
需要的话我可以整理成 PR 或直接贴给你 —— 它应该正好能当上面「按签名传载荷」的输入。

两个使用上的注意点：

- **`shapes[].types` 是有序形参类型表**，这才是签名，`Fire` 要按它填。
- `slots` 里会出现 `_1` / `_2` 后缀（`K2Node_Event_killer_1` 等）。
  那是 UE 在同一张蓝图里为多个事件复用同名局部变量时的去重后缀，
  同一个事件的**无后缀**名字才是权威槽位。按 `local` 字段归并即可。

---

## 环境

- 仓库：`CCB-TEAM/kards-sim` @ `main`
- 构建：`dotnet build KardsSim/KardsSim.csproj -c Release` → 0 错误
- 复现所用的对局数据：从 fyserver 只读接口导出的 5 局真实对局
  （`GET /replays/{id}` + `GET /replays/{id}/actions`），
  每局含完整开局快照与全部动作，动作里自带行动方**对手**的 HQ 当前防御力
  （字段下标每局不同，实测 5 局分别是 84 / 24 / 37 / 91 / 68）。
