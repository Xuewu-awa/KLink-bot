import json, collections, os
ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))

T = ['GetUnitTypeCountOnBoard', 'GetCardsOnBoardBySide', 'GetCardsInHandBySide', 'GetAdjacentCards',
     'GetRandomCard', 'DrawSpecificCardFromDeckBySide', 'DrawCardsFromDeckBySide',
     'SpawnCardInDeckBySide', 'SpawnCardInHandBySide', 'SpawnCardOnBattlefield',
     'GiveKreditsBySide', 'GetCardsToTheLeft', 'GetCardsToTheRight', 'IsSameSideUnit',
     'DamageCard', 'ChangeAttack', 'ChangeDefense', 'ChangeHeavyArmor', 'ChangeKreditCost',
     'ChangeOperationCost', 'HealCard', 'DestroyCard', 'RemoveCardFromBoard', 'GainKreditSlot',
     'GetLocationCardBySide', 'GetDestroyedCardsCountBySide', 'GetDestroyedCardsIDsThisBattle',
     'GetAllCardsOnBoard', 'GetAllUnitsOnBoard', 'GetAllCards', 'GetAllActiveStaticCards',
     'PinUnit', 'SuppressUnit', 'MakeVeteran', 'GetCardsPlayedThisTurn', 'GetMaxKreditsBySide',
     'GetKreditsBySide', 'IsCardReserved', 'getHasGameplayTag', 'GetTargetedCard', 'GetCard',
     'selectCardToDraw', 'selectTargetFromHand', 'Forecast', 'JSON_GetInt', 'JSON_SetInt',
     'GetCombatKeywords', 'HasCustomAbility', 'HasCustomAbilityFromCard', 'IsVeteran',
     'isBuffedByCard', 'getTotalHeavyArmor', 'IsDamaged', 'GetPlayFromHandDamage']

for t in T:
    shapes = collections.Counter()
    ex = {}
    for a, p in ir.items():
        for st in p.get('steps', []):
            if st.get('op') != 'call' or st.get('fn') != t:
                continue
            key = (len(st.get('args') or []), st.get('recv') is not None)
            shapes[key] += 1
            ex.setdefault(key, (a, json.dumps(st, ensure_ascii=False)[:260]))
    if not shapes:
        print(f'===== {t}: 未出现')
        continue
    print(f'===== {t}')
    for k, n in sorted(shapes.items()):
        print(f'   args={k[0]} recv={k[1]}  x{n}   e.g. {ex[k][0]}  {ex[k][1]}')
