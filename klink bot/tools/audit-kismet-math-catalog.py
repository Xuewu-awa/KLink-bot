#!/usr/bin/env python3
"""Inventory executable Kismet math nodes and compare them with the VM catalog."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path
from typing import Any


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_declared_math_names(source: str) -> tuple[set[str], set[str]]:
    catalog = re.search(
        r"ImplementedMathNames.*?= new HashSet<string>\(StringComparer\.Ordinal\)"
        r"\s*\{(.*?)\n\s*\};",
        source,
        re.S,
    )
    if catalog is None:
        raise ValueError("Could not locate KismetVm.ImplementedMathNames initializer")
    declared = set(re.findall(r'"([^"]+)"', catalog.group(1)))

    start = source.find("private object? EvalMath")
    end = source.find("internal static int ToInt", start)
    if start < 0 or end < 0:
        raise ValueError("Could not locate KismetVm.EvalMath body")
    cases = set(re.findall(r'case "([^"]+)"', source[start:end]))
    return declared, cases


def iter_expr_nodes(value: Any, path: str):
    if not isinstance(value, dict):
        return
    yield value, path
    for key in ("args", "array"):
        children = value.get(key)
        if isinstance(children, list):
            for index, child in enumerate(children):
                yield from iter_expr_nodes(child, f"{path}.{key}[{index}]")
    context = value.get("ctx")
    if isinstance(context, dict):
        yield from iter_expr_nodes(context, f"{path}.ctx")


def collect_steps(
    card_name: str,
    program_name: str,
    steps: Any,
    counts: dict[str, dict[str, Any]],
    declared_names: set[str],
    op_counts: Counter[str],
    unknown_ops: list[dict[str, Any]],
    invalid_math: list[dict[str, Any]],
) -> tuple[int, int]:
    expression_math_count = 0
    standalone_math_count = 0
    if not isinstance(steps, list):
        return expression_math_count, standalone_math_count

    for step_index, step in enumerate(steps):
        if not isinstance(step, dict):
            continue
        op = step.get("op")
        op_counts[str(op)] += 1
        instruction = step.get("i")
        byte_offset = step.get("bo")
        step_path = f"{program_name}[{step_index}]"

        def add_math(
            name: str,
            kind: str,
            expression_path: str,
            node: Any = None,
        ) -> None:
            record = counts.setdefault(
                name,
                {
                    "occurrence_count": 0,
                    "expression_math_count": 0,
                    "standalone_op_math_count": 0,
                    "examples": [],
                    "undeclared_occurrences": [],
                },
            )
            record["occurrence_count"] += 1
            record[f"{kind}_count"] += 1
            location = {
                "card": card_name,
                "program": program_name,
                "instruction_i": instruction,
                "bo": byte_offset,
                "expression_path": expression_path,
            }
            if len(record["examples"]) < 5:
                record["examples"].append(location.copy())
            if name not in declared_names:
                if isinstance(node, dict):
                    location["node"] = node
                record["undeclared_occurrences"].append(location)

        if op == "math":
            function = step.get("fn")
            if isinstance(function, str):
                standalone_math_count += 1
                add_math(function, "standalone_op_math", step_path, step)
            else:
                invalid_math.append(
                    {
                        "card": card_name,
                        "program": program_name,
                        "instruction_i": instruction,
                        "bo": byte_offset,
                        "expression_path": step_path,
                        "reason": "op=math step has no string fn",
                    }
                )

        if op == "unknown":
            unknown_ops.append(
                {
                    "card": card_name,
                    "program": program_name,
                    "instruction_i": instruction,
                    "bo": byte_offset,
                    "unknown": step.get("unknown"),
                }
            )

        expressions: list[tuple[str, Any]] = []
        for key in ("src", "cond", "recv"):
            if key in step:
                expressions.append((key, step[key]))
        for key in ("args", "values"):
            values = step.get(key)
            if isinstance(values, list):
                expressions.extend((f"{key}[{index}]", value) for index, value in enumerate(values))

        for expression_path, expression in expressions:
            for node, node_path in iter_expr_nodes(expression, expression_path):
                name = node.get("math")
                if isinstance(name, str):
                    expression_math_count += 1
                    add_math(name, "expression_math", node_path, node)
                elif "math" in node:
                    invalid_math.append(
                        {
                            "card": card_name,
                            "program": program_name,
                            "instruction_i": instruction,
                            "bo": byte_offset,
                            "expression_path": node_path,
                            "reason": "math key is not a string",
                        }
                    )

    return expression_math_count, standalone_math_count


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--repo",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help="KLink-bot repository root",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="output JSON path (defaults to out/audit/systematic-rules/)",
    )
    args = parser.parse_args()
    repo = args.repo.resolve()
    ir_path = repo / "klink bot/docs/card-ir.json"
    vm_path = repo / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs"
    output = args.output or (
        repo / "out/audit/systematic-rules/kismet-math-node-catalog-20261103.json"
    )
    output = output.resolve()

    ir_bytes = ir_path.read_bytes()
    ir = json.loads(ir_bytes)
    vm_source = vm_path.read_text(encoding="utf-8")
    declared, switch_cases = read_declared_math_names(vm_source)

    counts: dict[str, dict[str, Any]] = {}
    op_counts: Counter[str] = Counter()
    unknown_ops: list[dict[str, Any]] = []
    invalid_math: list[dict[str, Any]] = []
    expression_math_total = 0
    standalone_math_total = 0

    for card_name, card in ir.items():
        expression_count, step_math_count = collect_steps(
            card_name,
            "steps",
            card.get("steps"),
            counts,
            declared,
            op_counts,
            unknown_ops,
            invalid_math,
        )
        expression_math_total += expression_count
        standalone_math_total += step_math_count

        locals_map = card.get("locals")
        if isinstance(locals_map, dict):
            for local_name, steps in locals_map.items():
                expression_count, step_math_count = collect_steps(
                    card_name,
                    f"locals.{local_name}",
                    steps,
                    counts,
                    declared,
                    op_counts,
                    unknown_ops,
                    invalid_math,
                )
                expression_math_total += expression_count
                standalone_math_total += step_math_count

    for name, occurrence in counts.items():
        occurrence["declared_in_ImplementedMathNames"] = name in declared
        if name in declared:
            occurrence.pop("undeclared_occurrences", None)
        else:
            occurrence["undeclared_occurrence_count"] = len(
                occurrence["undeclared_occurrences"]
            )

    result = {
        "schema_version": 1,
        "report_id": "KISMET-MATH-NODE-CATALOG-20261103",
        "scope": "executable card IR steps and local-function steps; display/campaign and non-PvP assets are included",
        "inputs": {
            "klink bot/docs/card-ir.json": sha256(ir_path),
            "src/KLink.Bot/Effects/Blueprint/KismetVm.cs": sha256(vm_path),
            "klink bot/tools/audit-kismet-math-catalog.py": sha256(Path(__file__).resolve()),
        },
        "totals": {
            "ir_card_count": len(ir),
            "expression_nodes_with_math_key": expression_math_total,
            "standalone_op_math_steps": standalone_math_total,
            "total_math_call_sites": expression_math_total + standalone_math_total,
            "distinct_math_names": len(counts),
            "declared_math_names": len(declared),
            "evalmath_switch_case_names": len(switch_cases),
            "unknown_operation_steps": len(unknown_ops),
            "invalid_math_name_nodes": len(invalid_math),
        },
        "declaration_switch_comparison": {
            "declared_without_evalmath_case": sorted(declared - switch_cases),
            "evalmath_case_without_declaration": sorted(switch_cases - declared),
            "declared_names_not_seen_in_ir": sorted(declared - counts.keys()),
            "meaning": "name-set equality checks source catalog consistency only; it does not validate operation semantics",
        },
        "names": [
            {"name": name, **counts[name]}
            for name in sorted(counts)
        ],
        "unknown_operation_examples": unknown_ops[:20],
        "invalid_math_name_examples": invalid_math[:20],
        "limitations": [
            "This inventory follows executable steps and locals represented in the current card-ir.json; it does not prove PvP scope or reachability.",
            "ImplementedMathNames and EvalMath case-name agreement is not semantic validation against KARDS native behavior.",
            "Unknown IR operation bodies remain opaque; this report does not infer their behavior.",
            "A catalog name that is absent from the IR is not evidence of a missing live rule.",
        ],
    }

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    missing = [name for name, item in counts.items() if not item["declared_in_ImplementedMathNames"]]
    print(
        json.dumps(
            {
                "output": str(output),
                "totals": result["totals"],
                "undeclared_math_names": sorted(missing),
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
