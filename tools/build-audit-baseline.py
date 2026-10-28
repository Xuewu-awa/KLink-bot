"""Reconcile historical audit documentation and file provenance without certifying rules."""
import argparse
import csv
import hashlib
import io
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

HEX = re.compile(r'^[a-fA-F0-9]{64}$')
RULE = re.compile(r'RULE-(\d+)(?!\d)')
PATH_SUFFIX = re.compile(r'\.(json|py|cs|txt|exe|uasset|uexp|md|log|h|csv|tsv)$', re.I)
OPEN_FIELD = re.compile(r'(remaining|limits|limitations|not_claimed|not_verified|next_steps|unverified|unresolved|pending|open_items|gaps|boundaries)', re.I)


def sha_bytes(data):
    return hashlib.sha256(data).hexdigest()


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def rule_ids(value):
    return sorted({'RULE-' + str(int(n)).zfill(3) for n in RULE.findall(str(value))})


def pointer(parent, key):
    return parent + '/' + str(key).replace('~','~0').replace('/','~1')


def leaves(value, path='', active=False):
    if isinstance(value, str):
        if active:
            yield path, value
    elif isinstance(value, dict):
        for key, child in value.items():
            yield from leaves(child, pointer(path,key), active or bool(OPEN_FIELD.search(key)))
    elif isinstance(value, list):
        for i, child in enumerate(value):
            yield from leaves(child, pointer(path,i), active)


def file_bindings(value):
    found = defaultdict(list)
    def visit(node, path=''):
        if isinstance(node, dict):
            p = node.get('path', node.get('file', node.get('source_path')))
            h = node.get('sha256')
            if isinstance(p,str) and PATH_SUFFIX.search(p) and isinstance(h,str) and HEX.fullmatch(h):
                found[(p,h.lower())].append(path)
            for key, child in node.items():
                if PATH_SUFFIX.search(key) and isinstance(child,str) and HEX.fullmatch(child):
                    found[(key,child.lower())].append(pointer(path,key))
                visit(child,pointer(path,key))
        elif isinstance(node,list):
            for i,child in enumerate(node):
                visit(child,pointer(path,i))
    visit(value)
    return [dict(declared_path=p, expected_sha256=h, origins=origins) for (p,h),origins in sorted(found.items())]


def declared_versions(data):
    values=[]
    def visit(node):
        if isinstance(node,dict):
            for k,v in node.items():
                if k in ['exe_sha256','installed_exe_sha256','client_exe_sha256'] and isinstance(v,str) and HEX.fullmatch(v):
                    values.append(v.lower())
                visit(v)
        elif isinstance(node,list):
            for v in node:visit(v)
    visit(data)
    values += [b['expected_sha256'] for b in file_bindings(data)
               if b['declared_path'].replace('\\','/').split('/')[-1].lower()=='kards-win64-shipping.exe']
    return sorted(set(values))


def binding_role(path):
    p='/'+path.replace('\\','/').lower().lstrip('/')
    if '/.codex/dsh-jobs/' in p:return 'worker_metadata'
    if '/src/klink.bot/' in p or '/tools/botsim/' in p:return 'kernel_or_regression_source'
    if p.endswith('/kards-win64-shipping.exe'):return 'client_executable'
    if p.endswith('.exe'):return 'tool_executable'
    if '/runtime-snapshots/' in p or '/parsed/' in p or '/resolved/' in p:return 'captured_or_extracted_data'
    if '/out/audit/systematic-rules/' in p:return 'secondary_audit_report'
    if p.endswith('.py'):return 'audit_tool_script'
    if p.endswith(('.cs','.h')):return 'parser_or_sdk_source'
    return 'other_audit_artifact'


def ledger_sections(text):
    headings = list(re.finditer(r'^(#{1,6})\s+([^\n]+)',text,re.M))
    rows = []
    for i,match in enumerate(headings):
        identity = re.match(r'RULE-(\d+)(?!\d)(.*)',match[2])
        if not identity:
            continue
        end = next((h.start() for h in headings[i+1:] if len(h[1])<=len(match[1])),len(text))
        section = text[match.start():end]
        rows.append(dict(rule_id='RULE-'+str(int(identity[1])).zfill(3),
            title=identity[2].lstrip('：: -'), line=text.count('\n',0,match.start())+1,
            section_sha256=sha_bytes(section.encode('utf8')), documentation_status='historical_rule_record',
            semantic_status='not_recertified_by_documentation_import'))
    if len({r['rule_id'] for r in rows})!=len(rows):
        raise ValueError('duplicate RULE headings in ledger')
    return rows


class EvidenceChecker:
    def __init__(self, repo, allowed_roots, enabled):
        self.repo=repo.resolve()
        self.roots=[self.repo]+[p.resolve() for p in allowed_roots]
        self.enabled=enabled
        self.cache={}
        self.bytes_hashed=0

    def check(self, declared):
        p=Path(declared)
        if not p.is_absolute():
            p=self.repo/p
        p=p.resolve()
        if not any(p.is_relative_to(root) for root in self.roots):
            return dict(resolved_path=str(p),check_status='outside_explicit_evidence_roots',observed_sha256=None)
        if p in self.cache:
            return self.cache[p]
        result=dict(resolved_path=str(p),check_status='not_hashed',observed_sha256=None)
        if not p.is_file():
            result['check_status']='missing'
        elif self.enabled:
            try:
                before=p.stat();h=hashlib.sha256()
                with p.open('rb') as f:
                    while chunk:=f.read(1024*1024):h.update(chunk)
                after=p.stat();self.bytes_hashed+=before.st_size
                if (before.st_size,before.st_mtime_ns)!=(after.st_size,after.st_mtime_ns):
                    result['check_status']='changed_during_hash'
                else:
                    result.update(check_status='hashed',observed_sha256=h.hexdigest(),bytes=before.st_size)
            except OSError as exc:
                result.update(check_status='unreadable',error_kind=type(exc).__name__)
        self.cache[p]=result
        return result


def build(repo, plan_path, *, allowed_roots=(), hash_evidence=False, client_sha256=None, definitions=None):
    repo=repo.resolve();plan=load(plan_path);checker=EvidenceChecker(repo,allowed_roots,hash_evidence)
    if plan.get('schema_version')!=1:
        raise ValueError('unsupported work plan')
    units=plan.get('work_units',[]);ids={u['id'] for u in units}
    if len(ids)!=len(units):
        raise ValueError('duplicate work unit')
    for u in units:
        if not u.get('acceptance_criteria') or not u.get('deliverables') or not u.get('scope'):
            raise ValueError('work unit lacks an acceptance contract: '+u['id'])
        if not set(u.get('depends_on',[]))<=ids:
            raise ValueError('unknown work dependency')
        if u['status'] not in ['open','partial']:
            raise ValueError('completion requires a separately reviewed closure record')
    visiting=set();done=set()
    def acyclic(key):
        if key in visiting:
            raise ValueError('work dependency cycle')
        if key in done:return
        visiting.add(key)
        for child in next(u for u in units if u['id']==key).get('depends_on',[]):acyclic(child)
        visiting.remove(key);done.add(key)
    for key in ids:acyclic(key)

    ledger_path=repo/'klink bot/docs/系统规则审计-20261014.md'
    ledger=ledger_sections(ledger_path.read_text(encoding='utf-8-sig'))
    report_rows=[];bindings=[];claims=[];manifest={str(plan_path.relative_to(repo)):sha_bytes(plan_path.read_bytes()),
        'klink bot/docs/系统规则审计-20261014.md':sha_bytes(ledger_path.read_bytes())}
    decisions={r['claim_id']:r for r in plan.get('remaining_claim_reviews',[])}
    if len(decisions)!=len(plan.get('remaining_claim_reviews',[])):
        raise ValueError('duplicate remaining-claim review')
    reports_by_rule=defaultdict(list)
    for path in sorted((repo/'out/audit/systematic-rules').glob('*.json')):
        relative=path.relative_to(repo).as_posix();raw=path.read_bytes();data=json.loads(raw.decode('utf-8-sig'));manifest[relative]=sha_bytes(raw)
        stated_ids=rule_ids(data.get('rule_id',data.get('rule',''))) if isinstance(data,dict) else []
        for key in stated_ids:reports_by_rule[key].append(relative)
        versions=declared_versions(data)
        version_status=('not_declared' if not versions else 'client_not_checked' if client_sha256 is None else
                        'declared_current_exe_hash' if versions==[client_sha256] else
                        'mixed_declared_versions' if client_sha256 in versions else 'declared_other_exe_hash')
        own=[]
        for binding in file_bindings(data):
            result=checker.check(binding['declared_path']);status=result['check_status']
            if status=='hashed':status='match' if result['observed_sha256']==binding['expected_sha256'] else 'changed'
            own.append(dict(report=relative,**binding,**result,binding_status=status,role=binding_role(binding['declared_path'])))
        bindings.extend(own);states=Counter(r['binding_status'] for r in own)
        provenance=('no_recognized_file_bindings' if not own else 'declared_bindings_changed' if states['changed'] else
                    'declared_evidence_missing' if states['missing'] else
                    'all_recognized_file_bindings_match' if states['match']==len(own) else 'file_check_incomplete')
        report_rows.append(dict(path=relative,sha256=manifest[relative],rule_ids=stated_ids,
            reported_status=data.get('status') if isinstance(data,dict) else None,
            declared_exe_hashes=versions,version_status=version_status,
            file_binding_counts=dict(states),provenance_status=provenance,
            documentation_status='indexed_with_explicit_rule_identity_and_provenance',
            semantic_status='historical_claims_not_current_certification'))
        for field,text in leaves(data):
            identity=relative+'#'+field;fingerprint=sha_bytes(text.encode('utf8'));decision=decisions.get(identity)
            disposition='historical_untriaged';addressed=[];residual=None
            if decision:
                if decision['text_sha256']!=fingerprint:disposition='stale_claim_review'
                else:
                    if decision['disposition'] not in ['partially_addressed','still_open','needs_revalidation']:
                        raise ValueError('unsupported closure decision')
                    if not decision.get('basis_reports') or not decision.get('residual_scope'):
                        raise ValueError('remaining review requires evidence and residual scope')
                    for basis in decision['basis_reports']:
                        if sha_bytes((repo/basis['path']).read_bytes())!=basis['sha256']:
                            raise ValueError('remaining review basis changed')
                    disposition=decision['disposition'];addressed=decision.get('addressed_scope',[]);residual=decision['residual_scope']
            claims.append(dict(id=identity,report=relative,field=field,text=text,text_sha256=fingerprint,
                disposition=disposition,addressed_scope=addressed,residual_scope=residual,
                task_status='not_an_automatic_open_defect'))
    claim_ids={c['id'] for c in claims}
    if set(decisions)-claim_ids:raise ValueError('review references missing historical claim')
    for row in ledger:row['explicit_json_reports']=reports_by_rule.get(row['rule_id'],[])
    changed_groups=defaultdict(list)
    for binding in bindings:
        if binding['binding_status']=='changed':changed_groups[binding['resolved_path']].append(binding)
    changed_files=[dict(path=path,role=rows[0]['role'],observed_sha256=rows[0]['observed_sha256'],
        historical_expected_sha256=sorted({r['expected_sha256'] for r in rows}),
        report_references=sorted({r['report'] for r in rows}),binding_claims=len(rows),
        next_check='function/scope-level diff before requiring semantic revalidation; not an automatic defect')
        for path,rows in sorted(changed_groups.items())]

    cards=load(repo/'klink bot/docs/cards.json');manifest['klink bot/docs/cards.json']=sha_bytes((repo/'klink bot/docs/cards.json').read_bytes())
    source_definitions=load(definitions) if definitions else []
    authoritative={r['name']:r for r in source_definitions}
    if len(authoritative)!=len(source_definitions):raise ValueError('duplicate authoritative card identity')
    if definitions:manifest[str(definitions)]=sha_bytes(definitions.read_bytes())
    scope=[]
    for name in sorted(set(cards)|set(authoritative)):
        row=authoritative.get(name,{});fields=row.get('explicit_overrides',{});sim=cards.get(name,{})
        enum=lambda v:v.split('::')[-1] if isinstance(v,str) else v
        tag=enum(fields.get('cardSet'))
        scope.append(dict(card=name,asset=row.get('asset',sim.get('source_pak')),
            explicit_asset_card_set=tag,explicit_asset_type=enum(fields.get('Type')),
            simulator_card_set=sim.get('cardSet'),is_in_permanent_pool_explicit=fields.get('isInPermanentPool'),
            campaign_upgradable_explicit=fields.get('campaignUpgradable'),
            metadata_basis='version_unconfirmed_authoritative_export' if row else 'simulator_only',
            scope_status='unreviewed',
            work_bucket='explicit-cardSet:'+str(tag) if tag is not None else 'native_or_inherited_cardSet_needs_resolution',
            scope_blockers=['current PvP mode legality and dynamic generation closure unverified',
                            'collectible-only membership is insufficient; generated cards and HQs must be considered']))

    counts=dict(ledger_rule_records=len(ledger),json_reports=len(report_rows),
        ledger_rules_with_explicit_json_report=sum(bool(r['explicit_json_reports']) for r in ledger),
        json_reports_with_explicit_rule_identity=sum(bool(r['rule_ids']) for r in report_rows),
        distinct_declared_file_paths=len({r['declared_path'] for r in bindings}),file_binding_claims=len(bindings),
        physical_files_hashed=sum(r['check_status']=='hashed' for r in checker.cache.values()),bytes_hashed=checker.bytes_hashed,
        binding_states=dict(Counter(r['binding_status'] for r in bindings)),
        changed_binding_roles=dict(Counter(r['role'] for r in bindings if r['binding_status']=='changed')),
        distinct_changed_physical_files=len(changed_files),
        report_provenance_states=dict(Counter(r['provenance_status'] for r in report_rows)),
        report_version_states=dict(Counter(r['version_status'] for r in report_rows)),
        historical_remaining_claims=len(claims),remaining_dispositions=dict(Counter(r['disposition'] for r in claims)),
        work_units=len(units),acceptance_criteria=sum(len(u['acceptance_criteria']) for u in units),
        scope_candidates=len(scope),scope_metadata_buckets=dict(Counter(r['work_bucket'] for r in scope)),
        authoritative_pvp_denominator_established=False,
        semantic_completion_percent=None,remaining_work_percent=None)
    return dict(schema_version=1,counts=counts,inputs=manifest,client_exe_sha256=client_sha256,
        hash_evidence=hash_evidence,ledger=ledger,reports=report_rows,bindings=bindings,changed_files=changed_files,remaining_claims=claims,
        work_units=units,scope_candidates=scope,
        limitations=['Documentation and file provenance are not semantic revalidation.',
            'File-binding extraction recognizes path/sha256 objects and path-keyed 64-hex file maps; other schemas stay unconfirmed.',
            'Changed source fingerprints are not automatically unresolved rule bugs.',
            'Historical remaining claims are not summed as independent current defects.',
            'Explicit cardSet metadata is not proof of PvP inclusion/exclusion; export version is not certified current.',
            'Work units form an acceptance-contract taxonomy, not an exhaustive or effort-weighted denominator.'])


def write(result, output):
    output.mkdir(parents=True,exist_ok=True)
    compact=lambda x:json.dumps(x,ensure_ascii=False,separators=(',',':'))
    parts=[compact(k)+':'+('[\n'+',\n'.join(compact(r) for r in v)+'\n]' if isinstance(v,list) else compact(v)) for k,v in result.items()]
    (output/'baseline.json').write_bytes(('{\n'+',\n'.join(parts)+'\n}\n').encode('utf8'))
    for name in ['ledger','reports','bindings','changed_files','remaining_claims','work_units','scope_candidates']:
        rows=result[name];keys=list(dict.fromkeys(k for r in rows for k in r));stream=io.StringIO(newline='')
        writer=csv.DictWriter(stream,fieldnames=keys,delimiter='\t',lineterminator='\n');writer.writeheader()
        for row in rows:
            def cell(v):
                if v is None:return 'null'
                if isinstance(v,(dict,list)) or isinstance(v,str) and (not v or v[-1:].isspace()):return compact(v)
                return v
            writer.writerow({k:cell(row.get(k)) for k in keys})
        (output/(name+'.tsv')).write_bytes(b'\xef\xbb\xbf'+stream.getvalue().encode('utf8'))
    c=result['counts'];lines=['# 系统审计进度基线', '',
        '本页整理历史成果、原始文件可用性和验收任务，不把文档记录或指纹匹配当作规则认证。整体完成率仍暂不计算。', '',
        f"- 台账规则记录：{c['ledger_rule_records']}；JSON报告：{c['json_reports']}。两者不是一对一。",
        f"- 原始指纹引用：{c['distinct_declared_file_paths']}个不同声明路径，{c['file_binding_claims']}项报告内绑定。",
        f"- 已完整读取哈希：{c['physical_files_hashed']}个物理文件；核对字节：{c['bytes_hashed']}。",
        f"- 文件绑定结果：{compact(c['binding_states'])}。源码变化不自动等同于规则缺陷。",
        f"- 变动绑定归并到{c['distinct_changed_physical_files']}个物理文件，先按函数/范围检查变化，避免整份旧报告重做。",
        f"- 历史待核陈述：{c['historical_remaining_claims']}；处置：{compact(c['remaining_dispositions'])}。",
        f"- 工作域：{c['work_units']}；验收条件：{c['acceptance_criteria']}；范围候选资产：{c['scope_candidates']}。", '',
        '## 现有成果与待办', '',
        '- [ledger.tsv](ledger.tsv)：完整台账规则身份、行号、章节指纹及明确关联的JSON报告。',
        '- [reports.tsv](reports.tsv)：报告声称的范围、EXE版本声明及文件证据状态。',
        '- [bindings.tsv](bindings.tsv)：每个文件声明的预期/当前哈希和具体变动，保留报告来源。',
        '- [changed_files.tsv](changed_files.tsv)：归并后的变动文件及受影响报告，避免把重复引用算作独立缺陷。',
        '- [remaining_claims.tsv](remaining_claims.tsv)：历史陈述与人工处置；已被后续部分处理的内容不再重复算成全新缺陷。',
        '- [work_units.tsv](work_units.tsv)：当前工作域、依赖、验收条件和交付物。',
        '- [scope_candidates.tsv](scope_candidates.tsv)：按权威导出显式字段分组的资产候选；不按名称排除战役或衍生卡。', '',
        '## 何时可以估计进度', '',
        '先闭合当前版本的PvP资产与隐式规则范围，再把历史验证精确归入分支，最后对子任务定界和估量。',
        '当前工作域只是规划分组，不能用“完成几个/总共几个”换算整个审计百分比。',
        '可构筑卡池不能直接作为分母；还必须覆盖可生成/转换/选择的衍生对象、总部，以及相关原生默认和公共调用。', '',
        '更新示例：', '', '```powershell',
        'python tools/build-audit-baseline.py --hash-evidence --evidence-root "D:\\Codex Workshop" --evidence-root "C:\\Users\\AWQ\\.codex\\dsh-jobs" --evidence-root "D:\\Fmodel\\Output\\Exports" --evidence-root "D:\\Program Files\\KARDS - The WWII Card Game" --client-exe "D:\\Program Files\\KARDS - The WWII Card Game\\KARDS\\default\\game\\kards\\Binaries\\Win64\\kards-Win64-Shipping.exe" --definitions "D:\\Codex Workshop\\kards-install-review\\cards.definitions.json"',
        'python tools/test-audit-baseline.py', '```', '',
        '人工维护登记：`klink bot/docs/audit-work-plan.json`。陈述正文或依据报告变化时，旧处置会失效或拒绝生成，不能静默沿用。', '']
    (output/'README.md').write_bytes('\n'.join(lines).encode('utf8'))


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1]);p.add_argument('--output',type=Path);p.add_argument('--plan',type=Path);p.add_argument('--hash-evidence',action='store_true');p.add_argument('--evidence-root',type=Path,action='append',default=[]);p.add_argument('--client-exe',type=Path);p.add_argument('--definitions',type=Path);a=p.parse_args()
    repo=a.repo.resolve();client=sha_bytes(a.client_exe.read_bytes()) if a.client_exe else None
    result=build(repo,(a.plan or repo/'klink bot/docs/audit-work-plan.json').resolve(),allowed_roots=a.evidence_root,
        hash_evidence=a.hash_evidence,client_sha256=client,definitions=a.definitions)
    result['generator_sha256']=sha_bytes(Path(__file__).read_bytes());write(result,a.output or repo/'out/audit/progress-baseline');print(json.dumps(result['counts'],ensure_ascii=False,indent=2))


if __name__=='__main__':main()
