"""Build a conservative, reproducible card/function/dependency audit matrix.

Reports are secondary records, never game-rule authority. Static call names are
candidate dependencies, not a resolved PvP call graph or semantic certification.
"""
import argparse
import csv
import hashlib
import io
import json
import re
import subprocess
from collections import Counter, defaultdict
from pathlib import Path


def digest(blob):
    return hashlib.sha256(blob).hexdigest()


def walk(value, path='$'):
    if isinstance(value, dict):
        yield path, value
        for key, child in value.items():
            yield from walk(child, path + '.' + str(key))
    elif isinstance(value, list):
        for i, child in enumerate(value):
            yield from walk(child, path + '[' + str(i) + ']')


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def text_leaves(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for child in value.values():
            yield from text_leaves(child)
    elif isinstance(value, list):
        for child in value:
            yield from text_leaves(child)


def build(repo, review_path, client_sha256=None):
    repo = Path(repo)
    manifest = {}

    def read(relative):
        path = repo / relative
        raw = path.read_bytes()
        manifest[relative] = digest(raw)
        return json.loads(raw.decode('utf-8-sig'))

    database = read('klink bot/docs/cards.json')
    ir = read('klink bot/docs/card-ir.json')
    inventory = read('out/audit/systematic-rules/inventory.json')
    reviews = load(review_path)
    manifest[review_path.relative_to(repo).as_posix()] = digest(review_path.read_bytes())
    if reviews.get('schema_version') != 1:
        raise ValueError('unsupported review registry schema')

    inventory_cards = {r['name']: r for r in inventory['cards']}
    known = set(database) | set(ir) | set(inventory_cards)
    functions = {}
    for row in inventory['functions']:
        key = row['card'] + '::' + row['function']
        if key in functions:
            raise ValueError('duplicate function identity: ' + key)
        functions[key] = dict(id=key, card=row['card'], function=row['function'],
            inventory_runtime_path=row.get('runtime_path'),
            inventory_runtime_sha256=row.get('runtime_sha256'),
            runtime_match_status='historical_inventory_only',
            excluded_from_headless=row.get('scope_exclusion'),
            semantic_status='not_reviewed')

    programs = []
    edges = []
    for card, node in sorted(ir.items()):
        bodies = {'@ubergraph': node.get('steps', []), **node.get('locals', {})}
        for name, steps in sorted(bodies.items()):
            program_id = card + '::' + name
            calls = defaultdict(list)
            unknown = []
            for path, value in walk(steps):
                if 'unknown' in value or value.get('op') == 'unknown':
                    unknown.append(path)
                symbol = (value.get('fn') if value.get('op') in ['call', 'math']
                          else value.get('math', value.get('call')))
                if not isinstance(symbol, str):
                    continue
                kind = ('call' if value.get('op') == 'call' else
                        'expression_call' if 'call' in value else 'math')
                args = value.get('args', [])
                mode = None
                if symbol == 'ChangeAttack' and len(args) > 3:
                    arg = args[3]
                    mode = arg.get('int') if isinstance(arg, dict) and type(arg.get('int')) is int else 'dynamic'
                calls[(symbol, kind, str(mode))].append(dict(path=path, instruction=value.get('i')))
            for (symbol, kind, mode), sites in sorted(calls.items()):
                edges.append(dict(id=program_id + '->' + kind + ':' + symbol + ':' + mode,
                    card=card, program_id=program_id, symbol=symbol, kind=kind,
                    mode=None if mode == 'None' else int(mode) if mode.lstrip('-').isdigit() else mode,
                    sites=sites, same_asset_local_candidate=card + '::' + symbol if symbol in node.get('locals', {}) else None,
                    resolution='static_name_only; receiver/inheritance/dynamic closure unresolved',
                    semantic_status='not_reviewed'))
            programs.append(dict(id=program_id, card=card, program=name, statements=len(steps),
                entrypoints=node.get('entrypoints', {}) if name == '@ubergraph' else {},
                unknown_node_paths=unknown, semantic_status='not_reviewed',
                scope='program container; calls are not per-entrypoint reachability'))
            if name != '@ubergraph':
                key = card + '::' + name
                functions.setdefault(key, dict(id=key, card=card, function=name,
                    runtime_match_status='not_in_historical_inventory', excluded_from_headless=None,
                    semantic_status='not_reviewed'))
        for event in node.get('entrypoints', {}):
            key = card + '::' + event
            functions.setdefault(key, dict(id=key, card=card, function=event,
                runtime_match_status='not_in_historical_inventory', excluded_from_headless=None,
                semantic_status='not_reviewed'))
    for row in functions.values():
        node = ir.get(row['card'], {})
        name = row['function']
        row['ir_binding'] = ('local_body' if name in node.get('locals', {}) else
            'event_entrypoint' if name in node.get('entrypoints', {}) else
            'ubergraph_container' if name.startswith('ExecuteUbergraph_') and node.get('steps') else
            'not_directly_represented')
        row['entry_offset'] = node.get('entrypoints', {}).get(name)

    # Source locations only prove text-level presence, not correct argument binding.
    implementations = defaultdict(list)
    source_re = re.compile(r'\["([A-Za-z_][A-Za-z_0-9]*)"\]\s*=|(?:public|private|internal)\s+(?:(?:static|virtual|override|async)\s+)*[\w<>?,.\[\]]+\s+([A-Za-z_]\w*)\s*\(')
    for path in sorted((repo / 'src/KLink.Bot').rglob('*.cs')):
        if {'bin', 'obj'} & set(path.parts):
            continue
        relative = path.relative_to(repo).as_posix()
        raw = path.read_bytes()
        manifest[relative] = digest(raw)
        for number, line in enumerate(raw.decode('utf-8-sig').splitlines(), 1):
            for match in source_re.finditer(line):
                implementations[match.group(1) or match.group(2)].append(dict(path=relative, line=number,
                    kind='dispatch_key' if match.group(1) else 'declaration'))

    reports = []
    report_by_path = {}
    for path in sorted((repo / 'out/audit/systematic-rules').glob('*.json')):
        relative = path.relative_to(repo).as_posix()
        data = read(relative)  # malformed reports fail the build; never silently drop them
        content = {key: data[key] for key in ['status', 'findings', 'remaining', 'scope', 'limitations', 'title']
                   if isinstance(data, dict) and key in data}
        row = dict(path=relative, report_id=data.get('rule_id', data.get('rule')) if isinstance(data, dict) else None,
            sha256=manifest[relative], reported_status=data.get('status') if isinstance(data, dict) else None,
            review_status='untriaged_secondary_record',
            historical_remaining=list(text_leaves(data.get('remaining', []))) if isinstance(data, dict) else [],
            authority='secondary audit record; historical remaining items are not a current open-task list')
        row['_tokens'] = set(re.findall(r'[A-Za-z_][A-Za-z_0-9]*', '\n'.join(text_leaves(content))))
        reports.append(row)
        report_by_path[relative] = row

    partial_units = []
    seen_units = set()
    for unit in reviews.get('partial_evidence', []):
        if unit['id'] in seen_units:
            raise ValueError('duplicate review unit: ' + unit['id'])
        seen_units.add(unit['id'])
        # A partial regression must never be promoted by a count or a text match.
        if unit.get('status') != 'partial':
            raise ValueError('this registry accepts partial evidence only; certification needs a separate closure audit')
        report = report_by_path.get(unit['report'])
        reasons = []
        if not unit.get('source_bindings'):
            reasons.append('source_inputs_not_bound')
        if not unit.get('evidence_bindings'):
            reasons.append('raw_evidence_not_bound')
        if not report or report['sha256'] != unit['report_sha256']:
            reasons.append('report_changed_or_missing')
        if client_sha256 is None:
            reasons.append('client_exe_not_checked')
        elif client_sha256 != unit['client_exe_sha256']:
            reasons.append('client_exe_changed')
        for path, expected in unit['source_bindings'].items():
            if path not in manifest:
                bound = repo / path
                manifest[path] = digest(bound.read_bytes()) if bound.is_file() else None
            if manifest[path] != expected:
                reasons.append('bound_source_changed:' + path)
        evidence_checks = []
        for path, expected in unit.get('evidence_bindings', {}).items():
            bound = Path(path)
            if not bound.is_absolute():
                bound = repo / bound
            observed = digest(bound.read_bytes()) if bound.is_file() else None
            evidence_checks.append(dict(path=path, expected_sha256=expected, observed_sha256=observed))
            if observed != expected:
                reasons.append(('evidence_missing:' if observed is None else 'evidence_changed:') + path)
        partial_units.append({**unit, 'freshness': 'stale_or_unchecked' if reasons else 'bound_inputs_match',
            'stale_reasons': reasons, 'asset_version_status': 'not_confirmed_against_current_pak',
            'evidence_checks': evidence_checks, 'semantic_status': 'partial_only'})
        if report:
            report['review_status'] = 'explicit_partial_evidence_link; all other report claims untriaged'

    for edge in edges:
        edge['partial_evidence_candidates'] = [u['id'] for u in partial_units
            if u['symbol'] == edge['symbol'] and (edge['mode'] == 'dynamic' or edge['mode'] in u.get('modes', []))]
        # This association never changes edge/card/function semantic_status.
    symbols = sorted({e['symbol'] for e in edges})
    dependencies = []
    for symbol in symbols:
        uses = [e for e in edges if e['symbol'] == symbol]
        dependencies.append(dict(symbol=symbol, direct_candidate_cards=len({e['card'] for e in uses}),
            program_containers=len({e['program_id'] for e in uses}), call_sites=sum(len(e['sites']) for e in uses),
            kinds=sorted({e['kind'] for e in uses}),
            implementation_candidates=implementations.get(symbol, []),
            partial_evidence_units=[u['id'] for u in partial_units if u['symbol'] == symbol],
            secondary_report_mentions=[r['path'] for r in reports if symbol in r['_tokens']],
            semantic_status='not_fully_reviewed', priority_basis='static direct use count; not execution frequency'))
    for row in reports:
        del row['_tokens']

    by_card_programs = defaultdict(list)
    by_card_edges = defaultdict(list)
    for row in programs:
        by_card_programs[row['card']].append(row)
    for row in edges:
        by_card_edges[row['card']].append(row)
    scope_reviews = reviews.get('scope_reviews', {})
    if set(scope_reviews) - known:
        raise ValueError('scope review references unknown card')
    cards = []
    for name in sorted(known):
        card = database.get(name, {})
        old = inventory_cards.get(name, {})
        scope = scope_reviews.get(name)
        # An exclusion requires a pinned evidence artifact, not a name heuristic.
        scope_status = 'unreviewed'
        if scope:
            if scope.get('status') not in ['pvp_included', 'excluded_non_pvp'] or not scope.get('reason'):
                raise ValueError('invalid explicit scope decision: ' + name)
            proof = repo / scope['proof_path']
            if (proof.is_file() and digest(proof.read_bytes()) == scope['proof_sha256']
                    and client_sha256 is not None and scope.get('client_exe_sha256') == client_sha256
                    and scope.get('database_sha256') == manifest['klink bot/docs/cards.json']):
                scope_status = scope['status']
            else:
                scope_status = 'stale_scope_review'
        card_edges = by_card_edges[name]
        cards.append(dict(card=name, title=card.get('title'), type=card.get('type'), faction=card.get('faction'),
            asset=card.get('source_pak', ir.get(name, {}).get('asset', old.get('asset'))),
            inventory_parent=old.get('parent'), pvp_scope=scope_status,
            database_present=name in database, own_ir_present=name in ir,
            program_count=len(by_card_programs[name]),
            candidate_dependencies=sorted({e['symbol'] for e in card_edges}),
            partial_evidence_candidates=sorted({u for e in card_edges for u in e['partial_evidence_candidates']}),
            unknown_ir_nodes=sum(len(p['unknown_node_paths']) for p in by_card_programs[name]),
            native_defaults_and_implicit_rules='unreviewed', current_asset_version='unconfirmed',
            semantic_status='not_reviewed_end_to_end'))

    counts = dict(cards=len(cards), cards_with_own_ir=sum(c['own_ir_present'] for c in cards),
        pvp_included=sum(c['pvp_scope'] == 'pvp_included' for c in cards),
        excluded_non_pvp=sum(c['pvp_scope'] == 'excluded_non_pvp' for c in cards),
        pvp_scope_unresolved=sum(c['pvp_scope'] not in ['pvp_included', 'excluded_non_pvp'] for c in cards),
        function_identities=len(functions), headless_functions_excluded=sum(bool(f['excluded_from_headless']) for f in functions.values()),
        program_containers=len(programs), dependency_edges=len(edges), dependency_symbols=len(dependencies),
        secondary_reports=len(reports), explicit_partial_units=len(partial_units),
        partial_units_matching_bound_inputs=sum(u['freshness'] == 'bound_inputs_match' for u in partial_units),
        authoritative_current_pvp_denominator_available=False,
        fully_certified_cards=0, semantic_completion_percent=None, total_work_remaining_percent=None)
    return dict(schema_version=1, counts=counts, inputs=manifest, client_exe_sha256=client_sha256,
        historical_inventory_git_head=inventory.get('git_head'),
        limitations=['No authoritative PvP denominator yet; no global completion percentage.',
            'Program-level static name edges do not resolve receiver, inheritance, implicit native rules or dynamic calls.',
            'Report mentions and shared partial regressions never certify a card, function or branch.',
            'Historical remaining text is retained for triage; it is not automatically an active defect.',
            'EXE hash agreement alone does not verify current pak/card assets.',
            'This version deliberately rejects automatic full-certification entries.'],
        cards=cards, functions=sorted(functions.values(), key=lambda r:r['id']), programs=programs,
        edges=edges, dependencies=dependencies, reports=reports, partial_evidence=partial_units,
        open_items=reviews.get('open_items', []))


def write_outputs(result, output):
    output.mkdir(parents=True, exist_ok=True)
    # One record per line makes regenerated inventories practical to review in Git.
    sections = []
    compact = lambda v:json.dumps(v, ensure_ascii=False, separators=(',', ':'))
    for key, value in result.items():
        payload = ('[\n' + ',\n'.join(compact(row) for row in value) + '\n]') if isinstance(value,list) else compact(value)
        sections.append(compact(key) + ':' + payload)
    (output / 'matrix.json').write_bytes(('{\n' + ',\n'.join(sections) + '\n}\n').encode('utf8'))
    tables = dict(cards=result['cards'], functions=result['functions'], programs=result['programs'],
        dependencies=result['dependencies'], edges=result['edges'], reports=result['reports'],
        branches=result['partial_evidence'], open_items=result['open_items'])
    for name, rows in tables.items():
        fields = list(dict.fromkeys(k for row in rows for k in row))
        stream = io.StringIO(newline='')
        writer = csv.DictWriter(stream, fieldnames=fields, delimiter='\t', lineterminator='\n')
        writer.writeheader()
        for row in rows:
            # JSON encode embedded values, including line breaks, into a single cell.
            def cell(v):
                if v is None:
                    return 'null'
                if isinstance(v,(list,dict)) or isinstance(v,str) and (not v or v[-1:].isspace()):
                    return json.dumps(v,ensure_ascii=False)
                return v
            writer.writerow({k:cell(row.get(k)) for k in fields})
        (output / (name + '.tsv')).write_bytes(b'\xef\xbb\xbf' + stream.getvalue().encode('utf8'))
    c = result['counts']
    labels = dict(cards='仓库卡资产（含范围待确认项）', cards_with_own_ir='有自身IR的资产',
        pvp_included='已明确登记为PvP范围', excluded_non_pvp='已明确登记为非PvP',
        pvp_scope_unresolved='PvP范围尚未登记闭合', function_identities='函数身份',
        headless_functions_excluded='显示提示/委托签名排除项', program_containers='IR程序容器',
        dependency_edges='聚合静态依赖关联', dependency_symbols='依赖符号',
        secondary_reports='已索引JSON审计记录', explicit_partial_units='已人工关联局部证据单元',
        partial_units_matching_bound_inputs='局部证据绑定输入一致',
        authoritative_current_pvp_denominator_available='当前权威PvP分母已建立',
        fully_certified_cards='已完整认证单卡', semantic_completion_percent='整体语义完成百分比',
        total_work_remaining_percent='整体剩余工作百分比')
    lines = ['# 系统审计覆盖矩阵', '',
        '这是当前仓库资产与审计记录的可更新索引。PvP范围和完整语义认证尚未闭合，不能据此计算总完成百分比。', '',
        '资产行数来自仓库卡表/IR/历史库存的并集，不代表当前客户端的PvP卡数；当前pak资产版本仍待关联。', '',
        '| 项目 | 数量 |', '|---|---:|']
    lines += [f'| {labels.get(k,k)} | {("是" if v else "否") if type(v) is bool else v if v is not None else "未建立分母，暂不计算"} |' for k,v in c.items()]
    lines += ['', '## 使用入口', '',
        '- [cards.tsv](cards.tsv)：全资产范围、函数容器、依赖、待核状态；未按名称猜测排除战役卡。',
        '- [functions.tsv](functions.tsv)、[programs.tsv](programs.tsv)：历史函数身份与当前IR绑定；事件入口和共享ubergraph分开列。',
        '- [edges.tsv](edges.tsv)：静态调用位置、ChangeAttack字面模式及动态模式；不是实际执行可达性证明。',
        '- [dependencies.tsv](dependencies.tsv)：依赖影响范围、C#候选位置及报告文字关联；关联不等于验证。',
        '- [branches.tsv](branches.tsv)：人工确认的局部验证范围、版本/源码指纹与失效原因。',
        '- [reports.tsv](reports.tsv)：全部JSON审计记录；旧remaining保留为历史待整理文本。',
        '- [open_items.tsv](open_items.tsv)：明确整理的当前待办，和历史remaining分开。',
        '- [matrix.json](matrix.json)：完整机器可读数据与输入SHA-256。', '',
        'TSV中的null表示缺失/未知值；复杂字段为JSON单元格，完整类型以matrix.json为准。', '',
        '## 优先整理的公共依赖', '',
        '按直接静态引用卡数排列；未包含隐式基础规则、动态或传递调用，不能当作真实使用频率。', '',
        '| 符号 | 引用卡资产 | 程序容器 | 局部证据单元 |', '|---|---:|---:|---|']
    ranked = sorted((d for d in result['dependencies'] if 'call' in d['kinds']),
        key=lambda d:(-d['direct_candidate_cards'],d['symbol']))[:25]
    lines += [f"| {d['symbol']} | {d['direct_candidate_cards']} | {d['program_containers']} | {', '.join(d['partial_evidence_units']) or '尚未人工关联'} |" for d in ranked]
    lines += ['', '## 更新与准入', '',
        '在仓库根目录运行：', '', '```powershell',
        'python tools/build-audit-matrix.py --client-exe "D:\\Program Files\\KARDS - The WWII Card Game\\KARDS\\default\\game\\kards\\Binaries\\Win64\\kards-Win64-Shipping.exe"',
        'python tools/test-audit-matrix.py', '```', '',
        '更新 `klink bot/docs/audit-matrix-reviews.json` 维护局部证据、当前待办及有证据的PvP范围判定。',
        '绑定报告或源码改变、未检查EXE或EXE不匹配时，局部证据显示失效/未检查；不自动继承绿色状态。',
        '当前三条ChangeAttack局部单元只是首批人工关联；其他旧审计成果为待整理，不表示全部重做。',
        '完整单卡认证需要另行完成：当前版本资产、PvP范围、全部自定义/继承/原生路径、分支及联动验证。',
        '第一版不允许以手填百分比或局部共享测试自动认证整卡；完整认证准入尚待独立闭合。', '']
    (output / 'README.md').write_bytes(('\n'.join(lines)).encode('utf8'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--reviews', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--client-exe', type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    review_path = (args.reviews or repo / 'klink bot/docs/audit-matrix-reviews.json').resolve()
    result = build(repo, review_path, digest(args.client_exe.read_bytes()) if args.client_exe else None)
    # Code revision is deliberately separate from input fingerprints: committing
    # generated artifacts alone must not invalidate otherwise identical inputs.
    result['generation_git_head'] = subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip()
    result['generator_sha256'] = digest(Path(__file__).read_bytes())
    write_outputs(result, args.output or repo / 'out/audit/coverage-matrix')
    print(json.dumps(result['counts'], ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
