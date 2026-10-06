# 无头模拟器派分类审计

UI、战役、教程和客户端专用调用只做库存记录，不进入核心原语修复队列。

- 卡/程序条目：1735
- IR call 调用点：17949
- 唯一调用名：786
- 无头规则真缺口：69 种 / 160 个调用点
- 其中未知语义 unknown_gameplay：17 种 / 25 个调用点

## 分类统计

| 分类 | 函数数 |
|---|---:|
| `blueprint_function` | 27 |
| `blueprint_member` | 23 |
| `campaign_ui` | 432 |
| `dispatch` | 228 |
| `local_fallback` | 59 |
| `unknown_gameplay` | 17 |

## 无头规则候选 TOP 100

| 调用名 | 分类 | 调用点 | 卡数 | 未被本地函数覆盖 | 示例卡 |
|---|---|---:|---:|---:|---|
| `Set_Add` | `blueprint_member` | 14 | 4 | 16 | BP_CardHelp, card_event_focused_attack_ger, card_unit_14_panzergrenadier |
| `AddToTriggerQueue` | `blueprint_function` | 6 | 6 | 6 | card_event_air_escort, card_event_baker_street_irregulars, card_event_bpf |
| `ForceEndTurn` | `blueprint_function` | 5 | 5 | 5 | card_event_banzai_charge, card_event_calm_before_the_storm, card_event_protect_the_pocket |
| `GetHandLocationBySide` | `blueprint_function` | 5 | 5 | 5 | card_event_aerial_reconaissance, card_event_night_raid, card_event_orp_blyskawica |
| `TakeControlOfEnemyUnit` | `blueprint_function` | 5 | 5 | 5 | card_event_confusion, card_event_lost_cause_skirm, card_event_minority_recruits |
| `CheckHasUnitToSpawn` | `unknown_gameplay` | 4 | 4 | 4 | card_event_red_banner, card_event_refit, card_event_stand_together_brothers |
| `GiveRandomCombatKeyword` | `blueprint_function` | 4 | 4 | 4 | card_event_forged_in_fire, card_unit_77th_guards, card_unit_sapporo_regiment |
| `GetHQ_DamagedAmountThisTurnBySide` | `blueprint_function` | 3 | 3 | 3 | card_unit_304_panzergrenadier, card_unit_qf_40mm_mk_iii, card_unit_seagull |
| `GetOperationKreditsSpentThisTurn` | `blueprint_function` | 3 | 2 | 3 | card_event_claim_objective, card_event_sustained_pressure |
| `ResetUnitOperations` | `blueprint_function` | 3 | 3 | 3 | card_event_rain4_monsoon_rain3, card_event_tactical_withdrawal, card_unit_windhund_division |
| `SpawnNextToCard` | `blueprint_function` | 3 | 3 | 3 | card_unit_47th_infantry_regiment, card_unit_f4f_wildcat, card_unit_m24_chaffee |
| `AddDefenseToMultipleCards` | `blueprint_function` | 2 | 2 | 2 | card_event_root_out_the_enemy, card_unit_119_grenadier |
| `AdjustCardPositionInDeck` | `blueprint_function` | 2 | 1 | 2 | card_event_fliegerfuhrer_atlantik |
| `Apply The Buff` | `unknown_gameplay` | 2 | 1 | 2 | card_unit_sturmovik_pol |
| `ApplyGameplaySideEffect` | `blueprint_function` | 2 | 1 | 2 | card_unit_c6n_saiun |
| `GetReducedDamage` | `unknown_gameplay` | 2 | 1 | 2 | card_unit_seagull |
| `GiveCredits` | `unknown_gameplay` | 2 | 1 | 2 | card_unit_182_landwehr |
| `GiveTwoKredits` | `unknown_gameplay` | 2 | 1 | 2 | card_unit_2nd_michigan |
| `IncOpCountAndCheckVeteran` | `unknown_gameplay` | 2 | 1 | 2 | card_unit_182_landwehr |
| `ReleaseControlOfEnemyUnit` | `blueprint_function` | 2 | 2 | 2 | card_event_confusion, card_event_partisans |
| `RemoveAlpine` | `blueprint_function` | 2 | 1 | 2 | card_unit_67th_baranovichi |
| `RemoveGameplaySideEffect` | `blueprint_function` | 2 | 1 | 2 | card_unit_c6n_saiun |
| `RevealCard` | `blueprint_function` | 2 | 2 | 2 | card_event_hidden_plans, card_unit_lublin_r_xiii |
| `SalvageMultipleUnits` | `blueprint_function` | 2 | 2 | 2 | card_event_on_the_ascend, card_event_out_of_the_mist |
| `Set_Clear` | `blueprint_member` | 2 | 2 | 2 | card_unit_henschel_hs_126, card_unit_panzer_iii_e |
| `StealCardFromBoardToDeck` | `blueprint_function` | 2 | 2 | 2 | card_event_cobelligerents, card_event_honor_and_loyalty |
| `TriggerDeployment` | `blueprint_function` | 2 | 2 | 2 | card_event_air_escort, card_unit_sm_79 |
| `getFrontlineLimit` | `blueprint_function` | 2 | 2 | 2 | card_event_night_bombing, card_event_storm3_tropical_storm |
| `getHasVeteranUpgrade` | `blueprint_member` | 2 | 2 | 2 | card_event_battle_valor, card_unit_266th_guards_rifles |
| `Array_Resize` | `blueprint_member` | 1 | 1 | 1 | card_event_semper_fi |
| `CountFriendlyGuardUnits` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_sdf |
| `CustomEventOnCardDealDamage` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_220th_rifles |
| `CustomOnDealDamageToSelf` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_kyushu_j7w3 |
| `DeactivateOtherNFS` | `unknown_gameplay` | 1 | 1 | 1 | card_event_national_fire_service |
| `DeactivateOtherSniped` | `unknown_gameplay` | 1 | 1 | 1 | card_event_sniped |
| `Get Start Of Turn Spawn Cards` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `GetDefenseBuffFromAdjacentUnits` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_48th_armored_infantry |
| `GetDestroyedCardsIDsByTurn` | `blueprint_function` | 1 | 1 | 1 | card_unit_73rd_infantry_regiment |
| `GetHighestBomberAttack` | `unknown_gameplay` | 1 | 1 | 1 | card_event_for_precision_bombing |
| `GetMainNationForSide` | `blueprint_function` | 1 | 1 | 1 | card_event_pilot_escape |
| `GetUnitDestroyedThisTurn` | `blueprint_function` | 1 | 1 | 1 | card_unit_24th_uhlan |
| `Map_Add` | `blueprint_member` | 1 | 1 | 6 | BP_RenderedCardCache, card_event_the_big_three, createCard_NUI_Widget |
| `MoveCardInHandToLeftMost` | `blueprint_function` | 1 | 1 | 1 | card_event_hmas_warramunga |
| `OnEnterPlay` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_obice_da_75_13 |
| `Remove the Buff` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_l4_grasshopper |
| `RemoveBond` | `blueprint_function` | 1 | 1 | 1 | card_event_rationing |
| `RemoveSalvage` | `blueprint_function` | 1 | 1 | 1 | card_unit_raf_mitchell |
| `ResolveTriggerQueue` | `blueprint_function` | 1 | 1 | 1 | card_event_air_escort |
| `SelfCustomEventOnCardDealDamage` | `unknown_gameplay` | 1 | 1 | 1 | card_unit_su_100 |
| `Set_ToArray` | `blueprint_member` | 1 | 1 | 2 | BP_RenderedCardCache, card_unit_14_panzergrenadier |
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
| `getStaticVeteranUpgrade` | `blueprint_member` | 1 | 1 | 1 | card_unit_37mm_m1_aa_gun |
| `isAlreadyAffectedByOtherReckless` | `unknown_gameplay` | 1 | 1 | 1 | card_event_reckless_assault |
| `onAfterOtherCardAttacks` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
| `onOtherCardDealDamageAddDamage` | `blueprint_member` | 1 | 1 | 1 | card_brawl_test1 |
