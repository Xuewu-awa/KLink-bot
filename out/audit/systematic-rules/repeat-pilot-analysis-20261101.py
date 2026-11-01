"""Recompute the frozen pilot from local raw binaries. Never resplit or run matches."""
from pathlib import Path
from collections import Counter, defaultdict
import argparse, hashlib, json, math, struct

def sha(b):return hashlib.sha256(b).hexdigest()
def scope(rows):
    cells=defaultdict(list)
    for row in rows:cells[row['matchup'],row['turn']].append(row)
    result=[]
    for (matchup,turn),group in sorted(cells.items()):
        w=[x for x in group if x['win']];l=[x for x in group if not x['win']]
        result.append(dict(matchup=matchup,turn=turn,winRows=len(w),lossRows=len(l),winGames=len({tuple(x['game']) for x in w}),lossGames=len({tuple(x['game']) for x in l}),pairs=min(len(w),len(l))))
    pairs=sum(c['pairs'] for c in result)
    return dict(rows=len(rows),distinctGames=len({tuple(x['game']) for x in rows}),cells=result,comparableCells=sum(c['pairs']>0 for c in result),deterministicPairs=pairs,pairedRows=2*pairs,unpairedRows=len(rows)-2*pairs)

def analyze(repo,packet):
    planpath=repo/'out/audit/systematic-rules/repeat-pilot-plan-20261101.json';plan=json.loads(planpath.read_bytes())
    assert sha(planpath.read_bytes())==(packet/'plan.sha256').read_text()
    val=set(plan['validation_game_ids']);train=set(plan['training_game_ids']);n=plan['games_per_matchup']
    assert not val&train and val|train==set(range(n))
    statuses=json.loads((packet/'statuses.json').read_bytes());assert len(statuses)==len(plan['batches'])
    allrows=[];batches=[];bindings={};inputs=None;reasons=Counter();calls=Counter()
    simulator=repo/'out/audit/deepseek-action-provenance-20261101/publish-snapshot/tools/NNTrain/bin/Release/net10.0/KLink.Bot.dll'
    for batch,status in zip(plan['batches'],statuses):
        bid=batch['id'];assert status['batch']==bid and status['exit_code'] in (0,2)
        path=packet/f'batch-{bid}.bin';raw=path.read_bytes();qpath=Path(str(path)+'.quality.json');mpath=Path(str(path)+'.decks.json')
        q=json.loads(qpath.read_bytes());m=json.loads(mpath.read_bytes());magic,dim,dc=struct.unpack_from('<iii',raw)
        assert magic==0x324c4b41 and dim==q['dim'] and dc==q['deckCount'] and q['gateVersion']==2
        assert q['dataSha256']==sha(raw) and len(raw)==12+q['rows']*4*(dim+3)
        assert q['samplingMode']=='policy-and-choice-before/v1' and q['choicePolicy']=='first-index/v1' and q['observationPerspective']=='left'
        assert q['inputsSha256']['KLink.Bot.dll']==sha(simulator.read_bytes())
        if inputs is None:inputs=q['inputsSha256']
        assert q['inputsSha256']==inputs
        assert q['requestedGames']==n and m['pairs']==[[g,batch['left'],batch['right']] for g in range(n)]
        games={g['game']:g for g in q['games']};assert len(games)==len(q['games'])==n and set(games)==set(range(n))
        assert q['acceptedGames']==sum(g['accepted'] for g in games.values()) and q['rejectedGames']==n-q['acceptedGames']
        assert status['exit_code']==(2 if q['rejectedGames'] or not q['rows'] else 0)
        rr=[];labels=defaultdict(set);counts=Counter()
        for off in range(12,len(raw),4*(dim+3)):
            a=struct.unpack_from('<'+'f'*(dim+3),raw,off);assert all(math.isfinite(x) for x in a)
            y,gid,mid=a[dim:];assert y in (0,1) and gid.is_integer() and mid==batch['left']*dc+batch['right']
            g=int(gid);assert g in games and games[g]['accepted'];turn=round(a[0]*30);assert turn>=0
            rr.append(dict(game=[bid,g],win=int(y),matchup=int(mid),turn=turn,partition='validation' if g in val else 'training'))
            labels[g].add(y);counts[g]+=1
        for g,game in games.items():
            assert game['seed']==batch['seed']+g and game['leftDeck']==m['decks'][batch['left']] and game['rightDeck']==m['decks'][batch['right']]
            assert counts[g]==game['writtenSamples']
            if game['accepted']:
                assert len(labels[g])==1 and not game['reasons'] and counts[g]>0 and game['observedSamples']==counts[g]
                assert all(game[k]==0 for k in ['vmFaults','choiceFaults','driverFaults','pendingChoices']) and not game['driverError']
                assert not any(v>0 for v in game['unsupportedOps'].values())
                assert not any(v>0 and not (k.startswith('<local-ran:') and k.endswith('>') or k in ('<frontline-blocked-full>','<frontline-blocked-by-opponent>')) for k,v in game['calls'].items())
                assert len(game['sampleActors'])==len(game['sampleKinds'])==counts[g] and set(game['sampleActors'])<={1,2} and set(game['sampleKinds'])<={1,2}
                assert game['mainDecisions']>=game['sampleKinds'].count(1) and game['choiceDecisions']>=game['sampleKinds'].count(2)
            else:assert counts[g]==0 and game['reasons']
            reasons.update(game['reasons']);calls.update(game['calls'])
        assert len(rr)==q['rows']==sum(counts.values())
        partitions={}
        for name,ids in [('training',train),('validation',val)]:
            accepted=[g for g in sorted(ids) if games[g]['accepted']]
            partitions[name]=dict(planned=len(ids),accepted=len(accepted),rejected=len(ids)-len(accepted),gameIds=accepted,leftWins=sum(labels[g]=={1} for g in accepted),rightWins=sum(labels[g]=={0} for g in accepted),coverage=scope([row for row in rr if row['partition']==name]))
        batches.append(dict(batch=bid,left=m['decks'][batch['left']],right=m['decks'][batch['right']],requested=n,accepted=q['acceptedGames'],rejected=q['rejectedGames'],coverage=scope(rr),partitions=partitions))
        for f in [path,qpath,mpath]:bindings[f.relative_to(repo).as_posix()]=sha(f.read_bytes())
        allrows+=rr
    full=scope(allrows);tr=scope([x for x in allrows if x['partition']=='training']);va=scope([x for x in allrows if x['partition']=='validation'])
    trkeys={(c['matchup'],c['turn']) for c in tr['cells'] if c['pairs']}
    report=dict(plan_sha256=sha(planpath.read_bytes()),inputsSha256=inputs,raw_bindings=bindings,attempted=len(batches)*n,accepted=sum(b['accepted'] for b in batches),rejected=sum(b['rejected'] for b in batches),batches=batches,coverage=dict(full=full,training=tr,validation=va,lostPairsBySplit=full['deterministicPairs']-tr['deterministicPairs']-va['deterministicPairs'],validationPairsOutsideTrainComparableCells=sum(c['pairs'] for c in va['cells'] if (c['matchup'],c['turn']) not in trkeys)),reasons=dict(reasons),calls=dict(calls),limitations=['Execution health and observation coverage only; not game-rule or playing-strength certification.','Pairs count correlated observation rows, not independent games or training updates.','Fixed four-matchup pilot is not a representative meta sample. No favorable reseeding, rejected-game replacement or holdout search.','Raw binaries remain unpublished local evidence, bound by SHA-256. Quality manifests and compact row metadata are published.','This frozen split has not been wired into model training; existing random-split trainer must not be represented as using it.'])
    return report,allrows

if __name__=='__main__':
    a=argparse.ArgumentParser();a.add_argument('--repo',type=Path,required=True);a.add_argument('--packet',type=Path,required=True);o=a.parse_args()
    report,rows=analyze(o.repo,o.packet)
    for name,obj in [('analysis.json',report),('rows.json',rows)]:
        (o.packet/name).write_bytes((json.dumps(obj,ensure_ascii=False,indent=2)+'\n').encode())
    print(json.dumps({k:{f:v for f,v in report['coverage'][k].items() if f!='cells'} for k in ['full','training','validation']}))
