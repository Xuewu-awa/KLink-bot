"""Offline execution of captured IsDeckValid control flow with aggregation stubbed.

This is audit tooling, not a production legality implementation. All client helpers
are supplied fixtures. The card aggregation block [6180,12005) is NOT modeled.
"""
import argparse
import hashlib
import itertools
import json
from pathlib import Path

def load(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))

def execute(function, fixture):
    nodes={n['StatementIndex']:n for n in function['decoded']}; following=dict(zip(nodes,list(nodes)[1:]));trace=[]
    env={'DeckHeader':{'Name':'fixture custom deck'},'deckContainsReserved':False,'hasBadElites':False,
         'CardCount':0,'hasLocation':False,'factions':[], 'allyCardCount':0,'raritiesInDeck':set(),
         'typesInDeck':set(),'typeCounts':{},'_skirmishReason':'initial-skirmish','_knockoutReason':'initial-knockout'}
    obj={'tournamentID_beingWatched':fixture['tournament'],'hasBadRarity':False};flow=[]
    sk_rules=fixture['sk_rules'];tournament={'Rules':fixture['tournament_rules']}
    events=[];aggregate_visits=0
    def put(n,v):
        assert n['Inst'] in {'LocalVariable','LocalOutVariable','InstanceVariable'}
        (obj if n['Inst']=='InstanceVariable' else env)[n['Variable Name']]=v
    def val(n,receiver=None):
        k=n['Inst']
        if k in {'LocalVariable','LocalOutVariable'}:return env[n['Variable Name']]
        if k=='InstanceVariable':return (obj if receiver is None else receiver)[n['Variable Name']]
        if k=='Context':return val(n['Expression'],val(n['Context']))
        if k=='StructMemberContext':return val(n['StructExpression'])[n['Property Name']]
        if k in {'Self','ObjectConst'}:return 'fixture-service'
        if k in {'True','False'}:return k=='True'
        if k in {'IntConst','ByteConst','StringConst','NameConst'}:return n['Value']
        if k=='TextConst':return n.get('SourceString','')
        if k in {'CallMath','FinalFunction','LocalVirtualFunction'}:return call(n)
        raise AssertionError(n)
    def call(n):
        name=n.get('Function',n.get('FunctionName'));p=n.get('Parameters',[])
        if name=='IsChinaISBN':put(p[-1],fixture['basic']);return
        if name=='CheckUnlockingDecks':put(p[-1],fixture['unlocked']);return
        if name=='GetActorOfClass':return 'fixture-collection'
        if name=='GetPlatformEnum':put(p[-1],0);return
        if name=='GetDSession':put(p[-1],{'current_mini_sit_n_go':{'Rules':sk_rules}});return
        if name=='GetTournamentByID':put(p[-1],tournament);return
        if name=='JsonHasField':return val(p[1]) in val(p[0])
        if name=='Conv_JsonObjectToJsonValue':return val(p[0])[val(p[1])]
        if name=='Conv_JsonValueToObject':return val(p[0])
        if name=='checkCustomRules':
            val(p[0]);mode='skirmish' if p[-3]['Variable Name']=='CallFunc_checkCustomRules_isValid_1' else 'knockout';events.append(mode)
            put(p[-3],fixture[mode+'_custom_valid']);put(p[-2],mode+'-header');put(p[-1],mode+'-custom-reason');return
        if name=='MakeLiteralText':return val(p[0])
        if name=='Array_Length':return len(val(p[0]))
        a=[val(x) for x in p]
        if name in {'NotEqual_IntInt','NotEqual_ByteByte','NotEqual_StriStri'}:return a[0]!=a[1]
        if name=='Greater_IntInt':return a[0]>a[1]
        if name=='BooleanAND':return a[0] and a[1]
        if name=='Not_PreBool':return not a[0]
        raise AssertionError(n)
    pc=0
    while pc is not None:
        trace.append(pc);assert len(trace)<600
        if pc==6180:
            aggregate_visits+=1
            # The real card loop computes these values; supply them, then execute
            # its normal continuation. No ownership/static lookup code is tested.
            env.update(hasBadElites=fixture['bad_elites'],CardCount=fixture['count'],hasLocation=fixture['location'],
                       factions=list(range(fixture['factions'])),allyCardCount=fixture['allies'],
                       deckContainsReserved=fixture['reserved'],hasUnownedCards=fixture['unowned'])
            obj['hasBadRarity']=fixture['bad_rarity'];pc=flow.pop();continue
        n=nodes[pc];k=n['Inst'];nxt=following.get(pc)
        assert pc<6180 or pc>=12005,pc
        if k in {'Return','EndOfScript'}:break
        if k in {'Let','LetBool','LetObj'}:put(n['Variable'],val(n['Expression']))
        elif k=='PushExecutionFlow':flow.append(n['Offset'])
        elif k=='PopExecutionFlow':nxt=flow.pop()
        elif k=='PopExecutionFlowIfNot':
            if not val(n['Condition']):nxt=flow.pop()
        elif k=='Jump':nxt=n['Offset']
        elif k=='JumpIfNot':
            if not val(n['Condition']):nxt=n['Offset']
        else:val(n)
        pc=nxt
    keys=['IsValid','Reason','containsReserved','skirmishIsValid','skirmishReason','knockoutIsValid','knockoutReason']
    return {k:env[k] for k in keys},trace,events,aggregate_visits

def expected(f):
    reason=None
    if not f['basic'] and not f['unlocked']:reason='Play Starter Campaign to unlock'
    elif f['bad_elites']:reason='Includes ally Elite cards'
    elif f['bad_rarity']:reason='Wrong rarity count'
    elif f['count']!=40:reason='Deck not 40 cards'
    elif not f['location'] or f['factions']>2:reason='Please delete deck'
    if reason:
        keep=reason in {'Includes ally Elite cards','Wrong rarity count','Deck not 40 cards'}
        return dict(IsValid=False,Reason=reason,containsReserved=f['reserved'] if keep else False,
                    skirmishIsValid=False,skirmishReason=reason,knockoutIsValid=False,knockoutReason=reason)
    normal=True;nr='';sk=f['skirmish_custom_valid'];sr='skirmish-custom-reason'
    kr='initial-knockout';kn=True;tr=f['tournament_rules'];deck=tr.get('deck_rules',{})
    if f['tournament']>0:
        kn=f['knockout_custom_valid'] if 'deck_rules' in tr else not f['reserved']
        kr='knockout-custom-reason' if 'deck_rules' in tr else 'Contains Reserved cards'
    for failed,reason,key in [(f['allies']>12,'Too many ally cards','allow_more_ally_cards'),(f['unowned'],'Includes unowned cards','allow_unowned')]:
        if failed:
            if normal:normal=False;nr=reason
            if sk and key not in f['sk_rules']:sk=False;sr=reason
            if kn and f['tournament']>0 and ('deck_rules' not in tr or key not in deck):kn=False;kr=reason
    return dict(IsValid=normal,Reason=nr,containsReserved=f['reserved'],skirmishIsValid=sk,skirmishReason=sr,knockoutIsValid=kn,knockoutReason=kr)

def fixture():
    return dict(basic=False,unlocked=True,bad_elites=False,bad_rarity=False,count=40,location=True,factions=2,
                allies=12,reserved=False,unowned=False,tournament=0,sk_rules={},tournament_rules={},skirmish_custom_valid=True,knockout_custom_valid=True)

def verify(function):
    digest=hashlib.sha256();count=0;visited=set()
    def check(f):
        nonlocal count
        actual,trace,events,agg=execute(function,f);assert actual==expected(f),(f,actual,expected(f))
        assert agg==(0 if not f['basic'] and not f['unlocked'] else 1)
        visited.update(trace);digest.update(json.dumps([f,actual,trace,events],sort_keys=True).encode());count+=1
    for basic,unlocked,bad_elites,bad_rarity,reserved,location in itertools.product([False,True],repeat=6):
        for cards,factions in itertools.product([39,40,41],[1,2,3]):
            f=fixture();f.update(basic=basic,unlocked=unlocked,bad_elites=bad_elites,bad_rarity=bad_rarity,reserved=reserved,location=location,count=cards,factions=factions);check(f)
    # Field presence and field value are deliberately varied independently.
    policies=[{}, {'allow_more_ally_cards':False}, {'allow_unowned':False}, {'allow_more_ally_cards':True,'allow_unowned':True}]
    for allies,unowned,reserved,tournament,sv,kv,sk,td in itertools.product([12,13],[False,True],[False,True],[0,1],[False,True],[False,True],policies,[None]+policies):
        f=fixture();f.update(allies=allies,unowned=unowned,reserved=reserved,tournament=tournament,skirmish_custom_valid=sv,knockout_custom_valid=kv,sk_rules=sk,tournament_rules={} if td is None else {'deck_rules':td});check(f)
    return dict(cases=count,case_digest_sha256=digest.hexdigest(),visited_statement_offsets=sorted(visited),
                function_sha256=function['script_sha256'],scope='Fixture platform=0, nonstarter deck name; captured control flow with card aggregation block stubbed and custom-rule services supplied. Not full deck legality or actual session policy.')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--capture',required=True);p.add_argument('--output',required=True);a=p.parse_args();d=load(a.capture)
    f=next(x for x in d['functions'] if x['owner']=='BP_DeckAndCollectionLoader_C' and x['name']=='IsDeckValid')
    raw=bytes.fromhex(f['script_hex']);assert f['decode_complete'] and len(raw)==f['script_size'] and hashlib.sha256(raw).hexdigest()==f['script_sha256']
    result=verify(f);result['capture_sha256']=hashlib.sha256(Path(a.capture).read_bytes()).hexdigest();result['generator_sha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    Path(a.output).write_bytes((json.dumps(result,ensure_ascii=False,indent=2)+'\n').encode('utf8'));print(json.dumps({k:v for k,v in result.items() if k!='visited_statement_offsets'}))
