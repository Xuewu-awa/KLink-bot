# 四个 PvP 候选资产的显示依赖审查

仅审查四个缺口表达式的结果用途；不认证整卡或全部原生调用语义。

| 资产 | 来源 i | 最终用途（参数从 0 开始） |
|---|---:|---|
| `card_event_carrier_battle` | 680 | `SpawnCardInHandBySide` i=791 参数 6 |
| `card_event_seize_the_initiative` | 684 | `SpawnCardInHandBySide` i=801 参数 6 |
| `card_unit_115th_separate_btn` | 2725 | `AddNumberToText` i=2810 参数 1 |
| `card_unit_75th_rifle_regiment` | 0 | `PrintString` i=90 参数 1 |

四条链均未发现其他局部消费者；原始解析记录与 IR 的相关变量引用语句索引集合一致。两张指令通过显式 level 数值和持久化实现递增效果，第 115 独立营的该结果进入文本显示，第 75 步兵团的名称进入 PrintString。

原始模板分别为 `card_event_carrier_battle:{level}`、`card_event_seize_the_initiative:{level}`、`+{x}+{y}`；当前 IR 均丢失为字符串 `null`。无头规则范围内保留当前显示省略，不据此认定文本已还原。

VM 未实现数学节点记录计数并返回 null；普通执行继续，第 75 步兵团后续生成调用不会仅因名称缺失而被跳过。未实现计数对训练准入的影响仍须单独核查。

## 验证与边界

脚本固定点传播局部变量依赖，检查所有消费者及调用接收者，并核对原始解析函数的引用语句集合；出现额外消费者立即失败。它是当前四条链的可重复范围检查，不是通用语义证明器。

静态缺口口径保持 45 类 / 1013 调用，运行时代码未修改。原生调用内部、继承行为、结构体缺失字段及版本对应关系仍不由本报告认证。
