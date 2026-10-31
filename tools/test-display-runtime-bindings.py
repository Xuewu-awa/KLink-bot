"""Fail-closed regressions for the audit-only runtime binding tool."""
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


tool = load('runtime_binding', ROOT / 'tools/bind-display-runtime-programs.py')


class RuntimeBindingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        classifier = load('classifier', ROOT / 'tools/classify-display-call-sites.py')
        cls.classification = classifier.build(ROOT, Path('D:/Codex Workshop/kards-install-review/parsed'))
        cls.cards = json.loads((ROOT / 'klink bot/docs/card-ir.json').read_text(encoding='utf-8-sig'))
        cls.original = json.loads((ROOT / 'out/audit/systematic-rules/display-runtime-programs-20261101.json').read_text())

    def setUp(self):
        self.export = copy.deepcopy(self.original)

    def bind(self):
        return tool.bind(self.classification, self.export, self.cards)

    def mutate_program(self, change):
        row = self.export['programs'][1]
        p = json.loads(base64.b64decode(row['serializedProgramBase64']))
        change(p)
        raw = json.dumps(p).encode()
        row['serializedProgramBase64'] = base64.b64encode(raw).decode()
        row['runtimeProgramSha256'] = hashlib.sha256(raw).hexdigest()

    def test_current_scope_no_exemptions(self):
        rows = self.bind()
        self.assertEqual(len(rows), 5)
        self.assertTrue(all(not r['admission_exempt'] for r in rows))
        self.assertEqual(len({r['runtime_program_sha256'] for r in rows}), 4)

    def test_changed_hash(self):
        self.export['programs'][0]['runtimeProgramSha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'hash mismatch'): self.bind()

    def test_changed_identity_even_rehashed(self):
        self.mutate_program(lambda p: p.update(FunctionName='Other'))
        with self.assertRaisesRegex(ValueError, 'identity'): self.bind()

    def test_changed_source_entry(self):
        self.mutate_program(lambda p: p.update(SourceEntry=42))
        with self.assertRaisesRegex(ValueError, 'source entry'): self.bind()

    def test_changed_instruction(self):
        self.mutate_program(lambda p: p.update(Steps=[s for s in p['Steps'] if s['Index'] != 680]))
        with self.assertRaisesRegex(ValueError, 'missing instruction'): self.bind()

    def test_same_name_at_other_expression(self):
        def change(p):
            s = next(s for s in p['Steps'] if s['Index'] == 680)
            s['Condition'], s['Source'] = s['Source'], None
        self.mutate_program(change)
        with self.assertRaisesRegex(ValueError, 'missing source'): self.bind()

    def test_other_function(self):
        def change(p): next(s for s in p['Steps'] if s['Index'] == 680)['Source']['Math'] = 'Other'
        self.mutate_program(change)
        with self.assertRaisesRegex(ValueError, 'expression function'): self.bind()

    def test_missing_program(self):
        self.export['programs'].pop()
        with self.assertRaisesRegex(ValueError, 'incomplete program'): self.bind()

    def test_duplicate_program(self):
        self.export['programs'].append(copy.deepcopy(self.export['programs'][0]))
        with self.assertRaisesRegex(ValueError, 'duplicate program'): self.bind()

    def test_changed_ir(self):
        self.export['irSha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'IR binding'): self.bind()

    def test_capture_loss(self):
        for key in ('omitted', 'captureFailures'):
            with self.subTest(key=key):
                self.export = copy.deepcopy(self.original)
                self.export[key] = 1
                with self.assertRaisesRegex(ValueError, 'incomplete capture'): self.bind()

    def test_observation_changed_paths_or_hashes(self):
        for key in ('ExpressionPath', 'RuntimeProgramSha256', 'FunctionName', 'ProgramEntry', 'ByteOffset'):
            with self.subTest(key=key):
                self.export = copy.deepcopy(self.original)
                self.export['observed'][0]['site'][key] = 'changed'
                with self.assertRaisesRegex(ValueError, 'observation binding'): self.bind()

    def test_missing_or_duplicate_observation(self):
        self.export['observed'].pop()
        with self.assertRaisesRegex(ValueError, 'observation inventory'): self.bind()
        self.export['observed'] = [self.original['observed'][0]] * 2
        with self.assertRaisesRegex(ValueError, 'duplicate'): self.bind()

    def test_exemption_forbidden(self):
        self.export['admissionExempt'] = True
        with self.assertRaisesRegex(ValueError, 'exemption forbidden'): self.bind()


if __name__ == '__main__':
    unittest.main()
