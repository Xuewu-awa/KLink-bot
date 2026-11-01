from pathlib import Path
import json,hashlib
r=Path('D:/Codex Workshop/KLink-bot');p=r/'out/audit/deepseek-policy-speed-20261102'
read=lambda x:json.loads(x.read_bytes());sha=lambda x:hashlib.sha256(x.read_bytes()).hexdigest()
plan=read(p/'plan.json');meta=read(r/'out/audit/deepseek-policy-eval-20261102/verification.json')['modelMetadata'];c=meta['trainingContract']
assert sha(r/'out/audit/deepseek-policy-eval-20261102/win-model.bin')==plan['modelSha256']
assert sha(r/'out/audit/deepseek-frozen-split-20261102/pilot.bin')==plan['trainingSha256']
assert sha(p/'snapshot/tools/NNTrain/bin/Release/net10.0/NNTrain.dll')==plan['toolSha256']
assert sha(p/'reference-bin/NNTrain.dll')==plan['referenceToolSha256']
assert plan['referenceToolSha256']==read(r/'out/audit/deepseek-policy-eval-20261102/verification.json')['finalToolSha256']
oldseeds={x['seed'] for x in read(r/'out/audit/deepseek-frozen-split-20261102/pilot.bin.quality.json')['games']}
oldseeds|={x['seed'] for i in range(8) for x in read(r/f'out/audit/deepseek-external-eval-20261102/batch-{i}.bin.quality.json')['games']}
oldseeds|={x['seed'] for x in read(r/'out/audit/systematic-rules/policy-eval-plan-20261102.json')['evaluation']['games']}
oldseeds|={x['seed'] for x in read(r/'out/audit/systematic-rules/policy-eval-isolated-plan-20261102.json')['evaluation']['games']}
summaries=[];raw={}
def check(name,g,mode):
    file=p/(name+'.json');j=read(file);raw[name]=j
    assert j['planSha256']==sha(p/'plan.json') and j['modelSha256']==plan['modelSha256'] and j['trainingDataSha256']==plan['trainingSha256']
    assert j['toolSha256']==plan['referenceToolSha256' if mode=='reference' else 'toolSha256']
    assert j['simulatorSha256']==c['SimulatorSha256'] and j['inputs']==c['InputsSha256']
    assert (j['seed'],j['matchup'],j['maxSteps'],j['maxSeconds'])==(g['seed'],g['matchup'],plan['maxSteps'],plan['maxSeconds'])
    if mode!='reference':assert j['schemaVersion']==2 and j['replayMode']==mode
    for key in ['baseline','treatment']:
        a=j[key];assert a['admitted']==(len(a['reasons'])==0) and a['seed']==j['seed']
        assert a['modelLeft']==(key=='treatment') and len(a['trace']) in (a['steps'],a['steps']+1)
        if a['admitted']:
            assert a['winner'] in ['Left','Right'] and a['finalFingerprint'] and a['steps']>0
            if mode!='reference':
                w=a['work'];assert w['verifiedPrefixes']==a['steps']
                assert w['reconstructions']==a['decisions']+a['candidates']+(a['steps'] if mode=='full' else 0)
                if key=='baseline':assert w['replayedSteps']==(a['steps']*(a['steps']+1)//2 if mode=='full' else a['steps'])
        assert a['seconds']>=0
    assert j['comparable']==(j['baseline']['admitted'] and j['treatment']['admitted'])
    assert j['leftWinDelta']==(int(j['treatment']['winner']=='Left')-int(j['baseline']['winner']=='Left') if j['comparable'] else None)
    summary={k:v for k,v in j.items() if k not in ['baseline','treatment']};summary['rawSha256']=sha(file);summary['name']=name
    for key in ['baseline','treatment']:
        summary[key]={k:v for k,v in j[key].items() if k not in ['trace','finalFingerprint']}
        summary[key]['traceSha256']=hashlib.sha256(json.dumps(j[key]['trace'],ensure_ascii=False,separators=(',',':')).encode()).hexdigest()
        summary[key]['finalFingerprintSha256']=hashlib.sha256(j[key]['finalFingerprint'].encode()).hexdigest()
    summaries.append(summary);return j
bench=[]
for i,g in enumerate(plan['benchmark']):
    values={mode:check(f'benchmark-{i}-{mode}',g,mode) for mode in g['order']}
    for mode in ['full','incremental']:
        for key in ['baseline','treatment']:
            for field in ['admitted','reasons','winner','turn','steps','decisions','candidates','rejected','choices','trace','finalFingerprint']:
                assert values[mode][key][field]==values['reference'][key][field],(i,mode,key,field)
    assert all(x['comparable'] for x in values.values())
    bench.append({'seed':g['seed'],'equivalent':True,'timings':{mode:{key:x[key]['seconds'] for key in ['baseline','treatment']} for mode,x in values.items()},'work':{mode:{key:values[mode][key]['work'] for key in ['baseline','treatment']} for mode in ['full','incremental']}})
seen=set();expanded=[]
for i,g in enumerate(plan['expanded']):
    assert g['seed'] not in oldseeds|seen;seen.add(g['seed']);expanded.append(check(f'expanded-{i}',g,'incremental'))
pairs=[x for x in expanded if x['comparable']]
report={'spec':'policy-speed-verification/v1','planSha256':sha(p/'plan.json'),'benchmark':bench,'expandedPairs':len(expanded),'comparablePairs':len(pairs),'modelLeftWins':sum(x['treatment']['winner']=='Left' for x in pairs),'controlLeftWins':sum(x['baseline']['winner']=='Left' for x in pairs),'pairedDeltaSum':sum(x['leftWinDelta'] for x in pairs),'unpairedAttemptsRetained':sum(not x['admitted'] for j in expanded for x in [j['baseline'],j['treatment']]),'summaries':summaries,'checks':'Exact model/data/plan/tool/runtime/input bindings; benchmark complete action trace/fingerprint/outcome/count/health equivalence; all expanded seed disjointness, attempts, work counts and paired deltas.'}
(p/'verification.json').write_bytes((json.dumps(report,ensure_ascii=False,indent=2)+'\n').encode());print(json.dumps({k:v for k,v in report.items() if k not in ['summaries','benchmark']},ensure_ascii=False),flush=True)
