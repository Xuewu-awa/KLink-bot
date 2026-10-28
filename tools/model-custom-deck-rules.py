"""Execute captured checkCustomRules and its min/max helpers offline.

Text formatting is a fixture rendering, not localized FText parity. Enum display
labels/valid-value helpers and JSON/map behavior remain explicitly supplied.
"""
import argparse,hashlib,importlib.util,itertools,json,random
from pathlib import Path
s=importlib.util.spec_from_file_location('stats_model',Path(__file__).with_name('model-deck-statistics.py'));stats=importlib.util.module_from_spec(s);s.loader.exec_module(stats)

class CustomExecutor(stats.Executor):
    def put(self,n,value):
        if n['Inst']=='StructMemberContext':
            base=n['StructExpression'];assert base['Inst']=='LocalVariable'
            self.env.setdefault(base['Variable Name'],{})[n['Property Name']]=value
        else:super().put(n,value)
    def call(self,n):
        name=n.get('Function',n.get('FunctionName'));p=n.get('Parameters',[]);v=self.value
        if name in {'Conv_JsonValueToArray','Conv_JsonValueToString','Conv_StringToText','MakeLiteralText'}:return v(p[0])
        if name=='Conv_IntToInt64':return int(v(p[0]))
        if name=='NotEqual_IntInt':return v(p[0])!=v(p[1])
        if name=='BooleanAND':return v(p[0]) and v(p[1])
        if name in {'Set_Intersection','Set_Union'}:
            a,b=v(p[0]),v(p[1]);self.put(p[-1],a&b if name=='Set_Intersection' else a|b);return
        if name=='Set_IsNotEmpty':return bool(v(p[0]))
        if name=='Format':
            values={x['ArgumentName']:x['ArgumentValueInt'] if x['ArgumentValueType']==0 else x['ArgumentValue'] for x in v(p[1])}
            return v(p[0]).format(**values)
        if name in {'skirmishMinCardsOfType','Check Custom Rules Max Cards Of Type'}:
            child=stats.Executor(self.functions,self.services);field='min' if name=='skirmishMinCardsOfType' else 'max'
            child.env=dict(typeCounts=v(p[0]),Rules=v(p[1]),IsValid=None,Type='fixture-seed',**{field:-99});child.run(name)
            self.put(p[-3],child.env['IsValid']);self.put(p[-2],child.env['Type']);self.put(p[-1],child.env[field]);self.helper_traces[name]=child.trace;return
        return super().call(n)
    def run(self,name):
        ns={n['StatementIndex']:n for n in self.functions[name]['decoded']};nxt=dict(zip(ns,list(ns)[1:]));pc=0;flow=[];self.helper_traces={}
        while pc is not None:
            self.trace.append(pc);assert len(self.trace)<20000
            n=ns[pc];k=n['Inst'];following=nxt.get(pc)
            if k in {'Return','EndOfScript'}:return
            if k in {'Let','LetObj','LetBool'}:self.put(n['Variable'],self.value(n['Expression']))
            elif k=='SetSet':self.put(n['SetProperty'],{self.value(x) for x in n['Elements']})
            elif k=='SetArray':self.put(n['LeftSideExpression'],[self.value(x) for x in n['Values']])
            elif k=='PushExecutionFlow':flow.append(n['Offset'])
            elif k=='PopExecutionFlow':following=flow.pop()
            elif k=='PopExecutionFlowIfNot':
                if not self.value(n['Condition']):following=flow.pop()
            elif k=='Jump':following=n['Offset']
            elif k=='JumpIfNot':
                if not self.value(n['Condition']):following=n['Offset']
            else:self.value(n)
            pc=following

def execute(functions,fixture,enum_values):
    labels={v:'fixture-label-'+str(v) for v in enum_values};services=dict(enum_values=enum_values,valid_values={v:v for v in enum_values},enum_labels=labels)
    e=CustomExecutor(functions,services);e.env=dict(Rules=fixture['rules'],raritiesInDeck=set(fixture['rarities']),TypeCount=fixture['counts'],hasReserved=fixture['reserved'],DeckHeader={'main_faction':fixture['faction']},allyCardCount=fixture['allies'],typesInDeck=set(fixture['types']),customRulesBlacklist=set(),IsValid=None,Header='seed',Reason='seed');e.run('checkCustomRules')
    return {k:e.env[k] for k in ['IsValid','Header','Reason']},e.trace,e.helper_traces

def expected(f,values):
    bans=set(f['rules'].get('blacklist',[]));types=set(f['types']);rarities=set(f['rarities']);reason=None
    # Order follows execution-flow stack, not increasing statement offsets.
    checks=[(f['faction'] in bans,'Is not of correct nation'),('ally' in bans and f['allies']!=0,'Single nation decks only'),
      ('elite' in bans and 4 in rarities,'Contains Elite'),('special' in bans and 3 in rarities,'Contains Special'),
      ('limited' in bans and 2 in rarities,'Contains Limited'),('standard' in bans and 2 in rarities,'Contains Standard')]
    for key,allowed,text in [('orders',{2},'Orders'),('units',{3,4,5,6,7},'Units'),('ground',{3,6,7},'Ground units'),('air',{4,5},'Air units'),('infantry',{6},'Infantry'),('tank',{3},'Tanks'),('artillery',{7},'Artillery'),('bomber',{5},'Bombers'),('fighter',{4},'Fighters'),('countermeasure',{11},'Countermeasures')]:checks.append((key in bans and bool(types&allowed),'Contains '+text))
    for test,text in checks:
        if test:reason=text;break
    if reason is None:
        for key,ismin in [('max_cards_of_type',False),('min_cards_of_type',True)]:
            limits=f['rules'].get(key,{})
            for t in values:
                label='fixture-label-'+str(t)
                if label not in limits:continue
                count=f['counts'].get(t,0);limit=limits[label]
                if (limit>count if ismin else count>limit):
                    reason=('You must have a minimum of {x} cards of the type {type} ' if ismin else 'You can only have {x} cards of the type {type} ').format(x=limit,type='display-'+str(t));break
            if reason:break
    if reason is None and f['reserved'] and 'allow_reserved' not in f['rules']:reason='Contains Reserved cards'
    return dict(IsValid=reason is None,Header='' if reason is None else 'NOT AVAILABLE',Reason=reason or '')

def fixture():return dict(rules={},types=[],rarities=[],counts={},reserved=False,faction='germany',allies=0)

def verify(functions,values):
    digest=hashlib.sha256();cases=0;visited=set();helpers={};rng=random.Random(4004)
    def check(f):
        nonlocal cases
        actual,trace,ht=execute(functions,f,values);assert actual==expected(f,values),(f,actual,expected(f,values));cases+=1;visited.update(trace)
        for k,ts in ht.items():helpers.setdefault(k,set()).update(ts)
        digest.update(json.dumps([f,actual,trace,ht],sort_keys=True).encode())
    keys=['germany','ally','elite','standard','limited','special','orders','units','ground','air','infantry','tank','artillery','bomber','fighter','countermeasure','unrecognized']
    for key,t,rarity,reserved,allies in itertools.product(keys,range(14),range(7),[False,True],[0,1]):
        f=fixture();f.update(rules={'blacklist':[key]},types=[t],rarities=[rarity],reserved=reserved,allies=allies);check(f)
    for _ in range(600):
        f=fixture();f.update(rules={'blacklist':rng.sample(keys,rng.randrange(len(keys)+1))},types=rng.sample(list(range(14)),rng.randrange(15)),rarities=rng.sample(list(range(7)),rng.randrange(8)),reserved=bool(rng.randrange(2)),allies=rng.randrange(3));check(f)
    for allow,ma,mi,cnt in itertools.product([None,False,True],[-1,0,1,2],[0,1,2],[0,1,2]):
        f=fixture();f.update(reserved=True,counts={6:cnt},rules={'max_cards_of_type':{'fixture-label-6':ma},'min_cards_of_type':{'fixture-label-6':mi}})
        if allow is not None:f['rules']['allow_reserved']=allow
        check(f)
    return dict(cases=cases,case_digest_sha256=digest.hexdigest(),visited_offsets=sorted(visited),helper_visited_offsets={k:sorted(v) for k,v in helpers.items()},
                scope='Complete checkCustomRules AST composed with captured min/max helpers; actual raw enum order supplied, but enum display/validity, JSON/map and plain source-text formatting are fixtures. No native policy or localized text certification.')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--capture',required=True);p.add_argument('--enum-capture',required=True);p.add_argument('--output',required=True);a=p.parse_args()
    d=stats.load(a.capture);functions={f['name']:f for f in d['functions'] if f['owner']=='BP_DeckAndCollectionLoader_C'}
    en=stats.load(a.enum_capture);rows=next(e['entries'] for e in en['enums'] if e['name']=='ETypeEnum');values=[r['value'] for r in rows[:13]];assert len(values)==13
    for name in ['checkCustomRules','skirmishMinCardsOfType','Check Custom Rules Max Cards Of Type']:
        f=functions[name];assert f['decode_complete'] and hashlib.sha256(bytes.fromhex(f['script_hex'])).hexdigest()==f['script_sha256']
    result=verify(functions,values);result['raw_enum_values']=values;result['function_sha256']={k:functions[k]['script_sha256'] for k in ['checkCustomRules','skirmishMinCardsOfType','Check Custom Rules Max Cards Of Type']}
    result['bindings']={str(Path(p).resolve()):hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in [a.capture,a.enum_capture,__file__,Path(__file__).with_name('model-deck-statistics.py')]}
    Path(a.output).write_bytes((json.dumps(result,ensure_ascii=False,indent=2)+'\n').encode('utf8'));print(json.dumps({k:v for k,v in result.items() if 'offset' not in k and k!='bindings'},ensure_ascii=False))
