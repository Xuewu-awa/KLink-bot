# 无头模拟器派分类审计

UI、战役、教程和客户端专用调用只做库存记录，不进入核心原语修复队列。

- 卡/程序条目：1709
- IR call 调用点：16200
- 唯一调用名：358
- 无头规则真缺口：22 种 / 51 个调用点
- 其中未知语义 unknown_gameplay：4 种 / 4 个调用点

## 分类统计

| 分类 | 函数数 |
|---|---:|
| `blueprint_member` | 16 |
| `campaign_ui` | 26 |
| `dispatch` | 254 |
| `local_fallback` | 58 |
| `unknown_gameplay` | 4 |

## 无头规则候选 TOP 100

| 调用名 | 分类 | 调用点 | 卡数 | 未被本地函数覆盖 | 示例卡 |
|---|---|---:|---:|---:|---|
| `CustomEventOnCardDealDamage` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_220th_rifles |
| `CustomOnDealDamageToSelf` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_kyushu_j7w3 |
| `Get Start Of Turn Spawn Cards` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `OnEnterPlay` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_obice_da_75_13 |
| `SelfCustomEventOnCardDealDamage` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_su_100 |
| `doOnAfterOtherCardLeaveBoardOrOwner` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnCardDrawn` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnCardEnterPlay` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnCardPlayedFromHand` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnCardSpawnInHand` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnEndOfTurn` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnOtherCardAbilitiesChanged` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnOtherCardDestroyed` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnOtherCardKreditCostChanged` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnOtherCardReset` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnSpawnCardSelected` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnStartOfGame` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `doOnStartOfTurn` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `onAfterOtherCardAttacks` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `onOtherCardDealDamageAddDamage` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
