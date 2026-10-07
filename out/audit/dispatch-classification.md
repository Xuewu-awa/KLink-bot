# 无头模拟器派分类审计

UI、战役、教程和客户端专用调用只做库存记录，不进入核心原语修复队列。

- 卡/程序条目：1735
- IR call 调用点：17949
- 唯一调用名：786
- 无头规则真缺口：29 种 / 62 个调用点
- 其中未知语义 unknown_gameplay：8 种 / 8 个调用点

## 分类统计

| 分类 | 函数数 |
|---|---:|
| `blueprint_function` | 2 |
| `blueprint_member` | 17 |
| `campaign_ui` | 431 |
| `dispatch` | 269 |
| `local_fallback` | 59 |
| `unknown_gameplay` | 8 |

## 无头规则候选 TOP 100

| 调用名 | 分类 | 调用点 | 卡数 | 未被本地函数覆盖 | 示例卡 |
|---|---|---:|---:|---:|---|
| `GetHandLocationBySide` | `blueprint_function` | 5 | 5 | 5 | card_event_aerial_reconaissance, card_event_night_raid, card_event_orp_blyskawica |
| `CustomEventOnCardDealDamage` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_220th_rifles |
| `CustomOnDealDamageToSelf` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_kyushu_j7w3 |
| `DeactivateOtherNFS` | `unknown_gameplay` | 1 | 1 | 1 | card_event_national_fire_service |
| `DeactivateOtherSniped` | `unknown_gameplay` | 1 | 1 | 1 | card_event_sniped |
| `Get Start Of Turn Spawn Cards` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `GetHighestBomberAttack` | `unknown_gameplay` | 1 | 1 | 1 | card_event_for_precision_bombing |
| `GetMainNationForSide` | `blueprint_function` | 1 | 1 | 1 | card_event_pilot_escape |
| `Map_Add` | `blueprint_member` | 1 | 1 | 6 | BP_RenderedCardCache, card_event_the_big_three, createCard_NUI_Widget |
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
| `isAlreadyAffectedByOtherReckless` | `unknown_gameplay` | 1 | 1 | 1 | card_event_reckless_assault |
| `onAfterOtherCardAttacks` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `onOtherCardDealDamageAddDamage` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
