import json, collections, re, os

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))

# --- dispatch table from CardApiDispatch.cs ---
src = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\CardApiDispatch.cs'), encoding='utf-8').read()
dispatched = set(re.findall(r'^\s*\["([A-Za-z_0-9]+)"\]\s*=', src, re.M))
print('dispatch entries:', len(dispatched))

# also names handled elsewhere (KismetVm math/builtins)
vm = open(os.path.join(ROOT, r'src\KLink.Bot\Effects\Blueprint\KismetVm.cs'), encoding='utf-8').read()
mathops = set(re.findall(r'"([A-Za-z_0-9]+)"\s*=>', vm))

callcards = collections.defaultdict(set)
callcount = collections.Counter()
localcards = collections.defaultdict(set)
ops = collections.Counter()
for asset, prog in ir.items():
    for st in prog.get('steps', []):
        ops[st.get('op')] += 1
        if st.get('op') == 'call':
            fn = st.get('fn')
            callcards[fn].add(asset)
            callcount[fn] += 1
    for loc in (prog.get('locals') or {}):
        localcards[loc].add(asset)

print('ops:', dict(ops))
print('distinct call fns:', len(callcards))
print('distinct locals:', len(localcards))
print()
print('=== locals (card-private functions) ===')
for k, v in sorted(localcards.items(), key=lambda x: -len(x[1])):
    print(f'{k:50s} cards={len(v)}')

print()
print('=== call fns NOT in dispatch table ===')
miss = [(k, len(v), callcount[k]) for k, v in callcards.items() if k not in dispatched]
for k, nc, ncall in sorted(miss, key=lambda x: -x[1]):
    print(f'{k:55s} cards={nc:5d} calls={ncall}')
