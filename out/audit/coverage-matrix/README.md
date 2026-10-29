# 系统审计覆盖矩阵

这是当前仓库资产与审计记录的可更新索引。PvP范围和完整语义认证尚未闭合，不能据此计算总完成百分比。

资产行数来自仓库卡表/IR/历史库存的并集，不代表当前客户端的PvP卡数；当前pak资产版本仍待关联。

| 项目 | 数量 |
|---|---:|
| 仓库卡资产（含范围待确认项） | 2019 |
| 有自身IR的资产 | 1709 |
| 已明确登记为PvP范围 | 0 |
| 已明确登记为非PvP | 0 |
| PvP范围尚未登记闭合 | 2019 |
| 函数身份 | 5585 |
| 显示提示/委托签名排除项 | 61 |
| IR程序容器 | 2626 |
| 聚合静态依赖关联 | 18571 |
| 依赖符号 | 440 |
| 已索引JSON审计记录 | 420 |
| 已人工关联局部证据单元 | 22 |
| 局部证据绑定输入一致 | 22 |
| 规划工作域（不作完成率分母） | 14 |
| 明确验收条件 | 42 |
| 当前权威PvP分母已建立 | 否 |
| 已完整认证单卡 | 0 |
| 整体语义完成百分比 | 未建立分母，暂不计算 |
| 整体剩余工作百分比 | 未建立分母，暂不计算 |

## 使用入口

- [cards.tsv](cards.tsv)：全资产范围、函数容器、依赖、待核状态；未按名称猜测排除战役卡。
- [functions.tsv](functions.tsv)、[programs.tsv](programs.tsv)：历史函数身份与当前IR绑定；事件入口和共享ubergraph分开列。
- [edges.tsv](edges.tsv)：静态调用位置、ChangeAttack字面模式及动态模式；不是实际执行可达性证明。
- [dependencies.tsv](dependencies.tsv)：依赖影响范围、C#候选位置及报告文字关联；关联不等于验证。
- [branches.tsv](branches.tsv)：人工确认的局部验证范围、版本/源码指纹与失效原因。
- [reports.tsv](reports.tsv)：全部JSON审计记录；旧remaining保留为历史待整理文本。
- [open_items.tsv](open_items.tsv)：明确整理的当前待办，和历史remaining分开。
- [work_units.tsv](work_units.tsv)：工作域、依赖、验收条件及交付物；不以分组数量估计总体工作量。
- [进度基线](../progress-baseline/README.md)：台账/报告对齐、文件指纹复核和历史陈述去重。
- [成员变动复核](../change-review/README.md)：精确旧指纹对应的C#成员、数据变化和仍待恢复项。
- [matrix.json](matrix.json)：完整机器可读数据与输入SHA-256。

TSV中的null表示缺失/未知值；复杂字段为JSON单元格，完整类型以matrix.json为准。

## 优先整理的公共依赖

按直接静态引用卡数排列；未包含隐式基础规则、动态或传递调用，不能当作真实使用频率。

| 符号 | 引用卡资产 | 程序容器 | 局部证据单元 |
|---|---:|---:|---|
| Array_Get | 715 | 759 | 尚未人工关联 |
| IsLocatedOnBoard | 480 | 524 | 尚未人工关联 |
| IsUnit | 465 | 496 | 尚未人工关联 |
| GetOppositeSide | 459 | 482 | 尚未人工关联 |
| GetTargetedCard | 429 | 429 | 尚未人工关联 |
| ChangeAttack | 355 | 369 | RULE-525/ChangeAttack/ordinary, RULE-526/ChangeAttack/absolute, RULE-527/ChangeAttack/remove, RULE-528/ChangeAttack/custom-and-default |
| ChangeDefense | 325 | 330 | 尚未人工关联 |
| DamageCard | 262 | 265 | RULE-530/DamageCard/effect-entry, RULE-531/DamageCard/apply-bounded-defense, RULE-532/DamageCard/retained-lethal-removal, RULE-533/DamageCard/hq-id-winner-branch |
| GetCardsOnBoardBySide | 246 | 247 | 尚未人工关联 |
| GetCardFromID | 228 | 231 | 尚未人工关联 |
| PersistCustomFields | 217 | 230 | 尚未人工关联 |
| DrawCardsFromDeckBySide | 215 | 217 | 尚未人工关联 |
| GetLocationCardBySide | 210 | 216 | RULE-534/GetLocationCardBySide/explicit-hq-slots |
| IsSideActive | 209 | 210 | 尚未人工关联 |
| GetRandomCard | 156 | 156 | 尚未人工关联 |
| IsSameSideUnit | 148 | 149 | 尚未人工关联 |
| SpawnCardOnBattlefield | 138 | 138 | 尚未人工关联 |
| SpawnCardInHandBySide | 134 | 136 | 尚未人工关联 |
| JSON_Clear | 127 | 132 | 尚未人工关联 |
| Array_Clear | 126 | 127 | 尚未人工关联 |
| GetPlayFromHandDamage | 119 | 119 | 尚未人工关联 |
| DestroyCard | 112 | 113 | 尚未人工关联 |
| GetAllCards | 100 | 103 | 尚未人工关联 |
| GetAllUnitsOnBoard | 99 | 100 | 尚未人工关联 |
| getTotalDefense | 99 | 105 | 尚未人工关联 |

## 更新与准入

在仓库根目录运行：

```powershell
python tools/build-audit-matrix.py --client-exe "D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\kards-Win64-Shipping.exe"
python tools/test-audit-matrix.py
```

更新 `klink bot/docs/audit-matrix-reviews.json` 维护局部证据、当前待办及有证据的PvP范围判定。
绑定报告或源码改变、未检查EXE或EXE不匹配时，局部证据显示失效/未检查；不自动继承绿色状态。
当前22条局部单元已人工关联；其他旧审计成果为待整理，不表示全部重做。
完整单卡认证需要另行完成：当前版本资产、PvP范围、全部自定义/继承/原生路径、分支及联动验证。
第一版不允许以手填百分比或局部共享测试自动认证整卡；完整认证准入尚待独立闭合。
