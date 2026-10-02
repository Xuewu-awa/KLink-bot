import json, re, os, collections

ROOT = r'<repo-root>'
ir = json.load(open(os.path.join(ROOT, r'klink bot\docs\card-ir.json'), encoding='utf-8'))
srcs = []
for f in ['Engine/MatchEngine.cs', 'Effects/CardApi.cs', 'Effects/CardApiDispatch.cs',
          'Effects/Blueprint/KismetVm.cs', 'Effects/CardEffectScripts.cs', 'Replay/ReplayRunner.cs',
          'Bots/GreedyBot.cs', 'Engine/GameState.cs', 'Engine/CardInstance.cs']:
    srcs.append(open(os.path.join(ROOT, 'src/KLink.Bot', f), encoding='utf-8').read())
blob = '\n'.join(srcs)

fired = set(re.findall(r'"(On[A-Za-z_0-9]+)"', blob))
print('names appearing as string literals in kernel:', len(fired))

eps = collections.Counter()
for a, p in ir.items():
    for e in (p.get('entrypoints') or {}):
        eps[e] += 1

# gameplay heuristic: exclude obvious UI/animation
UI_PAT = re.compile(r'Timeline|Anim|Hover|Mouse|Actor|Widget|Fly|Fade|Showcase|Rarity|Render|Carousel|'
                    r'BndEvt|Touch|Drag|Popup|CardHelp|HelpBubble|Tutorial|Mobile|Camera|Sound|Play|'
                    r'Glow|Scale|Opacity|Viewport|Pop|Queue|Text|Color|Icon|Wildcard|Deck.*Look|'
                    r'Redraw|Mulligan|Spectat|Socket|Debug|Tick|Initialize|Construct|PreConstruct|'
                    r'DestroyPlaceholder|Placeholder|PageFlip|Rotation|Shake|SpawnFly|ConvertVFX|'
                    r'SetCardback|Slot|Bar|Bubble|Layout|Focus|Recycle|Craft|Achievement|Daily|Mission')

gp = {e: n for e, n in eps.items() if e.startswith('On') or e.startswith('Campaign') or e in (
    'CustomEvent', 'ReceiveDestroyed', 'ReceiveEndPlay', 'DamageCard', 'HealCard', 'LoseAttack',
    'SetDefense', 'GainAttack', 'GainDefense', 'OperationCostChanged', 'AddToTriggerQueue',
    'ForceEndTurn', 'GiveStarForCampaign', 'IncrementObjectiveCounter', 'ResolveTriggerQueue',
    'ShowCampaignMessage', 'UpdateCampaignStarStatus', 'ReportKreditTampering', 'ReportTampering',
    'GiveCredits', 'IncOpCountAndCheckVeteran', 'SpawnCard', 'CreateCard', 'PlayCardFromHand')}

covered, uncovered = [], []
for e, n in sorted(gp.items(), key=lambda x: -x[1]):
    (covered if e in fired else uncovered).append((e, n))

print(f'\n=== gameplay-ish entrypoints: {len(gp)}  fired: {len(covered)}  not fired: {len(uncovered)} ===')
print('\n--- FIRED (name appears in kernel) ---')
for e, n in covered:
    print(f'  {n:5d}  {e}')
print('\n--- NOT FIRED ---')
tot = 0
for e, n in uncovered:
    if n >= 2:
        print(f'  {n:5d}  {e}')
    tot += n
print(f'\n(single-subscriber not-fired entries omitted above; total card-entrypoint subscriptions not fired = {tot})')
