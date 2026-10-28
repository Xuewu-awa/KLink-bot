"""Meaningful regression checks for audit evidence attribution and invalidation."""
import copy
import csv
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('audit_matrix', Path(__file__).with_name('build-audit-matrix.py'))
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)


class MatrixTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.ir = {'unit': {'steps': [
            {'op':'call','fn':'ChangeAttack','i':10,'args':[{}, {}, {'int':3}, {'int':4}]},
            {'op':'set','src':{'call':'Lookup','args':[{'math':'Add_IntInt','args':[]}]}},
            {'op':'math','fn':'Clamp','args':[]}],
            'entrypoints':{'OnPlayed':10}, 'locals':{'LocalThing':[]}}}
        self.write('klink bot/docs/card-ir.json', self.ir)
        self.write('klink bot/docs/cards.json', {'unit':{'title':'测试卡'}, 'native':{}})
        self.write('out/audit/systematic-rules/inventory.json', dict(git_head='old',
            cards=[{'name':'unit'},{'name':'native'}], functions=[
                {'card':'unit','function':'OnPlayed','runtime_path':'old:OnPlayed'},
                {'card':'unit','function':'ShouldHighlightInHand','scope_exclusion':'display_hint'}]))
        self.report = 'out/audit/systematic-rules/report.json'
        self.write(self.report, dict(rule_id='RULE-X',status='claimed complete',
            findings=['ChangeAttack checked'],remaining=['Historical issue since resolved']))
        self.code = 'src/KLink.Bot/Effects/Api.cs'
        self.write_text(self.code, 'public void ChangeAttack() {}\n')
        self.registry = dict(schema_version=1,partial_evidence=[dict(id='unit4',status='partial',
            symbol='ChangeAttack',modes=[4],report=self.report,
            report_sha256=self.sha(self.report),client_exe_sha256='exe',
            evidence_bindings={self.report:self.sha(self.report)},
            source_bindings={self.code:self.sha(self.code),
                             'klink bot/docs/card-ir.json':self.sha('klink bot/docs/card-ir.json')})],
            scope_reviews={},open_items=[])
        self.review_path = self.repo / 'klink bot/docs/audit-matrix-reviews.json'
        self.save_registry()

    def write_text(self, relative, value):
        p=self.repo/relative
        p.parent.mkdir(parents=True,exist_ok=True)
        p.write_bytes(value.encode('utf8'))

    def write(self, relative, value):
        self.write_text(relative,json.dumps(value,ensure_ascii=False))

    def sha(self, relative):
        return matrix.digest((self.repo/relative).read_bytes())

    def save_registry(self):
        self.write(self.review_path.relative_to(self.repo),self.registry)

    def build(self, version='exe'):
        return matrix.build(self.repo,self.review_path,version)

    def test_full_inventory_and_nested_calls_without_semantic_promotion(self):
        r=self.build()
        self.assertEqual(2,r['counts']['cards'])
        self.assertEqual({'ChangeAttack','Lookup','Add_IntInt','Clamp'},{e['symbol'] for e in r['edges']})
        self.assertEqual(0,r['counts']['fully_certified_cards'])
        self.assertIsNone(r['counts']['semantic_completion_percent'])
        self.assertTrue(all(c['pvp_scope']=='unreviewed' for c in r['cards']))
        self.assertTrue(all(e['semantic_status']=='not_reviewed' for e in r['edges']))

    def test_literal_mode_candidate_is_partial_only(self):
        r=self.build()
        e=next(e for e in r['edges'] if e['symbol']=='ChangeAttack')
        self.assertEqual(['unit4'],e['partial_evidence_candidates'])
        self.assertEqual('bound_inputs_match',r['partial_evidence'][0]['freshness'])
        self.assertEqual('not_confirmed_against_current_pak',r['partial_evidence'][0]['asset_version_status'])

    def test_nonmatching_mode_does_not_inherit_evidence(self):
        self.ir['unit']['steps'][0]['args'][3]={'int':1}
        self.write('klink bot/docs/card-ir.json',self.ir)
        e=next(e for e in self.build()['edges'] if e['symbol']=='ChangeAttack')
        self.assertEqual([],e['partial_evidence_candidates'])

    def test_source_change_marks_unit_stale(self):
        self.write_text(self.code,'public void ChangeAttack() { Mutated(); }')
        r=self.build()
        self.assertEqual(0,r['counts']['partial_units_matching_bound_inputs'])
        self.assertIn('bound_source_changed:'+self.code,r['partial_evidence'][0]['stale_reasons'])

    def test_ir_change_marks_unit_stale(self):
        self.ir['unit']['steps'].append({'op':'return'})
        self.write('klink bot/docs/card-ir.json',self.ir)
        self.assertIn('bound_source_changed:klink bot/docs/card-ir.json',self.build()['partial_evidence'][0]['stale_reasons'])

    def test_unknown_or_different_exe_never_fresh(self):
        self.assertIn('client_exe_not_checked',self.build(None)['partial_evidence'][0]['stale_reasons'])
        self.assertIn('client_exe_changed',self.build('different')['partial_evidence'][0]['stale_reasons'])

    def test_changed_report_stale_and_historical_remaining_not_active(self):
        self.write(self.report,dict(status='fully verified',remaining=['old unresolved assertion']))
        r=self.build()
        self.assertIn('report_changed_or_missing',r['partial_evidence'][0]['stale_reasons'])
        self.assertEqual([],r['open_items'])
        self.assertEqual(0,r['counts']['fully_certified_cards'])

    def test_refuse_blanket_full_certification(self):
        self.registry['partial_evidence'][0]['status']='verified'
        self.save_registry()
        with self.assertRaisesRegex(ValueError,'partial evidence only'):
            self.build()

    def test_changed_or_missing_raw_evidence_invalidates_unit(self):
        self.registry['partial_evidence'][0]['evidence_bindings']={'missing-proof.json':'not-present'}
        self.save_registry()
        self.assertIn('evidence_missing:missing-proof.json',self.build()['partial_evidence'][0]['stale_reasons'])
        self.write_text('missing-proof.json','changed proof')
        self.assertIn('evidence_changed:missing-proof.json',self.build()['partial_evidence'][0]['stale_reasons'])

    def test_unbound_raw_evidence_cannot_claim_freshness(self):
        self.registry['partial_evidence'][0].pop('evidence_bindings')
        self.save_registry()
        self.assertIn('raw_evidence_not_bound',self.build()['partial_evidence'][0]['stale_reasons'])

    def test_duplicate_function_identity_rejected(self):
        p=self.repo/'out/audit/systematic-rules/inventory.json'
        d=matrix.load(p)
        d['functions'].append(copy.deepcopy(d['functions'][0]))
        self.write(p.relative_to(self.repo),d)
        with self.assertRaisesRegex(ValueError,'duplicate function'):
            self.build()

    def test_malformed_report_fails_closed(self):
        self.write_text('out/audit/systematic-rules/broken.json','{broken')
        with self.assertRaises(json.JSONDecodeError):
            self.build()

    def test_data_only_card_not_a_missing_effect(self):
        c=next(c for c in self.build()['cards'] if c['card']=='native')
        self.assertFalse(c['own_ir_present'])
        self.assertEqual('unreviewed',c['native_defaults_and_implicit_rules'])
        self.assertNotIn('defect',c['semantic_status'])

    def test_scope_exclusion_needs_current_pinned_proof(self):
        self.registry['scope_reviews']={'native':dict(status='excluded_non_pvp',reason='explicit evidence',
            proof_path=self.report,proof_sha256=self.sha(self.report),client_exe_sha256='exe',
            database_sha256=self.sha('klink bot/docs/cards.json'))}
        self.save_registry()
        self.assertEqual(1,self.build()['counts']['excluded_non_pvp'])
        self.write(self.report,{'changed':True})
        c=next(c for c in self.build()['cards'] if c['card']=='native')
        self.assertEqual('stale_scope_review',c['pvp_scope'])

    def test_unknown_card_scope_review_rejected(self):
        self.registry['scope_reviews']={'no_such_card':{}}
        self.save_registry()
        with self.assertRaisesRegex(ValueError,'unknown card'):
            self.build()

    def test_outputs_deterministic_and_tsv_roundtrip(self):
        r=self.build()
        out=self.repo/'generated'
        matrix.write_outputs(r,out)
        before={p.name:p.read_bytes() for p in out.iterdir()}
        matrix.write_outputs(self.build(),out)
        self.assertEqual(before,{p.name:p.read_bytes() for p in out.iterdir()})
        with (out/'cards.tsv').open(encoding='utf-8-sig',newline='') as f:
            rows=list(csv.DictReader(f,delimiter='\t'))
        self.assertEqual({'unit','native'},{r['card'] for r in rows})
        self.assertEqual('测试卡',next(r['title'] for r in rows if r['card']=='unit'))
        self.assertEqual(r,matrix.load(out/'matrix.json'))
        for path in out.glob('*.tsv'):
            for line in path.read_text(encoding='utf-8-sig').splitlines():
                self.assertEqual(line,line.rstrip(' \t'),str(path))


if __name__=='__main__':
    unittest.main(verbosity=2)
