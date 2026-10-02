# klink bot —— KARDS 离线规则内核 + AI

> **把一款商业卡牌游戏（KARDS）的蓝图字节码，逆向成一个不需要游戏客户端、可以离线执行、并且与真实客户端逐位可复现的规则内核；再用它自对弈、训练神经网络，最后把 AI 接回真实对局当对手。**

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![C#](https://img.shields.io/badge/C%23-net10.0-239120)
![Python](https://img.shields.io/badge/Python-3-3776AB)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)
![CI](https://img.shields.io/badge/CI-none%20(all%20numbers%20measured%20locally)-lightgrey)

> ⚠️ **本仓库没有 CI**，所以上面没有构建徽章。本文里所有数字都是**本机手工跑出来的**，
> 每条都附了命令或文件行号。凡是**没有独立核实**的，文中会明确标注「未独立复核」。
>
> 📦 **出处**：本仓库是开源项目 **KLink**（[`Xuewu-awa/KLink-dotnet`](https://github.com/Xuewu-awa/KLink-dotnet)，
> 启动器 + 私服）中**规则内核 + AI** 这一部分的独立抽取版。
> 启动器与私服**不在本仓库里，但都是开源的** —— 见 §5.6。
> **许可：GPL-3.0**（与来源仓库逐字节一致），详见 §10.5。
>
> 本文档核实时间：**2026-10-02**（提交 `d2d0f5c` 的工作树）。
> 作者的内部追踪文档在 [`klink bot/docs/内部现状与路线图.md`](klink%20bot/docs/内部现状与路线图.md)。

---

## 目录

1. [这是什么 / 解决什么问题](#1-这是什么--解决什么问题)
2. [核心难题：确定性锁步下的逐位复刻](#2-核心难题确定性锁步下的逐位复刻)
3. [数据流水线](#3-数据流水线)
4. [架构](#4-架构)
5. [能做什么（能力清单）](#5-能做什么能力清单)
6. [快速开始](#6-快速开始)
7. [度量与验证方法](#7-度量与验证方法)
8. [当前状态（实测数字）](#8-当前状态实测数字)
9. [路线图 / 已知缺口](#9-路线图--已知缺口)
10. [法律与伦理](#10-法律与伦理)
11. [贡献指南](#11-贡献指南)
12. [致谢](#12-致谢)
13. [目录结构](#13-目录结构)
14. [内部文档指路](#14-内部文档指路)

---

## 1. 这是什么 / 解决什么问题

### 1.1 一句话

**KARDS 是一个二战题材的卡牌游戏。本项目不需要启动游戏客户端，就能在本地把它的规则完整跑起来。**

具体来说：游戏里每张卡的效果不是写死在代码里的 `if/else`，而是**蓝图（Blueprint）**编译出来的
**Kismet 字节码**。本项目把那些字节码从游戏数据包里抽出来、压成一份中间表示（IR，Intermediate
Representation），然后写了一个**字节码解释器**去执行它。于是：

- 一张卡的效果 = 一段可解释执行的 IR；
- 加一张新卡的支持，通常**不需要写 C#**，只需要把原语（primitive）层补全；
- 整个对局可以**离线、无客户端、可重复**地跑。

### 1.2 它到底做了什么

```
游戏 pak（7.1 GB，AES-256 加密索引）
   │  解包 + 反编译蓝图
   ▼
蓝图字节码 JSON
   │  压成 IR
   ▼
card-ir.json（9.4 MiB / 1735 条）
   │  Kismet 字节码解释器（KismetVm）
   ▼
原语派发表（CardApiDispatch）+ 原语实现（CardApi）
   ▼
可离线跑对局的「规则内核」
   ├──► 自对弈  →  产出训练数据
   ├──► 训练神经网络  →  当 AI 的决策器
   └──► 重放真实对局的动作流  →  逐条与客户端对拍（审计）
```

### 1.3 为什么非 KARDS 玩家也可能觉得有意思

这个项目的核心不是「做一个 KARDS 机器人」，而是**解决了一个具体的、可验证的工程问题**：

> **把一个商业游戏的蓝图字节码逆向成一个可离线执行、逐位可复现的规则内核。**

这件事有意思的地方在于它的**判据是硬的**：

- 游戏的网络模型是**确定性锁步**（deterministic lockstep）：服务端**不保存棋盘、不做合法性校验**，
  每个客户端**在本地自己结算效果**，服务端只转发动作。
- 因此，**只要拿到「开局数据 + 全部动作流」，就一定能还原出客户端那个棋盘** ——
  游戏自己的重连机制就是这么做的。
- 于是：**内核重建不出客户端那个棋盘，就一定是内核的 bug，不是「数据不够」。**

这是一个**可以被逐位证伪**的目标，而不是一个模糊的「效果大致对上了」。

此外项目里还有几块单独拿出来看也成立的工程内容：

| 主题 | 内容 |
|---|---|
| **确定性随机数的逐位复刻** | 用反汇编得到的 LCG 常数，逐位复刻虚幻引擎 `FRandomStream`，并用一组公开测试向量钉死（见 §2.3） |
| **字节码解释器** | 一个 1273 行的 Kismet 字节码解释器，带步数预算、局部函数体兜底、事件变量槽位解析 |
| **大规模差分测试** | 4088 个 (卡, 入口, 摆位) 用例的全卡池烟雾测试，同时检查崩溃 / 未实现原语 / 步数上限 / 非确定性 / 零状态变化 |
| **带指纹的防回归守卫** | 用集合指纹（而不是总数）冻结「未实现原语」缺口，防止「修一个坏一个」互相抵消 |
| **对拍方法论** | 明确区分「哪些判据可靠、哪些只是弱约束」，并记录了一次「两个错互相抵消」的真实案例（见 §7.3） |

### 1.4 它在更大的项目里处于什么位置

本项目不是一个孤立的东西：它原本是开源项目 **KLink**（KARDS 私服启动器 + 私服）里的
**规则内核与 AI** 那一层，现在被抽成了独立仓库。围绕它的开源组件（**都不在本仓库里**）：

| 组件 | 仓库 | 说明 |
|---|---|---|
| **启动器 + 私服（.NET 10 / WPF，Windows）** | [`Xuewu-awa/KLink-dotnet`](https://github.com/Xuewu-awa/KLink-dotnet) | **本项目就出自这里**（`src/KLink.Bot`）。许可 **GPL-3.0** |
| **启动器（Android / Java 版）** | [`Xuewu-awa/KLink`](https://github.com/Xuewu-awa/KLink) | 同一启动器的 Android 版 |
| **服务端参考实现（Go）** | [`kardswalker/kards-server-go`](https://github.com/kardswalker/kards-server-go) | 私服协议的参考实现 |
| **实际承载本内核的私服核心（C#）** | [`CCB-TEAM/fyserver`](https://github.com/CCB-TEAM/fyserver) | 内核以 DLL 形式被它加载（见 §5.6） |

**接法**（可核实的证据）：`src/KLink.Bot/Engine/UeRandomStream.cs` 与本仓库里的那一份
**SHA-256 相同**（`55BD6F19…`），说明抽取是逐字节搬运、不是重新实现。

#### 1.4.1 为什么会有这个独立仓库

**同一份 bot 也会随启动器仓库一起发布**（`KLink-dotnet` 的 `src/KLink.Bot`）。
这里单独开一个仓库，**唯一目的是方便别人 clone**：

| | 启动器仓库 `KLink-dotnet` | **本仓库 `klink bot`** |
|---|---|---|
| 内容 | 启动器（WPF UI）+ 私服 + bot | **只有规则内核 + AI + 数据 + 工具** |
| clone 之后能做什么 | 开服、起启动器、进游戏打人机 | **直接跑规则 / 自对弈 / 训练 / 审计**（见 §5、§6） |
| 体量 | 大得多（含 WPF UI、服务器、资源） | **463 个文件 / 约 22.62 MiB** |
| 需要游戏本体吗 | 需要（要探测 `kds\kards\Binaries\Win64\kards-Win64-Shipping.exe`） | **不需要** |
| 需要 .NET 之外的依赖吗 | WPF / Windows 桌面 | 只要 .NET 10 SDK（Python 可选） |

⇒ 如果你只想**研究或改进这个规则内核与 AI**，clone 本仓库就够了；
如果你想**实际开一局**，需要启动器仓库 + 你自己的正版游戏。

#### 1.4.2 两个仓库的关系与同步

- **`src/KLink.Bot/**` 在两边是同一份代码**（本仓库是逐字节搬运，不是 fork）。
- 让本仓库能**独立跑起来**的东西是一并带上的：数据（`klink bot/docs/**`）、
  Python 工具（`klink bot/tools/**`）、审计产物（`out/audit/**`）、真实对局语料
  （`out/_server-replays/**`）。
- **只收了 5 个 C# 工具工程**（`BotSim` / `ServerBridgeTest` / `NNTrain` / `NNPlay` / `AotProbe`），
  即本文档里真正会让读者去跑的那几个。上游还有一批**没有纳入**：
  `DevProbe`（自对弈逐回合 diff）、`NNEarlyProbe`（NN 打分归因探针）、
  `TriCompare` / `BoardCompare` / `SimCompare`（三种对拍器）、`AuraDiag`（光环诊断）、
  `SmokeTest` / `PakTest` / `HostTest` / `LauncherApiTest` / `BotNameTest` / `FyServerStub`，
  以及 `NNTrain` 的训练数据文件（`*.bin`，最大一份 7.5 GB，被 `.gitignore` 排除）。
  ⚠️ 其中 `TriCompare` / `BoardCompare` / `SimCompare` 是**对拍工具链**（内核 vs 真实牌局逐字段对拍），
  如果你要继续做「三方对拍」，值得从上游一并取来。
- **建议的同步方向**：改动先落在上游 `KLink-dotnet`，再同步到本仓库，避免两边分叉
  （这一条是文档建议，不是硬性约束）。

---

## 2. 核心难题：确定性锁步下的逐位复刻

这一节是整个项目的地基。理解了它，才能理解为什么后面所有的工程决策长成那样。

### 2.1 服务端没有棋盘状态

KARDS 用的是**确定性锁步**网络模型：

- 服务端**不保存棋盘状态**；
- 服务端**不做合法性校验**（谁能不能出这张牌，是客户端自己判的）；
- 每个客户端收到动作后，**在本地把效果结算一遍**；
- 服务端只负责**转发动作**（`action_data`）。

这和「服务端权威（server-authoritative）」模型完全相反。它带来一个直接推论：

> **只要动作流一样、初始状态一样、双方的结算逻辑一样，双方棋盘就必然一样。**

### 2.2 重连机制证明了「动作流足以还原状态」

游戏自己的**重连**实现就是这条推论的应用：服务端把
**「开局数据（starting_data）+ 全部动作流」**发回客户端，**客户端自己重放还原棋盘**。

> ⇒ **内核重建不出客户端那个棋盘，就一定是内核的 bug。**

这是本项目所有工作的**判据来源**：不需要猜、不需要「大致对」，
只需要拿真实对局的快照 + 动作流，在内核里重放，然后逐条比。

### 2.3 随机数也是确定性可复现的

卡牌游戏里「随机抽一张牌」看起来是内核最不可能复现的部分。但客户端用的随机数来自
**虚幻引擎自带的 `FRandomStream`**（蓝图里的 `cardsRandomStream`），
它的**算法和常数完全公开**，而且已经在游戏二进制里被反汇编确认过。

所以内核不需要「猜」，只要：**用同一个种子、按同一个顺序、消耗同样多次**，
就能逐位复现客户端抽到的那张卡。

实现在 [`src/KLink.Bot/Engine/UeRandomStream.cs`](src/KLink.Bot/Engine/UeRandomStream.cs)：

| 细节 | 值 | 出处 |
|---|---|---|
| LCG 步进 | `Seed = Seed * 196314165 + 907633515 (mod 2^32)` | `UeRandomStream.cs:56,59,99-105` |
| `GetFraction()` | 取变换后种子的**高 23 位**，`(s >> 9) \| 0x3F800000` 当作 `[1,2)` 的 float 再减 1 | `UeRandomStream.cs:114-119` |
| `RandRange(min, max)` | **两端闭区间**：`min + floor(GetFraction() * (max - min + 1))` | `UeRandomStream.cs:129-138` |
| 播种 | 开局用 `match_id` 播种**一次**，之后连续推进（不再逐动作重播种） | `UeRandomStream.cs:25-32, 86-93` |
| `Array_ShuffleFromStream` | **前向** Fisher-Yates，循环跑满 **n** 次（不是 n−1 次） | `UeRandomStream.cs:157-168` |
| 保真度探针 | `ConsumedCount`：本局已消耗多少个随机数 | `UeRandomStream.cs:80` |

**为什么 `ConsumedCount` 是个强判据**：随机流是一条**游标**。内核漏掉一个消费点（某原语没实现），
游标就**落后**；多消费一次，游标就**超前**。两种情况都会让之后所有取数全部错位。
所以「游标位置对得上」几乎等价于「我们的执行路径与客户端一致」。

**逐位验证**（最硬的一条证据）：一份第三方逆向报告给出了一组可复算的测试向量
（`match_id = 1000000000`，重播种用 `CurrentActionId = 10` ⇒ `seed = 1000193900`，
连抽三次 `RandomIntFromRangeWithStream(0, 2)`）：

| 第几次 | 变换前 Seed | 变换后 Seed | 返回 |
|---|---|---|---|
| 1 | 1000193900 | 1626977479 | 1 |
| 2 | 1626977479 | 4280773790 | 2 |
| 3 | 4280773790 | 431904801 | 0 |

本内核**逐位命中**这三行（自测用例 `UeRandomStreamMatchesReportVector`）。
这三行同时钉住了**LCG 常数**、**高 23 位变换**、**闭区间**三件事 ——
任何一处写错都不可能命中。

### 2.4 ⇒ 项目本质是一场「逐位对拍」的长期工程

把上面三节合起来看：

1. 动作流 + 开局数据 **足以**还原棋盘（§2.2）；
2. 随机数**可以**逐位复现（§2.3）；
3. 所以**任何不一致都是内核的 bug**（§2.1）。

于是项目的工作方式不是「实现功能」，而是**不断缩小与真实客户端的差异**：

```
拿真实对局的动作流 → 在内核里重放 → 找到第一个漂开点
   → 定位根因（某个原语没实现 / 某个语义写错 / 某个消费点漏了）
   → 修 → 重跑全部回放，确认没有回归、且漂开点后移
```

这也是为什么本仓库里有一整套**审计脚本**和**冻结基线**（见 §7、§8）——
没有它们，就无法判断一次改动到底是「变好了」还是「只是换了个地方错」。

---

## 3. 数据流水线

```
kards-Windows.pak（7.1 GB，AES-256 加密索引）
   │  ① 解包 + 反编译蓝图（需要 pak 解密密钥；密钥【不在本仓库】）
   ▼
cards.full.json（原始 Kismet 字节码 JSON）
   │  ② klink bot/tools/gen-kismet-ir.py
   ▼
klink bot/docs/card-ir.json（IR —— 运行时就解释它）
   │  ③ src/KLink.Bot/Effects/Blueprint/KismetVm.cs（字节码解释器）
   ▼
src/KLink.Bot/Effects/CardApiDispatch.cs（名字派发：IR 里的调用名 → C# 原语）
   ▼
src/KLink.Bot/Effects/CardApi.cs（原语实现：伤害 / 触发 / 关键字 / 压制 / 老兵 / 生成卡 …）
   ▼
src/KLink.Bot/Engine/MatchEngine.cs（对局引擎：回合 / 部署 / 移动 / 攻击 / 摧毁 / 前线）
   ├──► tools/BotSim（自对弈 / selftest / 全卡池烟雾测试 / 缺口统计）
   ├──► src/KLink.Bot/NN/StateEncoder.cs + NnModel.cs（局面编码 → 打分）
   │        └── tools/NNTrain（自对弈产数据 → 训练 → 导出 nn-model.bin）
   │        └── src/KLink.Bot/Server/NnPolicy.cs（候选枚举 → 打分 → 产出动作）
   └──► src/KLink.Bot/Server/BotTurnService.cs（接回真实对局：宿主侧见 §5.6）
```

### 3.1 关键设计选择：解释 IR，而不是给每张卡写脚本

不给 1700+ 张卡手写 C# 效果脚本，而是**直接解释蓝图编译出来的 IR**。理由：

- 字节码里天然带着**数据流**：`CallFunc_<函数名>_<参数名>` 这种局部变量名就是那个调用的输出槽；
- 一张卡的效果因此就是「一串有输入输出的步骤」，配合 `JumpIfNot` 给出的控制流就能完整还原；
- **覆盖率随原语层完善自动提升**，不需要为每张卡单独写代码。

IR 的形状（生成器文档串，[`klink bot/tools/gen-kismet-ir.py`](klink%20bot/tools/gen-kismet-ir.py)）：

```jsonc
{
  "card_event_aans": {
    "programs": {
      "OnPlayedFromHand": { "entry": 10, "steps": [ /* ... */ ] }
    },
    "locals": { "CanPlayFromHand": { "entry": 3, "steps": [ /* ... */ ] } }
  }
}
```

单个步骤：

```jsonc
{"i":10,  "op":"call",      "fn":"GainKreditSlot", "args":[...], "outs":[]}
{"i":119, "op":"set",       "dst":"tempCard",      "src":{...}}
{"i":167, "op":"jumpIfNot", "cond":{...},          "to":252}
{"i":252, "op":"return"}
```

表达式：`{"var":"tempCard"}` / `{"int":3}` / `{"bool":true}` / `{"str":"x"}` /
`{"obj":"/Script/..."}` / `{"self":true}` / `{"none":true}` /
`{"call":"IsValid","args":[...]}` / `{"math":"Add_IntInt","args":[...]}` /
`{"unknown":"EX_Foo"}`（未支持的指令会被 VM 记录并跳过，不中断整局）。

### 3.2 ⚠️ 流水线的第 ①② 步在本仓库里**跑不了**

这是一个必须说清楚的事实，别高估仓库的自包含程度：

| 步骤 | 需要什么 | 在本仓库里吗 |
|---|---|---|
| ① pak → 蓝图字节码 | `kards-Windows.pak`（7.1 GB）、pak 解密密钥 `key.txt`、解包 / 反编译 CLI | ❌ **都不在**（见 §10.2） |
| ② 字节码 → IR | `cards.full.json`（反编译产物）、`gen-kismet-ir.py` | 脚本 ✅ 在；**输入 ❌ 不在** |
| ③ IR → 对局 | `card-ir.json` + 内核源码 | ✅ **全在**（这是本项目可独立运行的部分） |

**也就是说：从 clone 出来的仓库出发，你可以直接跑规则、自对弈、训练、审计；
但你不能从游戏本体重新生成 IR。** IR 与卡库是**随仓库携带的产物**。

---

## 4. 架构

> 所有路径都经过实际读代码核实；行号以提交 `d2d0f5c` 的工作树为准。

### 4.1 内核（`src/KLink.Bot/`，33 个文件）

| 模块 | 位置 | 作用 | 关键实现点 |
|---|---|---|---|
| **Kismet 字节码解释器** | `Effects/Blueprint/KismetVm.cs`（1273 行） | 解释 IR：`call` / `math` / `set` / `jumpIfNot` / `jump` / `popFlow` / `setArray`；局部变量、事件变量槽位解析、步数预算、`locals` 兜底 | 执行模型见 `:12-17`；`MaxStepsPerProgram = 5000`（`:27`）、`MaxStepsPerLocalProgram = 400_000`（`:43`）、`StepsPerPoolCard = 52`（`:79`）、`MaxStepsHardCap = 1_000_000`（`:89`） |
| **IR 数据结构** | `Effects/Blueprint/KismetIr.cs`（654 行） | IR 的解析与库索引（`KismetLibrary`）：按卡名 / 入口名取程序、`locals` 查找与基名回退 | — |
| **派发表** | `Effects/CardApiDispatch.cs`（4547 行 / 243 KB） | 蓝图里出现的**每一个函数名**在 C# 里的实现与名字派发 | `BuildDispatch()` 逐键注册；例：`["MakeCardsFight"] = (c, r, a) => DoMakeCardsFight(c, a)`（`:389`） |
| **卡牌 API（原语层）** | `Effects/CardApi.cs`（2605 行 / 131 KB） | 伤害 / 触发 / 关键字 / 压制 / 老兵 / 生成卡等上层原语实现 | `ImplementedNames` 是派发缺口的判据之一 |
| **派发缺口量化** | `Effects/Blueprint/DispatchGap.cs`（106 行） | 计算「IR 会调用、派发表没有、`locals` 也兜不住」的缺口集合与**指纹** | 三层判据见 `:20-32` |
| **对局引擎** | `Engine/MatchEngine.cs`（2287 行 / 126 KB） | `StartTurn` / `PlayCard` / `Attack` / `MoveUnit` / `Destroy` / 前线归属 / 回合推进 / 事件派发 | `MaxKreditCap = 24`（`:51`） |
| **状态与卡实例** | `Engine/GameState.cs`（681 行）/ `Engine/CardInstance.cs`（752 行） | 棋盘 / 手牌 / 牌库 / 弃牌堆、关键字、buff、**卡号分配** | `NextCardId`（`GameState.cs:422-484`，含与客户端逐位一致的 `id = 回合号 × 1000 + 本回合已生成数` 规则）；`MaxAttacksThisTurn => Fury ? 2 : 1`（`CardInstance.cs:94`） |
| **随机流** | `Engine/UeRandomStream.cs`（171 行） | UE `FRandomStream` 的逐位复刻（见 §2.3） | — |
| **确定性随机（非对局用）** | `Engine/DeterministicRandom.cs`（58 行） | 内核自带的 splitmix64，只用于训练侧采样，**不参与对局** | `UeRandomStream.cs:47-51` 的注释解释了为什么留它 |
| **协议动作** | `Engine/WireAction.cs`（287 行） | 网络动作的解析与**紧凑名 ↔ 全名**映射 | `CompactToFull`（`:57-87`）：`PC` / `ML` / `AC` / `CS` / `HT` / `MG` / `SG` / `CH` / `EM` |
| **回放执行** | `Replay/ReplayRunner.cs`（1539 行 / 79 KB）+ `Replay/ReplayData.cs`（270 行） | 拿动作流重放，逐步与客户端对拍；产出审计信号；`InferHqKey` 从动作流反推 HQ 采样键 | — |
| **AI 决策** | `Server/NnPolicy.cs`（359 行）/ `Server/BotTurnService.cs`（1019 行）/ `Bots/GreedyBot.cs`（229 行） | 候选枚举 → 神经网络打分 → 产出动作；`GreedyBot` 是 baseline | `NnPolicy` 候选 = 能动的单位 × 内核给的目标 / 能动的单位 × 所有前线槽位 / 结束回合 |
| **神经网络** | `NN/StateEncoder.cs`（373 行）/ `NN/NnModel.cs`（255 行） | 局面编码 → 打分；**训练与推理共用同一份编码器** | v2 布局 `Dim = 3 + 371 × 2 = 745`（`StateEncoder.cs:105-148`、`:168` 的 `Spec` 串）；v3 布局 `1065`（`:350`）；模型文件里存 `spec`，加载时逐项对账 |
| **卡库** | `Cards/CardDatabase.cs`（367 行）/ `CardInnateTable.cs`（740 行）/ `CardPoolTable.cs`（698 行）/ `CardVarDefaults.cs`（91 行）/ `DeckCodeParser.cs`（127 行）/ `MetaDecks.cs`（35 行） | 卡面数值（取自 pak 的 CDO 权威表）+ 关键字 / 重甲 + 卡池模板 + 卡组码解析 + 内置元卡组 | `CardVarDefaults` 补上「蓝图成员变量的 CDO 默认值」（`gen-kismet-ir.py` 只编字节码、不编默认值） |
| **服务器侧集成** | `Server/AtomicAction.cs`（396 行）/ `Server/ServerReplayBridge.cs`（133 行）/ `Server/ServerMatchSnapshot.cs`（82 行） | 把内核动作转成服务端协议动作、把宿主快照映射成内核局面 | 宿主是开源项目 KLink / fyserver，见 §5.6 |

### 4.2 数据与 Python 工具（`klink bot/`）

| 模块 | 位置 | 说明 |
|---|---|---|
| **IR** | `klink bot/docs/card-ir.json`（9.4 MiB，1735 条） | 解释器执行的东西 |
| **卡库** | `klink bot/docs/cards.live.json`（2.0 MiB，2021 条）/ `cards.json` | 卡面数值 |
| **效果调用表** | `klink bot/docs/card-effects.json`（2.7 MiB，2053 条） | 每张卡用了哪些外部调用 |
| **卡向量** | `klink bot/docs/card-vectors.json`（1.0 MiB） | NN 用的卡向量 |
| **卡组码表** | `klink bot/docs/deck_code_ids.json` / `.live.json`（各 88 KB，2499 条） | 2 字符卡组码 ↔ 卡名 |
| **事件契约** | `klink bot/docs/event-contracts.json`（62 KB，172 条） | 事件槽位表 |
| **规则参考** | `klink bot/docs/KARDS基础规则参考.md` | 真人玩家整理 + 蓝图层面的规则定案 |
| **真实对局语料** | `klink bot/docs/fresh-replays/`（7 局）/ `live-replays/`（5 局） | 快照 + 动作流 |
| **生成器** | `klink bot/tools/gen-kismet-ir.py` / `gen-card-db.py` / `gen-card-keywords.py` / `gen-card-pool-table.py` / `gen-card-vectors.py` / `gen-card-effects.py` | 从反编译产物生成上面的数据 |
| **训练 / 评估脚本** | `klink bot/tools/nn-*.py`（r4 → r9，约 40 个） | 训练、评估、消融、曲线 |
| **回放工具** | `klink bot/tools/wrap-fyserver-replay.py` / `decode-replay.py` / `analyze-replay.py` 等 | 把宿主导出的原始回放转成内核认识的形状（含全名 → 紧凑名映射，`:54-60`） |
| **参照物拉取** | `klink bot/tools/fetch-kards-sim.py` | 拉取第三方参照实现 `CCB-TEAM/kards-sim` 到 `ref/kards-sim`（`ref/` 被 gitignore；脚本默认走一个本机代理，可用环境变量 `KARDS_REF_PROXY` 覆盖，`:29`） |

### 4.3 验证与审计工具（仓库根的 `tools/`，属于本项目）

| 工具 | 位置 | 作用 |
|---|---|---|
| **BotSim** | `tools/BotSim/`（`Program.cs` 697 行 / `SelfTest.cs` 9566 行 / `SmokeAllCards.cs` 1813 行 / `DispatchGap.cs` / `GapReport.cs`） | `selftest` / `smoke-all-cards` / `dispatch-gap` / `play` / `decks` / `coverage` / `gaps` / `replay`（命令表见 `Program.cs:48-75`） |
| **ServerBridgeTest** | `tools/ServerBridgeTest/`（`Program.cs` 416 行 / `ReplayAudit.cs` 456 行 / `KreditTable.cs` / `LoadVerifier.cs` / `ServerDeckProbe.cs`） | `--audit-replay` 全套审计；`--verify-load`；`--kredit-table`；`--kredit-trace` |
| **NNTrain** | `tools/NNTrain/Program.cs`（927 行） | `dump`（自对弈产数据）/ `train` / `verify`；手写 MLP，**不依赖 numpy / torch** |
| **NNPlay** | `tools/NNPlay/Program.cs`（599 行） | 让训练好的 NN 下场和贪心 bot 打一局 |
| **AotProbe** | `tools/AotProbe/Program.cs`（219 行） | 在「反射式 JSON 序列化被关掉」的宿主里跑一遍内核全路径（见 §5.6） |
| **审计产物** | `out/audit/`（199 个跟踪文件） | 汇总脚本 + 取证脚本 + 报告 |

### 4.4 关于 `klink bot/` 这个**嵌套**目录

```
<仓库根>/klink bot/docs/     ← 数据 + 全部报告
<仓库根>/klink bot/tools/    ← Python 生成器与训练脚本（142 个文件）
```

`klink bot/` 是**嵌套目录，不是笔误**。原因是内核与工具的源码把数据路径**硬编码**成了
`klink bot/docs/...`：

- C# 里约 15 处运行时回退路径（`tools/BotSim/Program.cs`、`tools/ServerBridgeTest/*.cs`、
  `tools/NNTrain`、`tools/NNPlay`、`tools/AotProbe`）；
- `src/KLink.Bot/KLink.Bot.csproj` 里 4 处 `CopyToOutputDirectory`（把
  `..\..\klink bot\docs\*.json` 链接成输出目录下的 `Data\`）。

保持这个相对路径 ⇒ **不需要改任何构建文件**。如果你想要扁平的 `docs/`，
需要改那 ~15 处路径字面量。

仓库根必须保留 `KLink.slnx`：`BotSim` 与 `ServerBridgeTest` 的 `FindRepoRoot()`
是「从 exe 位置往上找第一个含 `KLink.slnx` 的目录」，缺了它工具在独立 clone 里找不到数据目录
（`KLink.slnx` 里的注释写明了这一点）。

---

## 5. 能做什么（能力清单）

**下面五件事全部不需要启动器、不需要游戏客户端。**

### 5.1 跑规则 / 自对弈

```powershell
# 1 局，打印逐回合过程
dotnet run --project tools\BotSim -c Release -- play --games 1 --verbose

# 1000 局，指定种子
dotnet run --project tools\BotSim -c Release -- play --games 1000 --seed 7

# 列出内置的 22 套元卡组及其解析结果
dotnet run --project tools\BotSim -c Release -- decks
```

### 5.2 训练神经网络

`tools/NNTrain` 是**手写 MLP**，不依赖 numpy / torch：

```powershell
# 自对弈产数据（每局从 22 套元卡组里随机抽 2 套不同的）
dotnet run --project tools\NNTrain -c Release -- dump   --games 10000 --out out\nn-data.bin
# 训练
dotnet run --project tools\NNTrain -c Release -- train  --data out\nn-data.bin --epochs 30 --out out\nn-model.bin
# 验证
dotnet run --project tools\NNTrain -c Release -- verify --data out\nn-data.bin --model out\nn-model.bin

# 让训练好的 NN 下场和贪心 bot 真打一局
dotnet run --project tools\NNPlay -c Release -- play --model out\nn-model.bin [--nn-side left|right] [--seed 12345]
```

判据很直接（`tools/NNTrain/Program.cs:11-18`）：**用一个网络从局面预测胜负。
准确率明显高于 50% = 编码里有信号；≈50% = 编码是垃圾。**

⚠️ `out/nn-data.bin` 这类文件被 `.gitignore` 排除（`*.bin`），本仓库里**没有**训练数据，
需要自己跑 `dump` 生成。

### 5.3 审计真实对局回放（逐条与客户端对拍）

```powershell
# 单局
dotnet run --project tools\ServerBridgeTest -c Release --no-build -- --audit-replay "out\_server-replays\replay-508065"

# 6 局汇总
& "out\audit\audit-all-replays.ps1"
# 按四条判据汇总（推荐，见 §7.1）
& "out\audit\audit-4metrics.ps1"
```

审计段落（`tools/ServerBridgeTest/ReplayAudit.cs`）：
`① 判死事件` / `② 死亡单位仍被移动或攻击` / `③ 终局场上状态` / `④ HQ 对不上` /
`⑤ 未应用的动作` / `⑤b 首个【人类】动作失败点` / `⑤c 身份不一致` /
`⑤d 目标过不了客户端的门` / `⑥ 撞到但没实现的原语` / `⑥a RNG 游标失同步` /
`⑥b 派发表静态缺口` / `⑥c 发号侧信号` / `⑦ RNG 游标计数`。

常用开关：`--rng-trace`（逐次 RNG 消费流水）/ `--dump-log` / `--identity-fix` /
`--identity-only <卡名>` / `--dup-start-kredit`。

### 5.4 全卡池烟雾测试

**脱离一切对局**，直接按 `(卡, 入口, 摆位)` 组合跑蓝图，抓五类问题：
抛异常 / 撞未实现原语 / 撞步数上限 / 零状态变化 / 非确定性。

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- smoke-all-cards
# ⇒ out\audit\smoke-all-cards.tsv / .txt
```

常用选项：`--seed S`（默认 20261002）/ `--only 子串` / `--entry 程序名` / `--limit N` /
`--include-non-live` / `--no-determinism` / `--no-integration` / `--no-two-passes` /
`--debug-trace` / `--out <tsv>` / `--summary <txt>`。

### 5.5 派发表静态缺口基线

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- dispatch-gap
# ⇒ 打印当前缺口种类数 / 调用点数 / 指纹，以及应当填进 tools\BotSim\DispatchGap.cs 的基线常量
```

`selftest` 里有一条**防回归守卫**：缺口集合的**指纹**必须与冻结基线逐位相等
（`tools/BotSim/DispatchGap.cs:75,78`）。用指纹而不是只比总数，是因为
「修一个 + 坏一个」会互相抵消、让总数看起来没变。

### 5.6 宿主集成：启动器与私服（**开源，但不在本仓库**）

内核为「接回真实对局」预留了完整的接口层（`src/KLink.Bot/Server/*`）。
**真正的宿主是启动器 + 私服，它们不在本仓库里 —— 但都是开源的**：

| 角色 | 仓库 | 许可 / 说明 |
|---|---|---|
| **启动器 + 私服**（.NET 10 / WPF，Windows） | [`Xuewu-awa/KLink-dotnet`](https://github.com/Xuewu-awa/KLink-dotnet) | **GPL-3.0**；本仓库就是它的 `src/KLink.Bot` 抽出来的 |
| **启动器**（Android / Java 版） | [`Xuewu-awa/KLink`](https://github.com/Xuewu-awa/KLink) | 同一启动器的 Android 版 |
| **私服核心**（C#，实际加载本内核） | [`CCB-TEAM/fyserver`](https://github.com/CCB-TEAM/fyserver) | 独立进程；启动器作为唯一入口去拉起它 |
| **服务端参考实现**（Go） | [`kardswalker/kards-server-go`](https://github.com/kardswalker/kards-server-go) | 私服协议的上游参考 |

**内核在宿主里的接法**（都可以在宿主仓库里核实）：

- **部署形态**：`KLink.Bot.dll` 与 `fyserver.dll` **同级**部署（启动器仓库的 `rel/data/fyserver/`），
  数据目录是 `<ContentRoot>/BotData`（`CCB-TEAM/fyserver` 的 `Services/ServerBotService.cs:193`；
  找不到时回退到 `AppContext.BaseDirectory/BotData`，`:196`）。
  `BotData/` 里放的就是本仓库 `klink bot/docs/` 那几份 JSON，外加训练好的 `nn-model.bin`。
- **调用链**：`MatchInfo ──ToSnapshot──▶ ServerMatchSnapshot ──▶ BotTurnService.DecideTurn`
  （`ServerBotService.cs:15` 的注释原文）。
- **部署脚本在启动器仓库里**：`tools/build-deploy-server.ps1` —— **本仓库没有这个文件**
  （本仓库只包含 5 个 C# 工具工程，见 §4.3）。
- **如果你想接自己的宿主**，看 `src/KLink.Bot/Server/` 里的三个契约类：
  `ServerMatchSnapshot`（宿主快照 → 内核局面）、`AtomicAction`（内核动作 → 协议动作）、
  `BotTurnService`（重建局面 → 候选枚举 → 打分 → 产出动作）。

⚠️ 顺带说明：**启动器 / 私服有自己的许可与免责声明**（`fyserver` 的 README 写明「非盈利性，
严禁用于任何商业或营利性目的」），与 `klink bot` 本仓库无关。见 §10.4。

**一个值得提前知道的宿主陷阱**（有真实事故记录）：宿主工程里如果写了
`<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>`
（为了 AOT 兼容），那是**进程级**开关。任何用了反射式 JSON 却没显式配 `TypeInfoResolver`
的代码，在那个宿主里抛异常、在别的宿主里正常。

实测代价是一整局日志全是「神经网络决策失败（Reflection-based serialization has been
disabled…），本回合改用贪心」—— **对局照常跑完，看起来像「AI 在打但很笨」，实际是 AI 根本没上场。**

`tools/ServerBridgeTest` 和 `tools/NNPlay` **测不出这个问题**（它们自己没关反射）。
为此专门有 `tools/AotProbe`：

```powershell
dotnet run --project tools\AotProbe -c Release --no-build -- "<数据目录>"
```

**改完内核跑它一次，比打一局真对局便宜得多。**

---

## 6. 快速开始

### 6.1 前置

| 需要 | 版本 | 说明 |
|---|---|---|
| .NET SDK | **10.0**（实测 `10.0.302`） | 内核与全部工具都是 `net10.0` |
| Python | 3.x（实测 `3.14.6`） | 只用于数据生成 / 分析脚本；**跑内核和审计不需要它** |
| 操作系统 | Windows（实测） | 路径与脚本按 Windows 写（`out\audit\*.ps1`）；内核本身是纯 .NET，理论上跨平台，但**未验证** |
| 游戏本体 | **不需要** | 卡牌数据与 IR 随仓库携带 |

⚠️ `bin/` 与 `obj/` 被 gitignore，**clone 出来没有编译产物**，必须自己先 build。

### 6.2 构建

```powershell
# 内核（唯一必须构建的）
dotnet build src\KLink.Bot\KLink.Bot.csproj -c Release

# 五个工具工程
dotnet build tools\BotSim           -c Release
dotnet build tools\ServerBridgeTest -c Release
dotnet build tools\NNTrain          -c Release
dotnet build tools\NNPlay           -c Release
dotnet build tools\AotProbe         -c Release
```

本次实测结果：**全部 0 error**；内核构建有 **1 个可空性警告**
（`CS8602`，`src/KLink.Bot/Server/BotTurnService.cs:191`）。

⚠️ **不要 `dotnet build KLink.slnx`** —— 它的 restore 是坏的（`KLink.slnx` 的注释里写明了），
请逐个项目 build。

### 6.3 跑自测

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- selftest
```

实测：**120 项 / 1 失败**（进程退出码 1）。那 1 项是**已知且刻意保留**的：

```
❌ 手牌目标：`gordon_highlanders` 的「选手牌里的指令」必须真的落实（0 费 + 回牌库顶）
```

### 6.4 跑全卡池烟雾测试

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- smoke-all-cards
```

会写出 `out\audit\smoke-all-cards.tsv`（逐用例，被 gitignore）与
`out\audit\smoke-all-cards.txt`（摘要，**被跟踪**）。想不覆盖跟踪文件：

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- smoke-all-cards `
  --out "$env:TEMP\smoke.tsv" --summary "$env:TEMP\smoke.txt"
```

### 6.5 跑回放审计

```powershell
# 单局
dotnet run --project tools\ServerBridgeTest -c Release --no-build -- --audit-replay "out\_server-replays\replay-508065"

# 6 局汇总（推荐：先跑这个）
& "out\audit\audit-4metrics.ps1"
& "out\audit\audit-all-replays.ps1"
```

两个脚本都会自己向上找含 `KLink.slnx` 的仓库根，所以在哪调用都行。

### 6.6 常见坑

| 坑 | 说明 |
|---|---|
| **增量编译假阴性** | `Copy-Item` 会保留源文件时间戳 ⇒ MSBuild 判定「源比输出旧 ⇒ 最新」⇒ **不重编译**。看到「改了没效果」，先怀疑增量编译 |
| **`--no-build` 用的是各自 bin 里的那份 DLL** | `dotnet run --project tools\ServerBridgeTest --no-build` 用的是 `tools\ServerBridgeTest\bin\...\KLink.Bot.dll` 这份**拷贝**。改完 `src\KLink.Bot` 后**必须显式重建**，并核对各份 DLL 的 SHA-256 是否一致 |
| **数据目录不是 `klink bot/docs/`** | 内核读的是各工程 `bin\Release\net10.0\Data\` 下那份拷贝（由 csproj 的 `CopyToOutputDirectory` 生成）。改了 `klink bot/docs/*.json` 必须重新 build 才生效（见 §8.8） |
| **仓库里有 7.1 GB 的 pak 与 249 MB 的 `.jmap`** | 任何「把整个目录 `git add`」的做法都会直接爆掉。`.gitignore` 已排除，但别手动绕开 |
| **清理临时文件必须显式列举目标** | 本项目出过一次事故：用通配符清理 `out\_*` 时**删掉了 7 局回放**（不可恢复）。不要用通配删除 |
| **宿主侧部署会锁 DLL** | 如果宿主进程正在运行，它会**独占** `KLink.Bot.dll`，部署会挂住。必须先关掉宿主 |

---

## 7. 度量与验证方法

这一节是项目里**最需要小心**的部分。同一个改动，用不同判据看会得到不同结论。

### 7.1 四条判据（按可靠性排序）

出处：[`out/audit/audit-4metrics.ps1`](out/audit/audit-4metrics.ps1) 的文件头注释（`:1-12`）与末尾（`:96`）。

| # | 判据 | 为什么可靠 |
|---|---|---|
| 1 | **首个漂开点（审计 ⑤b）是否后移或消失** | 最稳 —— 它是「第一条**人类**动作被内核拒绝」的位置，语义明确、单调 |
| 2 | **未实现原语（⑥）是否减少** | 集合型指标，方向明确 |
| 3 | **HQ 对不上（④，只看【人类】那一栏）是否减少** | 人类动作是 ground truth |
| 4 | **人类失败总数** | ⚠️ **只在没有随机效果参与时可靠** |

`audit-4metrics.ps1` 会把四条一起打出来，省得每次人工从长文本里抠。

### 7.2 三条口径警告

**① 只有【人类】的动作是 ground truth。**

回放里 **bot 自己的动作是旧内核生成的**，用新内核重放自然会被拒 —— **那不是保真度信号**。
所以汇总脚本把 left / right 分开统计（`out/audit/audit-all-replays.ps1:6-8` 就是为这件事写的注释）。

**② 随机效果从「静默失效」变成「正确执行」时，人类失败数可能反而上升。**

因为快照里**没有 RNG 种子**，随机效果本身不可复现。也就是说，「人类失败数」这个指标
对随机效果是**负向**的：修对了反而可能变差。

**③ 「花费 / 消耗」这类下界量只能证伪、不能证实。**

出处：`klink bot/docs/内核补全队列.md:8314-8321`。原文的方法论是：

> 用「花费 / 消耗」这类下界量去反推「上限 / 容量」时，永远要问：观测到的数是**紧贴**还是**松贴**？
> 紧贴（观测值 ≈ 模型预测值）⇒ 有信息量；松贴（观测值 << 模型预测值）⇒ **几乎没有信息量**，
> 换一个更大的模型也照样「成立」。

实例：kredit（费用）槽位模型。有人提出「槽位 = 全局回合号」，证据是「每回合花费只跟全局回合号吻合」；
但**花费是槽位的下界**，拿一个下界去比一个更大的数当然「每一行都成立」= **假吻合**。
真正紧贴的是 `自己回合 + 1`（后手有奖励槽）。

**⇒ 「无法被证伪」≠「被证实」。**

### 7.3 ★ 「两个错抵消」：**「0 失败」不等于正确**

这是本项目最重要的一条经验，也是为什么不能只看「人类失败数」。

**现象**：内核每回合会发**两条** `XActionEndOfTurn`。把内核多发的那条删掉之后，
审计结果**反而变差**：

| 指标 | 删掉之前 | 删掉之后 |
|---|---|---|
| 回放 `542091` 人类失败 | 0 | **2** |
| 回放 `542091` HQ 对不上 | 9 | 2 |

（两处失败都是 `#33 t7` 与 `#63 t13` 的「kredit 不足（kredits=2，费用=5）」。）

**结论（客观陈述）**：内核在人类 kredit 模型上本来就偏低，那条多余的
`EndTurn → StartTurn(Left)` **恰好补上了这个偏差**。这是「两个错误互相抵消」的典型症状，
真正的 bug 在 kredit / 回合推进模型里。

出处：`klink bot/docs/内核补全队列.md:7868-7873` 与 `:7921-7926`。

**⇒ 所以：**

- **「0 失败」不能当绿灯**；
- **「修对了反而变差」是正常现象**，不要因为指标变差就回滚一个语义上正确的修复。

同类实例（身份错造成的**假绿**）：回放 `773639` 里 `#46/#47` 的动作码是 `32`
（= `iron_from_the_north`，费 1），而内核里 `9001/3001` 被当成 `the_commonwealth`（费 12）——
卡号由 `colossus` / `seac` 的**随机**抄牌生成，所以「看起来能付得起」其实是在比两张不同的卡。
出处：`klink bot/docs/内核补全队列.md:8336-8338`。

### 7.4 证据等级：回放侧只能证明「没有回归」

**6 局回放只覆盖很少的卡。** 实测（本次独立复核）：

```
out/_server-replays/ 里 6 局回放，每局各含 43 个不同的 card_* 名字，6 局的并集也是 43
⇒ 6 局用的是同一对卡组
而 IR 里有 1735 条、卡池有 2021 张
```

**⇒ 回放语料只覆盖卡池的约 2%。** 因此：

- 一个修复如果**在回放里没有信号**，可能只是那张卡**根本没出现在这 6 局里**；
- 反过来，回放侧「逐位相同」只能说明**没有回归**，**证明不了修对了**。

凡是一条修复的证据链只有「IR 形状 + 反编译产物 + 自测」，本项目的做法是**如实标注**，
而不是当成已被回放验证。

### 7.5 烟雾测试能抓什么、抓不到什么

全卡池烟雾测试的自我声明（`out/audit/smoke-all-cards.txt` 的「这个测试能抓什么、抓不到什么」一节）：

| 能抓 | 抓不到 |
|---|---|
| 异常 / 崩溃 | **语义错**（跑得通但算错：该不该减免、该扣多少血、条件门读错对象） |
| 撞到未实现原语 | **数值 / 顺序错**（那必须跟客户端对拍） |
| 撞步数上限 | — |
| 程序跳转没解析 | — |
| 非确定性（同种子两次不同） | — |
| 「该有效果却零变化」 | — |

> **⇒ 它是「覆盖 / 烟雾测试」，不是「正确性判据」。没报错 ≠ 是对的。**

两个具体的前提，也一并记录：

- **事件入参是按 `event-contracts.json` 的槽位名近似填的**，局面也是合成的
  （双方 HQ + 5 兵种 + 手牌 + 牌库）。真实对局里事件入参不同、局面不同 ⇒ 这里没报错不代表真实路径没问题。
- 确定性那条结论有个前提：每个用例的两次执行都在**缓存已热**状态下比较
  （冷启动先跑一次并丢弃）。也就是说它证明的是「同一个用例连续跑两次一致」，
  **不**证明「结果与进程历史无关」。冷启动 vs 热启动实测有 **13 个用例**只有 VM 步数不同
  （预期：缓存命中跳过执行）；跨用例的进程级污染**没有观测到**。

---

## 8. 当前状态（实测数字）

> 下面每个数字都是本机跑出来的，命令随附。凡不是本次实测的，会注明来源。

### 8.1 构建

| 命令 | 结果 |
|---|---|
| `dotnet build src\KLink.Bot\KLink.Bot.csproj -c Release` | **0 error**，1 warning（`CS8602` @ `BotTurnService.cs:191`） |
| `dotnet build tools\{BotSim,ServerBridgeTest,NNTrain,NNPlay,AotProbe} -c Release` | **全部 0 error** |

### 8.2 自测

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- selftest
```

⇒ **120 项 / 1 失败**。失败项为已知的 `gordon_highlanders` 手牌目标用例。

### 8.3 六局回放审计

```powershell
& "out\audit\audit-all-replays.ps1"     # 应用率 + 人类失败
& "out\audit\audit-4metrics.ps1"        # 四条判据
```

| 回放 | 应用 | 应用率 | 人类失败 | ④HQ差(人) | ⑤b 首个漂开点 | ⑥ 未实现原语种类 | RNG 游标 |
|---|---|---|---|---|---|---|---|
| 214436 | 59/61 | 96.7% | 0 | 3 | 无（完全对齐） | 6 | 3 |
| 389594 | 95/97 | 97.9% | 0 | 0 | 无（完全对齐） | 4 | 69 |
| 508065 | 124/141 | 87.9% | **16** | 43 | `#54 t13 PC`（打不出） | 9 | 54 |
| 542091 | 75/78 | 96.2% | 0 | 0 | 无（完全对齐） | 5 | 4 |
| 773639 | 134/137 | 97.8% | 0 | 0 | 无（完全对齐） | 6 | 83 |
| 854099 | 101/118 | 85.6% | **14** | 24 | `#70 t15 PC`（打不出） | 4 | 53 |
| **合计** | **588/632** | **93.0%** | **30** | **70** | 4/6 局完全对齐 | — | — |

**读数**：

- 6 局里有 **4 局**的人类动作**完全被内核接受**（首个漂开点为空）；
- 剩下的失败**集中在 2 局**（`508065` 16 条 + `854099` 14 条 = 全部 30 条）；
- 这 2 局的首个漂开点**都是「出牌打不出」**（`PC`），与 §9.1 的随机效果选卡问题同源；
- `RNG 游标` 列是内核本局消耗的随机数个数，用于定位「游标落后 / 超前」（见 §9.1）。

⚠️ 只有【人类失败】是保真度信号；`bot 失败`（未列出）是旧内核动作被拒，属正常。

### 8.4 全卡池烟雾测试

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- smoke-all-cards
```

本次重跑（播种 `20261002`）与仓库里那份 `out/audit/smoke-all-cards.txt` **逐位相同**：

```
用例=4088  卡=1570  入口=55

  OK（跑通且有状态变化）    1586
  A  抛异常 / 崩溃              0
  B  撞未实现原语             763
  C  撞步数上限                 0
  D  零状态变化              1739
```

| 段 | 数字 |
|---|---|
| (A) 抛异常 | **0 张 / 0 个用例** |
| (B) 撞未实现原语 | **362 张 / 763 个用例 / 105 个原语** |
| (C) 撞步数上限 | **0 张 / 0 个用例** |
| (D) 零状态变化 | **657 张 / 1739 个用例** |
| 确定性（同种子两次逐位相同） | ✅ 全部用例一致（指纹 / RNG 消费次数 / 步数 / 未实现集合） |
| 引擎会派发的活入口点 | **64 个**，本次跑到 **55 个**；IR 里**没有任何卡注册**的活入口点 **9 个** |
| 未实现原语影响最大的几个 | `HasCampaignUpgrade` 35 张 / `ShouldGotchaTrigger` 34 张 / `MakeCardRetreat` 26 张 / `ConvertCard` 19 张 / `FullyHealCard` 17 张 |

### 8.5 派发表静态缺口

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- dispatch-gap
```

```
=== 派发表静态缺口（IR 会调用、派发表没有、locals 也兜不住）===
  种类：538    真缺口调用点：2788
  指纹：33D02CF8E0EEC7D5
```

与冻结基线逐位相同（`tools/BotSim/DispatchGap.cs:75,78`）。

缺口最大的几个（真缺口调用点数）：
`HasCampaignUpgrade` 247 / `CampaignSetText` 211 / `CampaignAddKreditCost` 79 /
`GiveStarForCampaign` 75 / `CampaignAddAttack` 59 / `CampaignAddDefense` 59 /
`GotchaTriggered` 54 / `ShouldGotchaTrigger` 53 / `CreateHelpBubbleEntry` 39 /
`MakeCardRetreat` 36 / `FullyHealCard` 33 / `ConvertCard` 26。

⚠️ 这个集合里**混着大量 UI / 动画 / 战役（Campaign）专用函数**，
它们**不需要**在对局内核里实现。判据本身只负责「集合不再悄悄变大」，
不判断「该不该修」（分类见 `out/audit/missing-keys-report.md`）。

### 8.6 数据规模（本次独立复核）

| 项 | 数字 | 怎么来的 |
|---|---|---|
| IR 条目总数 | **1735** | 读 `klink bot/docs/card-ir.json` 的 `len()` |
| ↳ 其中 | 1707 个 `card_*` + 24 个 `BP_*` + 2 个 `WBP_*` + 1 个 `BPI_*` + 1 个 `createCard_*` | 按键前缀统计 |
| IR 里注册的**不同入口名** | **449** | 聚合所有卡的 `entrypoints` 键 |
| 入口注册总数 | **3608** | 各卡 `entrypoints` 条数求和 |
| 带 `locals`（卡内私有函数体）的条目 | **683** | `locals` 非空 |
| ↳ 其中带 `CanPlayFromHand` 私有函数 | **438** | 自测里有一条「IR 必须带卡自己的 `CanPlayFromHand`」的重生成守卫 |
| 卡池（`cards.live.json`） | **2021** | 读 JSON |
| ↳ 卡池里**没有蓝图 IR** 的条目 | **314** | 多为 `card_display_*` / `card_location_ai_*` 等 UI / 战役条目 |
| ↳ IR 里有、卡池里没有的 | **0** | 集合差 |
| 卡组码映射 | **2499** | 读 `deck_code_ids.json` |
| 效果调用表条目 | **2053** | 读 `card-effects.json` |
| 事件契约条目 | **172** | 读 `event-contracts.json` |

### 8.7 仓库规模

| 指标 | 值 |
|---|---|
| 已跟踪文件 | **453 个** |
| 跟踪内容总字节 | **≈ 22.4 MiB**（`git ls-tree -r -l HEAD` 的 size 列求和；`.gitattributes` 已把行尾统一成 LF） |
| `.git` 目录 | **约 4.2 MiB**（其中 pack 3.98 MiB / 503 个对象）—— 大 JSON 压得很好 |
| 最大单文件 | `klink bot/docs/card-ir.json`，**9,847,589 B ≈ 9.4 MiB**（远低于 GitHub 的 100 MB 硬限制） |
| 真实对局语料 | `out/_server-replays/` 6 局；`klink bot/docs/fresh-replays/` 7 局；`live-replays/` 5 局 |
| 审计产物 | `out/audit/` 199 个跟踪文件 |
| 首次提交 | `d2d0f5c`（2026-10-02 17:31:28 +0800） |

> 首次提交时是 **461 个文件 / 21.38 MiB（仅纯 ASCII 文件名口径）**；
> 此后新增了 `LICENSE` 与 `klink bot/docs/内部现状与路线图.md`（后者是从根 `README.md`
> 用 `git mv` 移过去的，git 把它识别为 100% 的 copy），
> 并删除了 `goal.txt` 与 9 份已过期的内部文档（见 §14）。

⇒ **不需要 Git LFS。**

> ⚠️ 顺带说明一个容易算错的数字：只统计**纯 ASCII 文件名**的已跟踪文件时，合计约 **21.4 MiB** ——
> 差的 **约 1.02 MiB** 正是 **11 个中文名**跟踪文件
> （9 个 `klink bot/docs/*`、2 个 `out/audit/*`）。
> 用 `git ls-files` 走 shell 管道时，非 ASCII 路径会被 git 加引号转义，很容易被漏掉。

### 8.8 版本一致性与数据目录

- 内核读的数据目录**不是** `klink bot/docs/` 本身，而是构建时被
  `CopyToOutputDirectory` 复制到各工程 `bin\Release\net10.0\Data\` 的那一份
  （映射关系见 `src/KLink.Bot/KLink.Bot.csproj`）。改了 `klink bot/docs/*.json`
  必须重新 build 才会生效。
- 本次跑自测 / 审计 / 烟雾测试时，事件契约文件的来源路径被打印为
  `<repo-root>\klink bot\docs\event-contracts.json`，可用来确认数据目录解析正确。

### 8.9 已知的**过期**文档

`src/KLink.Bot/README.md`（240 行）**严重过期**，里面还写着：

- 「kredit 上限（现按 12）」—— 实际是 `MaxKreditCap = 24`（`MatchEngine.cs:51`）；
- 「card-ir.json 1636 张卡」—— 实际 1735 条；
- 「还缺棋盘状态」—— 棋盘状态已完整实现（`Engine/GameState.cs`）；
- 「修跳转语义」—— 已解决。

**请以本 README 为准。** 本次没有改它（避免与其他正在进行的改动冲突）。

---

## 9. 路线图 / 已知缺口

> 这一节**只列客观事实与规模**，按「已确认存在」→「未定位」→「未做」排列。

### 9.1 最大的一块：随机效果的「选卡」不一致

**状态：未修。**

- 已经确认**不是发号问题**：回放 `773639` 里内核台账的卡号（`3001/3002/3004`）与客户端引用的一致，
  **槽位一致、只有槽里的卡不同**。
- 分两类：

| 类 | 情况 | 可否修 |
|---|---|---|
| **① 静态卡池** | 池按名字排序 ⇒ 只可能是**下标不同** ⇒ 要么「池子大小 / 过滤器」不同，要么「随机值 / 游标」不同 | 可修（把每次消费的原始值记进流水账，拿客户端的卡反推下标） |
| **② 牌库派生池** | 快照的牌库顺序是宿主用非确定性的 `Random.Shared` 假洗出来的 | **修不了**（需要能拿到真实初始牌序的快照） |

- **影响面**：回放 `508065` 的 16 条人类失败（首个漂开 `#54 t13`）、
  回放 `854099` 的 14 条人类失败（首个漂开 `#70 t15`）—— 即 §8.3 里全部 30 条失败。

**未定位的具体差额**：回放 `508065` 的 `atlantic_convoy`（`#36 t9`）两次抽签，
内核落在随机流位置 `#42/#43`，客户端落在 **`#88`** ⇒ **内核落后 46 次消费**。
候选池本身已验证正确（102 张美国费 ≤ 3 的单位、字典序；客户端选中的卡在流位置 `#88` 上正好是 55 号，
与客户端一致）⇒ **差异只在流位置，不在候选集**。46 次的来源**未定位**。

出处：`out/audit/idfix/README.md:42-47`。

### 9.2 费用 / kredit 结算的剩余缺口

**状态：模型未定案。**

- 已核实的强线索就是 §7.3 的「两个错抵消」：内核在人类 kredit 模型上**偏低**。
- 未定问题：「发号用的回合号」与「kredit 槽自然增长」是不是**两个独立计数器**？
- 现状模型（`MatchEngine.cs:456-537`）：**槽位 = 自己第几个回合（+ 卡牌效果给的额外槽）**，
  与 `State.Turn` 无关；后手有奖励槽。
- **注意**：「花费 ≤ 槽位」只是**单侧弱约束**，只能证伪「self 模型」，永远证伪不了「global 模型」
  （global 恒 ≥ self）。见 §7.2 第 ③ 条。

### 9.3 结构性缺口

| 缺口 | 规模 | 出处 |
|---|---|---|
| **未实现原语** | **105 个**（影响 362 张卡 / 763 个用例） | `out/audit/smoke-all-cards.txt` |
| **派发表真缺口**（`locals` 也兜不住） | **538 种 / 2788 个调用点**，指纹 `33D02CF8E0EEC7D5` | `dispatch-gap` 实测 |
| **从不派发的玩法入口** | IR 入口名共 **449** 个，剔除 UI / 动画后仍有 **53 个玩法相关入口**内核从不派发；**23 张卡**的**全部**入口都是死入口 | `out/audit/semantic-reconcile-report.md` §5(N) |
| ↳ 三条完整的死事件链 | **Pincer**（7 张）+ **Intel**（3 张）+ **Lose Smokescreen**（3 张）= 13 张卡，按「一条链一次修」性价比最高 | 同上 |
| **`locals`-only 卡零覆盖** | **45 张**卡的 `entrypoints` 为空、逻辑全在 `locals`；烟雾测试按 `card.Entrypoints` 枚举用例 ⇒ 这 45 张**一个用例都没有**。连同 `entrypoints` 为空的共 **98 张**零覆盖 | `out/audit/semantic-reconcile-report.md` §5(L) |
| **`Gotcha` 子系统** | IR 调用点：`GotchaTriggered` **54 点 / 52 张卡**、`ShouldGotchaTrigger` **53 点 / 52 张卡**、`IsGotcha` **16 点 / 13 张卡**；回放里每局真触发 29~58 次。**故意不做**：它是整条子系统（`gotchaActivated` + `RearrangeLocation` + `SetCardsSeenByCipher` + Covert 揭示位 + cipher），半吊子实现比不实现更糟 | IR 实测 + `klink bot/docs/内核补全队列.md:8348` |
| **`changeType = 4` 在攻 / 防链上方向反了** | `ChangeAttack` 上共 **56 个调用点 / 41 张卡**；其中 **50 处 `amount = 0`**（真 no-op），**6 处非 0 ⇒ 把「撤销 buff」当成了「加 buff」**。涉及 5 张卡：`card_unit_ki_42_ii_ko`(2)、`card_unit_kyushu_j7w3`(2)、`card_unit_su_100`(4)、`card_unit_type_92_105mm_field_gun`(1)、`card_unit_type_97`(1×2)。对照：`DoChangeKreditCost` **处理了** `changeType == 4`（`RemoveCostBuff`）⇒ **同一个枚举，费用那条链修了、攻防那条链没修** | `src/KLink.Bot/Effects/CardApiDispatch.cs:2328-2365`、`:2434` |
| **「同一原语多种实参形状，实现只处理一种」** | 一个**系统性 bug 类**，已找到 9+ 个实例（`Array_Add` / `Array_Contains` / `IsSameSideUnit` / `MakeVeteran` / `CustomAbilityAdd` / `ChangeKreditCost` …）。危险之处：**这些原语都在派发表里 ⇒ 不计入「未实现原语」**，烟雾测试只看到「零变化」 | `out/audit/argshape-inventory.txt`、`out/audit/scan-target-shapes.py` |
| **`UnresolvedJumps` 不能当守卫判据** | 代码注释写「应恒为 0」，实测 **2212 个用例**非 0。静态复核表明跳转表本身没问题（1636 个蓝图的 `steps` + 881 个 `locals` 函数体，跳转目标缺失 **0 张 / 0 处**；692 张「首条是 `pushFlow`」的卡，派发返回地址 **692/692** 都指向 `return`）⇒ 非 0 是「派发返回地址」这条良性路径造成的 | `out/audit/smoke-all-cards.txt` 的「VM 诊断」一节 |
| **`SmokeAllCards.IsPurePrimitiveName` 大小写 bug** | `name.StartsWith("Get")` **区分大小写**，把小写开头的查询原语当成「写原语」⇒ D2 表（「调了写原语却零变化」）长期虚高。**未改源码**（怕影响可比性），已知虚高幅度 157 → 86 张卡 | `out/audit/semantic-reconcile-report.md` |
| **`GetRandomCard` 忽略 `AlwaysSelectedAsRandom`** | 潜伏（目前不触发） | `out/audit/没修的.md` |
| **`_bal` / `_vet` 平衡变体数值** | 蓝图里只有基础卡，数值微调在另一张表里（**尚未解出**）⇒ `CardDatabase.Find` 剥后缀回退到基础卡，**数值可能不准** | — |

### 9.4 证据链不足的地方（如实标注）

| 事项 | 情况 |
|---|---|
| 那 3 个语义修复（`IsSameSideUnit` / `MakeVeteran` / `damageToDeal` CDO 默认值） | 证据链是「烟雾用例翻转 + selftest 无回归 + CDO 原文 + 卡面数字吻合」，**不是**回放对拍。原因见 §7.4：涉及的卡在这 6 局里命中 0 张 |
| 静态「卡面文字 vs 实际行为」对账的精度 | **中等偏下**：(A) 档 9 条**全是假阳性**（把条件词读成效果词等），(B) 档 147 条里真正值得人看的约 20 条。真正抓到东西的是**动态**那一侧（调了写原语却零变化）+ **实参形状对账** |
| 自对弈胜率偏斜 | 早期实测出现过 241/59 这种偏斜，**预期内**（平衡变体未应用 + 部分效果是近似实现） |
| 6 局回放的 ⑥ 段原语并集 = 9 个，而烟雾测试 B 表有 105 个 | 即 **97 个未实现原语在回放里从没触发过**（数字出自 `klink bot/docs/内部现状与路线图.md`，本次**未独立复核**） |
| 平台支持 | 只在 Windows 上实测过；**跨平台未验证** |

### 9.5 建议的下一步顺序（按「成本 ÷ 收益」）

1. **批量做「容易的引用 / 包装」类原语**（约 40 种，语义都有蓝图出处）：
   `getCardsBuffedByThisCard`(25) / `SetCountdown`(15) / `getKreditTempBuffAmount`(11) /
   `RemovePin`(10) / `GetCardsPlayedFromHandLastTurn`(10) / `GetSupportLineLocationBySide`(19) /
   `DiscardRandomCardFromHand`(20) / `LoseKreditSlot`(16) …
2. **`FullyHealCard`(33)** —— 最简单的一个「真实现」。
3. **`MakeCardRetreat`(36) + `GetAllCardsInFrontline`(8)** —— 一起做，结构照 `DestroyCard`。
4. **`Gotcha` 子系统**（`GotchaTriggered` 54 + `ShouldGotchaTrigger` 53 + `IsGotcha` 15）——
   收益最大，但要做状态机，单独排一轮。
5. **`ConvertCard`(26)** —— 最后做，函数体最长（400+ 行）。

另外三条被点名的候选：

- 用「实参形状 vs 实现形状」对账**扫一遍全部原语** —— 目前唯一被证明能抓到「同一原语多种实参形状」类真 bug 的机械化判据；
- 修 **Pincer / Intel / Lose Smokescreen** 三条死事件链（13 张卡，一次一条链）；
- 给 `SmokeAllCards` **加 `locals` 模式**，把 45 + 53 张零覆盖的卡纳入测试。

⚠️ **不管做哪个，做完都要更新 `tools/BotSim/DispatchGap.cs` 的两个基线常量**
（跑 `dispatch-gap` 拿新值），否则守卫会（正确地）失败。

### 9.6 补充审计语料

当前只有 6 局在 `out/_server-replays/`，另有 `fresh-replays/` 7 局 + `live-replays/` 5 局。
**语料量是当前最大的瓶颈之一**：6 局只覆盖同一对卡组（§7.4）。

---

## 10. 法律与伦理

> **这一节请务必读完再决定怎么用这个仓库。**

### 10.1 项目性质

本项目是**逆向工程 / 安全研究 / 互操作性研究**性质的个人研究项目，目标是
**理解并离线复现一款已购买游戏的规则行为**。它不修改、不注入、不劫持游戏客户端进程。

### 10.2 仓库里**不含**任何游戏本体资源

这是硬保证，`.gitignore` 与已跟踪文件清单都可核对。**已跟踪文件里不存在**：

| 被排除的东西 | 大小 / 说明 |
|---|---|
| `kards-Windows.pak` | **7.1 GB** 游戏数据包本体 |
| `*.jmap` | UE 映射文件（183 MB + 66 MB，反编译用） |
| `key.txt` | **pak 的 AES-256 解密密钥** |
| `decompiled/` | 反编译原始产物（几十 MB 中间 JSON） |
| `live/`、`extracted-live/` | 从 pak 解出的 uasset 目录树 |
| 游戏素材（贴图 / 音频 / 模型） | 无 |
| `UAssetAPI-master/`、`UAssetCLI/` | 第三方 fork 与解包 CLI（各有自己的仓库） |
| NN 训练数据 / 模型 | `*.bin` / `*.npz` / `*.onnx`（最大一份 7.5 GB） |
| 宿主侧目录 | `rel/`、`tem/`、`setting.json`、日志、`BotData/` |
| 构建产物 | `bin/`、`obj/`、`publish/` |

核对方式：

```powershell
git ls-files | Select-String -Pattern '\.pak$|\.jmap$|key\.txt|^decompiled/|UAssetAPI|UAssetCLI|\.bin$|\.npz$'
# ⇒ 无输出
```

此外，本次核实了**已跟踪的源码 / 文档 / 脚本里不含本机绝对路径**
（唯一的例外是被移入 `klink bot/docs/内部现状与路线图.md` 的那份作者内部文档，
它在描述脱敏时提到了原始路径）。

### 10.3 使用者的义务

- **你必须自己拥有正版 KARDS 游戏。** 本项目不附带、不提供、也不指引获取游戏本体或解密密钥。
- **pak 解密密钥（`key.txt`）不在仓库里**，需要你自己从自己的游戏安装中取得。
- 本仓库携带的 `card-ir.json` / `cards.*.json` 等文件是**从游戏数据中提取的衍生物**，
  仅用于研究与互操作目的。**再分发前请自行评估**。
- 请遵守你所在地区的法律与游戏的服务条款。

### 10.4 与官方无关

- 本项目**与 1939 Games 没有任何关联**，未获其授权、认可或赞助。
- **本仓库本身**只是一个**离线规则内核 + AI**：它不提供任何绕过付费、绕过联机限制、
  作弊或修改对局结果的功能，也不包含任何注入 / 内存修改代码，**不是**游戏客户端或启动器。
- ⚠️ 但它出自开源项目 **KLink**（启动器 + 私服，见 §5.6）。**启动器 / 私服是独立项目**，
  有各自的仓库、许可与免责声明（例如 `fyserver` 的 README 写明「非盈利性，严禁用于任何
  商业或营利性目的」）。**它们的行为与责任不属于本仓库**；如果你要用它们，请读它们自己的声明。
- 修改、分发 KARDS 客户端可能违反 1939 Games 的服务条款。请仅用于个人研究。

### 10.5 许可（License）

**GNU General Public License v3.0（GPL-3.0）** —— 全文见仓库根的 [`LICENSE`](LICENSE)。

选它的理由是**与来源仓库保持一致**：

> 本仓库是从 [`Xuewu-awa/KLink-dotnet`](https://github.com/Xuewu-awa/KLink-dotnet) 的
> `src/KLink.Bot` **逐字节抽取**出来的（证据：`src/KLink.Bot/Engine/UeRandomStream.cs` 两边
> SHA-256 相同，`55BD6F19…`），而那个仓库的 `src/LICENSE` 就是 **GNU GPL v3.0 全文**，
> 覆盖 `src/` 下的 `KLink.App` / `KLink.Server` / **`KLink.Bot`**。
> 本仓库的 `LICENSE` 与它**是同一个 git 对象**（blob `f288702d…`，35,149 B）——
> 即两边提交进 git 的字节完全相同（`.gitattributes` 把行尾统一成 LF；
> 工作树里带 CRLF 的那份是 35,823 B / SHA-256 `230184F6…`）。

**这对使用者意味着什么**（客观陈述，不构成法律意见）：

- 你可以自由地**使用、研究、修改、再分发**本作品，甚至用于商业目的；
- 但**衍生作品必须同样以 GPL-3.0 开源**，并保留版权与许可声明、标注你修改过的地方；
- 它是 **copyleft（传染性）** 的：把 `KLink.Bot.dll` 作为库**链接进你的宿主**（例如
  `fyserver`）时，宿主侧也要满足 GPL 的相应义务 —— 这一点在 §5.6 的宿主集成场景里
  需要特别注意；
- GPL-3.0 第 11 节还包含**明示的专利授权**。

⚠️ **两个必须注意的点**：

1. **许可只能覆盖作者自己的代码。** 仓库里从游戏数据提取的产物（卡牌数据、IR、
   回放动作流）可能仍受游戏发行商的权利约束，**GPL-3.0 不能替你解决这部分**。
2. 文档（`*.md`）如果作者想单独授权，通常用 CC BY 4.0 之类；但**软件许可不要用 CC 系列**
   （Creative Commons 明确不建议用于软件）。目前 `*.md` 与代码同受 GPL-3.0 覆盖。

### 10.6 免责

本项目按「现状」提供，不附带任何明示或暗示的担保。使用本项目造成的任何后果由使用者自行承担。

---

## 11. 贡献指南

这个项目的验证方式比较特殊，欢迎按下面的方式参与。

### 11.1 最有价值的贡献：**真实对局回放**

**当前语料量是最大瓶颈**（6 局、只覆盖同一对卡组，见 §7.4）。如果你能提供
「开局快照 + 完整动作流」的真实对局（自己打的即可），价值远大于任何单个功能 PR。

放法：`out/_server-replays/replay-<match_id>.json` + `replay-<match_id>.actions.json`
（两件套；`audit-all-replays.ps1` 与 `audit-4metrics.ps1` 会自动发现它们）。
⚠️ **提交前请确认回放里不含账号 / 昵称 / 邮箱等个人信息。**

### 11.2 修 bug / 补原语的正确流程

```
1. 先跑基线：selftest + audit-4metrics.ps1 + smoke-all-cards（记录四条判据的值）
2. 改代码
3. 重建内核，并核对各份 KLink.Bot.dll 的 SHA-256 一致（避免跑到旧拷贝）
4. 重跑：selftest（不得出现新失败）
5. 重跑：audit-4metrics.ps1（⑤b 首漂开点应后移或消失；④ 人类 HQ 差应减少）
6. 若改了原语层：重跑 dispatch-gap，并按需要更新 tools\BotSim\DispatchGap.cs 的基线常量
7. 在 PR 描述里**写清哪条判据变好了、哪条没变、哪些没验证**
```

### 11.3 三条硬规矩

1. **不要用「人类失败数」当唯一判据。** 见 §7.2 与 §7.3 —— 随机效果会把它变成负向指标，
   而且「0 失败」可能是两个错互相抵消。
2. **不要在无法验证时声称验证过。** 回放覆盖不到 98% 的卡池（§7.4）。
   证据链只有「IR 形状 + 反编译产物 + 自测」时，**如实写出来**。
3. **不要用通配符做批量删除。** 本项目出过一次事故：清理 `out\_*` 时删掉了 7 局回放（不可恢复）。

### 11.4 代码风格

- C#：`net10.0`，`Nullable` 与 `ImplicitUsings` 均开启（`src/KLink.Bot/KLink.Bot.csproj`）。
  注释用中文，**注释里写「为什么」而不是「是什么」**——现有代码大量使用这种风格。
- Python：`klink bot/tools/` 下的脚本保持「单文件、可直接 `python xxx.py` 运行」。
- **不要改数据路径字面量**（`klink bot/docs/...`），除非你打算一次性改掉全部 ~15 处（见 §4.4）。

### 11.5 改动落在哪个仓库

`src/KLink.Bot/**` 同时存在于**上游启动器仓库**（[`Xuewu-awa/KLink-dotnet`](https://github.com/Xuewu-awa/KLink-dotnet)
的 `src/KLink.Bot`）与**本仓库**里，两边是同一份代码（见 §1.4.2）。

- **只改内核代码**：两个仓库都要落到，否则会分叉。建议**先上游、后本仓库**。
- **只改数据 / Python 工具 / 审计报告 / 本文档**：这些是本仓库独有的（让内核能独立跑起来的那部分），
  直接改本仓库即可。
- **不确定该改哪边**：先开 issue 说明，或按「先上游」处理。

### 11.6 报告问题

请附上：命令、完整输出、种子 / 回放编号、以及内核 DLL 的 SHA-256。
有回放编号的问题最好定位。

---

## 12. 致谢

| 对象 | 用途 |
|---|---|
| **[`Xuewu-awa/KLink-dotnet`](https://github.com/Xuewu-awa/KLink-dotnet)**（本项目出自这里） | 启动器 + 私服（.NET 10 / WPF，GPL-3.0）。本仓库就是它的 `src/KLink.Bot` 抽出来的；`tools/build-deploy-server.ps1`、`BotData/` 数据目录、`ServerBotService` 都在那边 |
| **[`Xuewu-awa/KLink`](https://github.com/Xuewu-awa/KLink)** | 同一启动器的 Android / Java 版（协议与 .NET 版一致） |
| **[`kardswalker/kards-server-go`](https://github.com/kardswalker/kards-server-go)** | 私服协议的 Go 参考实现，KLink 系列的共同上游 |
| **[`CCB-TEAM/fyserver`](https://github.com/CCB-TEAM/fyserver)** | 实际加载本内核的私服核心（C#）。内核作为 `KLink.Bot.dll` 与它同级部署 |
| **`CCB-TEAM/kards-sim`**（第三方开源参照实现） | 蓝图 AST → C# 直译的参照物。本项目用 `klink bot/tools/fetch-kards-sim.py` 把它拉到 `ref/kards-sim`（`ref/` 被 gitignore）。它对本项目最大的价值是**交叉验证**：它的直译产物是忠实的，因此两边都缺同一处效果时，可以判定缺口在引擎侧而不是直译侧（见 `klink bot/docs/issue-kards-sim.md`） |
| **`UAssetAPI`**（第三方 UE 资产库） | 读取 UE 资产 / 蓝图字节码 |
| **Unreal Engine 文档与引擎源码** | `FRandomStream` 的语义（LCG 常数、`GetFraction` 的高 23 位变换、闭区间取整） |
| **`Kards_RNG_report`**（第三方逆向报告） | 提供了一组**可复算的** `FRandomStream` 测试向量，是本项目 RNG 复刻的逐位判据（§2.3） |
| **UE4SS** | 曾尝试用它做运行时采集；在这个 UE5.6 fork 上**实测不可用**（AOB 扫描失败），相关目录保留在 `.gitignore` 中不随仓库分发 |
| **KARDS 玩家社区** | 规则细节（重甲是否减免指令伤害、压制 / 抑制的解除时机等）的交叉确认 |

⚠️ 除 KLink 系列外，上述第三方项目**不在本仓库内**，各自遵循自己的许可。

---

## 13. 目录结构

```
<仓库根>/
├── README.md                       ← 你正在读的文件
├── LICENSE                         ← GNU GPL v3.0 全文（与来源仓库 KLink-dotnet 逐字节相同）
├── KLink.slnx                      ← 仓库根标记（工具靠它定位数据目录）+ 5 个工程
├── NuGet.config / .gitignore / .gitattributes
│
├── src/KLink.Bot/                  ← 规则内核 + AI（33 个文件）
│   ├── Engine/                     ← 对局引擎 / 状态 / 卡实例 / 随机流 / 协议动作
│   ├── Effects/                    ← 原语层 + Kismet 解释器 + 派发表
│   │   └── Blueprint/              ← KismetVm / KismetIr / DispatchGap
│   ├── Replay/                     ← 回放执行与审计信号
│   ├── Cards/                      ← 卡库 / 卡组码 / 元卡组
│   ├── NN/                         ← 局面编码器 + 模型
│   ├── Bots/                       ← 贪心 baseline
│   └── Server/                     ← 宿主集成契约（快照 / 动作 / 决策服务）
│
├── tools/                          ← 本项目的验证工具（C#）
│   ├── BotSim/                     ← selftest / smoke-all-cards / dispatch-gap / play
│   ├── ServerBridgeTest/           ← --audit-replay 回放审计
│   ├── NNTrain/                    ← 自对弈产数据 + 训练 + 验证
│   ├── NNPlay/                     ← 让训练好的 NN 下场打一局
│   └── AotProbe/                   ← 在「反射被关掉」的宿主里跑内核全路径
│
├── out/
│   ├── _server-replays/            ← 6 局真实对局（快照 + 动作流）
│   └── audit/                      ← 审计脚本 + 取证报告（199 个跟踪文件）
│
└── klink bot/                      ← ⚠️ 嵌套目录，不是笔误（见 §4.4）
    ├── docs/                       ← IR / 卡库 / 卡向量 / 卡组码 / 事件契约 + 全部报告
    │   ├── card-ir.json            ← 9.4 MiB，1735 条（解释器执行的东西）
    │   ├── cards.live.json         ← 2021 张卡面数值
    │   ├── fresh-replays/          ← 7 局真实对局
    │   ├── live-replays/           ← 5 局真实对局
    │   └── 内部现状与路线图.md      ← 作者的内部追踪文档（见 §14）
    └── tools/                      ← Python 生成器与训练脚本（142 个文件）
        ├── gen-kismet-ir.py        ← 字节码 → IR
        ├── gen-card-*.py           ← 卡库 / 关键字 / 卡池 / 向量 / 效果表
        └── nn-*.py                 ← 训练 / 评估 / 消融（r4 → r9）
```

---

## 14. 内部文档指路

> **作者的内部追踪文档在 [`klink bot/docs/内部现状与路线图.md`](klink%20bot/docs/内部现状与路线图.md)。**

那份文档面向作者本人：里面有**逐轮的 A/B 记录、私人待办、以及对既有结论的自我更正清单**，
写作风格与本文不同（更口语、带大量「未核实」标注）。如果你只想了解**项目现状与怎么用**，
读本文即可；如果你想看**每一处判断是怎么被推翻和修正的**，那份文档更完整。

其它值得一读的文档（都在 `klink bot/docs/`）：

| 文档 | 内容 |
|---|---|
| `KARDS基础规则参考.md` | 规则参考（真人玩家整理 + 蓝图层面的印证） |
| `对局协议参考.md` | 85 个子动作 / 138 个参数键 / 76 个接收器；紧凑动作名映射 |
| `NN训练诊断.md`（268 KB） | 神经网络为什么学不出东西（r4 → r9 的完整记录） |
| `内核补全队列.md`（482 KB / 8636 行） | 项目的活日志：规则定案 + 待办 + 每一轮的 A/B 与教训 |
| `核心规则缺口审计.md`（94 KB） | 关键字 / 合法性两个维度的缺口审计（⚠️ 数字较旧） |
| `保真度对拍报告.md` | 内核 vs 真实对局的数字 |
| `三方对拍方案.md` | 对拍方法论 |
| `issue-kards-sim.md` | 与第三方参照实现的交叉验证结论 |
| `kards-cpp源码勘察.md` | 反编译源码勘察笔记 |

> ⚠️ 这两份内部文档（`内部现状与路线图.md`、`内核补全队列.md`）是作者的**工作快照**，
> 写的时候引用了不少**一次性笔记**（进度快照、方案提案、单项诊断等）。
> 那些笔记已在本仓库整理对外版时删除，所以这些文档里**个别路径可能已经不存在** ——
> 引用到的**内容本身仍然有效**，只是原文件不再随仓库分发。

审计侧的取证报告（`out/audit/`）：

| 文件 | 内容 |
|---|---|
| `idfix/README.md` | RNG 复刻 + 发号规则 + 安全网 + 四判据的前后对比 |
| `semantic-reconcile-report.md` | 逐卡「卡面 vs 实际行为」语义对账（含 §9.3 的 23 / 53 / 45 三个数字） |
| `missing-keys-report.md` | 派发表缺口分类报告（A / B / C 档） |
| `没修的.md` | 某一轮**没修**的缺口清单（⚠️ 部分已过期） |
| `dim3-keywords.md` / `dim2-legality.md` | 关键字 / 合法性两个维度的逐条审计 |
| `argshape-inventory.txt` | 原语调用点实参形状清单 |
