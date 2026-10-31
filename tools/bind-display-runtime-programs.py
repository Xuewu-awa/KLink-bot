"""Audit-only binding of reviewed IR sites to C# rebuilt program exports.

No output is consumed by admission. A successful match is provenance, never an
allowlist or a proof that an event reaches every instruction in its container.
"""
import argparse
import base64
import hashlib
import importlib.util
import json
from pathlib import Path


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def require(ok, message):
    if not ok:
        raise ValueError(message)


def bind(classification, export, cards):
    require(export['schema'] == 'display-runtime-program-export/v1'
            and export['hashSchema'] == 'kismet-program-json/v1', 'export schema')
    require(export['irSha256'] == classification['ir_sha256'], 'IR binding')
    require(export['admissionExempt'] is False, 'export exemption forbidden')
    require(export['omitted'] == 0 and export['captureFailures'] == 0, 'incomplete capture')
    reviewed = [r for r in classification['sites'] if r['classification'].startswith('reviewed_')]
    require(len(reviewed) == 5 and all(r['admission_exempt'] is False for r in reviewed), 'review scope')
    assets = {r['card'] for r in reviewed}
    expected = {(a, n, 'steps') for a in assets for n in cards[a]['entrypoints']}
    expected |= {(a, n, n) for a in assets for n in cards[a].get('locals', {})}
    programs = {}
    for row in export['programs']:
        key = row['asset'], row['function'], row['container']
        require(key in expected and key not in programs, 'unexpected or duplicate program')
        raw = base64.b64decode(row['serializedProgramBase64'], validate=True)
        require(sha(raw) == row['runtimeProgramSha256'], 'runtime hash mismatch')
        p = json.loads(raw)
        asset, name, container = key
        require((p['AssetName'], p['FunctionName']) == (asset, name), 'program identity')
        source = cards[asset]['entrypoints'][name] if container == 'steps' else cards[asset]['locals'][name][0]['i']
        require(p['SourceEntry'] == source, 'source entry')
        require(len({s['Index'] for s in p['Steps']}) == len(p['Steps']), 'duplicate instruction')
        require(any(s['Index'] == p['Entry'] for s in p['Steps']), 'missing program entry')
        # Synthetic dispatch is intentionally retained in the serialized hash.
        # Its full shape is a C# loader result, not reconstructed in Python.
        programs[key] = (row, p)
    require(set(programs) == expected, 'incomplete program inventory')
    # Reviewed events are explicit. Loader prunes other event bodies and adds a
    # dispatch prelude; raw ubergraph membership does not imply alias membership.
    events = {'card_event_carrier_battle': 'OnPlayedFromHand',
              'card_event_seize_the_initiative': 'OnPlayedFromHand',
              'card_unit_115th_separate_btn': 'OnEndOfTurn',
              'card_unit_75th_rifle_regiment': 'SpawnCopy'}
    bindings = []
    for site in reviewed:
        matches = []
        for (asset, name, container), (row, p) in programs.items():
            if (asset, name, container) != (site['card'], events[site['card']], site['program']):
                continue
            steps = [s for s in p['Steps'] if s['Index'] == site['instruction']]
            require(len(steps) == 1, 'missing instruction')
            step = steps[0]
            require(step['ByteOffset'] == site['byte_offset'], 'byte offset')
            path = site['expression_path']
            if path == '$.src':
                expr, runtime_path = step['Source'], 'src'
                require(isinstance(expr, dict), 'missing source expression')
                fn = expr.get('Math') or expr.get('Call')
            elif path == '$':
                runtime_path = '$'
                require(step['Op'] in ('math', 'call'), 'top-level operation')
                fn = step['Function']
            else:
                raise ValueError('unreviewed expression path')
            require(fn == site['function'], 'expression function')
            matches.append(dict(asset=asset, function=name, container=container,
                source_entry=p['SourceEntry'], program_entry=p['Entry'],
                instruction=step['Index'], byte_offset=step['ByteOffset'],
                expression_path=runtime_path, missing_key='math:' + fn,
                runtime_program_sha256=row['runtimeProgramSha256'],
                hash_schema=export['hashSchema'], admission_exempt=False))
        require(bool(matches), 'unmatched reviewed site')
        bindings.extend(matches)
    observed = export['observed']
    require(len(observed) == 2, 'observation inventory')
    seen = set()
    for item in observed:
        s = item['site']
        require(item['count'] == 1 and s['Key'] not in seen, 'observation count/duplicate')
        seen.add(s['Key'])
        require(s['SelfCard'] == 'card_unit_75th_rifle_regiment'
                and s['HashSchema'] == export['hashSchema'], 'observation identity/schema')
        fields = ('AssetName', 'FunctionName', 'SourceEntry', 'ProgramEntry', 'Instruction',
                  'ByteOffset', 'ExpressionPath', 'RuntimeProgramSha256', 'Key')
        values = tuple(s[k] for k in fields)
        require(sum(values == tuple(b[k] for k in ('asset', 'function', 'source_entry',
            'program_entry', 'instruction', 'byte_offset', 'expression_path',
            'runtime_program_sha256', 'missing_key')) for b in bindings) == 1,
            'observation binding')
    require(seen == {'math:GetDisplayName', 'math:PrintString'}, 'unexpected observed calls')
    return bindings


def build(repo, parsed, export_path):
    spec = importlib.util.spec_from_file_location('display_classifier', repo / 'tools/classify-display-call-sites.py')
    classifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(classifier)
    classification = classifier.build(repo, parsed)  # Revalidate raw consumers now.
    export = json.loads(export_path.read_text(encoding='utf-8-sig'))
    cards = json.loads((repo / 'klink bot/docs/card-ir.json').read_text(encoding='utf-8-sig'))
    bindings = bind(classification, export, cards)
    paths = ['tools/DisplayProgramAudit/Program.cs', 'tools/DisplayProgramAudit/DisplayProgramAudit.csproj',
             'tools/bind-display-runtime-programs.py', 'tools/test-display-runtime-bindings.py',
             'src/KLink.Bot/Effects/Blueprint/KismetNativeDefaults.cs']
    return dict(schema='display-runtime-binding-audit/v1',
        export_sha256=sha(export_path.read_bytes()), ir_sha256=export['irSha256'],
        library_assembly_sha256=export['libraryAssemblySha256'],
        source_bindings={**classification['source_bindings'], **{p: sha((repo / p).read_bytes()) for p in paths}},
        summary=dict(reviewed_source_sites=5, rebuilt_programs=len(export['programs']),
                     program_site_bindings=len(bindings), observed_vm_sites=2, admission_exempt_sites=0),
        bindings=bindings, limitations=[
            'C# loader prunes event bodies and adds synthetic dispatch; exact event bindings do not prove client reachability or execution of the Format sites.',
            'Only the two SpawnCopy sites were observed in a disposable simulator; this is not a client-memory invocation trace.',
            'Hash verifies exported bytes and exact sites, not trusted export generation, native equivalence or whole-card correctness.',
            'Assembly identity and source fingerprints record this build scope; rerun the C# exporter after any loader/VM/source change.',
            'No admission exemption is installed, even when every binding matches.',
            'Historical receiver links establish references only; interface invocation, subclass dispatch, scheduling and version equivalence remain open.'])


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[1])
    ap.add_argument('--parsed', type=Path, default=Path('D:/Codex Workshop/kards-install-review/parsed'))
    ap.add_argument('--export', type=Path, required=True)
    ap.add_argument('--output', type=Path, required=True)
    args = ap.parse_args()
    report = build(args.repo, args.parsed, args.export)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
