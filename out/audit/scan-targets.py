import json, collections, os

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))

TARGETS = ['IsVeteran', 'getTotalHeavyArmor', 'IsDamaged', 'updateCustomJsonIfNeeded',
           'GetCombatKeywords', 'GetCard', 'IsSideActive', 'GetOppositeSide', 'HasIntel',
           'isBuffedByCard', 'getHasGameplayTag', 'SetVisibility', 'HasCustomAbilityFromCard']

for t in TARGETS:
    withr = []
    without = []
    for a, p in ir.items():
        eps = p.get('entrypoints') or {}
        starts = sorted((v, k) for k, v in eps.items())
        for st in p.get('steps', []):
            if st.get('op') != 'call' or st.get('fn') != t:
                continue
            i = st.get('i', 0)
            owner = None
            for v, k in starts:
                if v <= i:
                    owner = k
            item = (a, owner, i, st.get('recv') is not None, json.dumps(st.get('args'), ensure_ascii=False)[:120])
            (withr if st.get('recv') is not None else without).append(item)
    print(f'===== {t}: recv={len(withr)} no-recv={len(without)}')
    ep = collections.Counter(x[1] for x in without)
    print('   no-recv entrypoints:', dict(ep.most_common(8)))
    for x in without[:6]:
        print(f'     {x[0]:45s} {x[1]} i={x[2]} args={x[4]}')
