# 其余显示调用的共同消费者（2026-11-01）

55 处调用位于 26 个资产。54 处 Format 的结果在各自完整原始函数中的直接引用，仅出现于 UpdateCampaignStarStatus 的第 2 参数；另 1 处是无返回值 PrintString。共有 54 个不同的消息消费者语句，按每个源点累计得到 64 条源点—消费者关联；重复赋值的局部变量没有按最近消费者截断。

原始蓝图显示 UpdateCampaignStarStatus 将 star/message/starStatus 写入持久帧，经入口 443 转发到 CardFunctionsNotifier.NotiferStarCounterChanged，后者通过 I_CampaignReceiver 接口调用 HandleStarCounterChanged。配套 JSON 记录全部调用点、原始消费者语句、共享转发语句、证据路径和 SHA-256。

SDK 声明消息为 FText&；事件读取 LocalOutVariable。IR 的 outs=[] 不能证明没有引用回写或原生副作用。未验证真实接口调用、继承分派、重入、执行可达性或当前版本等价。

本轮得到的是直接用途分组，55 处完整审查状态仍保持 unreviewed；不改变训练准入，也不因资产名或接口名排除整个资产的 PvP 可达性。10 项回归覆盖新分支、原始独有引用、改换消费者/参数、输出参数、共享接口和复用局部。

按用户只关注无头 PvP 的范围，后续不逐一展开这些战役消息回调，除非新的 PvP 直接调用或动态轨迹证明其阻塞核心。下一优先项是此前 4 个实际卡牌程序的共享文本/创建/通知边界及其对核心结算的影响，而不是继续扩展战役/UI 审计。本轮没有生产规则修复或整卡认证。
