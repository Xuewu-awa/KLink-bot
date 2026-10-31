import copy
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('campaign_group', ROOT / 'tools/audit-display-campaign-group.py')
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


class CampaignGroupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = tool.build(ROOT, Path('D:/Codex Workshop/kards-install-review/parsed'))
        cls.cards = json.loads((ROOT / 'klink bot/docs/card-ir.json').read_text(encoding='utf-8-sig'))

    def setUp(self):
        self.site = next(r for r in self.report['sites'] if r['card'] == 'card_location_german_scen1' and r['instruction'] == 4364)
        self.obj = copy.deepcopy(self.cards[self.site['card']])
        self.raw = json.loads(Path(self.report['parsed_evidence'][self.site['card']]['path']).read_text())
        self.site = {k: v for k, v in self.site.items() if k in ('card', 'asset', 'program', 'instruction', 'byte_offset', 'expression_path', 'function', 'classification', 'admission_exempt')}

    def review(self):
        return tool.review_site(self.site, self.obj, self.raw)

    def test_complete_group_stays_unreviewed(self):
        self.assertEqual(self.report['summary']['sites'], 55)
        self.assertEqual(self.report['summary']['boundaries'], {'direct_campaign_message_argument': 54, 'direct_void_log': 1})
        self.assertTrue(all(r['classification'] == 'unreviewed' and not r['admission_exempt'] for r in self.report['sites']))

    def test_reused_local_keeps_all_three_consumers(self):
        r = self.review()
        self.assertEqual([c['instruction'] for c in r['consumers']], [4485, 5347, 5875])
        self.assertEqual(r['local_reassignment_count'], 3)

    def test_new_branch_use_rejected(self):
        self.obj['steps'].append(dict(i=99999, op='jumpIf', cond={'var': 'CallFunc_Format_ReturnValue', 'scope': 'local'}))
        with self.assertRaisesRegex(ValueError, 'reference set'): self.review()

    def test_raw_only_use_rejected(self):
        self.raw['ExecuteUbergraph_' + self.site['card']]['bytecode'].append({'StatementIndex': 99999, 'Inst': 'LocalVariable', 'Variable Name': 'CallFunc_Format_ReturnValue'})
        with self.assertRaisesRegex(ValueError, 'reference set'): self.review()

    def test_consumer_change_rejected(self):
        next(s for s in self.obj['steps'] if s['i'] == 4485)['fn'] = 'DealDamage'
        with self.assertRaisesRegex(ValueError, 'direct consumer'): self.review()

    def test_other_argument_rejected(self):
        s = next(s for s in self.obj['steps'] if s['i'] == 4485)
        s['args'][0] = s['args'][1]
        with self.assertRaisesRegex(ValueError, 'unexpected argument'): self.review()

    def test_output_slot_rejected(self):
        next(s for s in self.obj['steps'] if s['i'] == 4485)['outs'] = [{'param': 1, 'slot': 'result'}]
        with self.assertRaisesRegex(ValueError, 'direct consumer'): self.review()

    def test_raw_consumer_change_rejected(self):
        next(s for s in self.raw['ExecuteUbergraph_' + self.site['card']]['bytecode'] if s['StatementIndex'] == 4485)['Expression']['FunctionName'] = 'DealDamage'
        with self.assertRaisesRegex(ValueError, 'raw consumer'): self.review()

    def test_shared_boundary_stays_unproven_invocation(self):
        self.assertFalse(self.report['shared_boundary']['actual_invocation_proven'])
        self.assertEqual(self.report['shared_boundary']['event_entry'], 443)
        self.assertIn('FText&', self.report['shared_boundary']['sdk']['declaration'])

    def test_other_interface_rejected(self):
        cf = json.loads(Path(self.report['parsed_evidence']['BP_CardFunctions']['path']).read_text())
        n = json.loads(Path(self.report['parsed_evidence']['U_CardFunctionsNotifier']['path']).read_text())
        n['NotiferStarCounterChanged']['bytecode'][0]['Context']['Expression']['Variable Name'] = 'OnlineMatchReceiver'
        with self.assertRaisesRegex(ValueError, 'interface boundary'): tool.shared_boundary(cf, n)


if __name__ == '__main__':
    unittest.main()
