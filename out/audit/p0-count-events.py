import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
names = ['OnSuppressed', 'OnCardReset', 'OnCreateCard', 'OnCardDrawnFromDeck',
         'OnCardSpawnedInHand', 'OnCardLocationMoved', 'OnOtherCardLocationMoved',
         'OnBeforeOtherCardPlayedFromHand', 'OnOtherCardDealDamage']
for f in ['bp-cardfn', 'bp-logic', 'bp-gamestate', 'bp-notifier', 'bp-matchcontroller',
          'bp-onlinematch', 'bp-cardscheck']:
    s = open('out/%s.json' % f, encoding='utf-8').read()
    out = []
    for name in names:
        c = s.count('"%s"' % name)
        if c:
            out.append('%s=%d' % (name, c))
    print(f, ' '.join(out) or '(none)')
