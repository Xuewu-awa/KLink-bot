"""Restricted offline AST executor for SCOPE-003. Services use explicit fixtures.

No client calls. Does not certify native lookups, enum display names, JSON/map
library error behavior, invalid inputs, or complete deck/mode legality.
"""
import argparse
import hashlib
import itertools
import json
from pathlib import Path

HELPERS=('IsRarityOK','skirmishMinCardsOfType','Check Custom Rules Max Cards Of Type')
def load(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))

class Executor:
    def __init__(self,functions,services=None):
        self.functions=functions;self.services=services or {};self.env={};self.obj={};self.trace=[];self.events=[]
    def value(self,n,receiver=None):
        k=n['Inst']
        if k in {'LocalVariable','LocalOutVariable'}:return self.env[n['Variable Name']]
        if k=='InstanceVariable':return (self.obj if receiver is None else receiver)[n['Variable Name']]
        if k=='Context':return self.value(n['Expression'],self.value(n['Context']))
        if k=='StructMemberContext':return self.value(n['StructExpression'])[n['Property Name']]
        if k in {'Self','ObjectConst'}:return 'fixture-service'
        if k in {'True','False'}:return k=='True'
        if k=='TextConst':return n.get('SourceString','')
        if k.endswith('Const') and 'Value' in n:return n['Value']
        if k=='StructConst':return [self.value(v) for v in n['Values']]
        if k in {'CallMath','FinalFunction','LocalVirtualFunction'}:return self.call(n)
        raise AssertionError(n)
    def put(self,n,value):
        assert n['Inst'] in {'LocalVariable','LocalOutVariable','InstanceVariable'}
        (self.obj if n['Inst']=='InstanceVariable' else self.env)[n['Variable Name']]=value
    def call(self,n):
        name=n.get('Function',n.get('FunctionName'));p=n.get('Parameters',[]);v=self.value
        if name=='PrintString':self.events.append('diagnostic-only');return
        if name=='GetEnumeratorValueFromIndex':return self.services['enum_values'][v(p[1])]
        if name=='GetValidValue':return self.services['valid_values'][v(p[1])]
        if name=='GetEnumeratorUserFriendlyName':return self.services['enum_labels'][v(p[1])] if 'enum_labels' in self.services else 'fixture-rarity-label'
        if name=='GetTypeAsText':self.put(p[-1],'display-'+str(v(p[0])));return
        if name=='JsonHasField':return v(p[1]) in v(p[0])
        if name=='Conv_JsonObjectToJsonValue':return v(p[0])[v(p[1])]
        if name in {'Conv_JsonValueToObject','Conv_JsonValueToInteger','MakeLiteralInt','Conv_ByteToInt','GetValidValue'}:return v(p[0])
        if name=='Conv_IntToByte':return v(p[0]) & 255
        if name in {'Conv_IntToString','Conv_TextToString','Conv_StringToName'}:return str(v(p[0]))
        if name=='Concat_StrStr':return v(p[0])+v(p[1])
        if name=='GetBPLevelParameters':self.put(p[-1],self.services['level_parameters']);return
        if name.startswith('GetStatic'):
            field={'GetStaticCardSet':'set','GetStaticFaction':'faction','GetStaticExileFaction':'exile','GetStaticRarity':'rarity','GetStaticType':'type'}[name]
            self.put(p[-1],self.services['static_cards'][v(p[0])][field]);return
        if name=='FactionStringToEnum':self.put(p[-1],self.services['main_faction']);return
        if name=='IsCardReserved':self.events.append(('reserved',v(p[0])));self.put(p[-1],self.services['static_cards'][v(p[0])]['reserved']);return
        if name=='IsRarityOK':
            child=Executor(self.functions);child.env=dict(rarity=v(p[0]),count=v(p[1]),isOK=False);child.run('IsRarityOK');self.put(p[-1],child.env['isOK']);return
        if name=='Array_Length':return len(v(p[0]))
        if name=='Array_Get':self.put(p[-1],v(p[0])[v(p[1])]);return
        if name=='Array_AddUnique':
            a=v(p[0]);x=v(p[1])
            if x not in a:a.append(x)
            return a.index(x)
        if name=='Map_Find':
            a=v(p[0]);key=v(p[1]);default={'count':0,'gold_card_count':0} if p[-1]['Variable Name'].endswith('_1') else 0
            self.put(p[-1],a.get(key,default));return key in a
        if name=='Map_Add':v(p[0])[v(p[1])]=v(p[2]);return
        if name=='Set_Add':v(p[0]).add(v(p[1]));return
        if name=='Set_Contains':return v(p[1]) in v(p[0])
        if name=='StartsWith':
            assert v(p[2])==1 # fixtures are lowercase; case behavior is not inferred
            return v(p[0]).startswith(v(p[1]))
        a=[v(x) for x in p]
        if name in {'NotEqual_ByteByte','NotEqual_IntInt'}:return a[0]!=a[1]
        if name in {'EqualEqual_ByteByte','EqualEqual_IntInt'}:return a[0]==a[1]
        if name=='Greater_IntInt':return a[0]>a[1]
        if name=='Less_IntInt':return a[0]<a[1]
        if name=='Add_IntInt':
            result=a[0]+a[1];assert -(2**31)<=result<2**31;return result
        if name=='BooleanOR':return a[0] or a[1]
        if name=='Not_PreBool':return not a[0]
        raise AssertionError(n)
    def run(self,name,start=None,flow=None,stop=None):
        ns={n['StatementIndex']:n for n in self.functions[name]['decoded']};nxt=dict(zip(ns,list(ns)[1:]));pc=next(iter(ns)) if start is None else start;flow=[] if flow is None else list(flow)
        while pc is not None and pc!=stop:
            self.trace.append(pc);assert len(self.trace)<15000,(name,pc)
            n=ns[pc];k=n['Inst'];following=nxt.get(pc)
            if k in {'Return','EndOfScript'}:return 'return'
            if k in {'Let','LetObj','LetBool'}:self.put(n['Variable'],self.value(n['Expression']))
            elif k=='SetSet':self.put(n['SetProperty'],{self.value(x) for x in n['Elements']})
            elif k=='PushExecutionFlow':flow.append(n['Offset'])
            elif k=='PopExecutionFlow':following=flow.pop()
            elif k=='PopExecutionFlowIfNot':
                if not self.value(n['Condition']):following=flow.pop()
            elif k=='Jump':following=n['Offset']
            elif k=='JumpIfNot':
                if not self.value(n['Condition']):following=n['Offset']
            else:self.value(n)
            pc=following
        return 'continuation'

def aggregate(functions,rows,static,owned,deck_id=1,main_faction=1):
    # Stable source array and explicitly supplied empty initial accumulator state.
    node=next(n for n in functions['IsDeckValid']['decoded'] if n['StatementIndex']==6321)
    prop=node['Expression']['Expression']['Parameters'][0]['Property Name']
    e=Executor(functions,dict(level_parameters={'DeckParameters':{prop:rows}},static_cards=static,main_faction=main_faction))
    e.env=dict(DeckHeader={'ID':deck_id,'main_faction':'fixture-faction'},CallFunc_GetActorOfClass_ReturnValue={'myCards':owned},
               hasUnownedCards=False,CardCount=0,allyCardCount=0,hasLocation=False,hasBadElites=False,
               deckContainsReserved=False,typeCounts={},typesInDeck=set(),raritiesInDeck=set(),factions=[])
    e.obj={'hasBadRarity':False};status=e.run('IsDeckValid',start=6180,flow=[724],stop=724)
    keys=['hasUnownedCards','CardCount','allyCardCount','hasLocation','hasBadElites','deckContainsReserved','typeCounts','typesInDeck','raritiesInDeck','factions']
    result={k:e.env[k] for k in keys};result['hasBadRarity']=e.obj['hasBadRarity'];result['status']=status
    if status=='return':result['early_outputs']={k:e.env[k] for k in ['IsValid','Reason','containsReserved','skirmishIsValid','skirmishReason','knockoutIsValid','knockoutReason']}
    return result,e.trace,e.events

def reference_aggregate(rows,static,owned,deck_id=1,main_faction=1):
    r=dict(hasUnownedCards=False,CardCount=0,allyCardCount=0,hasLocation=False,hasBadElites=False,deckContainsReserved=False,typeCounts={},typesInDeck=set(),raritiesInDeck=set(),factions=[],hasBadRarity=False,status='continuation')
    for row in rows:
        if row['deck_id']!=deck_id:continue
        c=static[row['card_type']];count=row['count']+row['gold_card_count']
        if c['set']==3 or row['count']<0 or row['gold_card_count']<0:
            r['status']='return';r['early_outputs']=dict(IsValid=False,Reason='Please delete deck',containsReserved=False,skirmishIsValid=False,skirmishReason='Please delete deck',knockoutIsValid=False,knockoutReason='Please delete deck');return r
        if c['faction']!=12 and c['exile']==0 and c['faction'] not in r['factions']:r['factions'].append(c['faction'])
        r['hasUnownedCards'] |= row['card_type'] not in owned or sum(owned.get(row['card_type'],{}).get(k,0) for k in ['count','gold_card_count'])<count
        r['typesInDeck'].add(c['type']);r['typeCounts'][c['type']]=r['typeCounts'].get(c['type'],0)+count
        r['hasBadRarity'] |= not (c['rarity'] in {1,2,3,4} and count<={1:4,2:3,3:2,4:1}[c['rarity']])
        if row['card_type'].startswith('card_location'):r['hasLocation']=True
        else:r['raritiesInDeck'].add(c['rarity'])
        r['CardCount']+=count
        if c['faction']!=12 and main_faction not in {c['faction'],c['exile']}:
            r['allyCardCount']+=count;r['hasBadElites'] |= c['rarity']==4
        r['deckContainsReserved'] |= c['reserved']
    return r

def serial(x):
    if isinstance(x,set):return sorted(x)
    raise TypeError(type(x))

def verify(functions):
    digest=hashlib.sha256();counts={'rarity':0,'type_limits':0,'aggregation':0};visited={k:set() for k in HELPERS};agg_offsets=set()
    def record(kind,data):counts[kind]+=1;digest.update(json.dumps(data,sort_keys=True,default=serial).encode())
    for rarity,count,seed in itertools.product(range(256),[-2,-1,0,1,2,3,4,5,6,2147483647],[False,True]):
        e=Executor(functions);e.env=dict(rarity=rarity,count=count,isOK=seed);e.run('IsRarityOK')
        expect=count<={1:4,2:3,3:2,4:1}[rarity] if rarity in {1,2,3,4} else False if rarity in {0,5} else seed
        assert e.env['isOK']==expect,(rarity,count,seed,e.env);visited['IsRarityOK'].update(e.trace);record('rarity',[rarity,count,seed,e.env['isOK'],e.trace])
    for name in HELPERS[1:]:
        ismin=name=='skirmishMinCardsOfType';key='min_cards_of_type' if ismin else 'max_cards_of_type';out='min' if ismin else 'max'
        for values in [list(range(13)),list(reversed(range(13)))]:
            labels={v:'fixture-label-'+str(v) for v in values};services=dict(enum_values=values,valid_values={v:v for v in values},enum_labels=labels)
            samples=[({},{}),({key:{}},{}),({key:{'unmapped-key':99}},{})]
            for t,limit,count,exists in itertools.product(values,[-1,0,1,4],[-1,0,1,4],[False,True]):
                samples.append(({key:{labels[t]:limit}},{t:count} if exists else {}))
            samples.append(({key:{labels[v]:2 if ismin else -1 for v in values}},{}))
            for rules,typecounts in samples:
                e=Executor(functions,services);e.env=dict(Rules=rules,typeCounts=typecounts,IsValid=None,Type='seed',**{out:-99});e.run(name)
                bad=[t for t in values if labels[t] in rules.get(key,{}) and (rules[key][labels[t]]>typecounts.get(t,0) if ismin else typecounts.get(t,0)>rules[key][labels[t]])]
                exp=(False,'display-'+str(bad[0]),rules[key][labels[bad[0]]]) if bad else (True,'',0)
                assert (e.env['IsValid'],e.env['Type'],e.env[out])==exp,(name,rules,typecounts,e.env,exp)
                visited[name].update(e.trace);record('type_limits',[name,values,rules,typecounts,list(exp),e.trace])
    for faction,exile,rarity,ctype,cs,count,gold,own in itertools.product([1,2,12],[0,1],[1,4,5],[1,2],[1,3],[0,1],[0,1],[False,True]):
        n='card_location_fixture' if ctype==1 else 'card_unit_fixture';row=dict(deck_id=1,card_type=n,count=count,gold_card_count=gold)
        static={n:dict(faction=faction,exile=exile,rarity=rarity,type=ctype,set=cs,reserved=True)};owned={n:dict(count=1,gold_card_count=0)} if own else {}
        actual,trace,_=aggregate(functions,[row],static,owned);expected=reference_aggregate([row],static,owned);assert actual==expected,(row,static,actual,expected)
        agg_offsets.update(trace);record('aggregation',[row,static,owned,actual,trace])
    # Ordered duplicate, other-deck, negative, and sticky flag cases.
    static={'card_unit_fixture':dict(faction=2,exile=0,rarity=4,type=2,set=1,reserved=True),'card_location_fixture':dict(faction=1,exile=0,rarity=1,type=1,set=1,reserved=False)}
    for order in itertools.permutations(['card_unit_fixture','card_location_fixture']):
        for count,gold,otherdeck in itertools.product([-1,0,1,4],[-1,0,1],[False,True]):
            rows=[dict(deck_id=2 if otherdeck else 1,card_type=n,count=count,gold_card_count=gold) for n in order]*2
            actual,trace,_=aggregate(functions,rows,static,{});expected=reference_aggregate(rows,static,{});assert actual==expected,(rows,actual,expected)
            agg_offsets.update(trace);record('aggregation',[rows,actual,trace])
    return dict(case_counts=counts,case_digest_sha256=digest.hexdigest(),helper_visited_offsets={k:sorted(v) for k,v in visited.items()},aggregation_visited_offsets=sorted(agg_offsets),
                scope='Complete helper ASTs and isolated aggregation control flow, under supplied enum/JSON/map/static lookup services and fresh accumulator fixtures. Not complete mode legality or actual native data; unknown rarity preserves supplied output seed.')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--capture',required=True);p.add_argument('--output',required=True);a=p.parse_args();d=load(a.capture)
    functions={f['name']:f for f in d['functions'] if f['owner']=='BP_DeckAndCollectionLoader_C'}
    for f in functions.values():
        raw=bytes.fromhex(f['script_hex']);assert f['decode_complete'] and len(raw)==f['script_size'] and hashlib.sha256(raw).hexdigest()==f['script_sha256']
    result=verify(functions);result['function_sha256']={k:f['script_sha256'] for k,f in functions.items()};result['capture_sha256']=hashlib.sha256(Path(a.capture).read_bytes()).hexdigest();result['generator_sha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    Path(a.output).write_bytes((json.dumps(result,ensure_ascii=False,indent=2)+'\n').encode('utf8'));print(json.dumps({k:v for k,v in result.items() if 'offset' not in k},ensure_ascii=False))
