"""Check direct consumers of remaining display sites, without PvP exclusions."""
import argparse
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path


def require(ok, message):
    if not ok:
        raise ValueError(message)


def walk(x):
    if isinstance(x, dict):
        yield x
        for v in x.values():
            yield from walk(v)
    elif isinstance(x, list):
        for v in x:
            yield from walk(v)


def refs(x, name):
    return any(n.get('var') == name and n.get('scope') == 'local' for n in walk(x))


def raw_refs(x, name):
    return any(n.get('Inst') in {'LocalVariable', 'LocalOutVariable'}
               and n.get('Variable Name') == name for n in walk(x))


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def shared_boundary(cf, notifier):
    stub = cf['UpdateCampaignStarStatus']['bytecode']
    frame = [s for s in stub if s.get('Inst') == 'LetValueOnPersistentFrame']
    require([(s['Property Name'], s['Expression'].get('Variable Name')) for s in frame]
            == [('K2Node_Event_star', 'star'), ('K2Node_Event_message', 'message'),
                ('K2Node_Event_starStatus', 'starStatus')], 'campaign frame boundary')
    require(frame[1]['Expression']['Inst'] == 'LocalOutVariable', 'reference parameter boundary')
    calls = [s for s in stub if s.get('Inst') == 'LocalFinalFunction']
    require(len(calls) == 1 and calls[0]['Function'] == 'ExecuteUbergraph_BP_CardFunctions'
            and calls[0]['Parameters'] == [{'Inst': 'IntConst', 'Value': 443}], 'campaign event entry')
    s = next(s for s in cf['ExecuteUbergraph_BP_CardFunctions']['bytecode'] if s['StatementIndex'] == 443)
    require(s.get('Context', {}).get('Variable Name') == 'CardFunctionsNotifier'
            and s.get('Expression', {}).get('FunctionName') == 'NotiferStarCounterChanged'
            and [p.get('Variable Name') for p in s['Expression']['Parameters']]
            == ['K2Node_Event_star', 'K2Node_Event_message', 'K2Node_Event_starStatus'], 'campaign notifier forwarding')
    n = notifier['NotiferStarCounterChanged']['bytecode'][0]
    receiver = n.get('Context', {})
    require(receiver.get('Inst') == 'InterfaceContext'
            and receiver.get('Expression', {}).get('Variable Name') == 'CampaignReceiver'
            and receiver['Expression'].get('Variable Outer', {}).get('PinSubCategoryObject')
            == '/Game/Blueprints/Logic/I_CampaignReceiver.I_CampaignReceiver_C'
            and n.get('Expression', {}).get('FunctionName') == 'HandleStarCounterChanged'
            and [p.get('Variable Name') for p in n['Expression']['Parameters']]
            == ['Star', 'Message', 'StarStatus'], 'campaign interface boundary')
    return dict(event_entry=443, reference_parameter='message', frame_writes=frame,
                forwarding_statement=s, interface_statement=n, actual_invocation_proven=False)


def review_site(site, obj, raw):
    require(site['program'] == 'steps', 'unexpected program')
    steps = obj['steps']
    sources = [s for s in steps if s['i'] == site['instruction']]
    require(len(sources) == 1, 'source identity')
    source = sources[0]
    require(source.get('bo') == site['byte_offset'], 'source offset')
    statements = raw['ExecuteUbergraph_' + site['card']]['bytecode']
    raw_source = [s for s in statements if s['StatementIndex'] == source['i']]
    require(len(raw_source) == 1, 'raw source identity')
    if site['function'] == 'PrintString':
        require(site['expression_path'] == '$' and source.get('op') == 'math'
                and source.get('fn') == 'PrintString' and not source.get('outs')
                and not any(source.get(k) for k in ('dst', 'src', 'cond', 'recv')), 'logging shape')
        require(raw_source[0].get('Inst') == 'CallMath'
                and raw_source[0].get('Function') == 'PrintString', 'raw logging shape')
        return dict(**site, boundary='direct_void_log', consumers=[],
                    raw_source=raw_source[0], local_reassignment_count=0)
    require(site['function'] == 'Format' and site['expression_path'] == '$.src'
            and source.get('op') == 'set' and source.get('dst_scope') == 'local'
            and source.get('src', {}).get('math') == 'Format', 'format shape')
    name = source['dst']
    require(raw_source[0].get('Variable', {}).get('Variable Name') == name
            and raw_source[0].get('Expression', {}).get('Function') == 'Format', 'raw format source')
    uses = [s for s in steps if refs(s, name)]
    definitions = [s for s in steps if s.get('dst') == name and s.get('dst_scope') == 'local']
    raw_uses = [s for s in statements if raw_refs(s, name)]
    require({s['StatementIndex'] for s in raw_uses}
            == {s['i'] for s in uses + definitions}, 'raw/IR reference set differs')
    require(bool(uses), 'missing consumer')
    consumers = []
    for s in uses:
        require(s['op'] == 'call' and s.get('fn') == 'UpdateCampaignStarStatus'
                and not s.get('outs'), 'unexpected direct consumer')
        require([i for i, arg in enumerate(s.get('args', [])) if refs(arg, name)] == [1]
                and not any(refs(s.get(k), name) for k in ('src', 'cond', 'recv')), 'unexpected argument')
        rs = next(x for x in raw_uses if x['StatementIndex'] == s['i'])
        require(rs.get('Inst') == 'Context', 'raw receiver shape')
        call = rs.get('Expression', {})
        require(call.get('Inst') == 'LocalVirtualFunction'
                and call.get('FunctionName') == 'UpdateCampaignStarStatus'
                and [i for i, arg in enumerate(call.get('Parameters', [])) if raw_refs(arg, name)] == [1]
                and not raw_refs(rs.get('Context'), name), 'raw consumer argument')
        consumers.append(dict(instruction=s['i'], byte_offset=s.get('bo'),
                              function=s['fn'], argument_zero_based=1, raw_statement=rs))
    # Inspect every assignment to reused locals; do not pair one definition with
    # only its nearest consumer and silently discard other function-level uses.
    for s in definitions:
        require(s.get('src', {}).get('math') == 'Format', 'unreviewed reassignment')
    return dict(**site, boundary='direct_campaign_message_argument',
                assigned_local=name, local_reassignment_count=len(definitions),
                consumers=consumers, raw_source=raw_source[0])


def build(repo, parsed):
    spec = importlib.util.spec_from_file_location('display_classifier', repo / 'tools/classify-display-call-sites.py')
    classifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(classifier)
    classification = classifier.build(repo, parsed)
    sites = [s for s in classification['sites'] if s['classification'] == 'unreviewed']
    cards = json.loads((repo / 'klink bot/docs/card-ir.json').read_text(encoding='utf-8-sig'))
    rows, evidence = [], {}
    for name in sorted({s['card'] for s in sites}):
        paths = list(parsed.rglob(name + '.functions.json'))
        require(len(paths) == 1, 'ambiguous/missing raw evidence: ' + name)
        p = paths[0]
        evidence[name] = dict(path=str(p), sha256=sha(p))
        raw = json.loads(p.read_text(encoding='utf-8'))
        rows.extend(review_site(s, cards[name], raw) for s in sites if s['card'] == name)
    shared_raw = {}
    for name in ('BP_CardFunctions', 'U_CardFunctionsNotifier'):
        paths = list(parsed.rglob(name + '.functions.json'))
        require(len(paths) == 1, 'missing/ambiguous shared evidence')
        evidence[name] = dict(path=str(paths[0]), sha256=sha(paths[0]))
        shared_raw[name] = json.loads(paths[0].read_text(encoding='utf-8'))
    shared = shared_boundary(shared_raw['BP_CardFunctions'], shared_raw['U_CardFunctionsNotifier'])
    sdk = Path('D:/Program Files/KARDS - The WWII Card Game/KARDS/default/game/kards/Binaries/Win64/ue4ss/CXXHeaderDump/BP_CardFunctions.hpp')
    declaration = 'void UpdateCampaignStarStatus(int32 star, FText& Message, ECampaignStarStatus starStatus);'
    require(declaration in sdk.read_text(encoding='utf-8-sig'), 'SDK reference declaration')
    shared['sdk'] = dict(path=str(sdk), sha256=sha(sdk), declaration=declaration)
    return dict(schema='display-campaign-direct-boundaries/v1',
        ir_sha256=classification['ir_sha256'],
        source_bindings={**classification['source_bindings'], **{p: sha(repo / p) for p in
            ['tools/audit-display-campaign-group.py', 'tools/test-display-campaign-group.py']}},
        parsed_evidence=evidence, shared_boundary=shared,
        summary=dict(sites=len(rows), assets=len({s['card'] for s in sites}),
            boundaries=dict(Counter(r['boundary'] for r in rows)),
            unique_direct_consumers=len({(r['card'], c['instruction']) for r in rows for c in r['consumers']}),
            admission_exempt_sites=0, pvp_excluded_assets=0),
        sites=rows, limitations=[
            'This group is a scheduling priority decision, not a PvP asset exclusion or runtime allowlist.',
            'Checks cover direct result references across each full raw function, including reused locals; they do not prove transitive side-effect safety.',
            'UpdateCampaignStarStatus has a reference text parameter in the SDK; IR outs=[] does not establish no native/by-reference mutation.',
            'Campaign notification routing, native Format implementation, client version, inherited dispatch and execution reachability remain open.',
            'Existing 55 unreviewed classifications remain unchanged; local consumer grouping does not satisfy the complete display-result review contract.',
            'Do not spend the PvP training audit on campaign callback implementation unless evidence makes it a core blocker.'])


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[1])
    ap.add_argument('--parsed', type=Path, default=Path('D:/Codex Workshop/kards-install-review/parsed'))
    ap.add_argument('--output', type=Path, required=True)
    args = ap.parse_args()
    report = build(args.repo, args.parsed)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
