import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec=importlib.util.spec_from_file_location('baseline',Path(__file__).with_name('build-audit-baseline.py'))
baseline=importlib.util.module_from_spec(spec);spec.loader.exec_module(baseline)


class BaselineTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.repo=Path(self.temp.name)
        self.report='out/audit/systematic-rules/report.json';self.proof='out/proof.txt'
        self.write(self.proof,'proof')
        self.write('klink bot/docs/cards.json',{'unit':{'cardSet':'OnlySpawnable'}})
        self.write('klink bot/docs/系统规则审计-20261014.md','## RULE-001：说明\n细节\n## RULE-002 space\ntext\n## RULE-003: ASCII\ntext\n')
        self.data=dict(rule='RULE-001',status='claims complete',native={'exe_sha256':'a'*64},
            sources={self.proof:self.sha(self.proof)},remaining=['old scope'])
        self.write(self.report,self.data)
        self.plan=dict(schema_version=1,work_units=[dict(id='A',title='scope',scope='bounded',status='partial',
            depends_on=[],acceptance_criteria=['check'],deliverables=['result'])],remaining_claim_reviews=[])
        self.plan_path=self.repo/'klink bot/docs/audit-work-plan.json';self.save_plan()

    def write(self,path,value):
        p=self.repo/path;p.parent.mkdir(parents=True,exist_ok=True)
        p.write_bytes((json.dumps(value,ensure_ascii=False) if not isinstance(value,str) else value).encode('utf8'))

    def sha(self,path):return hashlib.sha256((self.repo/path).read_bytes()).hexdigest()
    def save_plan(self):self.write(self.plan_path.relative_to(self.repo),self.plan)
    def build(self,**kw):return baseline.build(self.repo,self.plan_path,hash_evidence=True,client_sha256='a'*64,**kw)

    def test_all_heading_formats_and_no_number_gap_assumption(self):
        d=self.build();self.assertEqual(['RULE-001','RULE-002','RULE-003'],[r['rule_id'] for r in d['ledger']])
        self.assertEqual(1,d['counts']['ledger_rules_with_explicit_json_report'])
        self.assertEqual([],d['ledger'][1]['explicit_json_reports'])

    def test_duplicate_heading_rejected(self):
        self.write('klink bot/docs/系统规则审计-20261014.md','## RULE-001: a\n## RULE-1 b\n')
        with self.assertRaisesRegex(ValueError,'duplicate RULE'):self.build()

    def test_nested_exe_versions_are_distinct_from_code_hashes(self):
        self.assertEqual(['a'*64],baseline.declared_versions(self.data))
        self.assertEqual([],baseline.declared_versions({'sha256':'a'*64}))
        self.data['old']={'exe_sha256':'b'*64};self.write(self.report,self.data)
        self.assertEqual('mixed_declared_versions',self.build()['reports'][0]['version_status'])

    def test_executable_file_fingerprint_recovers_version_without_confusing_tools(self):
        self.assertEqual(['a'*64],baseline.declared_versions({'sources':{'client/kards-Win64-Shipping.exe':'a'*64}}))
        self.assertEqual([],baseline.declared_versions({'sources':{'tools/parser.exe':'a'*64}}))
        self.assertEqual('kernel_or_regression_source',baseline.binding_role('src/KLink.Bot/Engine/State.cs'))

    def test_evidence_match_never_certifies_semantics(self):
        d=self.build();self.assertEqual('match',d['bindings'][0]['binding_status'])
        self.assertEqual('all_recognized_file_bindings_match',d['reports'][0]['provenance_status'])
        self.assertEqual('historical_claims_not_current_certification',d['reports'][0]['semantic_status'])
        self.assertIsNone(d['counts']['remaining_work_percent'])

    def test_changed_source_is_not_an_automatic_defect(self):
        self.write(self.proof,'new proof');d=self.build()
        self.assertEqual('changed',d['bindings'][0]['binding_status'])
        self.assertEqual('not_an_automatic_open_defect',d['remaining_claims'][0]['task_status'])

    def test_missing_file_distinct_from_unhashed(self):
        (self.repo/self.proof).unlink();self.assertEqual('missing',self.build()['bindings'][0]['binding_status'])
        self.write(self.proof,'proof')
        d=baseline.build(self.repo,self.plan_path,hash_evidence=False)
        self.assertEqual('not_hashed',d['bindings'][0]['binding_status'])

    def test_relative_traversal_does_not_escape_allowed_roots(self):
        checker=baseline.EvidenceChecker(self.repo,[],True)
        self.assertEqual('outside_explicit_evidence_roots',checker.check('../private.txt')['check_status'])

    def test_declared_file_binding_schemas_and_deduplication(self):
        rows=baseline.file_bindings({'sources':{'src/a.cs':'a'*64},'evidence':[{'path':'src/a.cs','sha256':'a'*64}],
            'native':{'path':'/Script/kards.Func','sha256':'b'*64}})
        self.assertEqual(1,len(rows));self.assertEqual(2,len(rows[0]['origins']))

    def add_review(self,basis=None):
        r=dict(claim_id=self.report+'#/remaining/0',text_sha256=hashlib.sha256(b'old scope').hexdigest(),
            disposition='partially_addressed',basis_reports=[dict(path=basis or self.report,sha256=self.sha(basis or self.report))],
            addressed_scope=['specific branch'],residual_scope='native ABI open')
        self.plan['remaining_claim_reviews']=[r];self.save_plan()

    def test_reviewed_partial_scope_keeps_residual(self):
        self.add_review();r=self.build()['remaining_claims'][0]
        self.assertEqual('partially_addressed',r['disposition']);self.assertEqual('native ABI open',r['residual_scope'])

    def test_changed_claim_invalidates_old_disposition(self):
        self.add_review();self.data['remaining'][0]='reworded scope';self.write(self.report,self.data)
        self.assertEqual('stale_claim_review',self.build()['remaining_claims'][0]['disposition'])

    def test_changed_review_basis_rejected(self):
        self.write('out/basis.json',{'evidence':'old'});self.add_review('out/basis.json')
        self.write('out/basis.json',{'evidence':'new'})
        with self.assertRaisesRegex(ValueError,'basis changed'):self.build()

    def test_missing_review_target_rejected(self):
        self.add_review();self.plan['remaining_claim_reviews'][0]['claim_id']=self.report+'#/remaining/99';self.save_plan()
        with self.assertRaisesRegex(ValueError,'missing historical claim'):self.build()

    def test_acceptance_contract_required(self):
        self.plan['work_units'][0]['acceptance_criteria']=[];self.save_plan()
        with self.assertRaisesRegex(ValueError,'acceptance contract'):self.build()

    def test_no_unreviewed_done_status(self):
        self.plan['work_units'][0]['status']='done';self.save_plan()
        with self.assertRaisesRegex(ValueError,'closure record'):self.build()

    def test_dependency_cycle_rejected(self):
        self.plan['work_units'][0]['depends_on']=['A'];self.save_plan()
        with self.assertRaisesRegex(ValueError,'cycle'):self.build()

    def test_unknown_dependency_rejected(self):
        self.plan['work_units'][0]['depends_on']=['missing'];self.save_plan()
        with self.assertRaisesRegex(ValueError,'unknown work'):self.build()

    def test_cardset_or_name_does_not_decide_pvp_scope(self):
        d=self.build();self.assertEqual('unreviewed',d['scope_candidates'][0]['scope_status'])
        self.assertFalse(d['counts']['authoritative_pvp_denominator_established'])

    def test_duplicate_export_card_rejected(self):
        self.write('out/cards.json',[dict(name='unit'),dict(name='unit')])
        with self.assertRaisesRegex(ValueError,'duplicate authoritative'):self.build(definitions=self.repo/'out/cards.json')

    def test_outputs_reproducible_and_no_trailing_tsv_whitespace(self):
        result=self.build();out=self.repo/'generated';baseline.write(result,out)
        expected={p.name:p.read_bytes() for p in out.iterdir()};baseline.write(self.build(),out)
        self.assertEqual(expected,{p.name:p.read_bytes() for p in out.iterdir()})
        self.assertEqual(result,baseline.load(out/'baseline.json'))
        for p in out.glob('*.tsv'):
            for line in p.read_text(encoding='utf-8-sig').splitlines():self.assertEqual(line,line.rstrip(' \t'))


if __name__=='__main__':unittest.main(verbosity=2)
