# CanCardBeBuffed 矛盾调查

> 只读调查。**没有改 `src/` 或 `tools/` 的任何一行**；新增脚本全部在 `out/audit/ccbb-*.py`。
> 调查日期：本次会话。素材：`out/bp-cardfn.json`、`out/xr-cardfunctions.bpasm`、`klink bot/live/.../BP_CardFunctions.uasset`、
> `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`、`klink bot/kards1.60_No_UE4SS.jmap`、
> `E:\peoject\kards\Source\...`、`klink bot/docs/card-ir.json`、`src/KLink.Bot/Effects/CardApi*.cs`（只读）。

---

## 1. 一句话结论

**矛盾解开了。正确答案是：`CanCardBeBuffed` 对「在场」单位返回 `true`。**

原因不是蓝图怪，是**我们把蓝图里一条 `JumpIfNot` 的分支极性读反了**（假设 1 成立，假设 2/3/4 全部排除）。
`CanCardBeBuffed` 的 `location` switch **只对「未揭示的隐蔽卡」生效**，对普通卡根本不进 switch，直接返回 `true`。
我们的 `CardApi.CanCardBeBuffed` 把这个 switch 套在了**所有**卡上，于是造出了「在场 → false」这个不存在的结论。

蓝图的真语义（逐字）：

```
CanCardBeBuffed(Card):
    if (!Card.IsUnrevealedCovertCard())  →  CanBeBuffed = true; return     ; si=41 JumpIfNot -> si=730
    switch (Card.Location):                                                 ; si=55..711
        NotAvailable(0)      -> false
        Deck_Left(1)         -> true
        Deck_Right(2)        -> true
        Hand_Left(3)         -> true
        Hand_Right(4)        -> true
        Board_HQLeft(5)      -> false
        Board_HQRight(6)     -> false
        Board_Frontline(7)   -> false
        Discard(8)           -> false
        Deck(9)              -> true
        其它                 -> false（out 参数默认值，si=725 Jump -> si=773 Return，不赋值）
```

⇒ **「红牛/爱国热忱/敢死队/3掷弹兵」的自测与蓝图不冲突了**：那些卡的 `ChangeAttack(self,…)` 目标是在场单位，
门对在场单位**放行**。
⇒ **`GiveAlpineBonus` 不是恒空函数**：它的 si=37 守位对在场单位放行。
⇒ 但 `CardApi.CanCardBeBuffed`（`src/KLink.Bot/Effects/CardApi.cs:851`）**是错的，必须改**（见 §6）。

---

## 2. 四个假设逐条验证

### 假设 1「我们的 `CanCardBeBuffed` 实现是错的」——**成立**

`src/KLink.Bot/Effects/CardApi.cs:830-857` 现在的实现：

```csharp
/// si=41   JumpIfNot(IsUnrevealedCovertCard(Card)) -> si=730   ; 未揭示的隐蔽卡 ⇒ 可以直接 buff
/// si=55..711  switch(Card.location)： ... 5/6(半场/HQ) 7(前线) 8(弃牌堆) → si=746  False
public static bool CanCardBeBuffed(CardInstance card) => card.Location switch
{
    CardLocation.DeckLeft or CardLocation.DeckRight => true,   // 1 / 2
    CardLocation.HandLeft or CardLocation.HandRight => true,   // 3 / 4
    CardLocation.Deck => true,                                 // 9
    _ => false,                                                // 0 / 5 / 6 / 7 / 8
};
```

**switch 表本身是对的**（0/5/6/7/8→false，1/2/3/4/9→true，已逐条核对，见 §3）。
**错的是它被套在了哪些卡上**：蓝图里 switch 是「`IsUnrevealedCovertCard` 为真」那一支，
而 C# 把它当成了「所有卡」那一支。注释里那句「未揭示的隐蔽卡 ⇒ 可以直接 buff」也是同一个极性错误的产物——
按 UE 语义，`JumpIfNot` 是**条件为假时跳**，跳到 `si=730`（`CanBeBuffed = True`）意味着
**「不是未揭示隐蔽卡」⇒ true**，不是「是隐蔽卡 ⇒ true」。

极性判定不是靠猜，靠三条独立证据（§3.2）。其中一条是**同一资产里语义自明的校准点**：

| 出处 | 字节码 | 唯一合理的读法 |
|---|---|---|
| `ChangeAttack` si=57 | `JumpIfNot(IsValid(card)) -> si=393`，si=393 是 `ClientLoggerFunctions` 的报错分支 | 卡无效才去报错 ⇒ JumpIfNot 在**假**时跳 |
| `GiveSalvage` si=141 | `JumpIfNot(_card.hasSalvage) -> si=254`，si=254 起是「发 Salvage」的函数体（si=865 `_card.hasSalvage = True`），141..253 之间只有 `si=177 POP-FLOW`（提前返回） | 「**没有** salvage 才去发」 ⇒ JumpIfNot 在**假**时跳 |
| `ChangeAttack` si=934 | `JumpIfNot(EqualEqual_IntInt(getAndDecryptAttack(card), amount)) -> si=960`，si=948 是 `valueChanged = False` + `POP-FLOW` | 「数值**没变**才提前返回」 ⇒ JumpIfNot 在**假**时跳 |

反向假设（JumpIfNot 在真时跳）会让 `ChangeAttack` 只在卡**无效**时才继续执行、让 `GiveSalvage` 只给**已经有** salvage 的卡发 salvage。两条都是荒谬的。

**这条同时解释了两个"矛盾"**：矛盾①本来就不是矛盾——是我们自己实现的产物；矛盾②根本不成立——门对在场单位放行。

### 假设 2「存在同名不同义的多个 `CanCardBeBuffed`」——**排除了**

- 全仓二进制搜 `CanCardBeBuffed`：**唯一一个 `.uasset` 命中**是
  `klink bot/live/Cards/kards/Content/Blueprints/Cards/BP_CardFunctions.uasset`（2 次：一次在名字表，一次在 `LocalVirtualFunction` 引用）。
- `out/bp-cardfn.json`（= BP_CardFunctions 全量）52 次；`out/bp-onlinematch.json` / `bp-logic.json` / `bp-gamestate.json` / `bp-notifier.json` / `bp-matchcontroller.json` / `bp-cardscheck.json` 全部 **0 次**。
- `klink bot/kards-5.6.1-…jmap`（另一版本的 usmap）里，它的宿主只有一个：
  `"/Game/Blueprints/Cards/BP_CardFunctions.BP_CardFunctions_C:CanCardBeBuffed"`。
  其余命中全是**调用方**的临时变量名 `CallFunc_CanCardBeBuffed_CanBeBuffed`。
- C++ 侧没有这个符号（`E:\peoject\kards\Source` 全树 grep 无）。
- 我另外把**属性名**的 casing 统计了一遍（`out/audit/ccbb-defs.py`），确认没有「两个 `location`」这种事，见下。

⇒ 只有一份定义，没有重载，没有同名不同义。

### 假设 3「调用点上下文不同 / 走的不是这个门」——**排除了**

17 个调用点全部是同一个函数、同一个参数位（第 1 个参数 = **目标卡**）、同一个守位形状（见 §4）。
没有第二个 `CanCardBeBuffed` 重载，所以 `GiveAlpineBonus` 的 si=5 就是这道门，不是别的。
"门的意思是只能给手牌/牌库加 buff"这个读法被 §3.2 的极性证据直接否掉。

### 假设 4「客户端守 buff 记账，在场数值由服务端权威结算」——**排除了**

上一轮提的这个可能是**错的方向**，而且有直接反证：

`out/audit/ccbb-indirect.py` 扫出 **`BP_OnlineMatch` 的服务端 action 接收侧**就在调这些带门的函数：

```
ReceiveActionGainAttack        si=1859 -> ChangeAttack
ReceiveActionGainDefense       si=865  -> ChangeDefense
ReceiveActionLoseAttack        si=814  -> ChangeAttack
ReceiveActionSetKreditCost     si=1037 -> ChangeKreditCost
ReceiveActionCustomAbilityAdd  si=656  -> CustomAbilityAdd
ReceiveActionGiveAlpine        si=413  -> GiveAlpine
ReceiveActionGiveGuard         si=395  -> GiveGuard
ReceiveActionGiveAmbush/Blitz/Bond/Fury/Immune/Mobilize/Salvage/Shock/Smokescreen -> 同名函数
```

也就是说：**服务端发 `ZActionGainDefense` → 客户端 `ReceiveActionGainDefense` → `ChangeDefense` → 走 `CanCardBeBuffed` 这道门。**
如果这道门对在场单位返回 false，客户端会把服务端权威下发的在场单位数值改动**静默丢掉**（`si=472 qqq=False; return`）→ 必然 desync。
所以这道门不可能是在场单位的拒绝门。假设 4 不成立。

补一条：`BP_CardFunctions` 里**没有任何 `ZAction*` 调用**（唯一 `Notify*` 是 `NotifySideEffectTrigger`，与 Alpine 无关），
也没有任何 Alpine 专属的 `Notify*`。服务端/客户端分工这个方向在这份素材里找不到支撑。

---

## 3. `CanCardBeBuffed` 的完整函数体

### 3.1 三个互相独立的解码器，结果完全一致

| # | 解码器 | 产物 | 关键片段 |
|---|---|---|---|
| 1 | UAssetCLI / UAssetAPI（C#） | `out/bp-cardfn.json` | `si=41 JumpIfNot Offset=730`；`si=730 LetBool CanBeBuffed = True` |
| 2 | `E:\bpasm\bpasm.exe disasm`（独立工具，读原始 uasset） | `out/xr-cardfunctions.bpasm:8900` `.export 26 "CanCardBeBuffed"` | `jumpifnot @L730 { localvariable "CallFunc_IsUnrevealedCovertCard_isIt" }` |
| 3 | kards-sim 生成的 C#（第三方反编译器） | `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:5400` | `if (!(GetLocal(L,"CallFunc_IsUnrevealedCovertCard_isIt")).AsBool()) goto L_02DA;` 而 `L_02DA: L["CanBeBuffed"] = Val.True;` |

注意 #3 的标签是**十六进制字节偏移**：`L_02DA`=730、`L_02EA`=746、`L_02FA`=762、`L_0305`=773 —— 和 #1 的 `Offset`/#2 的 `@L…` 精确对上。
三方在「偏移坐标系」和「跳转目标」上完全一致，可以排除单个工具的标注错误。

### 3.2 原始字节码（`out/xr-cardfunctions.bpasm:8900-9098`，节选 + 逐条解读）

```text
.export 26 "CanCardBeBuffed" {
    context { localvariable @path(owner=27) "Card" } 19 @path(owner=null) {
        finalfunction @import(250) "IsUnrevealedCovertCard" {
            localvariable @path(owner=27) "CallFunc_IsUnrevealedCovertCard_isIt"
            endfunctionparms
        }
    }
    jumpifnot @L730 { localvariable "CallFunc_IsUnrevealedCovertCard_isIt" }
    letbool { localvariable "K2Node_SwitchEnum_CmpSuccess" } {
        callmath @import(155) "NotEqual_ByteByte" {
            context { localvariable "Card" } 9 @path(owner=-48) "location" {
                instancevariable @path(owner=-48) "location" }
            byteconst 0
            endfunctionparms } }
    jumpifnot @L746 { localvariable "K2Node_SwitchEnum_CmpSuccess" }
    ... byteconst 1 -> jumpifnot @L762
    ... byteconst 2 -> jumpifnot @L762
    ... byteconst 3 -> jumpifnot @L762
    ... byteconst 4 -> jumpifnot @L762
    ... byteconst 5 -> jumpifnot @L746          <-- 注意：5 是 False
    ... byteconst 6 -> jumpifnot @L746
    ... byteconst 7 -> jumpifnot @L746
    ... byteconst 8 -> jumpifnot @L746
    ... byteconst 9 -> jumpifnot @L762
    jump @L773
    letbool { localoutvariable "CanBeBuffed" } { true }      ; <-- L730（第一条 jumpifnot 的目标）
    jump @L773
    letbool { localoutvariable "CanBeBuffed" } { false }     ; <-- L746（0/5/6/7/8 的目标）
    jump @L773
    letbool { localoutvariable "CanBeBuffed" } { true }      ; <-- L762（1/2/3/4/9 的目标）
    return { nothing }
    endofscript
}
```

`out/audit/ccbb-decode.py decode` 输出的 30 条语句（同一份数据，便于对 si）：

```text
si=0      (Card).IsUnrevealedCovertCard(out CallFunc_IsUnrevealedCovertCard_isIt)
si=41     JUMP-IF-NOT(CallFunc_IsUnrevealedCovertCard_isIt) -> 730
si=55     K2Node_SwitchEnum_CmpSuccess = NotEqual_ByteByte(Card.location, 0)
si=108    JUMP-IF-NOT(K2Node_SwitchEnum_CmpSuccess) -> 746
si=122    ... NotEqual_ByteByte(Card.location, 1)      si=175  -> 762
si=189    ... 2                                        si=242  -> 762
si=256    ... 3                                        si=309  -> 762
si=323    ... 4                                        si=376  -> 762
si=390    ... 5                                        si=443  -> 746
si=457    ... 6                                        si=510  -> 746
si=524    ... 7                                        si=577  -> 746
si=591    ... 8                                        si=644  -> 746
si=658    ... 9                                        si=711  -> 762
si=725    JUMP -> 773                       ; 其它枚举值：不赋值，直接返回（out 默认 false）
si=730    bool CanBeBuffed = True           ; ★ 第一条 JumpIfNot 的目标
si=741    JUMP -> 773
si=746    bool CanBeBuffed = False          ; 0/5/6/7/8
si=757    JUMP -> 773
si=762    bool CanBeBuffed = True           ; 1/2/3/4/9
si=773    RETURN
si=775    END-OF-SCRIPT
```

**解读**（`JumpIfNot` 语义已由 §2 假设 1 的三条校准点钉死 = 条件为假时跳）：

```
CanBeBuffed = false                                  ; out 参数默认值
if (Card.IsUnrevealedCovertCard() == false) {        ; si=41 跳到 730
    CanBeBuffed = true;  return;
}
switch (Card.Location) { ... 见上表 ... }
return CanBeBuffed;
```

等价写法（同一张图的另一种画法）：`if (隐蔽卡) { switch(location) } else { true }`。

### 3.3 `Card.location` 就是 `ECardLocationEnum Location`

这一条单独查过，因为它是「0..9 到底是不是位置枚举」的前提：

1. 字节码里是 `Context(Card).location`，用 `NotEqual_ByteByte` 比较 ⇒ 属性是 **1 字节枚举**。
2. usmap 里 `BaseCardObject` 的属性表第 63 项：
   `{"name": "Location", "offset": 629, "size": 1, "type": "EnumProperty", "enum": "/Script/kards.ECardLocationEnum"}`
   —— 1 字节、枚举、`ECardLocationEnum`。
3. `klink bot/live/.../BP_CardFunctions.uasset` 名字表里 `Location` 与 `location` 是**同一个 FName 的两种 display casing**：
   ```
   pos=37770  len=9  name=Location   紧随 4 字节 = 71 7c b0 c2
   pos=37786  len=9  name=location   紧随 4 字节 = 71 7c 62 1f
   ```
   前两字节（UE 的**大小写无关**比较哈希）**完全相同** `71 7c`，只有后两字节（保留大小写的 display 哈希）不同。
   ⇒ `Card.location` 与 `Card.Location` 是同一个属性（UE 的 FName 查找本来就大小写无关）。
4. `E:\peoject\kards\Source\kards\Public\ECardLocationEnum.h` 正好 **10 个值**，且顺序就是 switch 里的 0..9：
   `NotAvailable, Deck_Left, Deck_Right, Hand_Left, Hand_Right, Board_HQLeft, Board_HQRight, Board_Frontline, Discard, Deck`。
5. switch 的 true/false 分组（1/2/3/4/9 vs 0/5/6/7/8）正好是「牌库+手牌 vs 半场+前线+弃牌堆+未就位」，
   在位置枚举上语义自洽；在别的 10+ 值枚举（如 `ETypeEnum`）上完全不成立。

### 3.4 `IsUnrevealedCovertCard` 读不出来（如实报告）

它是**原生函数**（`BaseCardObject.h:849  void IsUnrevealedCovertCard(bool& isIt);`），
SDK 里的实现是空壳（`BaseCardObject.cpp:296` 函数体为空），`bp-cardfn.json` 里也没有它。
所以「什么样的卡算未揭示隐蔽卡」我**读不出来**。
已知的相关字段：`bool hasCovert;`（`BaseCardObject.h:193`）、`bool isRevealed;`（`:310`）、`hasCovert` 初值 false（`BaseCardObject.cpp:41`）、`isRevealed` 初值 false（`:74`）。
合理猜测是 `hasCovert && !isRevealed`，但**这是猜测，不作为结论**。

**这不影响结论**：本内核没有建模 Covert（见 §6），`IsUnrevealedCovertCard` 恒假 ⇒ `CanCardBeBuffed` 恒真。

---

## 4. 全部调用点清单

**定义处**：`/Game/Blueprints/Cards/BP_CardFunctions.BP_CardFunctions_C:CanCardBeBuffed`（唯一一份）。
**直接调用点**：`BP_CardFunctions` 内部 **17 处**（`out/audit/ccbb-sites.py`）。全部传「**目标卡**」+ out 参数，形状统一为 `if (!CanCardBeBuffed(x)) 放弃`。

| # | 函数 | 调用 si | 传进去的卡 | 守位语句 | 期望 |
|---|---|---|---|---|---|
| 1 | `ChangeAttack` | 71 | `card` | si=103 `JumpIfNot -> 472`（`qqq=False`+return） | 改攻 |
| 2 | `ChangeDefense` | 48 | `card` | si=80 `JumpIfNot -> 466` | 改防 |
| 3 | `ChangeKreditCost` | 385 | `card` | si=417 `JumpIfNot -> 369` | 改费（手牌卡） |
| 4 | `CustomAbilityAdd` | 99 | `cardToChange` | si=131 `JumpIfNot -> 545` | 加能力 |
| 5 | `GiveAlpine` | 51 | `_card = GetCardFromID(cardID)` | si=83 `JumpIfNot -> 952` | 给山地 |
| 6 | **`GiveAlpineBonus`** | **5** | `card` | **si=37 `PopExecutionFlowIfNot`** | 山地 +1/+1 |
| 7 | `GiveAmbush` | 94 | `_cardFromID` | si=126 `JumpIfNot -> 546` | 给伏击 |
| 8 | `GiveBlitz` | 105 | `_cardToGive` | si=137 `JumpIfNot -> 1332` | 给闪击 |
| 9 | `GiveBond` | 94 | `_card` | si=126 `JumpIfNot -> 1291` | 给羁绊 |
| 10 | `GiveFury` | 94 | `_cardToGive` | si=126 `JumpIfNot -> 1602` | 给狂怒 |
| 11 | `GiveGuard` | 94 | `_cardToGive` | si=126 `JumpIfNot -> 1414` | 给护卫 |
| 12 | `GiveImmune` | 94 | `_card` | si=126 `JumpIfNot -> 1198` | 给免疫 |
| 13 | `GiveMobilize` | 99 | `_card` | si=131 `PopExecutionFlowIfNot` | 给动员 |
| 14 | `GiveRandomCombatKeyword` | 119 | `CardToGive` | si=151 `JumpIfNot -> 959` | 随机战斗关键字 |
| 15 | `GiveSalvage` | 99 | `_card` | si=131 `PopExecutionFlowIfNot` | 给打捞 |
| 16 | `GiveShock` | 94 | `_card` | si=126 `JumpIfNot -> 1147` | 给震击 |
| 17 | `GiveSmokescreen` | 94 | `CardToGive` | si=126 `JumpIfNot -> 1214` | 给烟幕 |

**间接调用点**（`out/audit/ccbb-indirect.py`，BP_CardFunctions 之外）：

- `BP_OnlineMatch`：`ReceiveActionGainAttack/GainDefense/LoseAttack/SetKreditCost/CustomAbilityAdd/GiveAlpine/GiveGuard/GiveAmbush/GiveBlitz/GiveBond/GiveFury/GiveImmune/GiveMobilize/GiveSalvage/GiveShock/GiveSmokescreen`（**服务端 action 接收侧**）；
  另有 `Player/OpponentDevCheatSetAttack/ChangeDefense`、`ToggleAbilitiesFromDevCheat`（调试）。
- `BP_Logic`：`GiveMobilizeBonus` si=664/803 → `ChangeAttack`/`ChangeDefense`。
- **卡自己的蓝图里 0 处**直接调 `CanCardBeBuffed`（`card-ir.json` 全扫 = 0）。

---

## 5. 对 Alpine（18 张）的结论

**能做。而且原来那个"恒空"的判定是错的。**

### 5.1 18 张是哪些（`src/KLink.Bot/Cards/CardInnateTable.cs`，自带 Alpine 关键字）

```
card_unit_27e_dia, card_unit_141_gebirgsjager, card_unit_kurmark_aufklarungs,
card_unit_136_gebirgsjager, card_unit_gebirger_pionier_95, card_unit_bergmann_battalion,
card_unit_139_gebirgsjager, card_unit_13_gebirgsjager, card_unit_2nd_alpini,
card_unit_3rd_alpini, card_unit_4th_alpini_regiment, card_unit_6th_alpini_regiment,
card_unit_7th_alpini, card_unit_mikawa_regiment, card_unit_3rd_maizuru_snlf,
card_unit_35th_mountain_rifles, card_unit_334th_mountain_rifles, card_unit_devils_brigade
```
（`out/audit/ccbb-h2h4.py` 数出来正好 **18** 张。）

### 5.2 `GiveAlpineBonus` 的真实函数体（39 条语句，`out/audit/ccbb-decode.py`）

```text
si=0    PushExecutionFlow(1175)
si=5    CanCardBeBuffed(card, out CanBeBuffed)
si=37   PopExecutionFlowIfNot(CanBeBuffed)                 ; ← 门
si=47   card.getHasAlpine(out doesIt)
si=88   card.IsLocatedOnBoard(out isIt)
si=129  card.getTotalDefense(out totalDefense)
si=170  def>0 = Greater_IntInt(getTotalDefense, 0)
si=204  and1  = BooleanAND(IsLocatedOnBoard, def>0)
si=242  and2  = BooleanAND(and1, getHasAlpine)
si=280  PopExecutionFlowIfNot(and2)                        ; 必须「在场 && 防御>0 && 有山地」
si=290  GetAllUnitsOnBoard(includeCovertCards=False, out cards)
si=314.. 循环： item.getHasAlpine() && item != card && item.side == card.side ⇒ bonus++
si=906  bonus > 0
si=940  PopExecutionFlowIfNot(bonus>0)                     ; bonus==0 就直接返回
si=950  ChangeAttack(card, card.cardID, bonus, 1, false)   ; changeType=1 ⇒ 加攻
si=1025 ChangeDefense(card, card.cardID, bonus, 1, false)
```

规则一句话：**一张自带山地的单位「进入战场」时，+1/+1 × 场上其它同阵营山地单位数**。

### 5.3 为什么"恒空"不成立

`GiveAlpineBonus` 的调用点（`out/audit/ccbb-alpine.py` + `ccbb-alpine-sites.py`）全部在**部署路径**上：

| 调用者 | 调用 si | 现场 |
|---|---|---|
| `SpawnCardToBoard` | 872 | si=817 `cardSpawned.getHasAlpine()` → si=858 `JumpIfNot -> 895` → 872 `GiveAlpineBonus(cardSpawned)` |
| `SpawnMultipleCardsOnBattlefield` | 2968 | si=2913 `cardSpawned.getHasAlpine()` → si=2954 `JumpIfNot` → 2968 |
| `PlayCardFromHand` | 2207 | 打出后的收尾 |
| `PlayCardDirectlyFromHand` | 3053 | si=3002 `card.getHasAlpine()` → si=3043 `PopExecutionFlowIfNot` → 3053 |
| `AfterWaitCardPlayFromHand` | 974 | 同上 |

而 `GiveAlpineBonus` 自己 si=204 要求 `IsLocatedOnBoard(card)`。
⇒ **能走到 si=280 的卡必然在场**（否则 si=280 就返回了，函数才是真的恒空）。
⇒ 既然在场，si=37 这道门就必须放行 —— 也就是 `CanCardBeBuffed(在场卡) == true`。
⇒ 加上「Alpine 是游戏里真实生效的机制、18 张卡」这一事实，**只能**是这个读法。

另有一条独立佐证：`BP_OnlineMatch::ReceiveActionGiveAlpine`（si=413）→ `GiveAlpine` → 自己的 si=51 同一道门。
服务端下发「给山地」时也要过这道门；`card_unit_67th_baranovichi` 就是靠 `GiveAlpine/RemoveAlpine` 给别人山地的卡
（`card-ir.json` 里 `GiveAlpine` 1 张卡 = 它）。门若拒绝在场卡，服务端下发的关键字会被静默丢掉。

### 5.4 还需要什么额外证据

**不需要额外证据就能下结论。** 唯一读不出来的只有 `IsUnrevealedCovertCard` 的原生实现（§3.4），
而它不影响 Alpine：本内核 Covert 恒假。

### 5.5 实现 Alpine 需要补什么（给改代码的人）

1. **改 `CardApi.CanCardBeBuffed`**（见 §6 的补丁）。
2. **补 `GiveAlpineBonus`**（派发表 `CardApiDispatch.cs` 里现在**没有**这一项）。它不在任何卡的 IR 里
   （`card-ir.json` 里 `GiveAlpineBonus` 调用卡数 = 0），是**引擎内部函数**，只能由内核在部署流程里主动调，
   对应蓝图里 `SpawnCardToBoard`/`PlayCardFromHand`/`PlayCardDirectlyFromHand`/`AfterWaitCardPlayFromHand`/
   `SpawnMultipleCardsOnBattlefield` 这 5 个位置。
   判据链（照抄蓝图）：`getHasAlpine(card)` → `IsLocatedOnBoard(card)` → `getTotalDefense(card) > 0`
   → 数 `GetAllUnitsOnBoard(includeCovertCards=False)` 里 `hasAlpine && 不是自己 && 同阵营` 的数量 `bonus`
   → `bonus > 0` 时 `ChangeAttack(card, card.cardID, +bonus, changeType=1)` 与 `ChangeDefense(..., +bonus)`。
   注意它是**加法**（`changeType=1` ⇒ `ChangeAttack` si=1224 分支 `Add_IntInt(decryptedAttack, amount)`），
   所以调用时机必须与蓝图一致（**只在刚部署的那一刻调一次**），否则会重复累加。
3. **`BP_Logic::GiveMobilizeBonus`** 是同一形状的另一个引擎内函数（si=664/803 → `ChangeAttack/ChangeDefense`），
   顺带一起看。

---

## 6. 对内核的影响

### 6.1 结论：**我们的实现错了**，但**目前没有造成运行时错误**，是一颗地雷

- `CardApi.CanCardBeBuffed` 被挂进了派发表：`src/KLink.Bot/Effects/CardApiDispatch.cs:648`
  `["CanCardBeBuffed"] = (c, r, a) => SelfArg(c, r, a) is { } x && CanCardBeBuffed(x),`
- 但**没有任何一张卡的 IR 直接调它**（`card-ir.json` 全扫 = 0）。
- 那 17 个带门的函数，内核要么自己重写掉了（`DoChangeAttack`/`DoChangeDefense` **故意不加门**，
  `CardApiDispatch.cs:1587-1611` 有记录），要么把门一起去掉了。
- ⇒ **今天**把 `CanCardBeBuffed` 改对，不会改变任何现有行为；**但是**：
  - 一旦有人按蓝图给 `DoChangeAttack`/`DoChangeDefense` 加上这道门（这是迟早的事），
    用现在的错误实现会**立刻**把 334 + 311 张卡的攻防改动全部打死；
  - 一旦有人实现 `GiveAlpineBonus`（Alpine 18 张）或 `GiveMobilizeBonus`，
    用现在的错误实现会**直接做出一个恒空函数** —— 也就是上一轮"Alpine 做不了"的死循环会重演。
- 所以**必须改**，而且要**先改**再动 Alpine。

### 6.2 建议的改法（一行判断 + 注释更正）

```csharp
/// 「这张卡能被加 buff 吗」—— 逐字对应 `BP_CardFunctions::CanCardBeBuffed`
/// （`out/bp-cardfn.json`，30 条语句）：
/// <code>
/// si=0    Card.IsUnrevealedCovertCard(out isIt)
/// si=41   JumpIfNot(isIt) -> si=730        ; ★ 条件为假才跳 ⇒ 「不是未揭示隐蔽卡」⇒ true
/// si=55..711  switch(Card.Location)        ; ★ 只在「是未揭示隐蔽卡」时才走
///             0(NotAvailable) / 5/6(半场 HQ) / 7(前线) / 8(弃牌堆) → si=746  False
///             1/2(牌库左右) / 3/4(手牌左右) / 9(牌库)             → si=762  True
/// si=725  其它枚举值 → 不赋值直接返回（out 默认 false）
/// </code>
/// ⇒ **在场单位（5/6/7）返回 true**；只有「未揭示的隐蔽卡」才按位置区分。
/// 本内核没有建模 Covert ⇒ `IsUnrevealedCovertCard` 恒假 ⇒ 本函数恒 true。
public static bool CanCardBeBuffed(CardInstance card)
{
    if (!card.IsUnrevealedCovertCard)   // 内核恒 false（未建模 Covert）
    {
        return true;
    }

    return card.Location switch
    {
        CardLocation.DeckLeft or CardLocation.DeckRight => true,   // 1 / 2
        CardLocation.HandLeft or CardLocation.HandRight => true,   // 3 / 4
        CardLocation.Deck => true,                                 // 9
        _ => false,                                                // 0 / 5 / 6 / 7 / 8
    };
}
```

### 6.3 还有哪些卡受影响

「受影响」= 调用了那 17 个带门函数中任意一个的卡。`out/audit/ccbb-impact.py` 统计：

| 带门函数 | 调用它的卡数 |
|---|---|
| `ChangeAttack` | 334 |
| `ChangeDefense` | 311 |
| `ChangeKreditCost` | 77 |
| `CustomAbilityAdd` | 76 |
| `GiveBlitz` | 55 |
| `GiveGuard` | 21 |
| `GiveShock` | 18 |
| `GiveAmbush` / `GiveFury` | 10 / 10 |
| `GiveImmune` / `GiveSmokescreen` | 9 / 9 |
| `GiveRandomCombatKeyword` | 4 |
| `GiveMobilize` / `GiveSalvage` | 3 / 3 |
| `GiveBond` | 2 |
| `GiveAlpine` | 1（`card_unit_67th_baranovichi`） |
| `GiveAlpineBonus` | 0（引擎内调，见 §5.5） |
| **去重合计** | **609 张卡** |

其中**必须**改对才有意义的（这些卡的效果全落在在场单位上，用错实现会整条失效）：
`GiveGuard` 21 张、`GiveShock` 18 张、`GiveAmbush`/`GiveFury` 各 10 张、`GiveImmune`/`GiveSmokescreen` 各 9 张、
`GiveMobilize`/`GiveSalvage` 各 3 张、`GiveBond` 2 张、`GiveAlpine` 1 张，以及 **Alpine 18 张**。

### 6.4 顺带更正的历史文档（**我没有改它们，只报告**）

- `out/audit/dim2-legality.md` §2.4.7 写的是「`i=0/41 IsUnrevealedCovertCard ⇒ true`」+
  「在场（5/6/7）、弃牌堆（8）、NotAvailable（0）不可被 buff」—— 同一个极性错误。
  同一段还写「内核零实现 / `CanCardBeBuffed` 不在派发表」，这条**已过期**（现在在 `CardApiDispatch.cs:648`）。
- `out/audit/dim3-keywords.md` 里 `GiveAlpineBonus` 那段抄的字节码是对的（si 号一致），
  但结论「i=37 IFNOT -> pop ⇒ 门拒在场」是错的。
- `src/KLink.Bot/Effects/CardApi.cs:1040-1052`（`CanCardBeBuffed` 的 doc comment）需要跟着改。
- `src/KLink.Bot/Effects/CardApiDispatch.cs:1587-1607` 那段「不落地这道门」的长注释：
  **结论（先别加门）可以保留**，但理由要换 —— 不是「蓝图自相矛盾」，而是「这道门在本内核里恒真，
  加了也只是空转；等 Covert 建模了再回来加」。红牛等 4 条自测从此不再构成反证。

---

## 7. 没做到 / 不确定的

1. **`IsUnrevealedCovertCard` 的原生实现读不出来**。`BaseCardObject.cpp:296` 是空壳，
   `bp-cardfn.json` 里没有它（它是 `@import(250)` 的原生函数）。
   所以「未揭示隐蔽卡」的精确定义（是否等于 `hasCovert && !isRevealed`）**是猜测**。
   影响面：只影响「隐蔽卡在场上能不能被 buff」这一支，本内核未建模 Covert，故对当前实现无影响。
2. **`SpawnCardToBoard` 里 `cardSpawned.Location` 具体在哪一条语句被写成 5/6/7，我没能钉死**。
   可以确定的是 `si=895 SetSet location = …` + `si=919 RefreshLocationStatus(location)` 排在
   `si=872 GiveAlpineBonus(cardSpawned)` **之后**，所以 si=872 那一刻读到的位置必须更早被设好
   （`si=1838 InjectCardIntoLocation` 那条路径上是这样）。
   由于 `GiveAlpineBonus` 自己 si=204 要求 `IsLocatedOnBoard`，而 Alpine 是真实生效的机制，
   「那一刻位置已是 5/6/7」是**从机制反推**的，不是直接读出来的。
   **实现 Alpine 时这一步必须自己确认**（否则会在 `IsLocatedOnBoard` 上翻车）。
3. ~~`ChangeKreditCost` si=417 的守位目标 369 未展开~~ —— 已补看，**不是不确定项**：
   `si=369 bool qqq = False` + `si=380 JUMP -> 2756`（返回），`si=431 JUMP -> 133` 是放行后的去处。
   与 `ChangeAttack` si=472/483 完全同形，17 个守位形状统一。
4. **`out/bp-cardfn.json` 的 `StatementIndex` 与 `Jump/JumpIfNot` 的 `Offset` 是否严格同坐标系**：
   我做了自洽性检验（所有 Offset 都精确落在某条语句的 StatementIndex 上，`Jump -> 773` 落在 `Return`），
   并有两个独立解码器（bpasm、kards-sim 生成 C#）给出相同结果。
   但我**没有**从原始 uasset 字节流手工反汇编逐字节验证偏移，所以「绝对偏移值」这一层是靠三方一致 + 自洽推定的。
   **相对结构（谁跳谁、谁真谁假）是确定无疑的**，结论只依赖相对结构。
5. 任务里提到的「P0 第 4 族 4 条自测」我**没有重跑** `tools/BotSim -- selftest`
   （那会读 `src/`，且另一个子代理正在改代码，我不想与它抢文件/产生误导性结果）。
   本报告只做静态证据判定：结论是那 4 条自测与蓝图**不冲突**，把门加回去（用改对的实现）应当仍然通过。

---

## 附：本次新增的脚本（都在 `out/audit/`，只读输入、只写自己的输出）

| 脚本 | 干什么 |
|---|---|
| `ccbb-decode.py` | 解码任意 BP_CardFunctions 函数；列出 `CanCardBeBuffed` 的 17 个调用点 |
| `ccbb-alpine.py` | Alpine 链路：谁调 `GiveAlpineBonus/GiveAlpine/RemoveAlpine/getHasAlpine`；IR 里哪些卡涉及 |
| `ccbb-alpine-sites.py` | `GiveAlpineBonus` 5 个调用点的前后文 |
| `ccbb-show.py` | 打印某函数某个 si 区间的语句（用法 `python out/audit/ccbb-show.py <fn> <lo> <hi>`） |
| `ccbb-defs.py` | 属性名 casing 统计 + 全仓搜 `CanCardBeBuffed` 的定义处 + `hasAlpine` 计数 |
| `ccbb-impact.py` | 受影响卡数统计（609 张） |
| `ccbb-indirect.py` | BP_CardFunctions 之外的间接调用点（服务端 action 接收侧） |
| `ccbb-sites.py` | 17 个调用点的实参 + 守位分支 |
| `ccbb-h2h4.py` | 假设 2/4 的证据（usmap 宿主、ZAction/Notify、Alpine 卡清单） |
| `ccbb-usmap.py` | usmap 里 `BaseCardObject.Location` 的类型 |
| `ccbb-nametable.py` | 证明名字表里 `Location`/`location` 是同一个 FName |
