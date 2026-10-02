import json, collections, sys

cards = json.load(open(r'<repo-root>\out\cards-full2.json', encoding='utf-8'))
print('cards:', len(cards))

FIELDS = """attack attackBuff defense range kredits kreditsBuff operationCost operationCostBuff
KreditsTax_AsEnemyTarget effectDamage heavyArmor heavyArmorBuff
selectTargetOnPlayedFromHand waitWithOnOtherCardPlayedUntilHandTargetSelected
isReserved isInPermanentPool campaignUpgradable showBoostIcon
hasBlitz hasAmbush hasGuard hasShock hasFury hasMobilize hasSmokescreen hasAlpine hasCovert
hasScrying hasDeployment hasDestruction hasPincer hasSalvage
movementLeft attackLeft enterPlayOnTurn attackCountThisTurn pinnedTurns
isImmune underEnemyControl isBeingGuarded isSuppressed isRevealed isSalvaged isGoldCard
maxAttack maxDefense cardsBuffedByThisCard buffsFromCards cardsGivingImmunity
receivedAbilitiesFromCards activeUpgrades usedTriggers suppressionExceptionTriggers
spawnCardName spawnCardChosen chooseOneCards chooseOneIndex customName1 customName2
customJson exileNation cipher gotchaActivated cardSeen hasEverAttacked hasAttackedThisTurn
hasBeenAttackedThisTurn isDraw isDiscard isRemoval isDirectDamage isHQDamage isHQRepair
isRepair isKreditsBuff isAttackBuff isDefenseBuff threat_level targetOverride skipCardMarkers
cardFunction EffectType""".split()

# default by type
BOOL_DEFAULT = False
INT_DEFAULT = 0

rows = []
for f in FIELDS:
    nondef = 0
    vals = collections.Counter()
    for c in cards:
        raw = c.get('raw') or {}
        if f not in raw:
            vals['<absent>'] += 1
            continue
        v = raw[f]
        vals[repr(v) if not isinstance(v, (dict, list)) else ('list[%d]' % len(v) if isinstance(v, list) else 'dict')] += 1
        if isinstance(v, bool):
            if v: nondef += 1
        elif isinstance(v, (int, float)):
            if v != 0: nondef += 1
        elif isinstance(v, str):
            if v != '': nondef += 1
        elif isinstance(v, list):
            if len(v) > 0: nondef += 1
        elif isinstance(v, dict):
            if len(v) > 0: nondef += 1
        elif v is None:
            pass
    top = [f'{k}x{n}' for k, n in vals.most_common(4)]
    rows.append((f, nondef, ' | '.join(top)))

print(f'{"field":52s} {"nondef":>6s}  top values')
for f, n, t in rows:
    print(f'{f:52s} {n:6d}  {t}')
