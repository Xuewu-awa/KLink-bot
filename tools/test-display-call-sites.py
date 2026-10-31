"""Negative regressions for the bounded display-result review, using local evidence."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("display_sites", ROOT / "tools/classify-display-call-sites.py")
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


class DisplaySitesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cards = json.loads((ROOT / "klink bot/docs/card-ir.json").read_text(encoding="utf-8-sig"))
        report = tool.build(ROOT, Path("D:/Codex Workshop/kards-install-review/parsed"))
        cls.raw = {r["card"]: json.loads(Path(r["parsed_evidence"]["path"]).read_text(encoding="utf-8"))
                   for r in report["reviews"]}

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.parsed = Path(self.temp.name)
        for card, raw in self.raw.items():
            (self.parsed / (card + ".functions.json")).write_text(json.dumps(raw), encoding="utf-8")
        self.cards = {c: copy.deepcopy(self.cards[c]) for c in self.raw}
        self.reviewer = tool.load_reviewer(ROOT, self.parsed)

    def classify(self):
        return tool.classify(self.cards, self.reviewer)

    def test_current_four_flows_and_log_are_not_admission_exemptions(self):
        rows, flows = self.classify()
        self.assertEqual(len(flows), 4)
        self.assertEqual(len(rows), 5)
        self.assertTrue(all(r["classification"].startswith("reviewed_") for r in rows))
        self.assertFalse(any(r["admission_exempt"] for r in rows))

    def add_consumer(self, step):
        self.cards["card_event_carrier_battle"]["steps"].append(dict(i=999999, bo=999999, **step))

    @staticmethod
    def ref():
        return {"var": "CallFunc_Format_ReturnValue", "scope": "local"}

    def test_branch_consumer_reopens_scope(self):
        self.add_consumer(dict(op="jump_if_not", cond=self.ref(), target=0))
        with self.assertRaisesRegex(ValueError, "unexpected dependent consumer"):
            self.classify()

    def test_instance_write_reopens_scope(self):
        self.add_consumer(dict(op="set", dst="ruleState", dst_scope="instance", src=self.ref()))
        with self.assertRaisesRegex(ValueError, "unexpected dependent consumer"):
            self.classify()

    def test_tainted_receiver_reopens_scope(self):
        steps = self.cards["card_event_carrier_battle"]["steps"]
        next(s for s in steps if s["i"] == 791)["recv"] = self.ref()
        with self.assertRaisesRegex(ValueError, "tainted receiver"):
            self.classify()

    def test_extra_arithmetic_copy_reopens_scope(self):
        self.add_consumer(dict(op="set", dst="numeric", dst_scope="local",
            src={"math": "Len", "args": [self.ref()]}))
        with self.assertRaisesRegex(ValueError, "unreviewed dependent assignment"):
            self.classify()

    def test_new_display_named_source_does_not_inherit_review(self):
        self.add_consumer(dict(op="set", dst="newText", dst_scope="local", src={"math": "Format", "args": []}))
        rows, _ = self.classify()
        extra = next(r for r in rows if r["instruction"] == 999999)
        self.assertEqual(extra["classification"], "unreviewed")
        self.assertFalse(extra["admission_exempt"])

    def test_missing_raw_evidence_fails_closed(self):
        (self.parsed / "card_event_carrier_battle.functions.json").unlink()
        with self.assertRaisesRegex(ValueError, "ambiguous/missing parsed evidence"):
            self.classify()

    def test_new_raw_only_consumer_fails_closed(self):
        card = "card_event_carrier_battle"
        raw = copy.deepcopy(self.raw[card])
        raw["ExecuteUbergraph_" + card]["bytecode"].append(dict(StatementIndex=999999,
            Inst="Return", Expression={"Inst": "LocalVariable", "Variable Name": "CallFunc_Format_ReturnValue"}))
        (self.parsed / (card + ".functions.json")).write_text(json.dumps(raw), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "raw/IR reference statement sets differ"):
            self.classify()

    def test_log_output_cannot_keep_unused_result_classification(self):
        step = next(s for s in self.cards["card_unit_75th_rifle_regiment"]["locals"]["SpawnCopy"] if s["i"] == 90)
        step["dst"] = "ruleState"
        with self.assertRaisesRegex(ValueError, "PrintString output or execution shape changed"):
            self.classify()

    def test_display_sink_output_consumer_reopens_scope(self):
        # A sink's output is conservatively dependent too; reviewing its text
        # argument must not hide a newly introduced rule branch on that output.
        self.cards["card_unit_115th_separate_btn"]["steps"].append(dict(i=999999,
            op="jump_if_not", cond={"var": "CallFunc_AddNumberToText_qqq_1", "scope": "local"}, target=0))
        with self.assertRaisesRegex(ValueError, "unexpected dependent consumer"):
            self.classify()


if __name__ == "__main__":
    unittest.main(verbosity=2)
