import argparse,copy,importlib.util,unittest
from pathlib import Path
s=importlib.util.spec_from_file_location('stats',Path(__file__).with_name('model-deck-statistics.py'));m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
p=argparse.ArgumentParser();p.add_argument('--capture',required=True);a,rest=p.parse_known_args();functions={f['name']:f for f in m.load(a.capture)['functions'] if f['owner']=='BP_DeckAndCollectionLoader_C'}

class StatsTests(unittest.TestCase):
    def helper(self,name,env,values=None):
        values=list(range(13)) if values is None else values
        e=m.Executor(functions,dict(enum_values=values,valid_values={v:v for v in values},enum_labels={v:'label-'+str(v) for v in values}));e.env=env;e.run(name);return e.env
    def agg(self,rows,static,owned=None):
        actual,*_=m.aggregate(functions,rows,static,owned or {});self.assertEqual(actual,m.reference_aggregate(rows,static,owned or {}));return actual
    def row(self,**kw):return dict(deck_id=1,card_type='card_unit_fixture',count=1,gold_card_count=0,**kw)
    def static(self,**kw):
        d=dict(faction=1,exile=0,rarity=1,type=2,set=1,reserved=False);d.update(kw);return {'card_unit_fixture':d}
    def test_unknown_rarity_retains_supplied_output(self):
        for seed in [False,True]:self.assertEqual(self.helper('IsRarityOK',dict(rarity=255,count=1,isOK=seed))['isOK'],seed)
    def test_negative_count_not_rejected_by_rarity_helper_alone(self):
        self.assertTrue(self.helper('IsRarityOK',dict(rarity=1,count=-1,isOK=False))['isOK'])
    def test_min_missing_count_uses_fixture_zero(self):
        e=self.helper('skirmishMinCardsOfType',dict(Rules={'min_cards_of_type':{'label-6':1}},typeCounts={},IsValid=None,Type='',min=0));self.assertFalse(e['IsValid']);self.assertEqual(e['Type'],'display-6')
    def test_first_failure_follows_supplied_enum_order(self):
        env=dict(Rules={'max_cards_of_type':{'label-1':-1,'label-12':-1}},typeCounts={},IsValid=None,Type='',max=0)
        self.assertEqual(self.helper('Check Custom Rules Max Cards Of Type',copy.deepcopy(env))['Type'],'display-1')
        self.assertEqual(self.helper('Check Custom Rules Max Cards Of Type',copy.deepcopy(env),list(reversed(range(13))))['Type'],'display-12')
    def test_other_deck_rows_are_skipped_before_static_query(self):
        row=self.row();row['deck_id']=99;self.assertEqual(self.agg([row],{})['CardCount'],0)
    def test_missing_static_source_fails_instead_of_inventing_defaults(self):
        with self.assertRaises(KeyError):m.aggregate(functions,[self.row()],{}, {})
    def test_zero_quantity_still_visits_type_and_reserved_checks(self):
        row=self.row();row['count']=0;r=self.agg([row],self.static(reserved=True))
        self.assertEqual(r['CardCount'],0);self.assertTrue(r['deckContainsReserved']);self.assertEqual(r['typesInDeck'],{2})
    def test_native_type_does_not_replace_name_prefix_check(self):
        self.assertFalse(self.agg([self.row()],self.static(type=1))['hasLocation'])
    def test_exile_matching_main_avoids_ally_count(self):
        r=self.agg([self.row()],self.static(faction=2,exile=1,rarity=4));self.assertEqual(r['allyCardCount'],0);self.assertFalse(r['hasBadElites'])
    def test_duplicate_rows_are_not_merged_in_this_block(self):
        row=self.row();row['count']=4;r=self.agg([row,row],self.static(),{'card_unit_fixture':{'count':4,'gold_card_count':0}})
        self.assertEqual(r['CardCount'],8);self.assertFalse(r['hasBadRarity']);self.assertFalse(r['hasUnownedCards'])
    def test_negative_gold_returns_shared_failure(self):
        row=self.row();row['gold_card_count']=-1;r=self.agg([row],self.static())
        self.assertEqual(r['status'],'return');self.assertFalse(r['early_outputs']['containsReserved'])
    def test_rarity_threshold_mutation_is_detected(self):
        f=copy.deepcopy(functions);n=next(n for n in f['IsRarityOK']['decoded'] if n['StatementIndex']==598);n['Expression']['Parameters'][1]['Value']=5
        e=m.Executor(f);e.env=dict(rarity=1,count=5,isOK=False);e.run('IsRarityOK');self.assertTrue(e.env['isOK'])
        self.assertFalse(self.helper('IsRarityOK',dict(rarity=1,count=5,isOK=False))['isOK'])
    def test_unknown_instruction_fails_closed(self):
        with self.assertRaises(AssertionError):m.Executor(functions).value({'Inst':'UnknownInstruction'})

if __name__=='__main__':unittest.main(argv=[__file__]+rest)
