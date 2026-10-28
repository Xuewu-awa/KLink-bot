import argparse
import copy
import importlib.util
from pathlib import Path
import unittest

s=importlib.util.spec_from_file_location('deck',Path(__file__).with_name('model-deck-validation-phase.py'));m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
p=argparse.ArgumentParser();p.add_argument('--capture',required=True);args,rest=p.parse_known_args()
function=next(f for f in m.load(args.capture)['functions'] if f['owner']=='BP_DeckAndCollectionLoader_C' and f['name']=='IsDeckValid')

class DeckPhaseTests(unittest.TestCase):
    def run_fixture(self,**changes):
        f=m.fixture();f.update(changes);actual,*_=m.execute(function,f);self.assertEqual(actual,m.expected(f));return actual
    def test_shared_failure_precedes_mode_exceptions(self):
        a=self.run_fixture(bad_elites=True,count=39,allies=13,sk_rules={'allow_more_ally_cards':True})
        self.assertEqual(a['Reason'],'Includes ally Elite cards');self.assertFalse(a['skirmishIsValid'])
    def test_present_false_is_still_exception_in_supplied_service_model(self):
        a=self.run_fixture(allies=13,unowned=True,tournament=1,sk_rules={'allow_more_ally_cards':False,'allow_unowned':False},tournament_rules={'deck_rules':{'allow_more_ally_cards':False,'allow_unowned':False}})
        self.assertFalse(a['IsValid']);self.assertTrue(a['skirmishIsValid']);self.assertTrue(a['knockoutIsValid'])
    def test_unowned_does_not_overwrite_earlier_normal_reason(self):
        self.assertEqual(self.run_fixture(allies=13,unowned=True)['Reason'],'Too many ally cards')
    def test_reserved_rejection_is_mode_specific_here(self):
        a=self.run_fixture(reserved=True,tournament=1);self.assertTrue(a['IsValid']);self.assertFalse(a['knockoutIsValid'])
    def test_unlock_early_return_skips_aggregation(self):
        f=m.fixture();f['unlocked']=False;a,_,_,count=m.execute(function,f)
        self.assertEqual(count,0);self.assertFalse(a['containsReserved'])
    def test_aggregation_is_explicitly_supplied(self):
        a=self.run_fixture(count=39);self.assertEqual(a['Reason'],'Deck not 40 cards')
        self.assertTrue(self.run_fixture(count=40)['IsValid'])
    def test_boundary_mutation_is_detected(self):
        changed=copy.deepcopy(function);node=next(n for n in changed['decoded'] if n['StatementIndex']==3000)
        self.assertEqual(node['Expression']['Parameters'][1]['Value'],12)
        node['Expression']['Parameters'][1]['Value']=13
        f=m.fixture();f['allies']=13;actual,*_=m.execute(changed,f);self.assertNotEqual(actual,m.expected(f))

if __name__=='__main__':unittest.main(argv=[__file__]+rest)
