"""Contract regressions against the extracted consumer evidence, no client calls."""
import copy
import importlib.util
import json
import unittest

from pathlib import Path

spec = importlib.util.spec_from_file_location('boundaries', Path(__file__).with_name('audit-display-consumer-boundaries.py'))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class Boundaries(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.parsed = json.loads((audit.DEFAULT_REVIEW / audit.PARSED).read_text(encoding='utf-8-sig'))
        cls.snapshot = json.loads((audit.DEFAULT_REVIEW / audit.SNAPSHOT).read_text(encoding='utf-8-sig'))

    def setUp(self):
        self.p = copy.deepcopy(self.parsed)
        self.s = copy.deepcopy(self.snapshot)

    def function(self, name, owner=None):
        return next(f for f in self.s['functions'] if f['name'] == name and (owner is None or f['owner'] == owner))

    def reject(self):
        with self.assertRaises((ValueError, KeyError)):
            audit.check(self.p, self.s)

    def test_current_evidence_does_not_grant_exemption(self):
        r = audit.check(self.p, self.s)
        self.assertEqual((len(r['functions']), len(r['forwarding']), r['callback_entry']), (6, 3, 12721))
        self.assertFalse(r['admission_exempt'])
        self.assertFalse(r['global_noninterference_proven'])

    def test_new_spawn_text_branch(self):
        self.p['SpawnCardinHandbySide']['bytecode'].append(dict(Inst='JumpIfNot', StatementIndex=99999,
            Offset=0, Condition=audit.variable('newCardText')))
        self.reject()

    def test_new_create_text_write(self):
        self.p['CreateCard']['bytecode'].append(dict(Inst='Let', StatementIndex=99999,
            Variable={'Inst': 'InstanceVariable', 'Variable Name': 'Attack'}, Expression=audit.variable('newCardText')))
        self.reject()

    def test_receiver_change(self):
        f = self.function('NotifyAppendNumberToCardText')
        f['decoded'][0]['Context']['InterfaceValue']['Variable Name'] = 'OtherReceiver'
        self.reject()

    def test_payload_change(self):
        f = self.function('NotifyAppendNumberToCardText')
        f['decoded'][0]['Expression']['Parameters'][1] = audit.variable('instigatorID')
        self.reject()

    def test_persistent_field_change(self):
        self.function('HandleAppendNumberToCardText', 'BP_OnlineMatch_C')['decoded'][1]['Property Name'] = 'Attack'
        self.reject()

    def test_entry_change(self):
        self.function('HandleAppendNumberToCardText', 'BP_OnlineMatch_C')['decoded'][4]['Parameters'][0]['Value'] += 1
        self.reject()

    def test_callback_falls_through(self):
        f = self.function('ExecuteUbergraph_BP_OnlineMatch')
        next(s for s in f['decoded'] if s['StatementIndex'] == 12771)['Inst'] = 'Nothing'
        self.reject()

    def test_new_payload_consumer(self):
        self.function('AddSubActionAppendNumberToCardText')['decoded'].insert(0,
            dict(Inst='Return', StatementIndex=-1, Expression=audit.variable('newCardText')))
        self.reject()

    def test_truncated_script(self):
        f = self.function('AddNumberToText', 'BP_CardFunctions_C')
        f['script_hex'] = f['script_hex'][:-2]
        self.reject()

    def test_incomplete_decoder(self):
        self.function('CreateAction_AddSubAction')['decode_complete'] = False
        self.reject()

    def test_duplicate_function(self):
        self.s['functions'].append(copy.deepcopy(self.function('NotifyAppendNumberToCardText')))
        self.reject()


if __name__ == '__main__':
    unittest.main()
