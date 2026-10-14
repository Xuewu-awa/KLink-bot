# 系统规则审计：结构基线

代码基线：`cc1e386b4d423a6149ef2e692f65d45a7318b0a8`

本报告不是穷尽缺陷清单，所有卡牌的语义状态保留为未验证。

| 检查项 | 数量 |
|---|---:|
| static_card_assets | 2019 |
| simulator_database_cards | 2019 |
| ir_cards | 1709 |
| card_assets_with_own_ir | 1709 |
| static_card_functions | 5585 |
| functions_with_runtime_match | 5585 |
| functions_without_direct_ir | 0 |
| excluded_display_or_delegate_functions | 61 |
| data_only_assets_without_own_function_exports | 310 |
| cards_with_structural_issues | 20 |
| missing_static_function_files | 0 |
| kernel_cs_files | 33 |
| kernel_cs_lines | 28294 |
| comment_leads | 49 |
| semantically_verified_cards | 0 |
| runtime_scalar_cards | 2019 |
| runtime_scalar_comparisons | 20190 |
| runtime_scalar_differences | 0 |
| runtime_trigger_array_cards | 2019 |
| runtime_trigger_arrays | 4038 |
| runtime_trigger_array_read_errors | 0 |
| runtime_trigger_array_differences | 0 |

## 直接 IR 缺失候选

必须检查继承、原生默认实现和 PvP 适用范围后才能认定缺陷。


## 结构异常

- `card_location_british_scen2`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_british_scen4`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_british_scen5`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_german_scen1`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_german_scen2`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_german_scen3`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_german_scen4`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_german_scen5`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_japan_scen1`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_japan_scen2`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_japan_scen3`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_japan_scen4`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_soviet_scen2`：[{"kind": "unknown_ir", "path": "$.steps[186].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[187].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[189]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 7139, "bo": 7139}}, {"kind": "unknown_ir", "path": "$.steps[190]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 7162, "bo": 7162}}]
- `card_location_soviet_scen4`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_soviet_scen5`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_usa_scen1`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_usa_scen2`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_usa_scen3`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_usa_scen4`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
- `card_location_usa_scen5`：[{"kind": "unknown_ir", "path": "$.steps[1].src", "node": {"unknown": "DynamicCast"}}, {"kind": "unknown_ir", "path": "$.steps[2].src", "node": {"unknown": "PrimitiveCast"}}, {"kind": "unknown_ir", "path": "$.steps[4]", "node": {"op": "unknown", "inst": "BindDelegate", "i": 90, "bo": 90}}, {"kind": "unknown_ir", "path": "$.steps[5]", "node": {"op": "unknown", "inst": "AddMulticastDelegate", "i": 113, "bo": 113}}]
