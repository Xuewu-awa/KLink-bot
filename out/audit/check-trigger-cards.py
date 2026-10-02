import json, os, collections
ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
for t in ['TriggerDestruction', 'TriggerDeployment', 'ApplyPincerEffects', 'RemovePincerEffects',
          'GiveAlpineBonus', 'ExecuteScryingEffectBySide', 'SalvageMultipleUnits', 'ApplySalvageChanges']:
    cards = set()
    eps = collections.Counter()
    for a, p in ir.items():
        starts = sorted((v, k) for k, v in (p.get('entrypoints') or {}).items())
        for st in p.get('steps', []):
            if st.get('op') == 'call' and st.get('fn') == t:
                cards.add(a)
                i = st.get('i', 0)
                o = None
                for v, k in starts:
                    if v <= i:
                        o = k
                eps[o] += 1
    print(f'{t}: cards={len(cards)} calls={sum(eps.values())} eps={dict(eps.most_common(4))}')
