# 显示调用运行时绑定审计（2026-11-01）

已重跑原始解析消费者审查，将 5 个已审查 IR 调用点绑定到 C# KismetLibrary 实际重建的 4 个程序；共导出 11 个程序。事件程序含加载器裁剪后的语句与合成分派前导，不能用原始 IR 文件哈希代替程序哈希。

- carrier_battle / OnPlayedFromHand：i680，src，Format。
- seize_the_initiative / OnPlayedFromHand：i684，src，Format。
- 115th_separate_btn / OnEndOfTurn：i2725，src，Format。
- 75th_rifle_regiment / SpawnCopy：i0，src，GetDisplayName；i90，$，PrintString。

独立模拟器实际观察到 SpawnCopy 两处调用；其余三处只完成静态来源绑定，未声称运行时执行。14 项负面/正面回归覆盖哈希、身份、入口、指令、表达式、缺失/重复、采集失败和豁免拒绝。所有调用继续计入健康缺口并阻止训练准入。本轮无生产代码变更。

数据加载需使用同目录、字节匹配的 card-ir.json、cards.json、card-ir-native-defaults.json。工具最初对 docs 目录校验失败，随后使用已验证发布快照 Release/Data 成功加载；未修改证明指纹或绕过保护。

C# 工具用 KLinkProject 属性指向已提交生产源快照，排除工作区无关改动；程序集 SHA-256 写入导出。工具和源文件指纹记录于绑定报告。哈希只能绑定此程序版本，不能证明原生实现等价。

历史接收者：DeepSeek 清点 2026-10-24 快照后，主代理独立解码核对 5 个实例的类、对象索引、GUObject 项、CDO 标志，9 项属性偏移和存储指针，以及跨对象引用闭合。它只证明该历史采样中的对象引用；未验证当前客户端版本、接口调用、动态分派、子类、原子性或调度。原始游戏内存及抓取脚本路径/指纹在配套历史报告中。

仍开放：真实接口调用轨迹、继承分派、原生文字实现、下游调度/重入，以及另外 55 处未审查显示调用。未认证整卡，未新增准入豁免。
