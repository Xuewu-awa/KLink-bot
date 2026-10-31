# 显示命名调用的逐点分类

基线提交：455630d。运行 `python tools/classify-display-call-sites.py --output <报告路径>` 可重新生成同名 JSON。输入是当前派生 IR 和本机提取的函数字节码；不使用回放作为规则权威。

当前 IR 共 60 处：Format 57、GetDisplayName 1、PrintString 2。四条已有结果链重新核对后仍与提取字节码的局部变量引用语句集合相符；另将第 75 步兵团链末端的 PrintString 独立登记为无返回值消费者的直接日志调用。

| 调用点 | 经核对的局部结果用途 |
|---|---|
| carrier_battle / steps / i680 Format | i754 转字符串，i791 SpawnCardInHandBySide 的参数6 |
| seize_the_initiative / steps / i684 Format | i764 转字符串，i801 SpawnCardInHandBySide 的参数6 |
| 115th_separate_btn / steps / i2725 Format | i2810 AddNumberToText 的参数1；其输出槽也纳入保守依赖检查 |
| 75th_rifle_regiment / SpawnCopy / i0 GetDisplayName | i29 拼接，i90 PrintString 的参数1 |
| 75th_rifle_regiment / SpawnCopy / i90 PrintString | IR 无目标槽/输出槽，提取字节码为直接 CallMath |

参数下标从0开始。这5处仅建立局部用途证据，均未豁免训练准入。其余55处仍为未审查，分布于26个带 location/scen 名称的资产；名称仅作定位，不据此认证其属于非PvP范围。本批没有扩大到战役/UI机制。

新增10项回归通过：当前链、分支消费者、实例写入、接收者、额外运算、终端API输出的后续使用、新增同名调用、缺失原始证据、原始字节码新增消费者、日志输出形态变化。危险数据流变化会使审查失败，新增调用不会自动继承结论。脚本不是通用控制流/别名分析器，也不认证整个原生调用。

DeepSeek 任务 `20261101-003800-display-site-flows-02439c66` 已完成机械枚举。主代理独立核对4个来源、转换和终端参数位置一致；worker未包含 AddNumberToText 的未使用输出槽，主代理将其保守纳入依赖并增加回归。worker使用的事件名称不作为真实 export 身份，报告保留 steps/SpawnCopy 的原始IR容器身份。

没有修改运行时代码、规则计算或训练准入，未重新运行C#全量测试；上一批1049项全量测试不是本批新增验证。矩阵重建和16项矩阵回归的结果见 coverage-matrix/validation.json。新报告绑定当前IR、审查脚本、相关运行时源文件和提取字节码哈希；没有刷新旧审计单元的过期证据。

尚需确认的边界：原生消费者及继承路径、相关通知回调、与真实运行时 program-json/v1 哈希及执行上下文的绑定。当前IR丢失的文字模板/结构体字段未修复；提取文件哈希不能证明它与更新后的客户端等价。完成这些边界前，不将这5处升级为规则无影响的运行时白名单。
