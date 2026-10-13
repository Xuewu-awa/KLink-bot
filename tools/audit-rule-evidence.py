"""Inventory current-pak evidence versus simulator IR without claiming semantic parity.

Usage: python tools/audit-rule-evidence.py --evidence <kards-install-review>
Reports are local; no historical replay or third-party simulator is authority.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re
import subprocess


def walk(value, path='$'):
    if isinstance(value, dict):
        yield path, value
        for key, child in value.items():
            yield from walk(child, path + '.' + str(key))
    elif isinstance(value, list):
        for i, child in enumerate(value):
            yield from walk(child, path + '[' + str(i) + ']')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    output = args.output or repo / 'out/audit/systematic-rules'
    output.mkdir(parents=True, exist_ok=True)
    manifest = {}

    def read(path):
        raw = path.read_bytes()
        manifest[str(path)] = {'sha256': hashlib.sha256(raw).hexdigest(), 'bytes': len(raw)}
        return json.loads(raw.decode('utf-8-sig'))

    definitions = read(args.evidence / 'cards.definitions.json')
    runtime = read(args.evidence / 'runtime-blueprint-bytecode-battle-20261014.json')
    ir = read(repo / 'klink bot/docs/card-ir.json')
    cards = read(repo / 'klink bot/docs/cards.json')
    scalar_dump = read(args.evidence / 'runtime-card-cdos.scalars.json')
    scalars = {r['card_id']: r['fields'] for r in scalar_dump['cards'] if r.get('is_static_asset_card')}
    scalar_fields = {'kredits': 'kredits', 'operationcost': 'operationCost',
                     'attack': 'attack', 'defense': 'defense', 'range': 'range',
                     'heavyArmor': 'heavyArmor', 'cipher': 'cipher',
                     'targetTax': 'KreditsTax_AsEnemyTarget', 'hasGuard': 'hasGuard',
                     'waitForHandTarget': 'waitWithOnOtherCardPlayedUntilHandTargetSelected'}
    scalar_differences = []
    scalar_checks = 0
    for name, fields in scalars.items():
        if name not in cards:
            scalar_differences.append({'card': name, 'issue': 'missing_database_card'})
            continue
        for key, runtime_key in scalar_fields.items():
            if key not in cards[name] or runtime_key not in fields:
                scalar_differences.append({'card': name, 'field': key, 'issue': 'missing_field'})
                continue
            scalar_checks += 1
            if cards[name][key] != fields[runtime_key]:
                scalar_differences.append({'card': name, 'field': key,
                                          'simulator': cards[name][key], 'runtime': fields[runtime_key]})
    live = defaultdict(dict)
    for fn in runtime['functions']:
        live[fn['owner_path']][fn['function_name']] = fn

    rows = []
    function_rows = []
    findings = []
    for card in definitions:
        name, asset = card['name'], card['asset']
        source = args.evidence / 'parsed' / Path(asset).with_suffix('.functions.json')
        # The extraction manifest distinguishes no function exports from a
        # missing extraction. Data-only native children have no functions file.
        no_own_functions = card.get('functions_file') is None and not card.get('functions')
        fns = read(source) if source.exists() else ({} if no_own_functions else None)
        owner = '/Game/' + asset.removeprefix('kards/Content/').removesuffix('.uasset') + '/' + card['class']
        node = ir.get(name, {})
        represented = set(node.get('entrypoints', {})) | set(node.get('locals', {}))
        missing = []
        for fn, body in (fns or {}).items():
            uber = fn.startswith('ExecuteUbergraph_')
            present = bool(node.get('steps')) if uber else fn in represented
            excluded = ('display_hint' if fn == 'ShouldHighlightInHand'
                        else 'delegate_signature' if fn.endswith('__DelegateSignature') else None)
            if not present and excluded is None:
                missing.append(fn)
            rt = live.get(owner, {}).get(fn)
            function_rows.append({'card': name, 'asset': asset, 'function': fn,
                                  'source': str(source), 'runtime_path': rt['path'] if rt else None,
                                  'runtime_sha256': rt['script_sha256'] if rt else None,
                                  'ir_representation_present': present,
                                  'scope_exclusion': excluded,
                                  'semantic_status': 'unverified'})
        issues = []
        for path, item in walk(node):
            if 'unknown' in item or item.get('op') == 'unknown':
                issues.append({'kind': 'unknown_ir', 'path': path, 'node': item})
        programs = {'ubergraph': node.get('steps', []), **node.get('locals', {})}
        for program, steps in programs.items():
            labels = [s['i'] for s in steps if 'i' in s]
            for label, count in Counter(labels).items():
                if count > 1:
                    issues.append({'kind': 'duplicate_label', 'program': program, 'label': label})
            labels = set(labels)
            for step in steps:
                if step.get('op') in ('jump','jumpIfNot','pushFlow') and step.get('to') not in labels:
                    issues.append({'kind': 'missing_branch_label', 'program': program, 'step': step})
        for fn, entry in node.get('entrypoints', {}).items():
            if entry not in {s.get('i') for s in node.get('steps', [])}:
                issues.append({'kind': 'missing_entry_label', 'function': fn, 'entry': entry})
        row = {'name': name, 'asset': asset, 'parent': card['parent'],
               'pvp_eligibility': 'unverified', 'database_present': name in cards,
               'own_ir_present': name in ir, 'static_functions_available': fns is not None,
               'no_own_function_exports': no_own_functions,
               'static_function_count': len(fns or {}), 'runtime_function_count': len(live.get(owner, {})),
               'functions_without_direct_ir': missing, 'structural_issues': issues,
               'semantic_status': 'unverified'}
        rows.append(row)
        if missing or issues or fns is None:
            findings.append(row)

    # Comments are leads only. Several historical limitation comments are stale.
    source_inventory = []
    leads = []
    for source in sorted((repo / 'src/KLink.Bot').rglob('*.cs')):
        if any(p in ('obj', 'bin') for p in source.relative_to(repo).parts):
            continue
        blob = source.read_bytes()
        lines = blob.decode('utf-8-sig').splitlines()
        relative = source.relative_to(repo).as_posix()
        source_inventory.append({'path': relative, 'lines': len(lines),
                                 'sha256': hashlib.sha256(blob).hexdigest(),
                                 'review_status': 'not_line_by_line_verified'})
        for i, line in enumerate(lines, 1):
            if re.search(r'TODO|近似|暂不|未实现|没有消费者|approximation', line, re.I):
                leads.append({'source': relative, 'line': i, 'text': line.strip(),
                              'status': 'comment_lead_not_confirmed_defect'})

    counts = {
        'static_card_assets': len(rows), 'simulator_database_cards': len(cards),
        'ir_cards': len(ir), 'card_assets_with_own_ir': sum(r['own_ir_present'] for r in rows),
        'static_card_functions': len(function_rows),
        'functions_with_runtime_match': sum(r['runtime_path'] is not None for r in function_rows),
        'functions_without_direct_ir': sum(not r['ir_representation_present'] and r['scope_exclusion'] is None for r in function_rows),
        'excluded_display_or_delegate_functions': sum(r['scope_exclusion'] is not None for r in function_rows),
        'data_only_assets_without_own_function_exports': sum(r['no_own_function_exports'] for r in rows),
        'cards_with_structural_issues': sum(bool(r['structural_issues']) for r in rows),
        'missing_static_function_files': sum(not r['static_functions_available'] for r in rows),
        'kernel_cs_files': len(source_inventory), 'kernel_cs_lines': sum(r['lines'] for r in source_inventory),
        'comment_leads': len(leads), 'semantically_verified_cards': 0}
    counts.update({'runtime_scalar_cards': len(scalars), 'runtime_scalar_comparisons': scalar_checks,
                   'runtime_scalar_differences': len(scalar_differences)})
    git_head = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    result = {'schema_version': 1, 'git_head': git_head, 'counts': counts,
              'limitations': ['Structural inventory only, not an exhaustive defect list.',
                             'PvP eligibility, native defaults, inherited behavior and semantic parity remain unverified.',
                             'Absent own IR may be intentional for data-only assets; do not treat as missing effects.',
                             'Runtime snapshot matching does not validate decoded bytecode semantics.'],
              'cards': rows, 'functions': function_rows, 'findings': findings,
              'source_inventory': source_inventory, 'comment_leads': leads, 'manifest': manifest,
              'runtime_scalar_differences': scalar_differences}
    (output / 'inventory.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    report = ['# 系统规则审计：结构基线', '', f'代码基线：`{git_head}`', '',
              '本报告不是穷尽缺陷清单，所有卡牌的语义状态保留为未验证。', '',
              '| 检查项 | 数量 |', '|---|---:|']
    report += [f'| {key} | {value} |' for key,value in counts.items()]
    report += ['', '## 直接 IR 缺失候选', '', '必须检查继承、原生默认实现和 PvP 适用范围后才能认定缺陷。', '']
    report += [f"- `{r['name']}`：" + ', '.join('`'+f+'`' for f in r['functions_without_direct_ir'])
               for r in rows if r['functions_without_direct_ir']]
    report += ['', '## 结构异常', '']
    report += [f"- `{r['name']}`：{json.dumps(r['structural_issues'], ensure_ascii=False)}" for r in rows if r['structural_issues']]
    (output / 'inventory.md').write_text('\n'.join(report)+'\n', encoding='utf-8')
    print(json.dumps(counts, indent=2))


if __name__ == '__main__':
    main()
