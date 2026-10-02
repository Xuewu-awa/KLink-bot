import json, collections, re, os

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))

hdr = open(r'E:\peoject\kards\Source\kards\Public\BaseCardObject.h', encoding='utf-8', errors='replace').read()
native_events = set(re.findall(r'\n\s*void\s+(On[A-Za-z_0-9]+)\s*\(', hdr))
fired = set(open(os.path.join(ROOT, r'out\audit\fired.txt'), encoding='utf-8').read().split()) if os.path.exists(os.path.join(ROOT, r'out\audit\fired.txt')) else set()

# 只统计「玩法入口点」里的调用
UI = re.compile(r'(Timeline|Anim|Hover|Mouse|Actor|Widget|Fly|Fade|Showcase|Rarity|Render|Carousel|'
                r'BndEvt|Touch|Drag|Popup|Help|Tutorial|Mobile|Camera|Sound|Glow|Scale|Opacity|'
                r'Viewport|Queue|Text|Color|Icon|Wildcard|Look|Redraw|Mulligan|Spectat|Socket|'
                r'Debug|Tick|Construct|Placeholder|PageFlip|Rotation|Shake|VFX|Cardback|Slot|Bar|'
                r'Layout|Focus|Recycle|Craft|Achievement|Daily|Mission|Preview|Init|Load|Setup|'
                r'Select|Clicked|Tap|Unblock|Block|Highlight|Set|Show|Hide|Update|Create|Draw)')

cards_missing_prim = set()
cards_missing_trig = set()
cards_all = set()
per_card_missing = collections.Counter()
for a, p in ir.items():
    if a.startswith('BP_') or a.startswith('WBP_') or a.startswith('U_'):
        continue
    cards_all.add(a)
    eps = p.get('entrypoints') or {}
    starts = sorted((v, k) for k, v in eps.items())
    hit_trig = False
    for e in eps:
        if e in native_events and e != 'OnPlayedFromHand' and e not in fired:
            hit_trig = True
    if hit_trig:
        cards_missing_trig.add(a)
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
        if owner is None or UI.search(owner):
            continue
        cards_missing_prim.add(a)
        per_card_missing[a] += 1

print('IR 里的卡资产:', len(cards_all))
print('调用了「未进派发表的原语」(玩法入口):', len(cards_missing_prim))
print('订阅了「内核从不派发的事件」:', len(cards_missing_trig))
print('两者并集:', len(cards_missing_prim | cards_missing_trig))
print()
print('每个卡平均缺几个原语:', round(sum(per_card_missing.values()) / max(1, len(per_card_missing)), 1))
print('缺原语最多的 10 张卡:')
for a, n in per_card_missing.most_common(10):
    print(f'   {a:48s} {n}')
