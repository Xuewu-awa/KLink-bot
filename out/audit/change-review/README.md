# 历史文件与函数范围复核

这是模拟器源码/数据变化的溯源，不是客户端规则认证。文件或成员令牌不变，也不能自动认证其调用依赖。

- 变动文件：26；历史源码版本对：524；精确恢复：523。
- 版本对分类：{"json_values_changed":5,"python_ast_changed":4,"historical_bytes_not_recovered":1,"member_or_context_tokens_changed":511,"trivia_only_for_this_exact_file_pair":3}

- [file_summary.tsv](file_summary.tsv)：26个文件的版本恢复及复核入口。
- [source_pairs.tsv](source_pairs.tsv)：每个精确旧指纹对应的成员变化、添加/删除及共享上下文风险。
- [json_differences.tsv](json_differences.tsv)：卡表与IR的具体JSON指针变化。
- [report_source_impacts.tsv](report_source_impacts.tsv)：变动绑定对原报告的关联；不自动撤销或延续旧结论。
- [external.tsv](external.tsv)：客户端执行程序和运行时捕获的独立版本问题。

旧版本仅从当前HEAD可达Git历史及能完全匹配旧报告SHA-256的行尾/BOM重构中恢复；未恢复的不会按日期猜选。
C#使用SDK自带Roslyn，不执行被检查代码。条件编译使用空符号集，禁用源文本和指令进入保守上下文指纹；不宣称所有构建配置语义一致。

重新使用范围结论前核对每行current_sha256及基线指纹；源码变化后不能自动延续旧比较结果。

```powershell
dotnet build tools/AuditHistory/AuditHistory.csproj -c Release --ignore-failed-sources
dotnet tools/AuditHistory/bin/Release/net10.0/AuditHistory.dll --selftest
python tools/analyze-audit-history.py --cache-dir "D:\Codex Workshop\kards-install-review\source-history-cache"
python tools/test-audit-history.py
```
