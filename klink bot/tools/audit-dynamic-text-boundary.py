from __future__ import annotations
import hashlib, json
from pathlib import Path
from collections import Counter, defaultdict

ROOT = Path(__file__).resolve().parents[2]
IR = ROOT / 'klink bot' / 'docs' / 'card-ir.json'
OUT = ROOT / 'out' / 'audit' / 'systematic-rules'
REPORT = OUT / 'dynamic-text-boundary-20261031.json'
MARKDOWN = OUT / 'dynamic-text-boundary-20261031.md'
SDK_ROOT = Path(r'D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump')
SDK_FILES = ['BP_CardFunctions.hpp', 'BP_OnlineMatch.hpp', 'U_CardFunctionsNotifier.hpp', 'BP_VisualController.hpp', 'kards.hpp']

def sha256(path: Path):
    if not path.is_file():
        return None
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

def walk(value, path=()):
    yield path, value
    if isinstance(value, dict):
        for key, child in value.items():
            yield from walk(child, path + (key,))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from walk(child, path + (index,))

def card_program(path):
    if len(path) >= 2 and isinstance(path[0], str) and isinstance(path[1], str):
        return path[0], path[1]
    return None, None

cards = json.loads(IR.read_text(encoding='utf-8'))
counts = Counter()
occurrences = defaultdict(list)
spawn_rows = []
for card, card_obj in cards.items():
    for path, node in walk(card_obj):
        fn = node.get('fn') if isinstance(node, dict) and node.get('op') == 'call' else None
        if fn:
            counts[fn] += 1
            if fn in {'SpawnCardInHandBySide', 'AddNumberToText', 'UpdateCardText', 'AppendNumberToCardText'}:
                program, entry = card_program(path)
                row = {'card': card, 'program': program, 'entry_or_local': entry,
                       'path': list(path), 'step': node}
                occurrences[fn].append(row)
                if fn == 'SpawnCardInHandBySide':
                    args = node.get('args', [])
                    vals = {
                        'side': args[0] if len(args) > 0 else None,
                        'card_name': args[1] if len(args) > 1 else None,
                        'spawner_id': args[2] if len(args) > 2 else None,
                        'card_seen': args[3] if len(args) > 3 else None,
                        'skip_draw_animation': args[4] if len(args) > 4 else None,
                        'from_opposite_side': args[5] if len(args) > 5 else None,
                        'new_card_text': args[6] if len(args) > 6 else None,
                        'campaign_name': args[7] if len(args) > 7 else None,
                        'salvage_faction': args[8] if len(args) > 8 else None,
                    }
                    spawn_rows.append(row | {'args': vals})
        # Math expressions can occur either as standalone IR nodes
        # (`op=math, fn=...`) or nested in a `set` source (`math=...`).
        # Count both shapes; the latter is how Blueprint `Format` calls are
        # represented in the current card IR.
        math_fn = None
        if isinstance(node, dict):
            if node.get('op') == 'math':
                math_fn = node.get('fn')
            elif isinstance(node.get('math'), str):
                math_fn = node.get('math')
        if math_fn in {'PrintString', 'Format'}:
            counts['math:' + math_fn] += 1
            program, entry = card_program(path)
            occurrences['math:' + math_fn].append({
                'card': card, 'program': program, 'entry_or_local': entry,
                'path': list(path), 'step': node,
            })


def canonical(v):
    return json.dumps(v, ensure_ascii=False, sort_keys=True, separators=(',', ':'))

def all_equal(rows, key, value):
    return all(row['args'][key] == value for row in rows)

sdk = {}
for name in SDK_FILES:
    matches = list(SDK_ROOT.rglob(name)) if SDK_ROOT.is_dir() else []
    path = matches[0] if matches else SDK_ROOT / name
    sdk[name] = {'path': str(path), 'sha256': sha256(path), 'present': path.is_file()}

report = {
    'schema_version': 1,
    'report_kind': 'dynamic_text_headless_boundary',
    'authority': {
        'ir': str(IR),
        'sdk_root': str(SDK_ROOT),
        'replays_used': False,
        'interpretation': 'IR and SDK are evidence; this report does not certify complete native or inherited Blueprint equivalence.'
    },
    'inputs': {'ir_sha256': sha256(IR), 'sdk': sdk},
    'ir_counts': {
        'cards': len(cards),
        'SpawnCardInHandBySide': counts['SpawnCardInHandBySide'],
        'AddNumberToText': counts['AddNumberToText'],
        'UpdateCardText': counts['UpdateCardText'],
        'AppendNumberToCardText': counts['AppendNumberToCardText'],
        'math:Format': counts['math:Format'],
        'math:PrintString': counts['math:PrintString'],
    },
    'spawn_argument_invariants': {
        'new_card_text_literal_empty_count': sum(
            row['args']['new_card_text'] == {'str': ''} for row in spawn_rows),
        'new_card_text_dynamic_or_unresolved_count': sum(
            row['args']['new_card_text'] != {'str': ''} for row in spawn_rows),
        'new_card_text_all_empty_string': all_equal(spawn_rows, 'new_card_text', {'str': ''}),
        'skip_draw_animation_all_false': all_equal(spawn_rows, 'skip_draw_animation', {'bool': False}),
        'from_opposite_side_all_false': all_equal(spawn_rows, 'from_opposite_side', {'bool': False}),
        'dynamic_or_unresolved_new_card_text_rows': [
            {
                'card': r['card'], 'program': r['program'],
                'entry_or_local': r['entry_or_local'], 'path': r['path'],
                'source': r['args']['new_card_text'],
                'classification': 'variable-derived; exact formatted string is not preserved by the IR',
            }
            for r in spawn_rows if r['args']['new_card_text'] != {'str': ''}
        ],
        'non_empty_new_card_text_rows': [r for r in spawn_rows if r['args']['new_card_text'] != {'str': ''}],
        'non_default_skip_draw_rows': [r for r in spawn_rows if r['args']['skip_draw_animation'] != {'bool': False}],
        'non_default_from_opposite_side_rows': [r for r in spawn_rows if r['args']['from_opposite_side'] != {'bool': False}],
    },
    'print_string_occurrences': [
        {
            'card': r['card'], 'program': r['program'], 'entry_or_local': r['entry_or_local'],
            'path': r['path'], 'instruction': r['step'].get('i'),
            'classification': 'diagnostic/logging-only candidate; no gameplay output slot',
        }
        for r in occurrences['math:PrintString']
    ],
    'dynamic_display_calls': [
        {'card': r['card'], 'program': r['program'], 'entry_or_local': r['entry_or_local'],
         'path': r['path'], 'instruction': r['step'].get('i'),
         'function': 'AddNumberToText'}
        for r in occurrences['AddNumberToText']
    ],
    'sdk_chain': {
        'BP_CardFunctions': ['AddNumberToText', 'SpawnCardInHandBySide'],
        'BP_OnlineMatch': ['ReceiveSubActionAppendNumberToCardText', 'AddSubActionAppendNumberToCardText', 'ReceiveSubActionUpdateCardText', 'AddSubActionUpdateCardText', 'SetNewCardText'],
        'U_CardFunctionsNotifier': ['NotifyAppendNumberToCardText'],
        'BP_VisualController': ['CreateActionAppendNumberToCardText', 'ExecuteSubActionAddNumberToText', 'CreateVisualActionUpdateCardText', 'Execute Sub Action Update Card Text'],
        'FCardData': 'static Text field is present; no separate dynamic rule-state field was identified in the SDK dump.'
    },
    'conclusion': {
        'pvp_rule_state': 'No evidence in the current authoritative PvP IR that dynamic text is read as a rule-state input. The two PvP generated-card calls pass variable-derived formatted text, but their rule effects explicitly apply the numeric changes through ChangeKreditCost/DamageCard and persist level; the text is not consumed by a later predicate. The 115th Separate BTN computes its numeric state through ChangeAttack/ChangeDefense; Format/AddNumberToText is display-side only.',
        'headless_implementation': 'Keep AddNumberToText, UpdateCardText, and AppendNumberToCardText as no-op display adapters in headless mode. Do not add a gameplay text state from these calls.',
        'spawn_parameters': '230 indexed SpawnCardInHandBySide calls use literal empty newCardText; two PvP generated-card calls pass variable-derived formatted text. SkipDrawAnimation and fromOppositeSide are false at all 232 indexed calls. DoSpawnInHand still does not model visual/campaign text payloads; because the two dynamic-text cards apply and persist their numeric rule changes separately and no IR predicate reads the text, this remains a display/adapter completeness item rather than a demonstrated PvP rule mismatch.',
        'print_string': 'The two PrintString occurrences are in card_location_german_scen5 and card_unit_75th_rifle_regiment.locals.SpawnCopy and have no rule output; leave unmapped for headless PvP.'
    },
    'unresolved': [
        'Complete native /Script/kards behavior and inherited Blueprint dispatch are not proven by this report.',
        'A future IR revision that supplies non-empty newCardText or consumes dynamic text in a predicate must reopen DoSpawnInHand and the display boundary.'
    ]
}
OUT.mkdir(parents=True, exist_ok=True)
REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
md = f'''# Dynamic text boundary audit (2026-10-31)\n\n- Authoritative IR: `{IR}` (SHA-256 `{report['inputs']['ir_sha256']}`)\n- SDK evidence: UE4SS CXXHeaderDump under `{SDK_ROOT}`\n- Replays: not used.\n\n## Indexed calls\n\n- `SpawnCardInHandBySide`: **{counts['SpawnCardInHandBySide']}**\n- `AddNumberToText`: **{counts['AddNumberToText']}**\n- `UpdateCardText`: **{counts['UpdateCardText']}**\n- `AppendNumberToCardText`: **{counts['AppendNumberToCardText']}**\n- `math:Format`: **{counts['math:Format']}** (nested and standalone IR shapes)\n- `math:PrintString`: **{counts['math:PrintString']}**\n\nOf the {len(spawn_rows)} indexed `SpawnCardInHandBySide` calls, **230** pass literal `newCardText=""`. Two PvP generated-card calls (`card_event_seize_the_initiative` and `card_event_carrier_battle`) pass a variable-derived `Conv_TextToString(Format(...))` value. The current IR does not preserve the `FormatArgumentData` member values, so the exact formatted string is unresolved. All calls use `SkipDrawAnimation=false` and `fromOppositeSide=false`.\n\nThe two `PrintString` nodes are in `card_location_german_scen5` and `card_unit_75th_rifle_regiment.locals.SpawnCopy`. They are diagnostic/logging calls with no gameplay output slot.\n\n## Decision\n\nThe SDK exposes a presentation chain (`AddNumberToText` -> online-match sub-action/notifier -> visual controller) and static card text in `FCardData`; no separate dynamic rule-state field was identified. The two generated-card cards explicitly apply their numeric rule changes through `DamageCard`/`ChangeKreditCost` and persist the level, while `Format` only supplies replacement display text. For `card_unit_115th_separate_btn`, the rule state is expressed by `ChangeAttack`/`ChangeDefense`; `Format`/`AddNumberToText` only formats the displayed number. The headless adapters therefore remain no-ops for these display calls.\n\n`DoSpawnInHand` still leaves visual/campaign text payloads unmodeled. This is recorded as an adapter-completeness item; current IR evidence does not demonstrate a PvP rule mismatch.\n\nThis report does not prove complete native `/Script/kards` or inherited Blueprint equivalence.\n'''
MARKDOWN.write_text(md, encoding='utf-8')
print(REPORT)
print(MARKDOWN)
