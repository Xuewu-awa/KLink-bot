import os, json, collections, re

ROOT = r'<repo-root>'
SRC = os.path.join(ROOT, 'src', 'KLink.Bot')
files = []
for dp, dn, fn in os.walk(SRC):
    if '\\obj\\' in dp + '\\':
        continue
    for f in fn:
        if f.endswith('.cs'):
            files.append(os.path.join(dp, f))

FIELDS = """attack attackBuff defense range kredits kreditsBuff operationCost operationCostBuff
KreditsTax_AsEnemyTarget effectDamage heavyArmor heavyArmorBuff
selectTargetOnPlayedFromHand waitWithOnOtherCardPlayedUntilHandTargetSelected
isReserved isInPermanentPool campaignUpgradable showBoostIcon
hasBlitz hasAmbush hasGuard hasShock hasFury hasMobilize hasSmokescreen hasAlpine hasCovert
hasScrying hasDeployment hasDestruction hasPincer hasSalvage hasBond cipher
movementLeft attackLeft enterPlayOnTurn attackCountThisTurn pinnedTurns hasEverAttacked
hasBeenAttackedThisTurn hasAttackedThisTurn isBeingGuarded isImmune cardsGivingImmunity
underEnemyControl maxAttack maxDefense CurrentTarget isGoldCard chooseOneIndex spawnCardChosen
spawnCardName isSalvaged salvageFaction isSuppressed isRevealed customName1 customName2
customJson cardsBuffedByThisCard buffsFromCards receivedAbilitiesFromCards activeUpgrades
usedTriggers suppressionExceptionTriggers exileNation cardSeen gotchaActivated
isDraw isDiscard isRemoval isDirectDamage isHQDamage isHQRepair isRepair isKreditsBuff
isAttackBuff isDefenseBuff threat_level targetOverride skipCardMarkers cardFunction EffectType
Location side originalSide locationNumber""".split()

# 中文/语义近似名（内核可能用别的名字表示同一字段）
ALIAS = {
    'attack': ['Attack', 'Definition.Attack'],
    'defense': ['Defense'],
    'range': ['Range'],
    'kredits': ['Kredits', 'KreditCost'],
    'operationCost': ['OperationCost'],
    'heavyArmor': ['HeavyArmor'],
    'hasBlitz': ['Keyword.Blitz'],
    'hasGuard': ['Keyword.Guard'],
    'hasAmbush': ['Keyword.Ambush'],
    'hasFury': ['Keyword.Fury'],
    'hasSmokescreen': ['Keyword.Smokescreen'],
    'hasAlpine': ['Keyword.Alpine'],
    'hasMobilize': ['Keyword.Mobilize'],
    'hasSalvage': ['Keyword.Salvage'],
    'hasShock': ['Keyword.Shock'],
    'isSuppressed': ['Keyword.Suppressed'],
    'pinnedTurns': ['Keyword.Pinned'],
    'isImmune': ['Keyword.Immune'],
    'enterPlayOnTurn': ['EnteredPlayOnTurn'],
    'hasAttackedThisTurn': ['HasAttackedThisTurn'],
    'movementLeft': ['HasMovedThisTurn'],
    'attackLeft': ['HasAttackedThisTurn'],
    'isGoldCard': ['IsGold'],
    'locationNumber': ['LocationNumber'],
    'chooseOneIndex': ['ChooseOne'],
    'customJson': ['CustomJson'],
    'maxDefense': ['MaxDefense'],
}

blobs = {os.path.relpath(f, ROOT): open(f, encoding='utf-8', errors='replace').read() for f in files}

OUT = open(os.path.join(ROOT, r'out\audit\field-usage.txt'), 'w', encoding='utf-8')
def P(*a):
    print(*a, file=OUT)

P(f'{"field":52s} {"hits":>5s}  where')
for f in FIELDS:
    pats = [f] + ALIAS.get(f, [])
    rows = []
    for pat in pats:
        for path, b in blobs.items():
            for m in re.finditer(r'\b' + re.escape(pat) + r'\b', b):
                line = b.count('\n', 0, m.start()) + 1
                ctx = b[max(0, m.start() - 90):m.end() + 40].replace('\n', ' ')
                rows.append((path, line, pat, ctx))
    # dedupe by path+line
    seen = set()
    uniq = []
    for r in rows:
        if (r[0], r[1]) in seen:
            continue
        seen.add((r[0], r[1]))
        uniq.append(r)
    P(f'{f:52s} {len(uniq):5d}')
    for path, line, pat, ctx in uniq[:4]:
        P(f'      {path}:{line}  [{pat}]  ...{ctx.strip()}...')
