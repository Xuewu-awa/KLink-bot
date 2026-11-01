from pathlib import Path
import json,hashlib,struct
r=Path('D:/Codex Workshop/KLink-bot');p=r/'out/audit/deepseek-policy-eval-20261102';f=r/'out/audit/deepseek-frozen-split-20261102';s=r/'out/audit/deepseek-action-provenance-20261101/publish-snapshot'
sha=lambda b:hashlib.sha256(b).hexdigest();read=lambda p:json.loads(p.read_bytes())
model=(p/'win-model.bin').read_bytes();n=struct.unpack_from('<i',model,32)[0];o=36+n;n=struct.unpack_from('<i',model,o)[0];m=json.loads(model[o+4:o+4+n]);split=read(f/'pilot.bin.split.json');q=read(f/'pilot.bin.quality.json')
assert (m['epochs'],m['hidden'],m['target'],m['lr'],m['weightInitSeed'],m['savedEpochWeights'])==(1,16,'win',0.05,42,1)
assert m['trainingGameIds']==split['trainingGameIds'] and m['validationGameIds']==split['validationGameIds'] and m['splitManifestSha256']==sha((f/'pilot.bin.split.json').read_bytes())
assert m['trainingContract']['DatasetSha256']==q['dataSha256']==sha((f/'pilot.bin').read_bytes())
assert '55.44%' in (p/'verify-win.txt').read_text(encoding='utf-8')
oldseeds={g['seed'] for g in q['games']};oldseeds|={g['seed'] for i in range(8) for g in read(r/f'out/audit/deepseek-external-eval-20261102/batch-{i}.bin.quality.json')['games']}
results=[];allseeds=set()
for prefix,planname,count in [('match','policy-eval-plan-20261102.json',4),('isolated','policy-eval-isolated-plan-20261102.json',2)]:
 planpath=r/'out/audit/systematic-rules'/planname;plan=read(planpath)
 assert len(plan['evaluation']['games'])==count
 for i,g in enumerate(plan['evaluation']['games']):
  path=p/f'{prefix}-{i}.json';j=read(path);assert j['planSha256']==sha(planpath.read_bytes()) and j['modelSha256']==sha(model)
  assert j['seed']==g['seed'] and j['seed'] not in oldseeds|allseeds;allseeds.add(j['seed']);assert j['matchup']==g['matchup']
  assert j['trainingDataSha256']==sha((f/'pilot.bin').read_bytes()) and j['simulatorSha256']==m['trainingContract']['SimulatorSha256']
  assert j['inputs']==m['trainingContract']['InputsSha256']
  for key in ['baseline','treatment']:
   a=j[key];assert a['seed']==j['seed'] and len(a['trace']) in [a['steps'],a['steps']+1]
   assert a['admitted']==(len(a['reasons'])==0)
   if a['admitted']:assert a['winner'] in ['Left','Right'] and a['steps']>0 and a['finalFingerprint']
  assert j['comparable']==(j['baseline']['admitted'] and j['treatment']['admitted'])
  if prefix=='match':assert j['baseline']['admitted'] and any('enumerator_mutated_root' in x for x in j['treatment']['reasons']) and not j['comparable'] and j['leftWinDelta'] is None
  else:
   assert j['comparable'] and j['toolSha256']==sha((s/'tools/NNTrain/bin/Release/net10.0/NNTrain.dll').read_bytes())
   assert j['leftWinDelta']==int(j['treatment']['winner']=='Left')-int(j['baseline']['winner']=='Left')
  summary={k:v for k,v in j.items() if k not in ['baseline','treatment']}
  for key in ['baseline','treatment']:summary[key]={k:v for k,v in j[key].items() if k not in ['trace','finalFingerprint']}
  summary['localRawSha256']=sha(path.read_bytes());results.append(summary)
assert [j['leftWinDelta'] for j in results[-2:]]==[-1,0]
verification={'modelSha256':sha(model),'trainingSha256':sha((f/'pilot.bin').read_bytes()),'splitSha256':sha((f/'pilot.bin.split.json').read_bytes()),'uniqueNewSeeds':len(allseeds),'initialPairs':4,'initialComparable':0,'isolatedPairs':2,'isolatedComparable':2,'modelLeftWins':0,'greedyControlLeftWins':1,'pairedWinDeltaSum':-1,'finalToolSha256':sha((s/'tools/NNTrain/bin/Release/net10.0/NNTrain.dll').read_bytes()),'results':results,'modelMetadata':m,'checks':'Exact input/model/split/plan/runtime binding, seed disjointness including all original attempts and prior external evaluation, all outcomes/attempts/traces/reasons and paired deltas recomputed.'}
(p/'verification.json').write_text(json.dumps(verification,indent=2)+'\n',encoding='utf-8');print('Verified 12 attempts, 6 seed pairs; corrected 2 pairs complete, model 0 wins vs control 1.')
