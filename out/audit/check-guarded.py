import json, os, re
ROOT = r'<repo-root>'
d = json.load(open(os.path.join(ROOT, r'out\bp-cardfn.json'), encoding='utf-8'))
v = d.get('UpdateGuarded')
print('keys:', list(v.keys()) if isinstance(v, dict) else type(v))
bc = v['bytecode']
print('statements:', len(bc))
for st in bc:
    i = st.get('StatementIndex')
    s = json.dumps(st, ensure_ascii=False)
    # 只打印含关键字的语句
    if re.search(r'GetAdjacentCards|getHasGuard|isBeingGuarded|IsUnrevealedCovertCard|GetCardsToTheLeft|GetCardsToTheRight|isBeingGuarded|Guarded|ChangeDefense|JSON_', s):
        print(f'i={i}  {s[:600]}')
