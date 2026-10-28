import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

s=importlib.util.spec_from_file_location('scope',Path(__file__).with_name('build-pvp-scope.py')); m=importlib.util.module_from_spec(s); s.loader.exec_module(m)

class ScopeTests(unittest.TestCase):
    def run_scan(self, body, fields=None, declared=True):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)
            cards=[dict(name=n,asset='kards/Content/Cards/'+n+'.uasset',**{'class':n+'_C'},parent='Base',functions=[{'name':'F'}] if declared else [],functions_file=n+'.json',explicit_overrides=fields or {}) for n in ['a','b']]
            cards[1]['explicit_overrides']={}
            if body is not None: (root/'a.json').write_text(json.dumps({'F':{'bytecode':body}}),encoding='utf8')
            return m.scan(cards,root)
    def test_exact_alias_no_fuzzy(self):
        _,e,_,_=self.run_scan([{'Inst':'NameConst','Value':'b_C'},{'Inst':'NameConst','Value':'almost_b'}])
        self.assertEqual([x['target'] for x in e],['b'])
    def test_spawn_next_name_slot_zero(self):
        _,_,c,_=self.run_scan([{'Inst':'LocalVirtualFunction','FunctionName':'SpawnNextToCard','Parameters':[{'Inst':'NameConst','Value':'b'},{'Inst':'IntConst','Value':4}]}])
        self.assertEqual(c[0]['candidate_targets'],['b']); self.assertEqual(c[0]['name_slot'],0)
    def test_convert_name_slot_two(self):
        _,_,c,_=self.run_scan([{'Inst':'FinalFunction','Function':'ConvertCard','Parameters':[{}, {}, {'Inst':'NameConst','Value':'b'}, {'Inst':'IntConst','Value':7}]}])
        self.assertEqual(c[0]['candidate_targets'],['b']); self.assertFalse(c[0]['receiver_and_reachability_verified'])
    def test_dynamic_kept(self):
        _,_,c,_=self.run_scan([{'Inst':'LocalVirtualFunction','FunctionName':'SpawnCardToBoard','Parameters':[{'Inst':'LocalVariable','Variable Name':'chosen'}]}])
        self.assertEqual(c[0]['status'],'dynamic_or_missing_argument')
    def test_unmapped_kept(self):
        _,_,c,_=self.run_scan([{'Inst':'CallMath','Function':'SpawnMystery','Parameters':[]}])
        self.assertEqual(c[0]['status'],'unmapped_symbol')
    def test_missing_and_data_only_differ(self):
        self.assertEqual(self.run_scan(None)[0][0]['export_state'],'missing_expected')
        self.assertEqual(self.run_scan(None,declared=False)[0][0]['export_state'],'data_only_no_own_functions')
    def test_cdo_delimited_candidates(self):
        _,e,_,_=self.run_scan([],{'spawnCardName':'b;missing','Name':'a','title':'b'})
        self.assertEqual(len(e),1); self.assertEqual(e[0]['target'],'b')
    def test_never_exclude_unreferenced(self):
        inventory,e,_,_=self.run_scan([])
        self.assertFalse(e); self.assertTrue(all(x['scope_decision']=='unreviewed' for x in inventory))

if __name__=='__main__': unittest.main()
