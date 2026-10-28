import argparse,copy,importlib.util,unittest
from pathlib import Path
s=importlib.util.spec_from_file_location('custom',Path(__file__).with_name('model-custom-deck-rules.py'));m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
p=argparse.ArgumentParser();p.add_argument('--capture',required=True);a,rest=p.parse_known_args();functions={f['name']:f for f in m.stats.load(a.capture)['functions'] if f['owner']=='BP_DeckAndCollectionLoader_C'}

class CustomTests(unittest.TestCase):
    def check(self,**changes):
        f=m.fixture();f.update(changes);actual,*_=m.execute(functions,f,list(range(13)));self.assertEqual(actual,m.expected(f,list(range(13))));return actual
    def test_absent_and_empty_blacklist_allow(self):
        self.assertTrue(self.check()['IsValid']);self.assertTrue(self.check(rules={'blacklist':[]})['IsValid'])
    def test_nation_failure_precedes_rarity_and_type(self):
        r=self.check(rules={'blacklist':['germany','elite','orders']},rarities=[4],types=[2]);self.assertEqual(r['Reason'],'Is not of correct nation')
    def test_execution_stack_priority_differs_from_source_offsets(self):
        r=self.check(rules={'blacklist':['elite','special','limited','standard']},rarities=[1,2,3]);self.assertEqual(r['Reason'],'Contains Special')
    def test_standard_and_limited_both_compare_two_in_captured_client(self):
        self.assertTrue(self.check(rules={'blacklist':['standard']},rarities=[1])['IsValid'])
        self.assertFalse(self.check(rules={'blacklist':['standard']},rarities=[2])['IsValid'])
        self.assertEqual(self.check(rules={'blacklist':['standard','limited']},rarities=[2])['Reason'],'Contains Limited')
    def test_units_group_does_not_invent_other_type_members(self):
        self.assertTrue(self.check(rules={'blacklist':['units']},types=[8,9,10])['IsValid'])
        self.assertEqual(self.check(rules={'blacklist':['units']},types=[3])['Reason'],'Contains Units')
    def test_orders_precede_units_if_both_fail(self):
        self.assertEqual(self.check(rules={'blacklist':['orders','units']},types=[2,3])['Reason'],'Contains Orders')
    def test_ally_uses_nonzero_including_negative_fixture(self):
        self.assertFalse(self.check(rules={'blacklist':['ally']},allies=-1)['IsValid'])
    def test_allow_reserved_presence_with_false_fixture_value(self):
        self.assertTrue(self.check(rules={'allow_reserved':False},reserved=True)['IsValid'])
    def test_max_limit_precedes_min_and_reserved(self):
        r=self.check(rules={'max_cards_of_type':{'fixture-label-6':0},'min_cards_of_type':{'fixture-label-6':2}},counts={6:1},reserved=True)
        self.assertEqual(r['Reason'],'You can only have 0 cards of the type display-6 ')
    def test_min_helper_composed_not_replaced(self):
        r=self.check(rules={'min_cards_of_type':{'fixture-label-6':1}},counts={});self.assertEqual(r['Reason'],'You must have a minimum of 1 cards of the type display-6 ')
    def test_mapping_mutation_does_not_get_normalized_away(self):
        altered=copy.deepcopy(functions);n=next(n for n in altered['checkCustomRules']['decoded'] if n['StatementIndex']==6001);n['Expression']['Value']=1
        f=m.fixture();f.update(rules={'blacklist':['standard']},rarities=[1]);actual,*_=m.execute(altered,f,list(range(13)));self.assertNotEqual(actual,m.expected(f,list(range(13))))

if __name__=='__main__':unittest.main(argv=[__file__]+rest)
