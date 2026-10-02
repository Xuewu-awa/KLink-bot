import json, re, os, collections

ROOT = r'<repo-root>'
hdr = open(r'<kards-src>\Source\kards\Public\BaseCardObject.h', encoding='utf-8', errors='replace').read()
ev = sorted(set(re.findall(r'\n\s*void\s+(On[A-Za-z_0-9]+)\s*\(', hdr)))
ir = json.load(open(os.path.join(ROOT, 'klink bot/docs/card-ir.json'), encoding='utf-8'))
subs = collections.Counter()
for a, p in ir.items():
    for e in (p.get('entrypoints') or {}):
        subs[e] += 1
fired = {'OnAfterAttack', 'OnAfterOtherCardAttacks', 'OnAfterOtherCardLeaveBoardOrOwner',
         'OnBecomingVeteran', 'OnBeforeAttack', 'OnBeforeDestroyed', 'OnBeforeOtherCardAttacks',
         'OnDestroyed', 'OnEndOfTurn', 'OnEnterPlay', 'OnFrontlineOwnershipChange',
         'OnHandTargetSelected', 'OnLeaveBoardOrOwner', 'OnMoveToFrontline', 'OnOtherCardDestroyed',
         'OnOtherCardDrawnFromDeck', 'OnOtherCardEnterPlay', 'OnOtherCardLeaveBoardOrOwner',
         'OnOtherCardMoveToFrontline', 'OnOtherCardPlayedFromHand', 'OnOtherCardReceiveDamage',
         'OnOtherCardSpawnedInHand', 'OnReceiveDamage', 'OnStartOfGame', 'OnStartOfTurn',
         'OnPlayedFromHand'}
rows = [(subs.get(e, 0), e) for e in ev if e not in fired]
rows.sort(reverse=True)
L = ['| 事件（原生 `BaseCardObject.h`） | 订阅卡数 |', '|---|---|']
for n, e in rows:
    if n == 0:
        continue
    L.append('| `%s` | %d |' % (e, n))
zero = [e for n, e in rows if n == 0]
L.append('')
L.append('订阅数为 0（卡蓝图一次都没实现）的事件共 %d 个：%s' % (len(zero), ', '.join('`%s`' % z for z in zero)))
open(os.path.join(ROOT, r'out\audit\appendix-a.md'), 'w', encoding='utf-8').write('\n'.join(L))
print('A rows with subs:', len([r for r in rows if r[0] > 0]), 'zero:', len(zero))

# 附录 B：未进派发表的原语（玩法入口）
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))
UI = re.compile(r'(Timeline|Anim|Hover|Mouse|Actor|Widget|Fly|Fade|Showcase|Rarity|Render|Carousel|'
                r'BndEvt|Touch|Drag|Popup|Help|Tutorial|Mobile|Camera|Sound|Glow|Scale|Opacity|'
                r'Viewport|Queue|Text|Color|Icon|Wildcard|Look|Redraw|Mulligan|Spectat|Socket|'
                r'Debug|Tick|Construct|Placeholder|PageFlip|Rotation|Shake|VFX|Cardback|Slot|Bar|'
                r'Layout|Focus|Recycle|Craft|Achievement|Daily|Mission|Preview|Init|Load|Setup|'
                r'Select|Clicked|Tap|Unblock|Block|Highlight|Set|Show|Hide|Update|Create|Draw)')
cards = collections.defaultdict(set)
calls = collections.Counter()
for a, p in ir.items():
    eps = p.get('entrypoints') or {}
    starts = sorted((v, k) for k, v in eps.items())
    for st in p.get('steps', []):
        if st.get('op') != 'call':
            continue
        fn = st.get('fn')
        if fn in dispatched:
            continue
        i = st.get('i', 0)
        owner = None
        for v, k in starts:
            if v <= i:
                owner = k
        if owner is None or UI.search(owner) or owner.startswith('Campaign'):
            continue
        cards[fn].add(a)
        calls[fn] += 1
L = ['| 原语 | 卡数 | 调用点 |', '|---|---|---|']
for fn, cs in sorted(cards.items(), key=lambda x: -len(x[1])):
    L.append('| `%s` | %d | %d |' % (fn, len(cs), calls[fn]))
open(os.path.join(ROOT, r'out\audit\appendix-b.md'), 'w', encoding='utf-8').write('\n'.join(L))
print('B rows:', len(cards))
