"""One-shot export of the predeclared pilot. No sampling, retries or split search."""
from pathlib import Path
import argparse,copy,hashlib,importlib.util,json,struct

def sha(b):return hashlib.sha256(b).hexdigest()
def merge(repo,packet,out):
    planpath=repo/'out/audit/systematic-rules/repeat-pilot-plan-20261101.json'
    plan=json.loads(planpath.read_bytes())
    spec=importlib.util.spec_from_file_location('pilot',repo/'out/audit/systematic-rules/repeat-pilot-analysis-20261101.py')
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    analysis,_=module.analyze(repo,packet) # Validate every raw source and admission boundary first.
    paths=[out,Path(str(out)+'.quality.json'),Path(str(out)+'.split.json'),Path(str(out)+'.provenance.json')]
    assert not any(f.exists() for f in paths),'Refuse overwriting any existing result'
    result=bytearray();quality=None;mapping=[];validation=[];training=[];sources={};next_id=0
    for batch in plan['batches']:
        path=packet/f"batch-{batch['id']}.bin";raw=path.read_bytes();qpath=Path(str(path)+'.quality.json');q=json.loads(qpath.read_bytes())
        magic,dim,count=struct.unpack_from('<iii',raw);stride=4*(dim+3)
        if quality is None:
            result+=raw[:12];quality=copy.deepcopy(q);quality.update(seed=0,requestedGames=0,acceptedGames=0,rejectedGames=0,rows=0,games=[])
            quality['scope']='Merged frozen pilot execution diagnostics only; see provenance for original seeds and composite identities. Not rule certification.'
        else:
            assert result[:12]==raw[:12] and quality['inputsSha256']==q['inputsSha256']
        remap={}
        for game in q['games']:
            gid=next_id;next_id+=1;remap[game['game']]=gid
            mapping.append(dict(game=gid,batch=batch['id'],localGame=game['game'],seed=game['seed'],accepted=game['accepted']))
            if game['accepted']:
                (validation if game['game'] in plan['validation_game_ids'] else training).append(gid)
            g=copy.deepcopy(game);g['game']=gid;quality['games'].append(g)
        for off in range(12,len(raw),stride):
            row=bytearray(raw[off:off+stride]);local=struct.unpack_from('<f',row,dim*4+4)[0]
            struct.pack_into('<f',row,dim*4+4,remap[int(local)]);result+=row
        for k in ['requestedGames','acceptedGames','rejectedGames','rows']:quality[k]+=q[k]
        for f in [path,qpath,Path(str(path)+'.decks.json')]:sources[f.relative_to(repo).as_posix()]=sha(f.read_bytes())
    assert training and validation,'No model-ready export when either accepted partition is empty; preserve diagnostic outputs'
    quality['dataSha256']=sha(result)
    split=dict(schemaVersion=1,datasetSha256=sha(result),sourcePlanSha256=sha(planpath.read_bytes()),trainingGameIds=training,validationGameIds=validation)
    provenance=dict(spec='frozen-pilot-merge/v1',sourcePlanSha256=sha(planpath.read_bytes()),sourceBindings=sources,mapping=mapping,datasetSha256=sha(result),
        transformation='Concatenate source rows in original batch order; replace only local game float with unique integer mapped from (batch,local-game). Preserve all other bytes, all attempted games and original seeds.',
        holdout='Frozen planned local IDs intersect admitted games, no replacement or re-splitting.',scope='Pipeline smoke dataset, not authoritative KARDS rules or representative meta coverage.')
    blobs=[bytes(result),*( (json.dumps(j,ensure_ascii=False,indent=2)+'\n').encode() for j in [quality,split,provenance])]
    out.parent.mkdir(parents=True,exist_ok=True)
    for path,blob in zip(paths,blobs):
        with path.open('xb') as f:f.write(blob)
    return dict(rows=quality['rows'],attempted=quality['requestedGames'],accepted=quality['acceptedGames'],trainingGames=len(training),validationGames=len(validation),datasetSha256=sha(result),splitSha256=sha(blobs[2]))

if __name__=='__main__':
    a=argparse.ArgumentParser();a.add_argument('--repo',type=Path,required=True);a.add_argument('--packet',type=Path,required=True);a.add_argument('--out',type=Path,required=True);o=a.parse_args()
    print(json.dumps(merge(o.repo,o.packet,o.out)))
