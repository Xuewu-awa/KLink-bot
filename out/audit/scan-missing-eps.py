import json, collections, re, os

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))

# gameplay entrypoint whitelist (non-UI)
UI = re.compile(r'(Timeline|Anim|Hover|Mouse|Actor|Widget|Fly|Fade|Showcase|Rarity|Render|Carousel|'
                r'BndEvt|Touch|Drag|Popup|Help|Tutorial|Mobile|Camera|Sound|Glow|Scale|Opacity|'
                r'Viewport|Queue|Text|Color|Icon|Wildcard|Look|Redraw|Mulligan|Spectat|Socket|'
                r'Debug|Tick|Construct|Placeholder|PageFlip|Rotation|Shake|VFX|Cardback|Slot|Bar|'
                r'Layout|Focus|Recycle|Craft|Achievement|Daily|Mission|Preview|Init|Load|Setup|'
                r'Select|Clicked|Tap|Unblock|Block|Highlight|Set|Show|Hide|Update|Create|Draw)')

# per fn: cards, and per entrypoint: call count
fn_cards = collections.defaultdict(set)
fn_ep = collections.defaultdict(collections.Counter)
for asset, prog in ir.items():
    eps = prog.get('entrypoints') or {}
    starts = sorted((v, k) for k, v in eps.items())
    steps = sorted(prog.get('steps', []), key=lambda s: s.get('bo', 0))
    for st in steps:
        if st.get('op') != 'call':
            continue
        fn = st.get('fn')
        if fn in dispatched:
            continue
        fn_cards[fn].add(asset)
        i = st.get('i', 0)
        # which entrypoint owns this statement: largest entry <= i
        owner = None
        for v, k in starts:
            if v <= i:
                owner = k
        fn_ep[fn][owner or '<none>'] += 1

rows = []
for fn, cards in fn_cards.items():
    eps = fn_ep[fn]
    gp = {e: n for e, n in eps.items() if e and not UI.search(e)}
    rows.append((fn, len(cards), sum(eps.values()), gp, eps))

rows.sort(key=lambda r: -r[1])
out = open(os.path.join(ROOT, r'out\audit\missing-calls-by-entrypoint.txt'), 'w', encoding='utf-8')
def P(*a): print(*a, file=out)

P('=== 未进派发表的原语：按卡数排序，并标出它出现在哪些入口点 ===')
for fn, nc, ncall, gp, eps in rows:
    if nc < 1:
        continue
    gpstr = ', '.join(f'{k}({v})' for k, v in sorted(gp.items(), key=lambda x: -x[1])[:6])
    P(f'{fn:48s} cards={nc:4d} calls={ncall:4d}  gameplay-eps: {gpstr or "(none — 只在 UI/战役入口)"}')
    if nc <= 3:
        P(f'{"":48s} all-eps: ' + ', '.join(f'{k}({v})' for k, v in eps.most_common(8)))

out.close()
print('written')
