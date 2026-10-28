import hashlib
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

spec=importlib.util.spec_from_file_location('history',Path(__file__).with_name('analyze-audit-history.py'))
history=importlib.util.module_from_spec(spec);spec.loader.exec_module(history)


class HistoryTests(unittest.TestCase):
    def test_variants_deduplicated_and_report_bytes_required(self):
        rows=list(history.variants(b'a\nb\n'))
        self.assertEqual(len(rows),len({r[1] for r in rows}))
        wanted=b'\xef\xbb\xbfa\r\nb\r\n'
        self.assertIn(wanted,[r[1] for r in rows])
        self.assertNotEqual(history.sha(wanted),history.sha(b'a\nb\n'))

    def test_json_type_bool_int_and_number_encoding_not_collapsed(self):
        self.assertEqual(['/x'],history.json_changes({'x':True},{'x':1}))
        self.assertEqual(['/x'],history.json_changes({'x':1},{'x':1.0}))

    def test_json_key_order_ignored_and_pointer_escaped(self):
        self.assertEqual([],history.json_changes({'a':1,'b':2},{'b':2,'a':1}))
        self.assertEqual(['/a~1b~0c'],history.json_changes({'a/b~c':1},{'a/b~c':2}))

    def test_json_array_order_and_added_removed_nodes(self):
        self.assertEqual(['/0','/1'],history.json_changes([1,2],[2,1]))
        self.assertEqual(['/1'],history.json_changes([1],[1,2]))
        self.assertEqual(['/x'],history.json_changes({'x':1},{}))

    def test_duplicate_json_keys_fail_closed(self):
        with self.assertRaisesRegex(ValueError,'duplicate'):history.strict_json(b'{"x":1,"x":2}')

    def test_nonstandard_json_fail_closed(self):
        with self.assertRaises(ValueError):history.strict_json(b'{"x":NaN}')

    def document(self,**kwargs):
        d=dict(parse_errors=[],duplicate_member_ids=[],context_sha256='ctx',structure_sha256='structure',
            has_conditional_directives=False,members=[dict(id='C::F',name='F',kind='method',tokens_sha256='same')])
        d.update(kwargs);return d

    def test_syntax_equality_never_certifies_semantics(self):
        result=history.compare_csharp(self.document(),self.document())
        self.assertEqual('trivia_only_for_this_exact_file_pair',result['classification'])
        self.assertIn('Token equality is not semantic certification; callees/state/version can change.',result['blockers'])

    def test_parse_errors_prevent_equivalence(self):
        r=history.compare_csharp(self.document(parse_errors=['bad']),self.document())
        self.assertEqual('parse_or_identity_ambiguous',r['classification'])

    def test_duplicate_member_identity_prevents_equivalence(self):
        r=history.compare_csharp(self.document(duplicate_member_ids=['C::F']),self.document())
        self.assertEqual('parse_or_identity_ambiguous',r['classification'])

    def test_changed_field_warns_about_unchanged_method_dependencies(self):
        after=self.document(structure_sha256='new',members=self.document()['members']+[dict(id='C::x',name='x',kind='field',tokens_sha256='field')])
        r=history.compare_csharp(self.document(),after)
        self.assertEqual(1,r['unchanged_members']);self.assertEqual('field',r['added_members'][0]['kind'])
        self.assertTrue(any('may affect unchanged methods' in x for x in r['blockers']))

    def test_context_change_kept_separate(self):
        r=history.compare_csharp(self.document(),self.document(context_sha256='different',structure_sha256='new'))
        self.assertTrue(r['context_changed']);self.assertEqual([],r['changed_members'])

    def test_exact_git_recovery_no_nearest_version_fallback(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);repo=root/'repo';repo.mkdir();cache=root/'cache'
            def git(*args):subprocess.run(['git',*args],cwd=repo,check=True,capture_output=True)
            git('init');git('config','user.name','Audit Fixture');git('config','user.email','fixture@invalid.local')
            p=repo/'sample.cs';p.write_bytes(b'class C { int F()=>1; }\n');git('add','sample.cs');git('commit','-m','one')
            expected=history.sha(b'\xef\xbb\xbfclass C { int F()=>1; }\r\n')
            p.write_bytes(b'class C { int F()=>2; }\n');git('add','sample.cs');git('commit','-m','two')
            baseline=dict(changed_files=[dict(path=str(p),historical_expected_sha256=[expected,'0'*64])])
            result=history.recover(repo,baseline,cache);rows={r['expected_sha256']:r for r in result['rows']}
            self.assertEqual('exact_bytes_recovered',rows[expected]['status'])
            self.assertEqual(expected,history.sha(Path(rows[expected]['snapshot_path']).read_bytes()))
            self.assertNotEqual('exact_bytes_recovered',rows['0'*64]['status'])

    def test_role_labels_do_not_exclude_adapters_from_rules(self):
        self.assertIn('usage needs tracing',history.review_lane('src/KLink.Bot/Server/Bridge.cs'))
        self.assertIn('not production-rule',history.review_lane('tools/BotSim/SelfTest.cs'))


if __name__=='__main__':unittest.main(verbosity=2)
