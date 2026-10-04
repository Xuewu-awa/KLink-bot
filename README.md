# klink bot —— KARDS 离线规则内核 + AI

> **工作树验证状态**：新增撤回与战斗规则仍在回放验收中；`508065` 已观察到应用率回归，
> 尚不能将新增原语视为完成。当前记录与待办见 [当前执行记录](docs/当前执行记录.md)。
>
> **2026-10-04 本轮已验收**：触发派发含手牌 + 反制卡装填门（见 §8.10）——
> 主对拍集 `787/835 → 793/835`、人类失败 `32 → 26`、④ 人类 HQ 差 `108 → 95`、
> 完全对齐 `6/10 → 8/10`，另外 12 局**逐位不变**，自测 151/151、`dispatch-gap` 逐位不变。
>
> **2026-10-04 第二轮已验收（回放侧无信号）**：压制门的**形状**改正三处
> （T15 / T32 / T7，见 §8.11）—— 22 局逐位不变、自测 151/151（含一条被改写的旧用例，
> 做过判死验证）。证据链是「蓝图原文 + 自测」，**不是**回放对拍。
>
> **2026-10-04 第四轮已验收（回放侧无信号）**：三个从未派发的触发点接线
> （T35 11 卡 / T61 2 卡 / T48 3 卡，见 §8.13）—— 22 局逐位不变、自测 152/152
> （新用例做过判死验证）、`dispatch-gap` 逐位不变、纯新增 +184/−0 行。
> 另：把 §8.12 那条"试过但回退"的修补**定位到了具体一行**
> （`night_raid` 的 `IsLocationFull(handLocation)`，见 §8.12.1）—— 结论是
> **先修 P6 的手牌虚增，再注册 `GetHandLocationBySide`**。
>
> **2026-10-04 第五轮（未改行为）**：手牌容量 —— 见 §8.14。
> 新增 env 门控探针 `GameState.TraceHandOverflow`（带调用栈），点名了两条越界路径
> （`DoDrawSpecific` / `SpawnCardInHand`）；按蓝图原文补上两道容量门后
> **793/835 → 735/835** ⇒ 回退。
> ⚠️ **并更正了一个我自己先写错的判断**：蓝图 `DrawSpecificCardFromDeckBySide`
> （`:12406`，全函数 33 行）**根本没有容量门** ⇒ **「手牌 > 9」本身不是 bug**，
> 不能拿它当"内核多进了一张"的判据（§8.14 ②/④）。
>
> **2026-10-04 第六~九轮（回放侧无信号，但都是真缺口）**：
> ⑥ 逐条对账后补上**唯一**缺失的手牌容量门 `SpawnCardInHand`（§8.15）；
> ⑦/⑧ 手牌/牌库**双向流水账探针**，把 `854099 t11` 的手牌差**最终归因到牌库顺序**
> （§9.1 第②类，**数据不可得，不是代码 bug** —— 这条线索到此为止，见 §8.16/§8.17）；
> ⑨ **T31 `OnOtherCardAttacks` 接线完成**（§8.18，README §9.5 **P1** 那条，20 张订阅卡）。
> 自测 **152 → 154 项全通过**（每条都做过判死验证）；22 局**逐位不变**、`dispatch-gap` 逐位不变。
> ⚠️ **这一轮没有推动四条判据**（都是"回放侧 0 命中"的正确性修复）；
> 能推动判据的那条（`GetHandLocationBySide` / 反制卡落点）被**数据**卡住，已如实记录。

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

⇒ **133 项 / 1 失败**（2026-10-03）。失败项为已知的 `gordon_highlanders` 手牌目标用例
（它自己的机制已查清：`JSON_Clear` 会清空整张卡 JSON ⇒ `found` 出参从不写入，见 §9.3）。

### 8.3 六局回放审计

```powershell
& "out\audit\audit-all-replays.ps1"     # 应用率 + 人类失败
& "out\audit\audit-4metrics.ps1"        # 四条判据
```

| 回放 | 应用 | 应用率 | 人类失败 | ④HQ差(人) | ⑤b 首个漂开点 | ⑥ 未实现原语种类 | RNG 游标 |
|---|---|---|---|---|---|---|---|
| 214436 | 59/61 | 96.7% | 0 | 3 | 无（完全对齐） | 4 | 3 |
| 389594 | 95/97 | 97.9% | 0 | 0 | 无（完全对齐） | 3 | 69 |
| 508065 | 130/141 | 92.2% | **10** | 43 | `#54 t13 PC`（打不出） | 7 | 55 |
| 542091 | 77/78 | 98.7% | 0 | 0 | 无（完全对齐） | 4 | 4 |
| 773639 | 134/137 | 97.8% | 0 | 0 | 无（完全对齐） | 5 | 83 |
| 854099 | 106/118 | 89.8% | **9** | 36 | `#78 t17 ML`（移动被拒） | 4 | 52 |
| **合计** | **601/632** | **95.1%** | **19** | **82** | 4/6 局完全对齐 | — | — |

**读数（2026-10-03 重测）**：

- 6 局里有 **4 局**的人类动作**完全被内核接受**（首个漂开点为空）；
- 与 2026-10-02 相比：应用 **588 → 601/632**、人类失败 **30 → 19**（`508065` 16→10、`854099` 14→9）；
  首漂开 `854099` 从 `#70` **后移到 `#78`**；
- ⚠️ **④ 人类 HQ 差从 70 涨到 82**（`854099` 15→36）—— 这是「候选集顺序改成插入序」那一步
  带来的，**原因未解释**（见 §9.1.7 的如实标注）。按 §7.1 的可靠性排序，它排在⑤b 与人类失败之后；
- `RNG 游标` 列是内核本局消耗的随机数个数，用于定位「游标落后 / 超前」（见 §9.1）。

⚠️ 只有【人类失败】是保真度信号；`bot 失败`（未列出）是旧内核动作被拒，属正常。

### 8.4 全卡池烟雾测试

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- smoke-all-cards
```

本次重跑（播种 `20261002`）结果如下；烟雾摘要属于可变审计产物，后续以命令重跑结果为准：

```
用例=4088  卡=1570  入口=55

  OK（跑通且有状态变化）    1597
  A  抛异常 / 崩溃              0
  B  撞未实现原语             731
  C  撞步数上限                 0
  D  零状态变化              1760
```

| 段 | 数字 |
|---|---|
| (A) 抛异常 | **0 张 / 0 个用例** |
| (B) 撞未实现原语 | **347 张 / 731 个用例 / 101 个原语** |
| (C) 撞步数上限 | **0 张 / 0 个用例** |
| (D) 零状态变化 | **664 张 / 1760 个用例** |
| 确定性（同种子两次逐位相同） | ✅ 全部用例一致（指纹 / RNG 消费次数 / 步数 / 未实现集合） |
| 引擎会派发的活入口点 | **64 个**，本次跑到 **55 个**；IR 里**没有任何卡注册**的活入口点 **9 个** |
| 未实现原语影响最大的几个 | `HasCampaignUpgrade` 35 张 / `ShouldGotchaTrigger` 34 张 / `MakeCardRetreat` 26 张 / `ConvertCard` 19 张 / `FullyHealCard` 17 张 |

### 8.5 派发表静态缺口

```powershell
dotnet run --project tools\BotSim -c Release --no-build -- dispatch-gap
```

```
=== 派发表静态缺口（IR 会调用、派发表没有、locals 也兜不住）===
  种类：534    真缺口调用点：2750
  指纹：3CC26AEAEBDBE25B
```

与冻结基线逐位相同（`tools/BotSim/DispatchGap.cs`）。**每次修完原语都要重跑并更新那两个常量**
（历史：2026-10-02 `538/2788/33D02CF8E0EEC7D5` → 补 `GetCardsPlayedFromHandLastTurn` 后
`537/2778/E674E0A25E96DAEA`；`MakeCardRetreat` 补丁 B 单独会把调用点降到 `2742`、
指纹变 `07EE956F68DC804A`，见 §9.5 P0）。

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

### 8.10 ★ 2026-10-04 本轮：触发派发含手牌 + 反制卡装填门（两处修复，A/B 见下）

> 本轮**没有新增原语**（`dispatch-gap` 逐位不变：`522 / 2462 / FFEC7E071E9518C0`），
> 只修了两处**派发口径**。所有数字都是本机跑出来的，命令随附。

#### 修复 ①（根因级）触发派发的收件人快照**漏了手牌**

- **落点**：`src/KLink.Bot/Effects/CardApi.cs` 新增 `FillTriggerSnapshot`，
  `FireTrigger` 与 `BroadcastWithOutParams` 两处共用；快照从「棋盘 + 弃牌堆」
  扩成「**棋盘 + 弃牌堆 + 手牌**」（顺序：左场/左弃/左手/右场/右弃/右手，
  原有相对先后逐位不变，只是往后多了一批收件人）。
- **判据**：蓝图里**大量**事件程序带「在手牌里」的分支。`docs/card-ir.json` 实测
  （只算 `entrypoints` 的 `On*` 程序体）：
  `IsLocatedInHand` **42 个触发名 / 90 个 (卡,触发) 对**；
  `IsLocatedInDeck` **12 个 / 22 个**。
  只扫「棋盘 + 弃牌堆」时这些分支是**死代码**。
- **决定性实例（真人对局 `711061` 的 ⑤b 首漂开）**：
  `card_unit_5th_regiment`（卡面「When you lose a kredit slot, this unit gets
  **+2+1 if on the battlefield or -2 cost if in hand**.」）**整张卡只有一个入口**
  `OnAfterExtraKreditSlotGain`（IR `i=178`），体内两条路：
  `i=287 jumpIfNot(IsLocatedOnBoard) → i=10`（在手牌那一支）→
  `i=110 ChangeKreditCost(self, -2)`。
  711061 里左方 t3 连丢两个槽位（`#10 air_land_sea`、`#12 40th_cavalry_regiment`），
  客户端因此把手里那张 5th_regiment 降费；内核一直按 **4 费** 算，
  而此刻池子只有 `kredits=1` ⇒ `#23 t5 PC` 被拒 ⇒ 那张牌留在手里 ⇒
  下游 `#28 t7 ML`、`#41 t9 AC` 接连失败 ⇒ 右方 HQ 少掉 6 点伤害
  （该局 ④ 人类 HQ 差 10 条**全部**由这一条派生）。
- **同时补的一道护栏**：`FireTrigger` 末尾「主体不在棋盘上时补发一次」的兜底分支
  加了 `subjectSeen` 判定 —— 手牌里的卡满足 `IsAlive && !IsBoard()`
  ⇒ 旧的兜底条件对它恒成立 ⇒ 同一个程序会**跑两遍**。

#### 修复 ②（被 ①暴露出来）`ShouldGotchaTrigger` 漏了「这张反制卡已装填」

- **落点**：`CardApi.ShouldGotchaTrigger` 从 `IsGotcha(self) && self.IsAlive`
  改成 **`... && self.GotchaActivated > 0`**。
- **判据**：参考实现把语义逐字写在注释里
  （`ref/kards-sim/KardsSim/Bridge/EngineHost.cs:1350-1356`）：
  「参数：(triggerCard, out shouldIt)。**只有盖着的反制卡才响应**。」
  而 `IsGotcha` 那边（`:1342-1347`）也写着「Gotcha 是**盖在场上没翻开**的状态」。
  内核里"盖着"就是 `GotchaActivated > 0`（唯一写入方
  `AssignGotchaActivatedOnPlayFromHand`，`BP_CardFunctions.g.cs:28316`；
  `GetActiveGotchasOrdered` 早就用同一道 `> 0` 门过滤，`:18733`）。
- **为什么它是被 ① 暴露的**：修复 ① 之前手牌里的卡从不收触发，
  所以"未装填的反制卡在手牌里被任意 `OnOther*` 事件触发"这条错误路径**没被走到**。
  修复 ① 一开，`773639` 立刻多出一个**假钉住**：
  右方未装填的 `card_event_unexpected_resistance`（"Pin a unit that moves to the
  frontline."）把左方刚上前线的 `card_unit_2nd_west_africa` 钉住
  （`[PIN] t=17 self=card_event_unexpected_resistance#53@Discard`，
  由临时探针取证、改完已移除）⇒ 人类 `#92 t20 AC` 被内核误拒。
  装上装填门后该局**完全恢复**（见下表）。
- **自测**：`GotchaShouldTriggerJudgesSelf` 从 3 条断言扩到 4 条
  （新增「未装填 ⇒ 必须为假」那一条），四条一起把「判 self / 判 a[0] / 已销毁 / 未装填」钉死。

#### 自测与回放 A/B（命令随附）

```powershell
dotnet build src\KLink.Bot\KLink.Bot.csproj -c Release
dotnet build tools\BotSim -c Release ; dotnet build tools\ServerBridgeTest -c Release
dotnet run --project tools\BotSim -c Release --no-build -- selftest        # 150 → 151 项，全通过
dotnet run --project tools\BotSim -c Release --no-build -- dispatch-gap    # 逐位不变
& "out\audit\audit-4metrics.ps1"
```

**主对拍集 `out/_server-replays`（10 局，2026-10-04 实测）**：

| 回放 | 应用 前→后 | 人类失败 前→后 | ④HQ差(人) 前→后 | ⑤b 首个漂开 前→后 |
|---|---|---|---|---|
| 214436 | 59/61 → 59/61 | 0 → 0 | 3 → 3 | 无 → 无 |
| 389594 | 95/97 → 95/97 | 0 → 0 | 0 → 0 | 无 → 无 |
| 508065 | 123/141 → 123/141 | 17 → 17 | 43 → 43 | `#54 t13 PC` → 不变 |
| 542091 | 77/78 → 77/78 | 0 → 0 | 0 → 0 | 无 → 无 |
| 653657 | 41/42 → 41/42 | 0 → 0 | 0 → 0 | 无 → 无 |
| **705344** | 46/50 → **49/50** | 3 → **0** | 3 → **0** | `#21 t5 PC` → **无（完全对齐）** |
| **711061** | 53/57 → **56/57** | 3 → **0** | 10 → **0** | `#23 t5 PC` → **无（完全对齐）** |
| 770857 | 53/54 → 53/54 | 0 → 0 | 13 → 13 | 无 → 无 |
| 773639 | 134/137 → 134/137 | 0 → 0 | 0 → 0 | 无 → 无 |
| 854099 | 106/118 → 106/118 | 9 → 9 | 36 → 36 | `#78 t17 ML` → 不变 |
| **合计** | **787/835 → 793/835（94.3% → 95.0%）** | **32 → 26** | **108 → 95** | 完全对齐 **6/10 → 8/10** |

**另外 12 局（`docs/fresh-replays` 7 局 + `docs/live-replays` 5 局）**：

| 语料 | 应用 | 人类失败 | ④人类HQ差 |
|---|---|---|---|
| `fresh-replays` | 628/710 → **628/710（不变）** | 24 → **24** | 217 → **217** |
| `live-replays` | 132/140 → **132/140（不变）** | 0 → **0** | 18 → **18** |

⇒ **修复的效果精确落在它该落的 2 局上，另外 12 局逐位不变、无任何回归。**
这是本轮最值得记的一条：**一个根因级修复可以只影响 2/22 局**，
所以「改了没动」不等于「没生效」（README §7.4 的同一件事的另一面）。

**两条如实标注**：

1. `711061` 的对齐**依赖一处已知偏差**：`ChangeKreditCost` 的 `changeType=1`
   在本内核里被当成"设成绝对值"，而 `EChangeType.h:6-17` 说 **1 = `permBuff`（相对永久）**
   （`CardApiDispatch.cs` 的 `ChangeTypeSetValue` 注释自己记了这件事，涉及 **41 个调用点**）。
   于是 5th_regiment 的 `-2` 被算成 `-2 - 卡面费`、4 费**一步到 0**；
   正确口径应是 4 → 2 → 0。**两种口径在 `#23 t5` 那一刻都是 0 费**，
   所以这一局的对齐结论成立，但**中间值不是逐位一致的**。
   自测因此**只断言"费确实下降"**、不断言"恰好 −2"（见
   `SelfTest.TriggerSnapshotIncludesHand` 的注释）；修那个偏差要一次动 41 个调用点，须单独立一支。
2. `card_unit_5th_regiment` 在**修复前后**的「棋盘那一支」都是 +2+1（`changeType=1`
   对攻/防走的是正确的相对语义）—— 所以本轮的净效果**只在手牌那一支**上。

### 8.11 ★ 2026-10-04 本轮第二轮：压制门的**形状**改正（三处）

> **结论：三条判据在全部 22 局上逐位不变**（`793/835, 26, 95` / `628/710, 24, 217` /
> `132/140, 0, 18`），自测 151/151（其中一条**被改写的旧用例**做了判死验证）。
> ⇒ 这是一个**证据链只有「蓝图原文 + 自测」、回放侧无信号**的修复（README §7.4 那类），
> **如实标注**。

**根因**：内核把 `JumpIfNot(cond) -> target` **读反了**。它的语义是「**cond 为假才跳**」，
而三处注释都把它当成「cond 为真 ⇒ 整段跳过」。三处蓝图原文（本次独立复核）：

| 触发点 | 蓝图原文（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`） |
|---|---|
| T32 `MakeVeteran` | `:26303 if (!card.isSuppressed) goto L_0AF6;` → `:26305 L_099F: Fetch(32)` → `:26347 L_0AF6: OnBecomingVeteran` → `:26349 L_0B1A: goto L_099F` |
| T15 `ExecuteOnBeforeOtherCardDestroyed` | `:14833 if (!_cardDestroyed.isSuppressed) goto L_0208;` → `:14835 L_00B0: Fetch(15)` → `:14874 L_0208: OnBeforeDestroyed` → `:14876 goto L_00B0` |
| T7 `ChangeDefense`（增量分支） | `:7954 if (!cardToChangeRef.isSuppressed) goto L_130F;` → `:7956 L_10FD: Fetch(7)` → `:8017 L_130F: OnAfterGainDefense` → `:8020 goto L_10FD` |

三处**同一个惯用法**：门跳过的是**主体自己那个程序**，而紧跟在自程序后面的
`goto <Fetch 标签>` **又跳回广播段** ⇒ **广播是两条路都会到的（无条件发）**。

**改了三处（症状各不相同）**：

| 处 | 旧行为 | 新行为 | 方向 |
|---|---|---|---|
| `MatchEngine.Destroy`（T15） | 被压制 ⇒ **自程序 + 广播都不发** | 自程序不发、**广播照发** | 少发 → 补上 |
| `CardApi.MakeVeteran`（T32） | 门装在**广播**上、自程序无条件发 | 广播无条件、**自程序带门** | **完全颠倒 → 掰正** |
| `CardApi.ApplyDefenseDelta`（T7） | self 与 T7 混在一次 `FireTrigger` 里 ⇒ **无法分别设门**，被压制时多发自程序 | self 带门、T7 广播无条件 | 多发 → 收敛 |

⚠️ 两处广播名（`OnBeforeOtherCardDestroyed` / `OnAfterOtherCardGainDefense`）
**不以 `OnOther` 开头**，必须显式传 `broadcastName: true`
（否则命名判据会把它当成"只发主体"——同一个坑 `CardApi.FireTrigger` 的参数注释里写过）。

⚠️ **README §9.5 P3 那条"别把 `ChangeDefense` 的 T7 当成本族"的警告仍然成立**：
T7 **广播**确实无条件发 —— 旧实现无条件发广播**是对的**；错的是它**同时**无条件发了自程序。

**自测改动（重要）**：已有的 `EventLayerSuppressionGate` 断言的是**旧的（错的）**语义
（"被压制 ⇒ 不广播"），本轮**按蓝图原文改写**成：
① T32 被压制 ⇒ **广播照发**且**自程序不发**；② 对照：未压制 ⇒ 自程序发；
③ T15 被压制 ⇒ 自程序不发；④ T15 被压制者被摧毁 ⇒ **旁观者仍收到广播**；⑤ 对照。
⇒ 该用例**做过判死验证**：把 T32 的广播门装回去 ⇒ 用例立刻失败。

### 8.12 ⛔ 2026-10-04 第三轮：**试过但回退** —— 「打出的反制卡留在手牌」

> 记在这里是因为它**蓝图依据很硬、但实测是负收益**，而且**回退的理由与项目已有政策一致**。
> 谁要再捡起来，从这里开始。

**蓝图依据（本次独立复核，逐行）**：`BP_CardFunctions.g.cs` 的 `PlayCardFromHand` 里，
反制卡（Gotcha）打出时**两条路都不换位置**：

```
:28080/:28082  location == 4 || location == 3          ; 在手牌里
:28088         IsGotcha(card)
:28092         gotchaActivated > 0                     ; 已经装填过 ⇒ 这次是"正常打出"
:28096             card.gotchaActivated = 0            ;   ⇒ 取消激活
:28316         否则 card.gotchaActivated = next + 1    ;   ⇒ 装填成陷阱（从 1 起）
:28098 / :28318 两条路都 → L_01B9: `_newLocation = _oldLocation`   ; ★★ 都不换位置
:28114         NotifyPlayFromHand(…, _newLocation, …)
```

⇒ **打出的反制卡应当留在手牌（盖着）**，`gotchaActivated > 0` 期间它才响应事件。

**内核现状（错的）**：`MatchEngine.PlayCard` 只判 `IsUnit || IsLocationCard` 才留场，
反制卡落到 `else` 分支 **进弃牌堆** ⇒ `IsAlive` 在 Discard 为假 ⇒
`ShouldGotchaTrigger` **恒假** ⇒ **52 张反制卡的整条效果全是死的**
（14 张订阅 `OnOtherCardAttacks`、13 张订阅 `OnCounterMeasureTriggered`）。

**实测 A/B（把它改成"留在手牌"）**：

| 语料 | 应用 | 人类失败 | ④人类HQ差 | ⑥未实现种 |
|---|---|---|---|---|
| `out/_server-replays` | **793/835 → 789/835（−4）** | 26 → 26 | 95 → 95 | **变差**（多局 +1，如 542091 `2 → 4`、389594 `1 → 2`） |
| `fresh-replays` | 628/710 → 628/710 | 24 → 24 | **217 → 208（变好）** | — |
| `live-replays` | 132/140 → 132/140 | 0 → 0 | 18 → 18 | — |

⑤b 与人类失败**都没动**（没有哪一局的"首个人类动作失败点"变差），
但**判据 ② 未实现原语种类变差**、应用率下降 ⇒ 按 §7.1 的可靠性排序**净负**，
⇒ **回退**（与项目已有政策一致：`CardApiDispatch.cs:1364-1368` 记着同一条规矩
——「任何一局应用率下降都算失败 ⇒ 回退」）。

**为什么它会是负收益（已定位到具体卡与具体行）**：新跑起来的反制卡程序立刻撞上
`GetHandLocationBySide`（回放 `542091` 一次审计里 `×82`），
而那个原语**是内核故意不注册的** —— `CardApiDispatch.cs` 记着它自己的 A/B：
注册它会让 `854099` 从 **106/118 掉到 100/118**（其余 8 局逐位不变），
属于典型的「**两个错抵消**」（README §7.3）⇒ 当时回退、只留缺口计数。

### 8.12.1 ★ 2026-10-04：把那个「两个错抵消」**定位到了具体一行**（然后仍然回退）

本轮实测复现了那次 A/B（**854099：应用 106→100、人类失败 9→16、⑤b `#78`→`#77`**），
并加了一条**临时探针**（env 门控，取证后已移除）把调用点钉死 ——
结论**推翻了原来的猜测**：

```
[HANDLOC] t=11 self=card_event_night_raid#5001@Discard side=Left -> 3
          | handL=9/9 handR=4/9 isFullL=True
```

- 全 854099 里 `GetHandLocationBySide` **只被调用一次**，来自 **`card_event_night_raid`**
  （不是 `aerial_reconaissance`，也不是反制卡路径 —— 原来的 5 张卡清单**不完整**）。
- 那一行是 `night_raid` 的 `IsLocationFull(handLocation)` 门
  （卡面「Copy random order from enemy deck. Add a No. 10 COMMANDO to support line.」）：
  **手牌满 ⇒ 跳过"复制一张敌方指令"那一段**（`BP_CardFunctions.g.cs` 的 `:904 GetHandLocationBySide`
  → `:1012 jumpIfNot(isFull) → :1295`）。
- 那一刻内核的**左方手牌是 9/9 = 满** ⇒ 装上真判据后内核**跳过了复制**；
  而不装时出参读成 `null(0)` ⇒ `IsLocationFull(NotAvailable)` 为假 ⇒ 内核**总是复制**
  （于是手牌涨到 10/9，正是别处日志里那条「手牌已满（10/9）」的来源）。

⇒ **被暴露的下游偏差是「内核手牌比客户端大」**（README §9.5 **P6** 那一族：
`ReplayRunner` 对「PC 引用但不在手牌」的卡会硬塞进手牌）。
也就是说：**客户端的 `night_raid` 当时并没有满手、确实复制了**，而内核的手牌已经被撑满，
于是一个**正确的门**反而让内核少拿一张牌 ⇒ 后面 6 条人类动作连带失败。

**仍然回退**，理由与验收口径一致：⑤b（§7.1 排第一的判据）后退了，
而暴露出来的根因是**另一个子系统**（手牌容量/手牌虚增），不是这一行本身。
⇒ **下一步顺序应当是：先修 P6 的手牌虚增，再注册 `GetHandLocationBySide`**，
那时这个门才不会与客户端相反。**这条诊断是本轮真正的产出**（把"未知的下游偏差"
变成了"一行 + 一个可复现探针 + 一个明确的先行修复项"）。

⇒ **结论：这是一簇"多个错互相抵消"的改动，必须整批做**：
① 注册 `GetHandLocationBySide`（先解决它自己那 5 张卡的抵消）；
② 再把「打出的反制卡留在手牌」改对；
③ 然后把 **T31 `OnOtherCardAttacks`**（20 张订阅，唯一有回放观测量的 `c` 类触发点，
见 §9.5 P1）接上 —— 反制卡落点修好之后，那 14 张 gotcha 订阅者才可能真生效。
**逐条验收口径**：⑤b 不得后退、⑥ 未实现种不得增加、应用率不得下降。

### 8.13 ★ 2026-10-04 第四轮：三个**从未派发**的触发点接线（T35 / T61 / T48）

> **22 局逐位不变**（`793/835, 26, 95` / `628/710, 24, 217` / `132/140, 0, 18`），
> `dispatch-gap` 逐位不变，自测 **151 → 152 项全通过**（新用例做过判死验证）。
> **纯新增（+184 行 / −0 行）**。

| 触发点 | 订阅卡 | 落点 | 蓝图原文（本次逐行复核） |
|---|---|---|---|
| **T35** `OnOtherCardCreatedAlterCard` | **11** | `CardApi.FireCardCreatedAlterCard`，在 `SpawnOnBattlefield` / `SpawnCardInHand` / `DoSpawnInDeck` 三处显式发 | `:10584 NotifyCreateNonVisualCard` → `:10586-10588 MakeVeteran` → `:10590 Fetch(35)` → `:10608 item.OnOtherCardCreatedAlterCard(createdCard, 0)` |
| **T61** `OnOtherUnitPinned` | 2 | `CardApi.PinUnit` 末尾 | `:27980 NotifyPinUnit` → `:27982 Fetch(61)` → `:28010 item.OnOtherUnitPinned(_card)` |
| **T48** `OnOtherCardLoseSmokescreen` | 3 | `CardApi.RemoveKeyword`（`keyword == Smokescreen`）末尾 | `:32691 Fetch(48)` → `:32709 item.OnOtherCardLoseSmokescreen(_cardFromID)` |

三条的实参名逐字取 `Generated/_index.g.cs`：T35 `{cardPlayed, method}`（`method`：`CreateCard` 路径 = **0**）、
T61 `{cardBeingPinned}`、T48 `{card}`。

**三个实现细节（都是"照蓝图顺序"而不是"随便塞"）**：

1. **T35 不能挂在 `GameState.CardCreated` 上**。那条钩子是**所有**建卡的漏斗，
   而 `GameState.CreateWithId`（回放装载初始牌库 40+40 张）也走它
   ⇒ 挂上去会让**开局**给 11 张订阅卡各广播一次"有新卡被生成"。
   ⇒ 只在**生成类原语**里显式发（`DoSpawnInFrontline` 复用 `SpawnOnBattlefield`，不必重复）。
2. **T35 在 `DoSpawnInDeck` 里必须排在那次随机数消耗之前**：
   蓝图 `SpawnCardInDeckBySide` 的顺序是 `CreateCard`（含 T35 广播）→ `GetDeckByside` → `RandomIntegerInRangeFromStream`
   （`:34856-34867`）⇒ 若订阅卡自己也消耗随机数，顺序错了游标就漂（§9.1 那一族）。
3. **T48 排在 `ZActionRemove{keyword}` 与 `FireAbilitiesChanged` 之后**：
   蓝图里它是 `RemoveSmokescreen` 的**最后一段**。而"烟幕真变了才发"由
   `RemoveKeyword` 开头那道 `if (!target.Keywords.Remove(keyword)) return;` 天然满足
   —— 自测里配了**反向断言**（第二次摘同一个不存在的烟幕时不该再广播）。

**如实标注（这三条都无法用回放验证）**：三族订阅卡在现有 22 局语料里
**一张都没出现过**（`docs/card-ir.json` 实测：T35 = `card_unit_144th_infantry_regiment` 等 11 张、
T61 = `card_unit_cromwell_mk_iv` / `card_unit_14_panzergrenadier`、
T48 = `card_unit_hirosaki_regiment` 等 3 张）
⇒ **判据只有「蓝图原文 + 自测」**，不是回放对拍。A/B 全 22 局逐位不变，**既无回归也无改善**。

⚠️ 一条族级偏差（未单独改，如实记）：内核 `FireTrigger` 的广播分支**排除主体**，
而蓝图 T35 `:10590-10608` 的循环里**没有**排除 `createdCard` 自己
⇒ 若某张卡自己订阅了 T35，内核会比客户端少发一次。与其它触发点同源。

### 8.14 ★ 2026-10-04 第五轮：手牌容量 —— 把「虚增」变成**可证伪的探针 + 两次证伪的修复**

> **本轮没有改行为**（22 局逐位不变），产出是**一个探针 + 一条被两次实验钉死的结论**。

#### ① 蓝图的两道手牌容量门（本次逐行复核，`BP_CardFunctions.g.cs`）

两条"往手牌里加牌"的漏斗**都**有这道门，且**都**改成 `Discard(8)`：

```
CreateCard（建卡到某位置）：
  :10510  IsLocationFull(_location) → :10512 wasFullBeforeCreating
  :10702  BooleanAND(Not(autoplay && spawnCardInHand), wasFullBeforeCreating)
  :10706      createdCard.location = 8          ; ★ Discard

MoveCardFromBoardToOwnersHand（场上 → 手牌）：
  :26399  FetchCardsByLocation(NewLocation) → isLocationFull
  :26405  if (!tmpNewLcationFull) → 正常路径
  :26407      tmpNewLocation = 8                ; ★ Discard
```

容量本身也是权威的：`FetchCardsByLocation` 的 **case 3,4 → `MaxQty = IntConst(9)`**
（= 内核 `GameState.HandCapacity = 9`）。内核的抽牌路径早就实现了同一语义
（`MatchEngine.DrawCard`：「手牌已满 ⇒ 烧牌，不进手牌」）。

#### ② 内核的越界**本身不是 bug** —— 这条我一开始判错了，如实更正

新增一个 env 门控探针（`GameState.TraceHandOverflow`，`$env:KLINK_TRACE_HANDOVER='1'`）：
手牌一超过 9 就报一行（并打印调用栈，用来点名路径）。22 局实测越界很多：
**10/9、11/9、12/9，最高 13/9**（`773639 t9`）。

⚠️ **但"超过 9 就一定是 bug"这个前提是错的**。蓝图只在**特定的几道门**上查容量
（见 ①），而下面这条路径**根本没有容量门**：

```
DrawSpecificCardFromDeckBySide   BP_CardFunctions.g.cs:12406（全函数仅 33 行）
  —— 体内**没有任何** IsLocationFull / HandLocation / MaxQty 提及
  ⇒ 「从牌库抽一张指定的牌进手」**允许**手牌超过 9
```

（另外 `CreateCard` 那条门自己还带一个例外：`autoplay && spawnCardInHand` 时不改送弃牌堆，
`:10702`。蓝图里还有一个专门的助手 `DiscardOnDrawingWithFullHands`（`:12126`），
只被**有门的**抽牌路径调用。）

探针点名的两条越界路径正是"效果驱动"的那两条，且**都属于不受门约束/带例外的**：

```
at CardApi.DoDrawSpecific            (CardApiDispatch.cs:1779)   ← DrawSpecificCardFromDeckBySide
at CardApi.SpawnCardInHand           (CardApi.cs:1873)          ← CreateCard（带 autoplay 例外）
```

⇒ **`手牌 > 9` 不能当作"内核多进了一张"的判据**。探针的价值只剩"**指出可疑路径**"，
要判它是不是 bug，必须**逐条对照该路径在蓝图里有没有门**。

#### ③ 把两道门都装上 ⇒ **大幅变差**，所以回退

按 ① 的原文在**唯一换区漏斗**（`GameState.Move` + `Create`）上加容量门
（"进入已满手牌 ⇒ 改送弃牌堆"）：

| 语料 | 应用 | 人类失败 | ⑤b |
|---|---|---|---|
| `out/_server-replays` | **793/835 → 735/835（−58）** | 26 → **64** | **4 局新增漂开**（389594 `#71`、773639 `#46`、854099 `#58`） |

⇒ **回退**。现在原因清楚了（结合 ②）：**一部分越界是合法的**
（`DrawSpecificCardFromDeckBySide` 那类路径客户端也会超），
所以"一刀切在换区漏斗上按 9 封顶"会把客户端**留着**的牌丢掉。

#### ④ 更正后的结论（**不要**沿用 ⑤ 的旧说法）

- ❌ **不能说**"三次实验共同证明内核手牌比客户端大"。**这个推断已被 ② 推翻**：
  越界有合法来源，所以"内核手牌更大"缺少独立证据。
- ✅ 能确定的：**内核缺的两道容量门是蓝图真有的**（① 的原文），
  但**不能一刀切**——必须逐条路径对照蓝图是否设门
  （有门：`CreateCard` / `MoveCardFromBoardToOwnersHand` / `DrawTopCardFromDeck`；
  无门：`DrawSpecificCardFromDeckBySide`）。
- ✅ 能确定的：`854099` 在注册 `GetHandLocationBySide` 之后掉 6 条动作，
  触发点是 `night_raid` 的 `IsLocationFull(手牌)` 门（§8.12.1）——
  这一条**仍然成立**，只是它的**根因还没定位**（不能再用"手牌虚增"当解释）。

**下一步（收窄后）**：
1. ✅ **已做**：逐条给"往手牌加牌"的路径标注蓝图有没有门 —— 结论是**唯一缺的只有 `CreateCard` 那条**
   （见 §8.15）。
2. ⏳ **已试过「从动作流反推手牌数」——不可行，如实记录**：
   854099 到 t11 为止，左方**自己的回合开始 5 次**（开局 4 张 + 4 次摸牌 = 8 张进手），
   而它**打出过 8 张 `PC`**，t11 时内核手里还有 **9** 张
   ⇒ **到 t11 为止至少 9 张是"效果驱动"进手的**（PAMS 开发 / `colossus` 的「复制三张指令」/
   `atlantic_convoy` / `iron_from_the_north` 那一族都在场）。
   也就是说**光靠动作流数不出客户端的真实手牌**（效果进手占了大头，且没有逐条的客户端计数）。
   ⇒ 下一步只能**逐效果审计**：用 `KLINK_TRACE_HANDOVER=1` 的调用栈把该局 t1..t11
   每一次进手逐条列出来，再对每一种效果问"客户端这一步会不会也进一张"。

### 8.15 ★ 2026-10-04 第六轮：按路径逐条对账后，补上**唯一**缺失的手牌容量门

**逐条对账结果**（"往手牌加牌"的每一条路径 × 蓝图有没有门 × 内核有没有实现）：

| 路径 | 蓝图 | 内核 | 结论 |
|---|---|---|---|
| 抽顶牌 `DrawCard` | **有**：`DrawTopCardFromDeck` `:12496-12500 isHandFull` | ✅ 已实现（`MatchEngine.cs:718-731`） | 一致 |
| `DrawCardsFromDeckBySide` | 委托给 `DrawTopCardFromDeck`（`:12298-12405` 体内调它） | ✅ 走同一个 `DrawCard` | 一致 |
| 抽指定牌 `DoDrawSpecific` | **没有门**：`DrawSpecificCardFromDeckBySide` `:12406`（全函数 33 行，无任何手牌提及） | 无门 | 一致（**允许**越界） |
| 回手 `DoMoveUnitFromBoardToOwnersHand` | **有**：`MoveCardFromBoardToOwnersHand` `:26399-26407`（满 ⇒ `Discard(8)`） | ✅ 已实现（`CardApiDispatch.cs:2572-2574`） | 一致 |
| 开发选牌 `selectCardToDraw` | **有**：`:33725-33737`（`isFull && !isEffect` ⇒ 不抽） | ✅ 已实现（`CardApiDispatch.cs:1469`） | 一致 |
| **建卡到手 `SpawnCardInHand`** | **有**：`CreateCard` `:10510/:10702-10706`（满 ⇒ `Discard(8)`） | ❌ **缺** | **本轮补上** |
| `SalvageMultipleUnits` | **有**：`:33276-33278` | 未实现（原语不在派发表） | 不适用 |

⇒ **唯一缺的就是 `SpawnCardInHand` 这一条**（实测越界调用栈也正好指向它：
`CardApi.SpawnCardInHand ← DoSpawnInHand`）。按蓝图原文补上（满手 ⇒ 新卡进**弃牌堆**）。

**结果**：22 局**逐位不变**（`793/835, 26, 95` / `628/710, 24, 217` / `132/140, 0, 18`），
自测 **152 → 153 项全通过**（新用例做过判死验证：把 `where` 改回 `side.HandOf()` 即失败），
`dispatch-gap` 逐位不变。探针确认 `SpawnCardInHand` 那条路径的越界不再叠加
（该局手牌峰值从 13/9 降到 11/9；**其余越界仍来自蓝图允许的 `DoDrawSpecific` 那类路径**）。

**如实标注两条**：
1. 蓝图对这条门有一个例外（`autoplay && spawnCardInHand` 时不改送弃牌堆，`:10702`），
   而内核**没有建模 `autoplay` 这个 gameplay tag** ⇒ 本实现是"无条件应用"，**是近似**。
2. 本轮**没有**、也不该去动 `DoDrawSpecific` —— 它在蓝图里**本来就没有门**，
   §8.14 那次"一刀切"的失败正是把这一类合法越界也封掉了。

### 8.16 ★ 2026-10-04 第七轮：`854099 t11` 的手牌差 —— 定位到**牌库内容**，不是容量门也不是循环

按 §8.15 的下一步，给手牌加了一条**进出流水账探针**
（`KLINK_TRACE_HAND=1`：每一次进/出手牌都打一行 + 调用栈；`KLINK_TRACE_HANDOVER=1` 只看越界）。
854099 左方 t≤11 的账（摘）：

```
t=5  NotAvailable->HandLeft 7/9  card_event_night_raid#5001 via Create   ← develop 先建到手
t=5  HandLeft->DeckLeft     6/9  card_event_night_raid#5001 via Move     ← 再塞进牌库
t=11 DeckLeft->HandLeft     8/9  card_event_colossus#24 via Move          ← 回合开始抽
t=11 DeckLeft->HandLeft     8/9  card_event_night_raid#5001 via DoDrawSpecific
t=11 DeckLeft->HandLeft     9/9  card_event_baker_street_irregulars#9003 via DoDrawSpecific
t=11 DeckLeft->HandLeft    10/9  card_event_pams#33 via DoDrawSpecific    ← ★ 越界在这里
t=11 HandLeft->Discard      9/9  card_event_night_raid#5001 via PlayCard   ← 打出（→ 手牌 9）
```

**三条 `DoDrawSpecific` 的发起者已被点名**（新增探针 `[DRAWSPEC]`）：
`self=card_unit_2nd_west_africa#39` —— 就是它自己的卡面效果：

> **2nd WEST AFRICA**（英，1 费 1/2）：**Deployment: Draw the cheapest order from your deck.
> Repeat if it did not start there.**

它的 IR 循环（`docs/card-ir.json`）读出来是：

```
i=1349  flag = false ; counter = 1        ; flag = "didStartThere"
i=990   NOT(flag) AND (counter <= 9)      ; ★ 重复条件
i=1101      → 再找一次"牌库最便宜的指令"
i=1106  DrawSpecificCardFromDeckBySide(…)
i=1192  Greater(抽到那张的 cardID, 99)     ; ★ "did not start there" = **卡 ID > 99**
i=1248      否 ⇒ i=1263: flag = true ⇒ 跳出
            是 ⇒ counter++ 回到 i=990（重复，最多 9 次）
```

⇒ **"did not start there" 就是「这张牌不是开局就在牌库里的」**（开局牌 ID 1..81，
对局中生成的卡 ID ≥ 1001）—— 即**抽到一张"生成出来的"指令就再来一次**，
直到抽到一张开局就在牌库里的指令（或满 9 次）。

**结论（如实）**：
1. 内核的循环**忠实于蓝图**（它就是这个 IR 程序被解释执行），
   3 次抽牌（`night_raid#5001` → `baker_street#9003` → `pams#33`，前两张 ID>99、第三张 ≤99 停）
   **符合上面那条规则** ⇒ **循环本身不是 bug**。
2. `DoDrawSpecific`（= `DrawSpecificCardFromDeckBySide`）在蓝图里**没有容量门** ⇒
   手牌走到 `10/9` 也**不是**容量门的 bug（§8.14 ②）。
3. ⇒ 真正的差异在**输入**：**内核牌库里"最便宜的指令"是哪些**。
   该循环会**反复抽走生成出来的便宜指令**（PAMS 开发出来的 0 费卡正是"最便宜"且 ID>99），
   所以**牌库里多一张生成出来的 0 费指令 ⇒ 这里就多抽一张 ⇒ 手牌多一张**。
   ⇒ **手牌差 1 是"牌库内容差"的症状**，根因要往 `PAMS` / develop 那条链
   （`DevelopChosenCard` → `SpawnCardInDeckBySide` → 费用设 0）去找。

**下一步（已收窄到一条链）**：把 854099 t1..t11 里**每次进牌库**的卡逐条列出
（`[HAND]` 探针已有 `DeckLeft` 侧的进出，再加一条牌库侧的就够），
与客户端动作流能推出的牌库变化对账，找出**多进牌库的那一张**。

### 8.17 ★★ 2026-10-04 第八轮：`854099 t11` 的**最终归因** —— **牌库顺序**，不是代码 bug（这条别再追）

加了牌库侧流水账（`KLINK_TRACE_DECK=1`）后，854099 左方 t≤11 的**进牌库/出牌库**全部列出。
两条**决定性**记录：

```
t=7  DeckLeft->HandLeft  card_unit_2nd_west_africa#40 via ReplayRunner.Run
t=11 DeckLeft->HandLeft  card_unit_2nd_west_africa#39 via ReplayRunner.Run
```

`ReplayRunner.Run` 那一帧就是内核自认的**保真度缺口**（`ReplayRunner.cs:853-857`）：
人类动作引用了「**PC 但不在手牌**」的卡 ⇒ 内核从**牌库**把它**硬塞进手牌**（`injected++`）。

⇒ 也就是说：**客户端手里有 `2nd_west_africa`（人类从手牌打出它），而内核把它留在牌库里**
⇒ 内核**早先的抽牌抽到了别的卡** ⇒ **两边牌库顺序不同**。
对照：内核 t11 的回合开始抽到的是 `colossus#24`，而人类 t11 打出的却是
`2nd_west_africa#39`（客户端 t11 抽到的应该就是它）。

**这就是 README §9.1 的第 ② 类**，原文已经写明：

> **② 牌库派生池**：快照的牌库顺序是宿主用非确定性的 `Random.Shared` 假洗出来的 ⇒ **修不了**
> （需要能拿到真实初始牌序的快照）。

⇒ **结论（这条线索到此为止）**：
1. §8.12.1 那条「注册 `GetHandLocationBySide` 让 854099 掉 6 条动作」的**真正阻塞是数据**：
   初始牌序拿不到 ⇒ 内核的牌库顺序与客户端不同 ⇒ 依赖牌库顺序的效果
   （`2nd_west_africa` 的「抽最便宜的指令」、`night_raid` 的「满手就不复制」）
   会在**内核自己的牌库状态**上做出与客户端不同的判断。
   **不是**容量门写错、**不是**循环写错、**不是** `night_raid` 写错。
2. 因此 §8.12.1 的实验**继续维持回退**，并且**不应该**再花时间追这条 ——
   除非能拿到**带真实初始牌序**的快照（那才是解锁点）。
3. 反过来，§8.14/§8.15 补的**容量门**是独立的正确性修复（蓝图逐行有据），该留；
   §8.13 的三个触发点接线同理。
4. ⚠️ **一个方法论要点**：这条链上我**三次**以为找到了内核 bug
   （手牌虚增 → 容量门缺失 → 循环条件错），**三次都被自己的探针推翻**。
   ⇒ 在「效果驱动的状态差」上，**"指标变差"必须配合"点名到具体原语 + 蓝图逐行对照"
   才能归因**；只凭指标方向会连续误判。

### 8.18 ★★ 2026-10-04 第九轮：**T31 `OnOtherCardAttacks` 接线完成**（README §9.5 **P1** 的那条）

20 张订阅卡（`docs/card-ir.json` 的 **`locals`**、`entrypoints` **0 张**）此前整条行为是死的。

**蓝图原文（本次逐行复核，`BP_CardFunctions.g.cs` 的 `AttackCard`）**：

```
:4334  SetStopAttack(GameStateRef, False)                 ; 每轮先复位
:4336  tmpAttackedAndStopped = False
:4338  FetchAllCardsWithEventTrigger(31)                  ; ★ 唯一 Fetch 点
:4356      NotEqual_IntInt(attackerCardID, item.cardID)   ; ★ 只排除**攻击者本人**（防御方照收）
:4385/:4434  item.OnOtherCardAttacks(_attackerCard, _defenderCard, out stopAttack, out AttackedAndStopped)
:4389      if (stopAttack) SetStopAttack(True)            ; 不跳出轮
:4412      if (AttackedAndStopped) tmpAttackedAndStopped = True
:4487  GetStopAttack() 真 ⇒ :4510 success = True → :4512 **直接返回**（油费**不扣**）
:4513  ChangeKreditsBySide(-costToPay)                    ; ★ 扣油费在窗口**之后**
:4643  if (tmpAttackedAndStopped) ⇒ ExecuteStoppedAttack(…)（**整段伤害跳过**）
```

**改动面（三处，全部照蓝图）**：

| # | 落点 | 内容 |
|---|---|---|
| 1 | `CardApi.BroadcastWithOutParams` | 新增 `exclude` 参数（+ 循环里一行 skip）—— 表达 `:4356` 的"只排除攻击者"。⚠️ 这与 `FireTrigger` 广播分支的"排除**主体**"**不是**一回事：这里**防御方必须照收**（`beaufighter` / `33rd_livorno` / `bm_13n_us6` 就是靠"自己是被打的那个"触发）。 |
| 2 | `CardApi.FireOtherCardAttacks` + `AttackIntercept` | 走完整轮再判定；两个出参**独立累加**、`stopAttack` **优先**（`:4487` 早于 `:4643`）。 |
| 3 | `MatchEngine.Attack` 两处 | ① **在 `State.AddKredits` 之前**（`:4513` 在窗口之后；`stopAttack` 真时油费不扣、不记"已攻击"）；② T13 广播之后、伤害之前（`AttackedAndStopped` 早退，`ZActionStoppedAttack` + `OnAttackStopped`，**不设** `defender.HasBeenAttackedThisTurn` —— 它在蓝图的 `ExecuteAttackCard :12782` 里）。 |

**`stopAttack` 是死代码（本次自己统计，`card-ir.json`）**：20 张订阅卡的 `stopAttack` 写入
**33 处、全是字面量 `false`**；而 `AttackedAndStopped` 是 **5 处 `true` + 26 处 `false` + 2 处计算式**
⇒ **活的杠杆是 `AttackedAndStopped`**。机制仍然留着（它是唯一的"整条攻击作废"通道，
且两条路的**代价不同**：一个不扣油费、一个扣）。

**A/B 结果**：
- **22 局逐位不变**（`793/835, 26, 95` / `628/710, 24, 217` / `132/140, 0, 18`），
  `dispatch-gap` 逐位不变，自测 **153 → 154 全通过**（新用例做过判死验证）。
- ⚠️ **回放侧验证不了**：新探针 `KLINK_TRACE_T31=1` 实测，10 局主对拍集里
  T31 **触发 58 次、订阅者出现 0 次**（那 20 张卡一张都没进过局内）
  ⇒ 判据只有「蓝图原文 + 自测」。自测用 `card_unit_beaufighter_tf_mk_x`
  （「Any unit that attacks this unit takes 3 damage first.」，**非 gotcha** ⇒ 不受
  `ShouldGotchaTrigger` 影响）：攻击者 1 防吃 3 点必死 ⇒ `AttackedAndStopped = true`
  ⇒ **防御方零伤害、但油费照扣、照记"已攻击"**。

**未做（如实标注）**：**T30 `OnOtherCardAttackSwitchTarget`**（2 张，出参 `newDefender`
**真的改攻击目标**）**没有接** —— 它要求把 `Attack` 里的 `defender` 局部化并让下游所有门与结算
都改用新目标（范围不小），按 §9.5 P1 的建议**单独一支**做。
另：T31 的收件人快照**不含牌库**（`FillTriggerSnapshot` 有意排除，理由见该方法的注释）
—— 蓝图 `AllCardsInBattle` 是否含牌库**未核实**，如实标注。

---


## 9. 路线图 / 已知缺口

> 这一节**只列客观事实与规模**，按「已确认存在」→「未定位」→「未做」排列。

### 9.1 最大的一块：随机效果的「选卡」不一致

**状态：根因已定位（未修）。**

#### 9.1.1 现象（历史记录，仍然成立）

- 已经确认**不是发号问题**：回放 `773639` 里内核台账的卡号（`3001/3002/3004`）与客户端引用的一致，
  **槽位一致、只有槽里的卡不同**。
- 分两类：

| 类 | 情况 | 可否修 |
|---|---|---|
| **① 静态卡池** | 池按名字排序 ⇒ 只可能是**下标不同** ⇒ 要么「池子大小 / 过滤器」不同，要么「随机值 / 游标」不同 | 可修（把每次消费的原始值记进流水账，拿客户端的卡反推下标） |
| **② 牌库派生池** | 快照的牌库顺序是宿主用非确定性的 `Random.Shared` 假洗出来的 | **修不了**（需要能拿到真实初始牌序的快照） |

- **影响面**：回放 `508065` 的 16 条人类失败（首个漂开 `#54 t13`）、
  回放 `854099` 的 14 条人类失败（首个漂开 `#70 t15`）—— 即 §8.3 里全部 30 条失败。

出处：`out/audit/idfix/README.md:42-47`。

#### 9.1.2 ⚠️ 下面这段旧的「未定位」描述**已过期**，保留作历史

> **未定位的具体差额**：回放 `508065` 的 `atlantic_convoy`（`#36 t9`）两次抽签，
> 内核落在随机流位置 `#42/#43`，客户端落在 **`#88`** ⇒ **内核落后 46 次消费**。
> 候选池本身已验证正确（102 张美国费 ≤ 3 的单位、字典序；客户端选中的卡在流位置 `#88` 上正好是 55 号，
> 与客户端一致）⇒ **差异只在流位置，不在候选集**。46 次的来源**未定位**。

**为什么过期**：那个「102 张」是**候选池修复之前**的旧口径数字，而「客户端游标 = 88」
正是从它反推出来的。

#### 9.1.3 曾怀疑的两个「缺失的随机消费点」——**现已双双排除**（2026-10-02 凌晨六 更正）

| # | 缺失点 | 蓝图出处 | 内核现状 |
|---|---|---|---|
| ① | `selectCardToDraw` 的**候选表洗牌** | `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:33750` 判 `keepOrder`，假时 `:33783` 执行 `Array_ShuffleFromStream(possibleChooseCards, cardsRandomStream)` ⇒ **消费 = 候选表长度**；`card_event_pams` **硬编码 `keepOrder = false`**（`ref/kards-sim/.../card_event_pams.g.cs:245`） | `src/KLink.Bot/Effects/CardApiDispatch.cs:1197-1209` **有意不执行**这次消费（当年实现过一次，人类失败 13 → 22 所以回退）。⚠️ **现已用穷举否证「该补这次洗牌」** —— 见 §9.1.7 |
| ② | `SetCardsSeenByCipher` | ⚠️ **本条已更正**：调用门是**三重** —— `:6050` `IsActionProcess` → `:6052/:6054` **`cipher > 0`** → `:6058` `IsGotcha` 为假，才在 `:6175` 调用。原文写「每张非 Gotcha 出牌都调」**漏了 `cipher > 0` 这道门**。消费公式 = 对手手牌里 `cardSeen == false` 的**张数**（`:34137` 过滤 → `:34166` 前向 Fisher-Yates 跑满 n 次），**与 cipher 无关**；cipher 只决定置位几张（`:34170/:34172/:34174`），数组为空则 0 消费（`:34150`） | **完全没有这个子系统**（`grep -rn "SetCardsSeenByCipher" src/` ⇒ **0 命中**）。**`508065` 上它消费 0**：该局 44 条 `PC` **无一 cipher>0**（全池 cipher>0 仅 25 张；唯一写方 `AddIntelToCard`（`:382-386`）的 3 个调用点**全在 `card_unit_lublin_r_xiii`**，该卡本局出现 0 次）⇒ 实现它对这一局的游标是 **no-op**。⚠️ **但其余局不是 0**：6 局全扫发现 **`389594`（`#44 PC`）与 `773639`（`#63 PC`）真的出了 `card_event_scouting_party`（cipher=1）** ⇒ 那两局此函数**会被调用**，消费 = 对手手牌里 `cardSeen == false` 的张数。⇒ 这是个**真实缺口**，只是**不在** `508065` 上 |

⇒ ~~两者叠加 ⇒ 内核在 `#36` 至少落后 41 + 13 = 54 次~~ —— **该推算已作废（2026-10-02 凌晨六）**：
② 在 `508065` 上消费 **0**（见上表），所以只剩 ①；而 ① 的「洗牌 + 取第 k 张」模型
已在**池口径 × 排序键 × 全局偏移 K × 洗牌消耗次数 × 不洗牌**五个维度上被**穷举否证**（见 §9.1.7）。

⇒ **没有任何可归因的缺失消费点。**「内核落后 46 次」这个结论本身应作废 ——
它的客户端游标 `88` 是用**修复前的 102 张池**反推出来的（见 §9.1.2 / §9.1.4）。

⇒ **给 508065 的 16 条人类失败另找原因**：四判据里 ⑤b 的首漂开是
`#54 t13 PC：打不出`（**内核拒绝打出一张客户端打出的牌**）—— 那是**合法性/候选**问题，
不是随机流问题。这条与 `CanSelectAsTarget` 接攻击路径（§9.3）是同一类。

**「46」这个数本身也不可靠**：同一个「游标 → 下标」模型对第二张牌失效
（游标 89 → idx 6，而客户端第二张是 `card_unit_p40_warhawk`，池内 idx 82）；
而且候选池口径已经变了（见下）。

#### 9.1.4 候选池口径：**已实现**，不需要改代码

`CardApiDispatch.cs:1697` 的 `StaticCardPool` 在 `:1727` 过 `CardPoolTable.IsSetInPool`（卡集层）、
`:1732` 过 `CardPoolTable.IsReserved`（预备卡层）；第三层（服务端 `cards_blacklist`）离线恒空。
离线复算：全卡库 **2021 → 过卡集 1542 → 排除 563 张预备卡 979**。
于是候选表长度：`atlantic_convoy`（USA ∧ 单位 ∧ 费 ≤ 3）= **53 张**、
`pams`（Britain ∧ order ∧ 费 < 5）= **41 张**。

**而「102 张」正是 `atlantic_convoy` 不过滤时的旧口径**：`card_unit_fifth_ohio` 在旧 102 张表里
正好是 **idx 55**，新表只有 53 张 ⇒ 「`#88` → 55 号」这个推导**失效**。
唯一离线不可得的是**第三层**（服务端 `cards_blacklist`），它的影响是
「每黑一张卡池少一张 ⇒ 之后下标整体偏移」，是个**有界**未知量。

#### 9.1.5 当年那条假设已被证伪：锅不在 `keepOrder`

`CardApiDispatch.cs:1205` 的注释写「我们的 `keepOrder` 判定与客户端不一致」。实测：
pams 蓝图里 `keepOrder = false` 是**硬编码字面量**；内核 IR 里 pams 的 `GetChooseSpawnCards`
是**完整忠实的循环**（31 步，`i=824 set keepOrder = false`）；`CardApiDispatch.cs:2002` 的初值也是 `false`
⇒ **两边一致**，当年那次回归的锅**不在 `keepOrder`**。

#### 9.1.6 判决性实验**已做**：洗牌模型被证伪（6 局 0/6 命中）

三条证据**夹住**了「补洗牌」这个方案：

1. 客户端 pams 实际选中的（回放 `508065` 的 `#34 CS` → `04` = `atlantic_convoy`）正好是
   **不洗牌时的第 0 张**；
2. 把洗牌放在最自然的游标位置（S=41）**复现不出**它；
3. ★ **零参数判决性实验**：取 6 局各自的**第一次 pams**（种子 = 该局 `match_id`），
   按「洗牌在最前、起始游标 = 内核在该动作开始前的游标」复算 `shuffled[观测下标]`，
   与客户端实际选中的卡比对 ⇒ **0/6 命中**
   （模型成立应当 6/6；偶然全中概率 ≈ (1/41)⁶ ≈ 2.1×10⁻¹⁰）。

⚠️ **第 3 条的边界**：它依赖「洗牌点上客户端的游标 = 内核的游标」。所以严格说
**至少有一条假设是错的** —— 要么洗牌模型不对，要么游标不等（即 pams 之前还有未找到的消费点）。
**两种情况下都不能直接「把洗牌加上去」。**

这与当年那次「实现后人类失败 13 → 22」的回归**方向一致** ⇒ **不要贸然实现**。
下一步应先查清 `CS` 动作的字段语义（字段 `1` 是不是「洗牌后的下标」）
与「洗牌 / 塞回牌库随机位 / 发牌」三者的消费顺序。
另外，客户端候选表的真实长度依赖服务端状态（`DSession.cards_reserve_changes` / blacklist），
**离线拿不到**。

**完整依据**（逐条 `文件:行号` + 可复跑命令）：`klink bot/docs/内核补全队列.md` 的
「2026-10-02（凌晨四）：RNG 游标失同步」一节。

#### 9.1.7 ★ 「补 pams 洗牌」已被**穷举否证**（2026-10-02 凌晨六）

模型：「客户端 `selectCardToDraw` 在洗牌（前向 Fisher-Yates，消耗 n=41）之后取
`shuffled[候选下标]`」。判据用 **6 局各自的第一次 pams**（种子 = 该局 `match_id`），
**6 个独立约束对 1 个未知量** ⇒ 只有**全中**才算证据（偶然全中概率见下）。

| 维度 | 试过的取值 | 全中 |
|---|---|---|
| **池口径** | 41（卡集 ∧ 排除预备）/ 71 / 54 / 84 | 只有 54 那档出现 **1 个孤立解**（期望 0.26，P(≥1)≈23%，**噪声**） |
| **排序键** | 资产名 Ordinal / 忽略大小写、显示名 `title` Ordinal / 忽略大小写、`cards.live.json` 插入顺序、两种逆序 | **全无解** |
| **全局起始偏移 K** | `0..2000` | **无解**（偶然全中 ≈ 2001×(1/41)⁶ ≈ **4.2×10⁻⁷**） |
| **洗牌消耗次数** | `n` / `n−1` | **全无解** |
| **洗牌方向/算法** | 前向 `i=0..n-1, j=RandRange(i,n-1)`（内核现口径）/ **后向** `i=n-1..1, j=RandRange(0,i)`（参考实现 `ref/kards-sim/KardsSim/Core/Rng.cs:43-50` 用的就是这个，消耗 n−1）/ 后向含 `i=0` / 前向全区间 | **全无解**。⚠️ 这一维必须测：内核自述的「前向 + 消耗 n」**没有外部向量**（只有自洽测试），而后向 FY 的结果与消费次数都不同 |
| **不洗牌模型**（取排序表第 k 张） | 7 种排序键 | **全 0/6** |
| **消费顺序** | 已从蓝图定死：`OnPlayedFromHand`→`selectCardToDraw`（洗牌）→ `CS` → `OnHandTargetSelected` 的 `RandomIntFromRangeWithStream`；`OnPlayedFromHand` **整条链只有这一个消费点** | 不是顺序造成的（起点几何自洽） |

另外两条**读不到**的事实（所以只能穷举测试）：
- `SortCardsByName` 在随附转译源码里**只有调用点、没有函数体**（`BP_Logic.g.cs:24086`，全树 grep 只此一处）
  ⇒ **排序键是原生实现**；`GetAllStaticCardsSortedByName`（`BP_GameState_Battle.g.cs:1615`）只是返回缓存成员。
- `CardDatabase.cs:209` 已核实 `"Britain" => 2`，与 pams 蓝图 `:212` 的 `faction == 2` 一致 ⇒ 阵营不是问题。

⇒ **结论：「补上这次洗牌」不是修复方案，作者当年那次回归（人类失败 13 → 22）不是偶然。**
⇒ 连同 §9.1.3 的 ② 在该局消费 0 ⇒ **没有可归因的缺失消费点**。

⚠️ **这个否证的边界（必须如实说）**：上面 6 个维度都把「候选下标」当成
**洗牌后数组的下标**。而 `CS` 的字段 `1` 是不是下标**没有独立证据**：

- `WireAction.cs:156` 里 `CS` 的 **`CardId` 取自 `CodeSlotA`（字段 `"2"`）**，字段 `"1"` 只被
  `SecondId` 读出来**记日志**（`ReplayRunner.cs:308` 那句「候选下标」是**内核作者的命名**）；
- 内核回放路径实际是 `pickDevelop(...)`（`CardApiDispatch.cs:1229`）—— **直接用答复里的卡**，
  **不拿字段 `1` 去索引候选表**；
- 而 `autoPickCardToDraw`（`ref/kards-sim/.../BP_Logic.g.cs:207`）是用
  `Map_Find(selectCardToDrawPending, <int>, out value)` **按键**取待选项的 ——
  说明那个 int 也可能是**映射的键**而不是数组下标。

⇒ 所以两种可能都指向**同一个行动结论**：
- 若字段 `1` **是**下标 ⇒ 上表 6 个维度已把「洗牌 + 取第 k 张」否证；
- 若字段 `1` **不是**下标 ⇒ 这个模型**离线根本不可验证**（我们没有任何能观测到「洗牌后顺序」的数据）。

**两种情况下都不该实现它** —— 这正是 `CardApiDispatch.cs:1199-1209` 当年得出的结论，
只是当年靠的是「实现后整体变差」的经验，现在有了 6 个维度的穷举否证 + 边界说明。

### 9.2 费用 / kredit 结算的剩余缺口

**状态：模型未定案。**

- 已核实的强线索就是 §7.3 的「两个错抵消」：内核在人类 kredit 模型上**偏低**。
- 未定问题：「发号用的回合号」与「kredit 槽自然增长」是不是**两个独立计数器**？
- 现状模型（`MatchEngine.cs:456-537`）：**槽位 = 自己第几个回合（+ 卡牌效果给的额外槽）**，
  与 `State.Turn` 无关；后手有奖励槽。
- **注意**：「花费 ≤ 槽位」只是**单侧弱约束**，只能证伪「self 模型」，永远证伪不了「global 模型」
  （global 恒 ≥ self）。见 §7.2 第 ③ 条。

### 9.3 结构性缺口

> **规则来源说明（2026-10-03）**：通用规则的首要证据是反编译得到的
> `BP_CardFunctions`，而不是当前 C# 的近似行为或卡面文字。仓库已有的
> `out/bp-cardfn.json` / `out/xr-cardfunctions.bpasm`（若在本地取回原始产物）以及
> `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`，用于确认控制流、参数形状
> 和触发顺序。下面的“未实现”表示内核尚未完整接入，不表示蓝图语义未知。

| 缺口 | 规模 | 出处 |
|---|---|---|
| **未实现原语** | **101 个**（影响 347 张卡 / 731 个用例；本次战斗路径修复后重测） | `out/audit/smoke-all-cards.txt` |
| **派发表真缺口**（`locals` 也兜不住） | 当前 `533` 种 / `2714` 个调用点，指纹 `B24F1E59D20FC2D8`；其中混有 UI / 战役 / 表现层调用 | `dispatch-gap` 实测 |
| **从不派发的玩法入口** | IR 入口名共 **449** 个，剔除 UI / 动画后仍有 **53 个玩法相关入口**内核从不派发；**23 张卡**的**全部**入口都是死入口 | `out/audit/semantic-reconcile-report.md` §5(N) |
| ↳ 三条完整的死事件链 | **Pincer**（7 张）+ **Intel**（3 张）+ **Lose Smokescreen**（3 张）= 13 张卡，按「一条链一次修」性价比最高 | 同上 |
| **`locals`-only 卡零覆盖** | **45 张**卡的 `entrypoints` 为空、逻辑全在 `locals`；烟雾测试按 `card.Entrypoints` 枚举用例 ⇒ 这 45 张**一个用例都没有**。连同 `entrypoints` 为空的共 **98 张**零覆盖 | `out/audit/semantic-reconcile-report.md` §5(L) |
| **`Gotcha` 子系统** | IR 调用点：`GotchaTriggered` **54 点 / 52 张卡**、`ShouldGotchaTrigger` **53 点 / 52 张卡**、`IsGotcha` **16 点 / 13 张卡**；回放里每局真触发 29~58 次。**故意不做**：它是整条子系统（`gotchaActivated` + `RearrangeLocation` + `SetCardsSeenByCipher` + Covert 揭示位 + cipher），半吊子实现比不实现更糟 | IR 实测 + `klink bot/docs/内核补全队列.md:8348` |
| **`changeType = 4` 在攻 / 防链上方向反了**（**已修**：`klink bot` `c54865d` / 上游镜像 `src` `33da1bd`） | `ChangeAttack` 上共 **56 个调用点**（全量 `steps` + `locals` 口径；只看 `steps` 时 ct 分布是 `{0:60, 1:314, 2:6, 4:48}`）。**受影响 41 张卡**（34 张的调用点在 `steps`、8 张在 `locals`，交集 1）；其中 **50 处 `amount = 0`**（真 no-op），**6 处非 0 ⇒ 旧实现把「撤销 buff」当成了「加 buff」**（5 张卡：`card_unit_su_100`、`card_unit_ki_42_ii_ko`、`card_unit_type_97`、`card_unit_kyushu_j7w3`、`card_unit_type_92_105mm_field_gun`）。⚠️ **`ChangeDefense` 的 ct=4 语义不同**：蓝图 `:7646` 把它路由到 `L_0E96` = `DirectClientLogger` 输出 `change type incorrect for "Change Defense"` 后返回（`:7906-7911`）⇒ **非法值、什么都不改**，所以内核补的是 **no-op**，不是「对称地撤销」（`ChangeDefense` 的 ct 分布 `{1:333, 2:10}`，**ct=4 有 0 个调用点**）。对照：`DoChangeKreditCost` **处理了** `changeType == 4`（`RemoveCostBuff`）⇒ 同一个枚举，费用那条链早就修了 | `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:6492/6789/6805/7543/7646/7906`、`src/KLink.Bot/Effects/CardApiDispatch.cs` |
| **`CanSelectAsTarget` 接进攻击路径**（**已修**：`klink bot` `7acc35f` / 上游镜像 `src` `8678281`） | 那道门**早就实现**且**已接进出牌候选枚举**：`src/KLink.Bot/Effects/CardApiDispatch.cs:310`（派发表）/`:4415`（本体）、`src/KLink.Bot/Engine/MatchEngine.cs:1842`（`LegalPlayTargets`）、`src/KLink.Bot/Server/NnPolicy.cs:129`、`tools/BotSim/SelfTest.cs`（「目标门」自测）。「IR 里 0 命中」**不是**判据 —— 唯一调用方是客户端 UI 蓝图 `BP_HandCard::DoesThisCardHasAnyTarget`，不是卡。**原先的缺口**：`MatchEngine.LegalTargets` 与 `Attack` **从不调它**，而蓝图 `cardsCheckFunctions::CanAttack`（`:902`，`byPlayFromHand=False`）会调并把它当 `failReason`。**现已接入**（共用 helper `AttackTargetGate`）：`LegalTargets` 末尾过滤 + `Attack` 里、**`State.AddKredits` 之前**（⚠️ 门的 ⑤ 自己会算一次行动费，放在扣油费之后会**双扣** ⇒ 费用紧时误拒合法攻击）。同时把 `CanOtherCardBeTargetted` 从**恒放行桩**改成按蓝图实现（触发点 2 的订阅表 + 跑**卡自己的**函数体，IR 缺体时按名字转写唯一实现者）。⚠️ **真正会变行为的只有 2 条**：⑥ 额外税（3 张税卡）+ ⑧ commando 否决位；③④⑦ 在 `byPlayFromHand=false` 上是**死代码**、⑤ 与 `Attack` 已有的油费门（`:1571`）**等价**。新增 4 条自测（含 ★★「必须放行」的反向用例，防恒拒） | `ref/kards-sim/KardsSim/Generated/_deps/cardsCheckFunctions.g.cs:902`、`src/KLink.Bot/Effects/CardApiDispatch.cs:4520`、`src/KLink.Bot/Engine/MatchEngine.cs` |
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

> ⚠️ **本节的数字与优先级是 2026-10-03 用实测重排的**（旧版第 3 条「`MakeCardRetreat` 一起做」
> 已经**做完并被一个上游 bug 挡住**，见 P0）。四条判据的口径见 §7.1。

#### P0 ★ 解开 `MakeCardRetreat` 的阻塞：**`ResolveEventVar` 的 `?? _ctx.Self`**

`MakeCardRetreat` 的**实现已经写好并验证过**，卡在一个**上游 VM bug** 上：

- **补丁**：`out/_ab/makecardretreat-B.patch`（撤回；25.6 KB）。
  语义已按蓝图定案（`BP_CardFunctions.g.cs` 分发体 `:25764-25904` + 逐张体 `:3478-3743`）：
  **前线 ⇒ 本方半场；半场满、或本来就在半场 ⇒ 退回拥有者手牌**
  （⚠️ **不是**「一律退回手牌」——那只是分支③④）。36 个调用点 / 35 张卡，**只有一种实参形状**。
- **它自己的 A/B**：`770857` 的 `#35` 从「重复记录」变成**应用成功**、⑤b **消失** ✓；
  六局逐局**完全相同** ✓；但**五局变差**（`310284` 89/95 + 新 ⑤b）✗。
- **变差的根因（已核实，不在撤回逻辑里）**：`KismetVm.cs:1247-1248`
  ```csharp
  var eventSubject = _ctx.Trigger ?? _ctx.Target;
  return eventSubject ?? _ctx.Self;      // ← 无目标时，任何事件变量都读成【施法者自己】
  ```
  于是「**无目标打出**的卡」读 `K2Node_Event_targetCard` 拿到的是**自己**。
  实证：`310284 #20 t7` 的 `card_unit_m16_halftrack`（`targetID=0`）本该在
  `IsValid(targetCard)`（IR `i=39`）就返回，却拿到 `[自己]` ⇒ 撤回把它**自己**退回手牌
  ⇒ `#31` 人类移动被拒。
- **为什么不能直接改**：`out/_ab/makecardretreat-C.patch` 在 `CardApi.cs:49` 精确传了
  `NamedArgs["targetCard"]`，**五局被精确还原** ✓，但**六局变差**（594/632，`508065`
  130→123）✗ —— 说明那里有卡**依赖旧行为**，而**它没被根因定位**。
- **验收（可证伪）**：修好 fallback 后应用 B 补丁 ⇒
  **六局 ≥ 601/632、五局 ≥ 132/140、770857 的 ⑤b 消失**，三者同时成立才算过。

#### P1 ★ `OnOtherCardAttacks`（T31，**20 张卡**）—— ✅ **已完成（2026-10-04，见 §8.18）**

> ✅ **2026-10-04：已接线并验收**（§8.18）。三处改动、自测 154/154（判死验证）、22 局逐位不变。
> ⚠️ 但**回放侧验证不了**：10 局里 T31 触发 58 次、**订阅者出现 0 次**
> ⇒ 判据只有「蓝图原文 + 自测」。**T30 仍未接**（见下）。

**（以下是接线前的分析与勘误，保留作历史）**

> ⚠️ **本节 2026-10-04 更正（未实施，只是把事实改对）**。

**事实（子代理按当前工作树复核，本次未逐条独立复核）**：

- 那句「`entrypoints` 里一个订阅者都没有」的注释**不在** `:1818-1823`（那里是另一条讲
  `OnBeforeAttack`/T13 合并的注释）；真正那句在 **`MatchEngine.cs:1940-1945`**。
- 结论本身仍然成立：T31 在 **0 张 `entrypoints`**、**20 张 `locals`** 里
  （复现：`python -c "…; print(len([k for k,v in d.items() if 'OnOtherCardAttacks' in v.get('locals',{})]))"` ⇒ 20）。
- ⚠️ 但**缺口比原文说的小**：出参广播机制**早就存在**
  —— `CardApi.BroadcastWithOutParam` / `BroadcastWithOutParams`
  （T14/T23/T24 在用，`Vm.RunLocalProgramMulti` 从 `Frame` 读出参）。
  真正缺的只有三件：① `BroadcastWithOutParams` 加一个 `exclude`（蓝图 `:4356` 排除攻击者本人）；
  ② 两个钩子方法（`FireOtherCardAttacks` / `SwitchAttackTargetIfAny`）；
  ③ `MatchEngine.Attack` 三处接线（**必须在 `State.AddKredits` 之前** —— 蓝图里
  T30/T31 两轮排在扣油费 `:4513` 之前，且 `stopAttack` 为真时油费不扣）。
- **出参语义不是同义词**：`stopAttack=true` ⇒ 整条攻击作废（油费不扣、不记"已攻击"）；
  `AttackedAndStopped=true` ⇒ 油费照扣、照记"已攻击"，但**整段伤害跳过**。
  20 张订阅卡的 44 处出参写入里 **`stopAttack` 的字面量只有 `false`**（该分支目前是死代码），
  活的是 `AttackedAndStopped`（5 张写死 `true` + 2 张按"攻击者还在不在场"算）。
- ⚠️ **真正的前置阻塞不是 T31，而是反制卡的落点**：内核把打出的 gotcha 送进弃牌堆
  （`IsAlive` 在 Discard 为假）⇒ `ShouldGotchaTrigger` 对它们恒假 ⇒ 14 张 gotcha 订阅者
  接完 T31 仍然原地返回。⇒ 建议**先**按蓝图把"打出的 gotcha 留在手牌"修掉（独立一支），
  再接 T31；否则只有 6 张非 gotcha 卡真正生效。
- **验收**：该钩子被派发、出参被尊重；**另外**：`card_unit_3_panzergrenadier` 那句话要删
  —— 它订阅的是 **T4 `OnAfterOtherCardAttacks`**（内核已在派发），**不订阅 T31**。

---
#### P2 ★ 「**早快照 + 自身效果在前 + 广播在后**」—— 修掉一处「症状对、机制错」

`MatchEngine.PlayCard` 里，`otherCards` 在蓝图里是 **`:6060-6066` 的早快照**、广播在 **`:6462`**，
而卡自己的 `OnPlayedFromHand` 在 **`:6396`** ⇒ 蓝图是「早快照 + **自身效果在前** + 广播在后」。
2026-10-02 那轮把广播**提前**，症状（新生成单位误收广播）好了，但那是靠**顺序**凑的；
**蓝图靠的是 `6060` 那个快照**。内核 `FireTrigger` 是即取即发，**没有「先快照后派发」的能力**
⇒ 要**加上这个能力**，并把顺序**改回**蓝图那样。**验收**：新生成单位仍收不到广播，
且 5 个触发点的先后与蓝图逐条一致。

#### P3 「压制门」那一族（**所有触发点**）

> ⚠️ **本节 2026-10-04 被大幅更正**。原文把蓝图的两层机制混成了一层，而且方向搞反了。

蓝图里有**两层独立**的压制机制：

| 层 | 位置 | 作用面 |
|---|---|---|
| **A（全局门）** | `_deps/BP_GameState_Battle.g.cs:1057-1063`，在 `FetchAllCardsWithEventTrigger`（起 `:1003`）收订阅者时：`isSuppressed && !suppressionExceptionTriggers.Contains(trigger)` ⇒ 跳过该候选卡 | **广播的收件人** |
| **B（调用点自门）** | ~30 处 `if (!<主体>.isSuppressed) goto L_SELF;` | **主体自己的那个程序** |

★★ **层 B 的统一形状（本次独立复核，读了 `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs` 三处原文）**：

```
L_…:  if (!X.isSuppressed) goto L_SELF;     ; 未压制 ⇒ 跳去自程序
L_FETCH:  FetchAllCardsWithEventTrigger(N)  ; ★ 广播：压制时**直接落到这里**
          loop → item.OnOtherXxx(…)
L_SELF:  X.OnXxx(…)                         ; 自程序
         goto L_FETCH                       ; ★ 跑完自程序**又跳回广播**
```

⇒ **层 B 的门只管"主体自己那个程序"；广播是两条路都会到的（即无条件发）。**

实测三处（行号即 g.cs 行号，原文可查）：

| 触发点 | 门 | 广播 | 自程序 | 跳回 |
|---|---|---|---|---|
| T15 `OnBeforeOtherCardDestroyed` | `:14833 if (!_cardDestroyed.isSuppressed) goto L_0208;` | `:14835 Fetch(15)` | `L_0208:14874 OnBeforeDestroyed` | `:14876 goto L_00B0` → 回 `:14835` |
| T32 `OnOtherCardBecomingVeteran` | `:26303 if (!card.isSuppressed) goto L_0AF6;` | `:26305 Fetch(32)` | `L_0AF6:26347 OnBecomingVeteran` | 回 `:26305` |
| T7 `OnAfterOtherCardGainDefense` | `:7954 if (!cardToChangeRef.isSuppressed) goto L_130F;` | `:7956 Fetch(7)` | `L_130F:8017 OnAfterGainDefense` | `:8020 goto L_10FD` → 回 `:7956` |

⚠️ **方法论教训（本轮最有价值的一条）**：内核的注释把
`JumpIfNot(cond) -> target` **读反了**。它的语义是「**cond 为假才跳**」，
所以 `JumpIfNot(isSuppressed) -> L_SELF` 的意思是「**未**压制 ⇒ 去自程序」，
而不是「压制 ⇒ 两个都不发」。内核目前三处都按后一种（错的）读法实现：
- `MatchEngine` 的 T15：`if (!Suppressed) { FireTrigger(自 + 广播) }`
  ⇒ **被压制的卡被摧毁时，内核漏发了 T15 广播**（蓝图无条件发）；
- `CardApi.MakeVeteran`：门装在了 **T32 广播**上、自程序反而无条件发 ⇒ **完全颠倒**；
- `CardApi.ApplyDefenseDelta`：self 与 T7 混在一次 `FireTrigger` 里，无法分别设门
  ⇒ 被压制时**多发**了 `OnAfterGainDefense` 自程序。

**层 A（全局门）内核完全没实现**（`grep SuppressionException src/` ⇒ 0 命中），
所以已实现的 30 多个 a/b 触发点**都还没有这道收件人过滤**。

**例外集合不是全局常量，是逐卡数据**：蓝图的 `suppressionExceptionTriggers` 来自卡数据
（`ref/kards-sim/cards.json`）。⚠️ 下面的具体名单**来自子代理的统计、本次未独立复核**：
11 张卡 / 3 个触发点（`OnStartofTurn` / `OnEndOfTurn` / `OnOtherCardDrawnFromDeck`）。

**建议的下一步（按风险从小到大）**：
1. ✅ **已做**（2026-10-04 第二轮，见 §8.11）：**只改层 B 的三处形状**（T15 / T32 / T7），
   配自测「被压制 ⇒ 自程序不发、广播照发」，并**改写了原先断言旧（错）语义的那条用例**。
   结果：22 局逐位不变（回放侧无信号，如实标注）。
2. ⏳ **未做**：层 A 全局门 + 例外表（11 张卡）—— 改的是**所有**触发点的收件人集合，风险最大，
   必须单独一支并配「例外卡仍收得到」的反向断言（防"压制=全哑"）。
   以及 `AttackCard`（`:4542`）与 `MakeVeteran` 之外的其余 ~7 处层 B 门（**未逐处核实**）。

⚠️ **更正一条旧结论**：原文说「内核只在 `MatchEngine.cs:2238`（T15）自己加了一道」。
子代理核对称该行是**重甲减伤**的注释、与压制无关，内核实际有约 10 处压制门
（**本次未独立复核**这一条）。`MatchEngine` 里那道 T15 门现在的实际位置见上面第 1 条。

#### P4 其余「内核完全没有」的触发点（按订阅数）

> ⚠️ **本节 2026-10-04 更正 + 重排（子代理按当前工作树逐条复核，本次未独立复核）**。

**勘误**：`OnCounterMeasureTriggered`(T21) / `OnIntelTriggered`(T28) / `OnOtherCardRetreat`(T54)
**已经实现了**（T21 在 `CardApi.cs` 的 Gotcha 区段，由 `GotchaTriggered` 驱动；
T28 在 `SetCardsSeenByCipher` 内；T54 在 `CardApiDispatch` 的撤回链上）。
⇒ 原来的第一梯队名单是**过期**的。

**重排后（按「成本 ÷ 收益」，且以**能否被现有语料观测**为准）**：

| 顺位 | 触发点 | 订阅 | 成本 | 回放可观测 |
|---|---|---|---|---|
| **1** | ~~**T35 `OnOtherCardCreatedAlterCard`**~~ ✅ **已做（2026-10-04，§8.13）** | 11 | ★★ | ✗ |
| 2 | T22 `OnDeckShuffled` | 5 | ★（`ShuffleDeckBySide`，但**必须先补 `skipSubAction` 门**，蓝图只在它为真时发） | ✗ |
| 3 | T3 `OnAfterDeckChanged` | 3 | ★★（8 个调用方，内核对应 6 处牌库操作） | ✗ |
| **4** | ~~T61 `OnOtherUnitPinned`~~ ✅ **已做（2026-10-04，§8.13）** | 2 | ★ | ✗ |
| **5** | ~~T48 `OnOtherCardLoseSmokescreen`~~ ✅ **已做（2026-10-04，§8.13）** | 3 | ★ | ✗ |
| 6 | T45 `OnOtherCardKreditCostChanged` | 4 | ★（**两个门**：只有"改自己的费"才发；广播排除被改的那张卡） | ✗ |
| 7 | T62 `OnOtherUnitUnpinned` | 3 | ★★（要先补 `RemovePin` 派发键） | ✗ |
| 8 | T49 `OnOtherCardMoveFromFrontline` | 3 | ★★ | ✗ |
| 9 | T68 `OnOtherCardOperationKreditsSpent` | 3 | ★★ | ⚠️ 1/3 |
| — | **T60 / T65（Covert）** | 15+1 | ★★★ | ✗ |
| — | **T34 `OnOtherCardConverted`** | 3 | ★★★ | ✗ |
| — | **T1 / T40 / T67** | 25/1/2 | — | **不该做** |
| — | **T18 / T26** | 0/0 | — | **无事可做** |

> ⚠️ **T35 / T61 / T48 已接线，但都"回放侧无信号"**（订阅卡在 22 局语料里 0 命中）
> —— 它们的证据链是「蓝图原文 + 自测」，**不是**回放对拍（§8.13）。
> 剩下的 T22 / T3 / T45 / T62 / T49 / T68 同样是 0 命中，**验收只能靠自测**。

**两条硬结论**：

1. ★ **T31 是唯一有回放观测量的 `c` 类触发点**（20 张订阅里 **4 张**在语料里出现过）；
   其余全部 0 张命中 ⇒ **T35/T22/T3/T45/T61/T62 等的验收只能靠自测，不能靠回放**。
2. **三条不该单做**：
   - **T60/T65（Covert）**：派发点严格在一道门里面
     （`IsUnrevealedCovertCard`，内核里是**恒 false 的桩**，`CardApi.cs`）。
     为发 T60 而把门改成真，会让**每一张普通卡被打出时**都给 15 张订阅卡广播一次
     —— 比不实现更糟（§9.3「半吊子实现比不实现更糟」）。⇒ 要做就 **T55 `RevealCard`
     + T60 + T65 + 攻击路径的两处 `RevealCard`** 一条链一次做，并且要知道它会
     **顺带改变 `CanCardBeBuffed` 对 11 张 Covert 卡的行为**（必须配回归自测）。
   - **T34 `OnOtherCardConverted`**：派发点在 `ConvertCard` 里，而 `ConvertCard`
     **不在派发表**（IR 里 26 个调用点 / 25 张卡）⇒ 单做 T34 是死代码。
   - **T1 / T40 / T67**：全 `Generated` 目录**没有任何** `FetchAllCardsWithEventTrigger`
     调用它们（T1 的 25 张是战役地点卡、调用方在客户端 C++ 里）⇒ **不该做**。
   - **T18 / T26**：订阅数 **0** ⇒ 纯空转。

**一个命名坑**（会让人查不到订阅）：三个触发点的**蓝图名与枚举名不同**
—— T33 `OnOtherCardBlitzChange` → 蓝图 `OnOtherCardBlitz**ed**`、
T62 `OnOtherUnitUnPinned` → `OnOtherUnitUn**p**inned`、
T68 `OnOtherCardOperationKreditSpent` → `OnOtherCardOperationKredit**s**Spent`（复数）。

完整对照表见 [`docs/触发点普查表.md`](docs/触发点普查表.md)（68 项 × 蓝图行号 × 内核行号 × 判定 × 订阅数）。

#### P5 原语实参形状普查的剩余项

这一族（README §9.3）已修：`Array_Add` / `Array_Contains` / `IsSameSideUnit` / `MakeVeteran` /
`CustomAbilityAdd` / `ChangeKreditCost` / `IsVeteran` / `GetOppositeSide` / `GetLocationCardBySide` /
`JSON_Clear` / `PersistCustomFields` / `DrawCardsFromDeckBySide` / `DamageCard` a[2]。
**负结论（别再重扫）**：`changeType=4` 五条链**全处理了**；**无越界读**；
13 个「lambda 恒返回 null 且有 out 槽」的原语里**只有 3 个是真缺口**（都已修）。

#### P6 手牌虚增 ⇒ 回手溢出到弃牌堆

`ReplayRunner.cs:816-820` 对「PC 引用但不在手牌」的卡会从牌库/弃牌堆**硬塞进手牌**。
后果实测（`310284 #64 t16`）：`ace_of_spades` 的「所有单位退回手牌」把 6 张挤进**弃牌堆**，
而客户端容得下（真实手牌 ≈5/4 vs 内核 7/8）⇒ 下游 `#80/#81 AC`、`#87/#89 ML`、`#92 AC`
一连串「不在场上」。**这是 `ReplayRunner` 类注释自认的保真度缺口。**

#### P7 随机效果分岔（`508065` 的 `#54`、`854099` 的 `#78`）—— **等新观测量**

四条成因已逐条关掉：① 漏/多消费点（洗牌两变体、`SetCardsSeenByCipher`、develop 消费全否证）
② 静默 no-op（⑥ 段逐个查过）③ 候选集**顺序**（✅ 已修：插入序 + HQ 最先，见 §9.1.7）
④ 候选集**内容**（✅ 已排除：`cards_blacklist` 恒空、`isReserved` 与内核表**逐张一致** 563/563）。
⇒ 只剩「`cardsRandomStream` 上某个**调用点/时机**差异」，而且**不是缺函数**
（真正被调用的 8 个 RNG 函数全部已实现）。**要再往前推需要新观测**，例如客户端某次
候选表的完整快照、服务端黑名单/预备表、或一次带全字段的抓包。

⚠️ **不管做哪个，做完都要更新 `tools/BotSim/DispatchGap.cs` 的两个基线常量**
（跑 `dispatch-gap` 拿新值），否则守卫会（正确地）失败。

### 9.6 补充审计语料

**现在的语料是 12 局**（2026-10-03 更新）：

| 来源 | 局数 | 说明 |
|---|---|---|
| `out/_server-replays/` | **6** | 主对拍集：`214436 389594 508065 542091 773639 854099`（同一对卡组，§7.4） |
| `tem/fyserver/…/data/live-replays/` | **5** | `130691 165924 310284 563868 955337`（从**服务端数据目录**里找到的，其中 3 局只有 3 条动作） |
| `out/_server-replays/replay-770857` | **1** | ★ **真人对局**（2026-10-03 抓），54 条动作，首漂开 `#35 t9` 由 `MakeCardRetreat` 未实现引起 |
| `fresh-replays/` | 7 | 另有 |

★ **新增能力：可以在服务端**运行时**把当前对局抓下来**（不必等回放落盘）：

```powershell
# 服务端在跑（KLink.App 拉起 fyserver，端口 5231）时：
python tools/fetch-match.py <matchId>          # 从管理接口抓 -> out/_server-replays/replay-<id>.{json,actions.json}
dotnet run --project tools/ServerBridgeTest -c Release --no-build -- --audit-replay "out\_server-replays\replay-<id>"
```

⚠️ **两个坑**（都踩过）：
1. 管理接口 `/admin/api/matches/history/{id}` 把 **summary 序列化成 camelCase**（`leftPlayerId`），
   而 `ReplayData.Load`（`:135-136`）读 **snake_case**（`left_player_id`）⇒ 不转就会
   玩家 ID 读成 0、**每条动作都「无法确定行动方」**。`fetch-match.py` 已做这个转换。
2. 动作那份必须是**对象**（含 `actions` 数组），不能是裸列表 —— `ReplayData.cs:188-190` 走的是
   `actionsDoc["actions"]`。

**语料量仍是最大的瓶颈之一**：12 局里 6 局是同一对卡组，3 局只有 3 条动作。
真人对局（`770857` 那种）价值最高 —— 它能抓到卡组/局面各不相同的漂开。

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

此外，本次核实了**已跟踪的源码 / 文档 / 脚本里不含本机绝对路径** —— 本机路径一律
写成占位符：

| 占位符 | 它代表的本机路径（前缀） |
|---|---|
| `<kards-src>` | KARDS 客户端的 C++ 源码树（本机在 `E:` 盘的 `\peoject\kards`） |
| `<bpasm-dir>` | 蓝图字节码汇编 / 反汇编器 `bpasm.exe` 所在的目录（`E:` 盘的 `\bpasm`） |
| `<klink-src>` | 上游 KLink 仓库（`E:` 盘的 `\klink`） |
| `<repo-root>` | 本仓库所属的父仓库根（`E:` 盘的 `\项目\klink-dotnet`） |

替换**只动前缀**，后面的分隔符与子路径原样保留：`…\peoject\kards\Source\BaseCardObject.h`
⇒ `<kards-src>\Source\BaseCardObject.h`；`\` 与 `/` 两种写法都认。

这套约定由 `out/audit/desensitize-paths.py` 执行、也可随时复查（映射表的原值就在它的
`PATH_MAP` 里）：

```powershell
python out/audit/desensitize-paths.py --check
# ⇒ 干净：没有发现 PATH_MAP 里可映射的本机绝对路径
```

**唯一的已知例外**是 `klink bot/docs/内部现状与路线图.md` —— 那份作者内部文档在
**描述脱敏过程本身**时提到了原始路径（即 `<repo-root>` 与 `<user-home>` 的原值），
改了这段话就自相矛盾，所以脚本显式跳过它。
（`src/KLink.Bot/Effects/CardApiDispatch.cs` 的同类残留由另一条清理线负责，
`desensitize-paths.py` 暂时也把它列在跳过名单里。）

按约定，`--check` 还会把**不在映射表里**的 `E:` 盘前缀单列出来，**只报告、不猜着改**。
目前剩两类：`E:` 盘上的 UE 引擎安装目录（出现在 `kards-cpp源码勘察.md` 引用的
`pakcook/settings.json` 片段里），以及一句「本轮工作目录在 `E:` 盘根」的叙述 ——
内层发布副本与父仓库的工作副本各一份，共 6 处。要清理就先给它们定下占位符，
再往 `PATH_MAP` 里加一条。

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
