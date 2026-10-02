import os, re, json, subprocess

ROOT = r'<repo-root>'
SRC = os.path.join(ROOT, 'src', 'KLink.Bot')
files = []
for dp, dn, fn in os.walk(SRC):
    if '\\obj\\' in dp + '\\':
        continue
    for f in fn:
        if f.endswith('.cs'):
            files.append(os.path.join(dp, f))
blobs = {f: open(f, encoding='utf-8', errors='replace').read() for f in files}

CAND = """DamageMultipleCards DestroyMultipleCards DiscardCardFromHand DiscardRandomCardFromHand
DiscardCardFromDeck DiscardOnDrawingWithFullHands FullyHealCard MakeCardRetreat ResetUnitOperations
TriggerDestruction TriggerDeployment RevealCard TakeControlOfEnemyUnit ReleaseControlOfEnemyUnit
StealCardFromBoardToDeck ConvertCard MoveUnitFromBoardToOwnersHand MoveUnitFromSupportToFrontLine
SpawnCardInFrontline SpawnCardToBoard SpawnCardinHandbySide SpawnCardonBattlefield
SpawnMultipleCardsOnBattlefield SpawnNextToCard LoseKreditSlot GainKreditSlot EndMatch ForceEndTurn
SetCountdown AddGameplayRestriction RemoveGameplayRestriction AddKreditsTax AddIntelToCard
IncrementObjectiveCounter AddAttackUntilEndOfTurn WhichStrategy IsUsingStrategy MakeCardsFight
MoveMultipleCardsToTopOfOwnersDeck SetCardSeen SetCardsSeenByCipher GetCardsInSupportLineBySide
GetSupportLineLocationBySide GetCardsInFrontlineBySide GetAllCardsInFrontline IsLocationFull
IsCardReserved getFrontlineLimit ChangeFrontlineLimiter UpdateFrontlineIfNeeded
DoesSideControlTheFrontline UpdateGuarded isBeingGuarded CanCardBeBuffed PayCardCost PayMovementCost
CalculateDamageDealt ExecuteBeforeReceiveDamage ExecuteOnDealDamageAddDamage ExecuteOnSurvivedCombatEvents
ExecuteStoppedAttack GiveAlpineBonus GiveMobilizeBonus ExecuteScryingEffectBySide ExecuteOnCovertCardSpawned
ExecuteOnDeploymentTriggered ExecuteOnDestructionEffectTriggered ResetCardInBattle SetAttackerHasAttacked
AddToTriggerQueue ResolveTriggerQueue FetchCardsByLocation GetNextCardLocationNumber
GetDestroyedCardsIDsByTurn GetHQ_DamagedAmountThisTurnBySide GetOperationKreditsSpentThisTurn
GetCardsPlayedFromHandThisTurn GetCardsPlayedFromHandLastTurn GetCardsPlayedFromHandByTurnNumber
GetUnitDestroyedThisTurn GetTotalKreditsLostThisBattle GetHandLocationBySide GetLeftMostCardInHand
GetRightMostCardInHand MoveCardInHandToLeftMost MoveCardInHandToNewIndex MoveCardToFrontline
WasLeftMostCardWhenPlayedFromHand WasRightMostCardWhenPlayedFromHand GetAllCardsPlayedThisBattle
GetAllForecastCards GetActiveGotchasOrdered Forecast IsForecastCard ApplyPincerEffects RemovePincerEffects
ApplySalvageChanges SalvageMultipleUnits RemoveSalvage SuppressMultipleUnits AddDefenseToMultipleCards
ApplyFatigueDamage ApplyGameplaySideEffect RemoveGameplaySideEffect AddCustomGameplayTag RemoveCustomGameplayTag
GetCardFromID GetAllCards GetAllUnitsOnBoard GetCardsOnBoardBySide GetCardsInHandBySide
GetDeckByside GetKreditsBySide GetMaxKreditsBySide GiveKreditsBySide ChangeKreditsBySide
ChangeKreditSlotsBySide SetKreditsAndKreditSlots ShuffleDeckBySide DrawCardsFromDeckBySide
DrawSpecificCardFromDeckBySide DrawTopCardFromDeck AdjustCardPositionInDeck SetDeckBySide
SpawnCardInDeckBySide ConstructAndCopyCard CreateCard CreateCardObject ForceCardChangeLocation
SetCardLocationAndLocNumber InjectCardIntoLocation CreateLocationNumberGapForCard RearrangeLocation
SortCardsByLocationNumber GetNewLocationNumbers RefreshLocationStatus GetLocationCardBySide
GiveRandomCombatKeyword HasBond UpdateBondVisuals SetActiveBondsAtStartOfTurn IsTopDeckNavy
OnNavalEngagementPlayed MakeCountAsTank GetUnitTypeCountOnBoard Get_X_AndMoreAttackCardsOnBoard
WhichChooseOne UpdateChooseOneActive GetTargetedCard selectTargetFromHand selectCardToDraw
KreditCheckAndAutoBanIfNeeded getKreditSlotsLostBySide getKreditsSpentThisTurn
addOperationKreditsSpentThisTurn resetOperationKreditsSpentThisTurn getCardsPlayedFromHandByTurn
updateCardsPlayedFromHandByTurn DecrementTurnGameplayRestrictions IsThereGameplayRestriction
IsCheatGameplayRestrictionActive CanPlayWeatherCard SetHasPlayedWeatherCardThisTurn
GetFatigueDamageBySide IncrementFatigueDamageBySide GetStopAttack SetStopAttack
GetStopFurtherActions SetStopFurtherActions UpdateDestroyedCardsIDs GetDestroyedCardsIDs
CanSideDrawCards CanSideGainKreditSlots CanPlayCardFromHand CanCardDoAnything CanIDoAnything
HasDeploymentSickness GiveMobilizeBonus DecrementPinnedTurnsEndTurn GetCardsPinnedThisTurn
RunStartOfGameEffects StartOfGameEffects DoOnStartOfTurn StartTurnBySide HasAttackLeft HasMovementLeft
CanMoveAndAttackInTheSameTurn CanOperateThisTurn getActiveEffects CanOtherCardBeTargetted CanSelectAsTarget
CanAttack getTotalAttack getTotalDefense getTotalHeavyArmor getTotalKreditCost getTotalOperationCost
AddToTriggerQueue ExecuteEndOfTurnQueue ExecuteEndOfTurnEvents ExecuteStartOfTurnEvents
ExecuteBeforeStartOfTurnEvents ExecuteOnAfterAttackEvents ExecuteOnCardDestroyedFunction
ExecuteOnCardDealDamageEffects ExecuteOnDrawnFromDeck ExecuteOnSpawnedInHandEvents
ExecuteOnMoveToFrontlineCardEffects ExecuteOnCardLocationMoved ExecuteOnCardMoveFromFrontline
ExecuteOnOperationKreditsSpent ExecuteOnOtherCardsAbilitiesChanged ExecuteOnAfterDeckChanged
ExecuteOnBeforeLeaveBoardOrOwnerEvents ExecuteOnAfterLeaveBoardOrOwnerEvents
ExecuteOnBeforeOtherCardDestroyed ExecuteOnEnterPlayEvents ExecuteOnMoveToFrontlineCardEffects
FetchAllCardsWithEventTrigger NotifySideEffectTrigger NotifyEffectTagTriggered
AddBuffsToRemoveEndOfTurn RemoveBuffsEndOfTurn GetBuffsToRemoveEndOfTurn
UpdateCardFunctionTriggerMap GetDeckBySide FetchCardFromCardID GetAllCardInBattle
GetFrontlineOwnerSide UpdateFrontlineOwnerSide UpdateFrontlineLimiter GetStartingSide GetPlayingSide
GetOpponentSide GetTurnNumber GetAllyNationForSide GetMainNationForSide GetClientSide""".split()

print(f'{"name":46s} {"files"::>0s}')
hits = {}
for name in CAND:
    where = []
    for f, b in blobs.items():
        n = b.count(name)
        if n:
            where.append((os.path.relpath(f, ROOT), n))
    hits[name] = where

implemented = []
missing = []
for name in CAND:
    w = hits[name]
    # ignore pure comment mentions? can't easily. Just report
    if w:
        implemented.append((name, w))
    else:
        missing.append(name)

print('=== NO OCCURRENCE ANYWHERE in src/KLink.Bot ===')
for n in missing:
    print('  ', n)
print()
print('=== present (file:count) ===')
for n, w in implemented:
    print(f'{n:46s} ' + ', '.join(f'{p}:{c}' for p, c in w))
