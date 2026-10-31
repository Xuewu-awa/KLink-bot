# 显示消费者与通知回调边界

基线提交 efffc2e。运行 `python tools/audit-display-consumer-boundaries.py --output <路径>` 重建 JSON，运行 `python tools/test-display-consumer-boundaries.py` 验证12项契约回归。本批只读本机历史 pak 解析和客户端内存提取，不调用或写入游戏进程，不将回放作为规则权威。

核查到的三个直接参数用途（下标从0开始）：

| 来源 | 下游 |
|---|---|
| SpawnCardinHandbySide.newCardText | i333 CreateCard 参数7 |
| CreateCard.newCardText | i2880 NotifyDrawCardFromDeck 参数9 |
| AddSubActionAppendNumberToCardText.newCardText | i154 ActionValueString 参数1，字段名 number |

每处检查了该变量在完整函数解码中的直接引用集合，新增分支、写入或其他消费者会拒绝这条转发契约。该检查不追踪 callee 内部或其他字段的别名，也不证明整个函数无副作用。大小写不同的 SpawnCardinHandbySide 是 pak 中真实导出名，不能将 IR 派发别名直接当成原始导出身份。

AddNumberToText 的 BP_CardFunctions 实现 i1391 写卡牌 Text；i1507 按 IsActionProcess 的结果跳转，通知分支 i1558 经 CardFunctionsNotifier 调用 NotifyAppendNumberToCardText。通知器通过 OnlineMatchReceiver 接口调用 HandleAppendNumberToCardText，后者把四个参数写入持久帧，再调用 ExecuteUbergraph_BP_OnlineMatch(12721)。本批核对该入口的两条语句：转发四字段到 AddSubActionAppendNumberToCardText，然后 PopExecutionFlow。这里只核对显式入口窗口，不把 PopExecutionFlow 解释为任何执行上下文下必然返回，也未认证调度、重入或其余 ubergraph。

AddSubActionAppendNumberToCardText 将数据封装后传入 CreateAction_AddSubAction，动作名为 ZActionAppendNumberToCardText。文字会进入对局的子动作通路，不能直接按显示名称认定全局无影响。报告另外列出六个函数的原始脚本/解码哈希和相关调用参数、接收上下文。脚本字节哈希只校验快照内部长度和字节，不验证解码器正确性；提取文件哈希不等于当前版本一致证明，也不等于运行时 kismet-program-json/v1 哈希。

CardFunctionsStub.AddNumberToText 的零脚本声明与 BP_CardFunctions 的1633字节实现并存。零脚本不能据此断言整个调用未实现；已有蓝图实现也不能替代真实实例、继承和虚调用绑定证据。PrintString 在 SDK Engine.hpp 中有声明，但声明不提供原生函数体，本批没有认证其内部实现。

DeepSeek 任务 20261101-004725-display-boundary-a784d824 机械枚举四函数。主代理独立遍历核对17个调用的语句、指令、名称和表达式路径完全匹配；赋值数量45、委托指令数量0匹配。接口上下文调用不是委托广播，未凭此扩大到全局接收者结论。主代理另核对了 worker 输入外的 ubergraph 12721 入口窗口。

12项回归通过：当前契约、新增生成文字分支/写入、接收者变化、通知参数变化、持久帧字段变化、入口变化、回调落空继续执行、子动作新增消费者、脚本截断、解码不完整、重复函数身份。它们只保护列明的契约，不是任意 AST 变化的拒绝器。原生文本助手、实际接口实现绑定、其他接收路径、下游调度以及运行时精确程序身份继续开放；所有调用均不豁免训练准入，不认证整卡。

本批未修改生产或规则运行时代码，未重跑 C# 全量自测。矩阵重建及16项矩阵回归见 coverage-matrix/validation.json；未刷新旧证据指纹。
