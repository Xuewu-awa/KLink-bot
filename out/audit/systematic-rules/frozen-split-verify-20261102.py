from pathlib import Path
from collections import defaultdict,Counter
import struct,json,hashlib,subprocess,importlib.util
r=Path('D:/Codex Workshop/KLink-bot');p=r/'out/audit/deepseek-frozen-split-20261102';rawp=r/'out/audit/deepseek-repeat-pilot-20261101'
sha=lambda b:hashlib.sha256(b).hexdigest()
read=lambda f:json.loads(f.read_bytes())
b=(p/'pilot.bin').read_bytes();magic,dim,dc=struct.unpack_from('<iii',b);stride=4*(dim+3);gidOffset=dim*4+4
mapping=read(p/'pilot.bin.provenance.json')['mapping'];split=read(p/'pilot.bin.split.json');q=read(p/'pilot.bin.quality.json')
assert len(mapping)==96 and {x['game'] for x in mapping}==set(range(96)) and len({(x['batch'],x['localGame']) for x in mapping})==96
lookup={(x['batch'],x['localGame']):x for x in mapping};expectedVal=[24*i+g for i in range(4) for g in range(6)]
assert split['validationGameIds']==expectedVal and len(split['trainingGameIds'])==72
assert set(split['trainingGameIds']).isdisjoint(expectedVal) and set(split['trainingGameIds'])|set(expectedVal)==set(range(96))
assert split['datasetSha256']==q['dataSha256']==sha(b)
position=12;compared=0
for batch in range(4):
 original=(rawp/f'batch-{batch}.bin').read_bytes();oq=read(rawp/f'batch-{batch}.bin.quality.json');assert original[:12]==b[:12]
 for off in range(12,len(original),stride):
  old=original[off:off+stride];new=b[position:position+stride];local=int(struct.unpack_from('<f',old,gidOffset)[0]);gid=struct.unpack_from('<f',new,gidOffset)[0];m=lookup[batch,local]
  assert gid==m['game'] and old[:gidOffset]==new[:gidOffset] and old[gidOffset+4:]==new[gidOffset+4:]
  compared+=1;position+=stride
 for og in oq['games']:
  ng=dict(q['games'][lookup[batch,og['game']]['game']]);ng['game']=og['game'];assert ng==og
assert position==len(b) and compared==q['rows']==11740
model=(p/'smoke-model.bin').read_bytes();assert struct.unpack_from('<ii',model)==(0x314d4c4b,1)
specLen=struct.unpack_from('<i',model,32)[0];metaLenOffset=36+specLen;metaLen=struct.unpack_from('<i',model,metaLenOffset)[0];meta=json.loads(model[metaLenOffset+4:metaLenOffset+4+metaLen]);(p/'model-meta.json').write_text(json.dumps(meta,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
assert meta['validationGameIds']==expectedVal and meta['trainingGameIds']==split['trainingGameIds']
assert meta['splitSource']=='frozen-manifest/v1' and meta['splitManifestSha256']==sha((p/'pilot.bin.split.json').read_bytes())
assert (meta['epochs'],meta['hidden'],meta['target'],meta['lr'])==(1,16,'pair',0.05)
assert meta['trainingContract']['DatasetSha256']==sha(b) and meta['validationMetricTotal']==1049 and meta['validationMetricCorrect']==599
assert abs(meta['valAccuracy']-599/1049)<1e-12
cm=read(p/'coverage-model.json');cf=read(p/'coverage-manifest.json');assert cm['coverage']==cf['coverage'] and json.loads(json.dumps(meta['pairCoverage']),object_hook=lambda d:{k[0].lower()+k[1:]:v for k,v in d.items()})==cm['coverage'] and cm['simulatorMatchesCurrent']
assert cm['trainingGameIds']==split['trainingGameIds'] and cm['validationGameIds']==expectedVal
for name,rows,games,pairs,cells in [('full',11740,96,3747,123),('training',8457,72,2495,118),('validation',3283,24,1049,103)]:
 c=cm['coverage'][name];assert (c['rows'],c['distinctGames'],c['deterministicPairs'],c['comparableCells'])==(rows,games,pairs,cells)
for name in ['verify','verify-saved']:
 text=(p/(name+'.txt')).read_text(encoding='utf-8');assert '(599/1,049 对，验证集 24 局)' in text
# Original merged output is never overwritten.
f=r/'out/audit/systematic-rules/frozen-pilot-merge-20261102.py';spec=importlib.util.spec_from_file_location('merge',f);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
try:m.merge(r,rawp,p/'pilot.bin');raise RuntimeError('Overwrite accepted')
except AssertionError as e:assert 'Refuse overwriting' in str(e)
assert sha((p/'pilot.bin').read_bytes())==sha(b)
summary=dict(rowsComparedBytewiseExceptGameId=compared,attemptedIdentitiesPreserved=96,trainingGames=72,validationGames=24,validationGameIds=expectedVal,savedMetric=dict(correct=599,total=1049,accuracy=599/1049),processedTrainingUnits=meta['processedTrainingUnits'],modelSha256=sha(model),datasetSha256=sha(b),splitSha256=meta['splitManifestSha256'],checks='Independent raw-byte and per-game metadata comparison, exact saved split/metric/coverage binding, saved-only/file-bound Verify, merge overwrite refusal.')
(p/'verification.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8');print(json.dumps(summary),flush=True)
