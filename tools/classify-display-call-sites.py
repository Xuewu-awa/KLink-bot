"""Inventory display-named calls; review exact local flows without changing admission.

IR is derived evidence. Raw parsed references identify the extracted bytecode being
compared, not equivalence to a newer client. No row produced here is an exemption.
"""
import argparse
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path

NAMES = {"Format", "GetDisplayName", "PrintString"}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def nodes(value, path="$"):
    if isinstance(value, dict):
        yield path, value
        for key, child in value.items():
            yield from nodes(child, path + "." + key)
    elif isinstance(value, list):
        for i, child in enumerate(value):
            yield from nodes(child, path + f"[{i}]")


def inventory(cards):
    rows = []
    for card, obj in cards.items():
        for program, steps in [("steps", obj.get("steps", [])), *obj.get("locals", {}).items()]:
            for step in steps:
                for path, node in nodes(step):
                    fn = node.get("math") or node.get("call")
                    if node.get("op") in {"call", "math"}:
                        fn = node.get("fn")
                    if fn in NAMES:
                        rows.append(dict(card=card, asset=obj.get("asset"), program=program,
                            instruction=step["i"], byte_offset=step.get("bo"), expression_path=path,
                            function=fn, classification="unreviewed", admission_exempt=False))
    return rows


def load_reviewer(repo, parsed):
    path = repo / "klink bot/tools/audit-four-display-flows.py"
    spec = importlib.util.spec_from_file_location("four_display_flows", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.PARSED = parsed
    return module


def classify(cards, reviewer):
    rows = inventory(cards)
    flows = []
    # Re-run the existing exact consumer and raw/IR reference-set checks. A
    # changed scope fails this build; an additional site stays unreviewed.
    for card, scope in reviewer.CASES.items():
        review = reviewer.review(card, cards[card], scope)
        program, fn, source_i, *_ = scope
        matches = [r for r in rows if (r["card"], r["program"], r["instruction"],
            r["function"], r["expression_path"]) == (card, program, source_i, fn, "$.src")]
        if len(matches) != 1:
            raise ValueError(f"{card}: ambiguous or changed source shape")
        matches[0]["classification"] = "reviewed_local_display_argument_flow"
        matches[0]["review_index"] = len(flows)
        flows.append(review)

    # The logging endpoint in the reviewed 75th chain is itself a missing call.
    # Check both representations: direct void call, no return/output consumer.
    card = "card_unit_75th_rifle_regiment"
    step = next(s for s in cards[card]["locals"]["SpawnCopy"] if s["i"] == 90)
    if step.get("op") != "math" or step.get("fn") != "PrintString" or any(
        step.get(k) for k in ("dst", "outs", "src", "cond", "recv")):
        raise ValueError("75th: PrintString output or execution shape changed")
    raw = next(f["parsed_evidence"] for f in flows if f["card"] == card)
    statement = next(s for s in raw["reference_statements"] if s["StatementIndex"] == 90)
    if statement.get("Inst") != "CallMath" or statement.get("Function") != "PrintString":
        raise ValueError("75th: raw logging endpoint is no longer a direct CallMath")
    matches = [r for r in rows if (r["card"], r["program"], r["instruction"], r["function"],
        r["expression_path"]) == (card, "SpawnCopy", 90, "PrintString", "$")]
    if len(matches) != 1:
        raise ValueError("75th: ambiguous logging endpoint")
    matches[0].update(classification="reviewed_unused_logging_result",
        review_index=next(i for i, f in enumerate(flows) if f["card"] == card))
    return rows, flows


def build(repo, parsed):
    ir = repo / "klink bot/docs/card-ir.json"
    cards = json.loads(ir.read_text(encoding="utf-8-sig"))
    reviewer = load_reviewer(repo, parsed)
    rows, flows = classify(cards, reviewer)
    source_files = ["tools/classify-display-call-sites.py", "tools/test-display-call-sites.py",
        "klink bot/tools/audit-four-display-flows.py",
        "src/KLink.Bot/Effects/Blueprint/KismetIr.cs", "src/KLink.Bot/Effects/Blueprint/KismetVm.cs",
        "src/KLink.Bot/Effects/Blueprint/KismetMissingCallSites.cs",
        "src/KLink.Bot/Effects/CardApi.cs", "src/KLink.Bot/Effects/CardApiDispatch.cs"]
    return dict(schema_version=1, kind="display_call_site_classification", ir_sha256=sha(ir),
        source_bindings={p: sha(repo / p) for p in source_files},
        summary=dict(total_sites=len(rows), functions=dict(sorted(Counter(r["function"] for r in rows).items())),
            classifications=dict(sorted(Counter(r["classification"] for r in rows).items())),
            admission_exempt_sites=0), sites=rows, reviews=flows,
        limitations=[
            "All sites remain subject to existing training admission. This inventory is not a runtime allowlist.",
            "Review proves a scoped function-local result-use boundary, not native callee equivalence or whole-card correctness.",
            "Unreviewed location/scenario-named assets are not automatically excluded from PvP by their names.",
            "IR omits text templates/struct fields. Raw parsed hashes identify extraction artifacts, not current-client version equivalence.",
            "Source hashes and IR hashes are input bindings, not runtime-program-json/v1 hashes; do not compare them as the same scheme.",
            "No path reachability, inherited dispatch or hidden native text consumers are certified.",
        ])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--parsed", type=Path, default=Path("D:/Codex Workshop/kards-install-review/parsed"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = build(args.repo, args.parsed)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report["summary"]))


if __name__ == "__main__":
    main()
