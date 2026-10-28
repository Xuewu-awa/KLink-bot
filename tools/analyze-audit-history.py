"""Recover exact historical source bytes and compare syntax/data, never rule correctness."""
import argparse
import ast
import hashlib
import io
import json
import subprocess
from collections import Counter, defaultdict
from pathlib import Path


def sha(data):return hashlib.sha256(data).hexdigest()
def load(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def save(path,value):path.write_bytes((json.dumps(value,ensure_ascii=False,indent=2)+'\n').encode('utf8'))


def review_lane(path):
    p=path.replace('\\','/').lower()
    if p.endswith('/selftest.cs'):return 'regression_suite_history; not production-rule implementation'
    if p.startswith('tools/'):return 'diagnostic_or_integration_tool; usage still needs scope review'
    if p.endswith('/card-ir.json'):return 'interpreted_card_program_data'
    if p.endswith('.json'):return 'card_catalog_data'
    if p.endswith('.py'):return 'generator_source_and_template'
    if '/replay/' in p or '/server/' in p:return 'input_adapter_or_snapshot; headless usage needs tracing'
    return 'runtime_implementation_members_and_shared_context'


def variants(raw):
    # Do not use canonicalized bytes as proof. Only a variant whose entire
    # SHA-256 equals the report fingerprint may be used for syntax comparison.
    yield 'git_blob',raw
    try:text=raw.decode('utf-8-sig')
    except UnicodeDecodeError:return
    text=text.replace('\r\n','\n')
    seen={raw}
    for ending,body in [('lf',text),('crlf',text.replace('\n','\r\n'))]:
        for bom in [False,True]:
            data=(b'\xef\xbb\xbf' if bom else b'')+body.encode('utf8')
            if data not in seen:
                seen.add(data);yield ending+('_bom' if bom else ''),data


def git_versions(repo,paths):
    raw=subprocess.check_output(['git','log','--format=@@%H','--raw','--no-abbrev','HEAD','--',*paths],cwd=repo)
    entries=defaultdict(dict);commit=None
    for line in raw.decode('utf8').splitlines():
        if line.startswith('@@'):commit=line[2:];continue
        if not line.startswith(':') or line.startswith('::'):continue
        header,_,path=line.partition('\t');parts=header.split()
        if len(parts)!=5 or path not in paths:continue
        blob=parts[3]
        if blob!='0'*40:entries[blob][path]=commit
    return raw,entries


def recover(repo,baseline,cache):
    cache.mkdir(parents=True,exist_ok=True);snapshots=cache/'snapshots';snapshots.mkdir(exist_ok=True)
    targets={};external=[]
    for row in baseline['changed_files']:
        p=Path(row['path']).resolve()
        if p.is_relative_to(repo):
            relative=p.relative_to(repo).as_posix()
            targets[relative]=set(row['historical_expected_sha256'])
        else:external.append(dict(path=str(p),status='external_version_or_capture_requires_separate_recovery',expected_sha256=row['historical_expected_sha256']))
    history,entries=git_versions(repo,list(targets));(cache/'history.raw.txt').write_bytes(history)
    found={};objects=0
    with subprocess.Popen(['git','cat-file','--batch'],cwd=repo,stdin=subprocess.PIPE,stdout=subprocess.PIPE) as process:
        assert process.stdin and process.stdout
        for oid,paths in entries.items():
            pending=[p for p in paths if any((p,h) not in found for h in targets[p])]
            if not pending:continue
            process.stdin.write((oid+'\n').encode());process.stdin.flush()
            header=process.stdout.readline().decode().strip().split()
            if len(header)!=3 or header[0]!=oid or header[1]!='blob':raise ValueError('unexpected git batch object')
            n=int(header[2]);raw=process.stdout.read(n)
            if len(raw)!=n or process.stdout.read(1)!=b'\n':raise ValueError('truncated git object')
            if hashlib.sha1(b'blob '+str(n).encode()+b'\0'+raw).hexdigest()!=oid:raise ValueError('git blob hash mismatch')
            objects+=1
            for kind,data in variants(raw):
                fingerprint=sha(data)
                for path in pending:
                    if fingerprint not in targets[path] or (path,fingerprint) in found:continue
                    destination=snapshots/(fingerprint+Path(path).suffix)
                    destination.write_bytes(data)
                    found[path,fingerprint]=dict(path=path,expected_sha256=fingerprint,status='exact_bytes_recovered',
                        git_blob=oid,representative_commit=paths[path],reconstruction=kind,snapshot_path=str(destination),bytes=len(data))
            if objects%100==0:print('checked_git_blobs',objects,flush=True)
        process.stdin.close();process.wait()
        if process.returncode:raise ValueError('git cat-file failed')
    rows=[]
    for path,hashes in sorted(targets.items()):
        for h in sorted(hashes):rows.append(found.get((path,h),dict(path=path,expected_sha256=h,status='not_recovered_from_reachable_git_and_exact_eol_variants')))
    documents=[]
    for path in sorted(targets):
        if Path(path).suffix=='.cs':documents.append(dict(id=path+'@current',path=str(repo/path)))
    documents += [dict(id=r['path']+'@'+r['expected_sha256'],path=r['snapshot_path']) for r in rows if r['status']=='exact_bytes_recovered' and Path(r['path']).suffix=='.cs']
    save(cache/'roslyn-request.json',dict(documents=documents))
    result=dict(git_head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),
        git_history_sha256=sha(history),git_objects_checked=objects,rows=rows,external=external)
    save(cache/'recovery.json',result);return result


def json_changes(old,new,path=''):
    if type(old) is not type(new):return [path or '/']
    if isinstance(old,dict):
        out=[]
        for k in sorted(set(old)|set(new)):
            p=path+'/'+str(k).replace('~','~0').replace('/','~1')
            out += [p] if k not in old or k not in new else json_changes(old[k],new[k],p)
        return out
    if isinstance(old,list):
        out=[]
        for i in range(max(len(old),len(new))):
            p=path+'/'+str(i)
            out += [p] if i>=len(old) or i>=len(new) else json_changes(old[i],new[i],p)
        return out
    return [] if old==new else [path or '/']


def strict_json(raw):
    def pairs(values):
        result={}
        for k,v in values:
            if k in result:raise ValueError('duplicate JSON key')
            result[k]=v
        return result
    return json.loads(raw.decode('utf-8-sig'),object_pairs_hook=pairs,
        parse_constant=lambda v:(_ for _ in ()).throw(ValueError('nonstandard JSON constant')))


def compare_csharp(before,after):
    if before['parse_errors'] or after['parse_errors'] or before['duplicate_member_ids'] or after['duplicate_member_ids']:
        return dict(classification='parse_or_identity_ambiguous',changed_members=[],added_members=[],removed_members=[],unchanged_members=0,
            blockers=['Syntax errors or duplicate member identities; no equivalence inferred.'])
    old={m['id']:m for m in before['members']};new={m['id']:m for m in after['members']}
    changed=[dict(id=k,name=new[k]['name'],kind=new[k]['kind']) for k in sorted(old.keys()&new.keys()) if old[k]['tokens_sha256']!=new[k]['tokens_sha256']]
    added=[dict(id=k,name=new[k]['name'],kind=new[k]['kind']) for k in sorted(new.keys()-old.keys())]
    removed=[dict(id=k,name=old[k]['name'],kind=old[k]['kind']) for k in sorted(old.keys()-new.keys())]
    unchanged=sum(old[k]['tokens_sha256']==new[k]['tokens_sha256'] for k in old.keys()&new.keys())
    context=before['context_sha256']!=after['context_sha256']
    status='trivia_only_for_this_exact_file_pair' if before['structure_sha256']==after['structure_sha256'] else 'member_or_context_tokens_changed'
    risks=[]
    if context:risks.append('using/type/attribute/directive/disabled-source context changed')
    if any(m['kind']!='method' for m in changed+added+removed):risks.append('field/property/constructor/declaration changes may affect unchanged methods')
    if before['has_conditional_directives'] or after['has_conditional_directives']:risks.append('conditional compilation: empty-symbol parse only, disabled source retained conservatively')
    if status!='trivia_only_for_this_exact_file_pair' and not changed and not added and not removed and not context:
        risks.append('declaration ordering or other non-member syntax changed')
    return dict(classification=status,unchanged_members=unchanged,changed_members=changed,added_members=added,removed_members=removed,
        context_changed=context,blockers=risks+['Token equality is not semantic certification; callees/state/version can change.'])


def compare(repo,baseline,recovery,cache,roslyn):
    documents={d['id']:d for d in roslyn.get('documents',[])};pairs=[];data_rows=[]
    for row in recovery['rows']:
        path=row['path'];current=(repo/path).read_bytes();result={**row,'current_sha256':sha(current)}
        if row['status']!='exact_bytes_recovered':result['classification']='historical_bytes_not_recovered';pairs.append(result);continue
        previous=Path(row['snapshot_path']).read_bytes()
        if sha(previous)!=row['expected_sha256']:raise ValueError('recovered snapshot changed')
        if previous==current:result['classification']='exact_file_bytes_equal'
        elif Path(path).suffix=='.cs':
            before=documents.get(path+'@'+row['expected_sha256']);after=documents.get(path+'@current')
            if not before or not after:result['classification']='roslyn_document_missing'
            else:
                if before['raw_sha256']!=sha(previous) or after['raw_sha256']!=sha(current):raise ValueError('Roslyn/source fingerprint mismatch')
                result.update(compare_csharp(before,after))
        elif Path(path).suffix=='.json':
            try:
                old=strict_json(previous);new=strict_json(current);changes=json_changes(old,new)
                result.update(classification='json_serialization_only' if not changes else 'json_values_changed',changed_json_paths=changes)
                data_rows.append(dict(path=path,expected_sha256=row['expected_sha256'],changed_json_paths=changes))
            except (ValueError,UnicodeDecodeError):result['classification']='json_parse_ambiguous'
        elif Path(path).suffix=='.py':
            try:
                old=ast.dump(ast.parse(previous,type_comments=True),include_attributes=False);new=ast.dump(ast.parse(current,type_comments=True),include_attributes=False)
                result['classification']='python_comments_or_spacing_only' if old==new else 'python_ast_changed'
            except (ValueError,SyntaxError,UnicodeDecodeError):result['classification']='python_parse_ambiguous'
        else:result['classification']='unsupported_source_format'
        pairs.append(result)
    by_pair={(r['path'],r['expected_sha256']):r for r in pairs};impacts=[]
    for b in baseline['bindings']:
        if b['binding_status']!='changed':continue
        p=Path(b['resolved_path'])
        if p.is_relative_to(repo):
            relative=p.relative_to(repo).as_posix();entry=by_pair.get((relative,b['expected_sha256']))
            impacts.append(dict(report=b['report'],source_path=relative,expected_sha256=b['expected_sha256'],
                source_pair_classification=entry['classification'] if entry else 'historical_bytes_not_recovered',
                disposition='file-change triage only; report scope not recertified'))
    summary=[]
    for changed in baseline['changed_files']:
        p=Path(changed['path']);relative=p.relative_to(repo).as_posix() if p.is_relative_to(repo) else None
        own=[r for r in pairs if r['path']==relative] if relative else []
        summary.append(dict(path=str(p),role=changed['role'],report_references=len(changed['report_references']),
            review_lane=review_lane(relative) if relative else 'external_executable_or_capture_version',
            historical_versions=len(changed['historical_expected_sha256']),recovered_versions=sum(r['status']=='exact_bytes_recovered' for r in own),
            pair_classifications=dict(Counter(r['classification'] for r in own)),
            next_action='review changed members and shared contexts' if relative else 'version/capture-specific exact recovery required'))
    return dict(git_head=recovery['git_head'],git_objects_checked=recovery['git_objects_checked'],parser_version=roslyn.get('parser_version'),
        parse_options=roslyn.get('parse_options'),counts=dict(changed_files=len(summary),source_snapshot_pairs=len(pairs),
        recovered_pairs=sum(r['status']=='exact_bytes_recovered' for r in pairs),pair_classifications=dict(Counter(r['classification'] for r in pairs))),
        file_summary=summary,source_pairs=pairs,json_differences=data_rows,report_source_impacts=impacts,external=recovery['external'],
        limitations=['Git is historical simulator source, not game-rule authority.',
            'Only whole-file SHA-256 matches reconstruct a historical snapshot; no time-based substitutions.',
            'Roslyn syntax/member tokens are not semantic compilation, call-graph closure or game-rule equivalence.',
            'Unchanged members may depend on changed fields, dispatchers, callees or client assets.',
            'External executable/capture files require separate exact-version recovery.'])


def write(result,output):
    output.mkdir(parents=True,exist_ok=True);compact=lambda v:json.dumps(v,ensure_ascii=False,separators=(',',':'))
    (output/'review.json').write_bytes(('{\n'+',\n'.join(compact(k)+':'+('[\n'+',\n'.join(compact(r) for r in v)+'\n]' if isinstance(v,list) else compact(v)) for k,v in result.items())+'\n}\n').encode('utf8'))
    for name in ['file_summary','source_pairs','json_differences','report_source_impacts','external']:
        rows=result[name];fields=list(dict.fromkeys(k for r in rows for k in r));stream=io.StringIO(newline='')
        import csv
        writer=csv.DictWriter(stream,fieldnames=fields,delimiter='\t',lineterminator='\n');writer.writeheader()
        for row in rows:
            def cell(v):return 'null' if v is None else compact(v) if isinstance(v,(dict,list)) or isinstance(v,str) and (not v or v[-1:].isspace()) else v
            writer.writerow({k:cell(row.get(k)) for k in fields})
        (output/(name+'.tsv')).write_bytes(b'\xef\xbb\xbf'+stream.getvalue().encode('utf8'))
    c=result['counts'];lines=['# 历史文件与函数范围复核', '',
        '这是模拟器源码/数据变化的溯源，不是客户端规则认证。文件或成员令牌不变，也不能自动认证其调用依赖。', '',
        f"- 变动文件：{c['changed_files']}；历史源码版本对：{c['source_snapshot_pairs']}；精确恢复：{c['recovered_pairs']}。",
        '- 版本对分类：'+compact(c['pair_classifications']), '',
        '- [file_summary.tsv](file_summary.tsv)：26个文件的版本恢复及复核入口。',
        '- [source_pairs.tsv](source_pairs.tsv)：每个精确旧指纹对应的成员变化、添加/删除及共享上下文风险。',
        '- [json_differences.tsv](json_differences.tsv)：卡表与IR的具体JSON指针变化。',
        '- [report_source_impacts.tsv](report_source_impacts.tsv)：变动绑定对原报告的关联；不自动撤销或延续旧结论。',
        '- [external.tsv](external.tsv)：客户端执行程序和运行时捕获的独立版本问题。', '',
        '旧版本仅从当前HEAD可达Git历史及能完全匹配旧报告SHA-256的行尾/BOM重构中恢复；未恢复的不会按日期猜选。',
        'C#使用SDK自带Roslyn，不执行被检查代码。条件编译使用空符号集，禁用源文本和指令进入保守上下文指纹；不宣称所有构建配置语义一致。', '',
        '重新使用范围结论前核对每行current_sha256及基线指纹；源码变化后不能自动延续旧比较结果。', '',
        '```powershell',
        'dotnet build tools/AuditHistory/AuditHistory.csproj -c Release --ignore-failed-sources',
        'dotnet tools/AuditHistory/bin/Release/net10.0/AuditHistory.dll --selftest',
        'python tools/analyze-audit-history.py --cache-dir "D:\\Codex Workshop\\kards-install-review\\source-history-cache"',
        'python tools/test-audit-history.py', '```', '']
    (output/'README.md').write_bytes('\n'.join(lines).encode('utf8'))


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1]);p.add_argument('--cache-dir',type=Path,required=True);p.add_argument('--recover-only',action='store_true');p.add_argument('--reuse-recovery',action='store_true');p.add_argument('--roslyn-dll',type=Path);p.add_argument('--output',type=Path);a=p.parse_args()
    repo=a.repo.resolve();baseline=load(repo/'out/audit/progress-baseline/baseline.json')
    for row in baseline['changed_files']:
        if sha(Path(row['path']).read_bytes())!=row['observed_sha256']:raise ValueError('baseline current file changed; rebuild baseline first: '+row['path'])
    recovery=load(a.cache_dir/'recovery.json') if a.reuse_recovery else recover(repo,baseline,a.cache_dir)
    expected={(Path(r['path']).relative_to(repo).as_posix(),h) for r in baseline['changed_files']
              if Path(r['path']).is_relative_to(repo) for h in r['historical_expected_sha256']}
    actual={(r['path'],r['expected_sha256']) for r in recovery['rows']}
    if actual!=expected or len(actual)!=len(recovery['rows']):raise ValueError('recovery manifest does not match baseline pairs')
    if a.reuse_recovery and subprocess.run(['git','merge-base','--is-ancestor',recovery['git_head'],'HEAD'],cwd=repo).returncode:
        raise ValueError('cached Git ancestry not reachable; recover again')
    if a.recover_only:
        print(json.dumps(dict(rows=len(recovery['rows']),recovered=sum(r['status']=='exact_bytes_recovered' for r in recovery['rows']))));return
    dll=a.roslyn_dll or repo/'tools/AuditHistory/bin/Release/net10.0/AuditHistory.dll'
    subprocess.run(['dotnet',str(dll),str(a.cache_dir/'roslyn-request.json'),str(a.cache_dir/'roslyn-output.json')],check=True)
    result=compare(repo,baseline,recovery,a.cache_dir,load(a.cache_dir/'roslyn-output.json'))
    result['generator_sha256']=sha(Path(__file__).read_bytes());result['baseline_sha256']=sha((repo/'out/audit/progress-baseline/baseline.json').read_bytes())
    result['recovery_manifest_sha256']=sha((a.cache_dir/'recovery.json').read_bytes())
    result['roslyn_output_sha256']=sha((a.cache_dir/'roslyn-output.json').read_bytes())
    result['roslyn_tool_sha256']=sha(dll.read_bytes())
    result['roslyn_dependencies_sha256']={p.name:sha(p.read_bytes()) for p in [dll.parent/'Microsoft.CodeAnalysis.dll',dll.parent/'Microsoft.CodeAnalysis.CSharp.dll']}
    write(result,a.output or repo/'out/audit/change-review');print(json.dumps(result['counts'],indent=2))


if __name__=='__main__':main()
