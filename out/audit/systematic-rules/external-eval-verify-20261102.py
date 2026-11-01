from pathlib import Path
import json,hashlib,struct,collections,math
r=Path('D:/Codex Workshop/KLink-bot');p=r/'out/audit/deepseek-external-eval-20261102';old=r/'out/audit/deepseek-frozen-split-20261102'
sha=lambda f:hashlib.sha256(f.read_bytes()).hexdigest()
planpath=r/'out/audit/systematic-rules/external-eval-plan-20261102.json';assert sha(planpath)==(p/'plan.sha256').read_text();plan=json.loads(planpath.read_bytes());train=json.loads((old/'pilot.bin.quality.json').read_bytes());trainseeds={g['seed'] for g in train['games']};seen=set();totals=collections.defaultdict(collections.Counter);batches=[];bindings={}
f32=lambda x:struct.unpack('<f',struct.pack('<f',x))[0]
b=(old/'smoke-model.bin').read_bytes();magic,version,dim,hidden,carddim,zones,perside,act=struct.unpack_from('<8i',b);pos=32
length=struct.unpack_from('<i',b,pos)[0];pos+=4;spec=b[pos:pos+length].decode();pos+=length
length=struct.unpack_from('<i',b,pos)[0];pos+=4;meta=json.loads(b[pos:pos+length]);pos+=length
values=struct.unpack_from('<'+'f'*((len(b)-pos)//4),b,pos);off=0
parts=[]
for n in [dim,dim,hidden*dim,hidden,hidden,1]:parts.append(values[off:off+n]);off+=n
mean,std,w1,b1,w2,b2=parts
assert off==len(values) and sha(old/'smoke-model.bin')==plan['modelSha256'] and sha(old/'pilot.bin')==plan['trainingDatasetSha256']
def predict(x):
 z=[f32(f32(a-b)/c) for a,b,c in zip(x,mean,std)];hs=[]
 for j in range(hidden):
  v=b1[j]
  for d in range(dim):v=f32(v+f32(w1[j*dim+d]*z[d]))
  hs.append(max(v,0))
 v=0
 for a,b in zip(w2,hs):v=f32(v+f32(a*b))
 return f32(v+b2[0])
def count(rows,field):
 cells=collections.defaultdict(lambda:[[],[]])
 for row in rows:cells[row['matchup'],row['turn']][0 if row['label']==1 else 1].append(row)
 c=collections.Counter(correct=0,incorrect=0,ties=0,total=0)
 for wins,losses in cells.values():
  for w,l in zip(wins,losses):
   a,b=(0,0) if field is None else (w[field],l[field]);c['correct' if a>b else 'incorrect' if a<b else 'ties']+=1;c['total']+=1
 return dict(c)
for batch in plan['batches']:
 i=batch['id'];data=p/f'batch-{i}.bin';qp=Path(str(data)+'.quality.json');ep=p/f'batch-{i}-eval.json';dp=Path(str(data)+'.decks.json');q=json.loads(qp.read_bytes());e=json.loads(ep.read_bytes());decks=json.loads(dp.read_bytes());raw=data.read_bytes()
 assert q['dataSha256']==e['datasetSha256']==sha(data) and e['qualitySha256']==sha(qp)
 assert q['inputsSha256']['KLink.Bot.dll']==plan['simulatorSha256'];assert q['inputsSha256']==json.loads((p/'batch-0.bin.quality.json').read_bytes())['inputsSha256']
 assert q['requestedGames']==12 and len(q['games'])==12 and q['seed']==batch['seed']
 for g in q['games']:
  assert g['seed']==batch['seed']+g['game'] and g['seed'] not in trainseeds|seen;seen.add(g['seed'])
 assert len(decks['pairs'])==12
 for d in decks['pairs']:assert d[1]==batch['left'] and d[2]==batch['right']
 assert struct.unpack_from('<iii',raw)==(0x324c4b41,dim,22);stride=4*(dim+3);assert len(raw)==12+q['rows']*stride
 rows=e['rows'];assert len(rows)==q['rows'];counts=collections.Counter();labels={};byid={g['game']:g for g in q['games']};samples=0
 for rowidx,row in enumerate(rows):
  vals=struct.unpack_from('<'+'f'*(dim+3),raw,12+rowidx*stride);x=vals[:dim];label,gid,pair=vals[dim:]
  assert all(math.isfinite(v) for v in vals) and row['game']==gid and row['label']==label and row['matchup']==pair==batch['left']*22+batch['right'] and row['turn']==round(f32(x[0]*30))
  assert f32(row['hqScore'])==f32(x[3]-x[3+perside]);assert byid[gid]['accepted'];counts[gid]+=1
  if gid in labels:assert labels[gid]==label
  labels[gid]=label
  if rowidx in {0,len(rows)//4,len(rows)//2,3*len(rows)//4,len(rows)-1}:
   expected=predict(x);assert math.isclose(expected,row['modelScore'],abs_tol=1e-5,rel_tol=1e-5),(i,rowidx,expected,row['modelScore']);samples+=1
 for g in q['games']:assert counts[g['game']]==g['writtenSamples']
 metrics={name:count(rows,field) for name,field in [('model','modelScore'),('hqDifference','hqScore'),('constant',None)]}
 for name,c in metrics.items():
  assert all(e['aggregate'][name][k]==v for k,v in c.items());totals[name].update(c)
  totals[('seen' if i<4 else 'new')+'-'+name].update(c)
 assert e['aggregate']['unpairedRows']==len(rows)-2*metrics['model']['total']
 batches.append(dict(batch=i,left=batch['left'],right=batch['right'],attempted=12,accepted=q['acceptedGames'],rejected=q['rejectedGames'],rows=len(rows),modelChecks=samples,metrics=metrics))
 for f in [data,qp,ep,dp]:bindings[f.relative_to(r).as_posix()]=sha(f)
result=dict(spec='external-evaluation-independent-verification/v1',planSha256=sha(planpath),batches=batches,totals={k:dict(v) for k,v in totals.items()},attempted=len(seen),accepted=sum(b['accepted'] for b in batches),rows=sum(b['rows'] for b in batches),sourceBindings=bindings,modelSpotchecks=sum(b['modelChecks'] for b in batches),scope='All raw rows, game counts, seeds, pair keys and HQ scores checked; 40 model logits independently recomputed with float32 arithmetic. Counts independently rebuilt in Python; no rule or policy certification.')
(p/'verification.json').write_bytes((json.dumps(result,indent=2)+'\n').encode());print(json.dumps({k:v for k,v in result.items() if k not in ['batches','sourceBindings']},indent=2))
