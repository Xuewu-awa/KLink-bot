import json, collections, re, os

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))

UI = re.compile(r'(Timeline|Anim|Hover|Mouse|Actor|Widget|Fly|Fade|Showcase|Rarity|Render|Carousel|'
                r'BndEvt|Touch|Drag|Popup|Help|Tutorial|Mobile|Camera|Sound|Glow|Scale|Opacity|'
                r'Viewport|Queue|Text|Color|Icon|Wildcard|Look|Redraw|Mulligan|Spectat|Socket|'
                r'Debug|Tick|Construct|Placeholder|PageFlip|Rotation|Shake|VFX|Cardback|Slot|Bar|'
                r'Layout|Focus|Recycle|Craft|Achievement|Daily|Mission|Preview|Init|Load|Setup|'
                r'Select|Clicked|Tap|Unblock|Block|Highlight|Set|Show|Hide|Update|Create|Draw)')
CAMPAIGN = re.compile(r'^Campaign')

fn_cards = collections.defaultdict(set)
fn_ep = collections.defaultdict(collections.Counter)
for asset, prog in ir.items():
    eps = prog.get('entrypoints') or {}
    starts = sorted((v, k) for k, v in eps.items())
    for st in sorted(prog.get('steps', []), key=lambda s: s.get('bo', 0)):
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
        if owner is None:
            continue                      # ubergraph 前导块：内核从 entrypoints 起跳，不会执行
        if UI.search(owner) or CAMPAIGN.search(owner):
            continue
        fn_cards[fn].add(asset)
        fn_ep[fn][owner] += 1

out = open(os.path.join(ROOT, r'out\audit\missing-calls-gameplay.txt'), 'w', encoding='utf-8')
def P(*a): print(*a, file=out)
P('=== 只在「玩法入口点」被调用、但没进内核派发表的原语（已排除 UI/战役/前导块）===')
P(f'{"原语":46s} {"卡数":>5s} {"调用":>5s}  出现的入口点')
rows = sorted(fn_cards.items(), key=lambda x: -len(x[1]))
for fn, cards in rows:
    eps = fn_ep[fn]
    P(f'{fn:46s} {len(cards):5d} {sum(eps.values()):5d}  ' +
      ', '.join(f'{k}({v})' for k, v in eps.most_common(6)))
out.close()
print('rows', len(rows), 'written')
