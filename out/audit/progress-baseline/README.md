# 系统审计进度基线

本页整理历史成果、原始文件可用性和验收任务，不把文档记录或指纹匹配当作规则认证。整体完成率仍暂不计算。

- 台账规则记录：544；JSON报告：419。两者不是一对一。
- 原始指纹引用：5360个不同声明路径，7608项报告内绑定。
- 已完整读取哈希：5331个物理文件；核对字节：2091342344。
- 文件绑定结果：{"match":6902,"changed":706}。源码变化不自动等同于规则缺陷。
- 变动绑定归并到26个物理文件，先按函数/范围检查变化，避免整份旧报告重做。
- 历史待核陈述：2228；处置：{"partially_addressed":4,"historical_untriaged":2224}。
- 工作域：14；验收条件：42；范围候选资产：2019。

## 现有成果与待办

- [ledger.tsv](ledger.tsv)：完整台账规则身份、行号、章节指纹及明确关联的JSON报告。
- [reports.tsv](reports.tsv)：报告声称的范围、EXE版本声明及文件证据状态。
- [bindings.tsv](bindings.tsv)：每个文件声明的预期/当前哈希和具体变动，保留报告来源。
- [changed_files.tsv](changed_files.tsv)：归并后的变动文件及受影响报告，避免把重复引用算作独立缺陷。
- [文件/成员变动复核](../change-review/README.md)：从精确旧指纹恢复源码后，定位成员与数据差异；结论使用前核对快照指纹。
- [remaining_claims.tsv](remaining_claims.tsv)：历史陈述与人工处置；已被后续部分处理的内容不再重复算成全新缺陷。
- [work_units.tsv](work_units.tsv)：当前工作域、依赖、验收条件和交付物。
- [scope_candidates.tsv](scope_candidates.tsv)：按权威导出显式字段分组的资产候选；不按名称排除战役或衍生卡。

## 何时可以估计进度

先闭合当前版本的PvP资产与隐式规则范围，再把历史验证精确归入分支，最后对子任务定界和估量。
当前工作域只是规划分组，不能用“完成几个/总共几个”换算整个审计百分比。
可构筑卡池不能直接作为分母；还必须覆盖可生成/转换/选择的衍生对象、总部，以及相关原生默认和公共调用。

更新示例：

```powershell
python tools/build-audit-baseline.py --hash-evidence --evidence-root "D:\Codex Workshop" --evidence-root "C:\Users\AWQ\.codex\dsh-jobs" --evidence-root "D:\Fmodel\Output\Exports" --evidence-root "D:\Program Files\KARDS - The WWII Card Game" --client-exe "D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\kards-Win64-Shipping.exe" --definitions "D:\Codex Workshop\kards-install-review\cards.definitions.json"
python tools/test-audit-baseline.py
```

人工维护登记：`klink bot/docs/audit-work-plan.json`。陈述正文或依据报告变化时，旧处置会失效或拒绝生成，不能静默沿用。
