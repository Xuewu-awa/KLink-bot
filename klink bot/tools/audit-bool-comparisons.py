#!/usr/bin/env python3
"""Bind Boolean equality IR inventory to current-client wrapper evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any


FUNCTIONS = ("EqualEqual_BoolBool", "NotEqual_BoolBool")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inventory(ir: dict[str, Any], function: str) -> dict[str, Any]:
    rows: list[dict[str, Any]] = []

    def visit(value: Any, card: str, program: str) -> None:
        if isinstance(value, dict):
            if value.get("op") == "math" and value.get("fn") == function:
                rows.append({
                    "card": card,
                    "program": program,
                    "instruction_i": value.get("i"),
                    "byte_offset": value.get("bo"),
                })
            if value.get("math") == function:
                rows.append({"card": card, "program": program, "expression": True})
            for child in value.values():
                visit(child, card, program)
        elif isinstance(value, list):
            for child in value:
                visit(child, card, program)

    for card, definition in ir.items():
        visit(definition.get("steps", []), card, "steps")
        for name, steps in (definition.get("locals") or {}).items():
            visit(steps, card, f"locals.{name}")
    return {
        "callsite_count": len(rows),
        "asset_count": len({row["card"] for row in rows}),
        "program_count": len({(row["card"], row["program"]) for row in rows}),
        "sample_callsites": rows[:12],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--capture", type=Path, default=Path("out/audit/systematic-rules/kismet-bool-comparisons-native-capture-current-20261104.json"))
    parser.add_argument("--output", type=Path, default=Path("out/audit/systematic-rules/bool-comparisons-native-contract-20261104.json"))
    parser.add_argument("--sdk-header", type=Path, default=Path(r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump\Engine.hpp"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    capture_path = (repo / args.capture).resolve() if not args.capture.is_absolute() else args.capture.resolve()
    output = (repo / args.output).resolve() if not args.output.is_absolute() else args.output.resolve()
    ir_path = repo / "klink bot/docs/card-ir.json"
    vm_path = repo / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs"
    fixture_path = repo / "tools/BotSim/Fixtures/kismet-bool-comparisons-native-20261104.json"
    capture = json.loads(capture_path.read_text(encoding="utf-8-sig"))
    fixture = json.loads(fixture_path.read_text(encoding="utf-8-sig"))
    ir = json.loads(ir_path.read_text(encoding="utf-8-sig"))
    header = args.sdk_header.read_text(encoding="utf-8", errors="replace")
    signatures = {}
    for function in FUNCTIONS:
        match = re.search(rf"\bvoid\s+{function}\(bool\s+A,\s*bool\s+B\);", header)
        if match is None:
            raise ValueError(f"SDK signature not found for {function}")
        signatures[function] = match.group(0)
        if function not in capture.get("functions", {}):
            raise ValueError(f"Native capture is missing {function}")
        if function not in fixture.get("functions", {}):
            raise ValueError(f"Regression fixture is missing {function}")
    if capture.get("exe_sha256") != fixture.get("exe_sha256"):
        raise ValueError("Capture and BotSim fixture executable hashes differ")
    capture_hash = sha256(capture_path)
    if capture_hash != fixture.get("capture_sha256"):
        raise ValueError("Capture and BotSim fixture SHA-256 differ")

    result = {
        "schema_version": 1,
        "report_id": "KISMET-BOOL-COMPARISONS-NATIVE-CONTRACT-20261104",
        "status": "partial",
        "scope": "Current-client KismetMathLibrary Boolean equality wrappers, complete two-value BotSim truth tables, SDK declaration and static IR callsite inventory; not card-effect or PvP certification.",
        "client": {
            "executable": capture["module"]["path"],
            "sha256": capture["exe_sha256"],
        },
        "native_capture": {
            "path": capture_path.relative_to(repo).as_posix(),
            "sha256": capture_hash,
            "owner": capture["owner"],
            "functions": {
                function: {
                    "exec_rva": capture["functions"][function]["exec_rva"],
                    "ownership_verified": capture["functions"][function]["ownership_verified"],
                    "output_instructions": fixture["functions"][function]["instructions"],
                }
                for function in FUNCTIONS
            },
            "method": "Read-only live UFunction ownership and entry-code capture. BotSim validates the local VM against the captured final condition-code and output-store instructions; the game functions were not called and process memory was not written.",
        },
        "sdk": {
            "header": str(args.sdk_header.resolve()),
            "sha256": sha256(args.sdk_header),
            "function_signatures": signatures,
        },
        "semantics": {
            "EqualEqual_BoolBool": "true iff both Boolean operands have the same value; final native output uses sete.",
            "NotEqual_BoolBool": "true iff the Boolean operands differ; final native output uses setne.",
            "validated_inputs": fixture["binary_cases"],
            "native_result_vectors": {name: fixture["functions"][name]["results"] for name in FUNCTIONS},
            "regression_cases": 8,
        },
        "inventory": {
            function: inventory(ir, function)
            for function in FUNCTIONS
        },
        "source_sha256": {
            "card_ir": sha256(ir_path),
            "vm": sha256(vm_path),
            "selftest": sha256(repo / "tools/BotSim/SelfTest.KismetMath.cs"),
            "selftest_registry": sha256(repo / "tools/BotSim/SelfTest.cs"),
            "project": sha256(repo / "tools/BotSim/BotSim.csproj"),
            "fixture": sha256(fixture_path),
            "capture_script": sha256(repo / "out/audit/capture-kismet-bool-comparisons-current-20261104.py"),
            "audit_script": sha256(Path(__file__).resolve()),
        },
        "limitations": [
            "The machine-code evidence is valid only for the recorded executable SHA-256.",
            "Only the two valid Boolean values and their complete pairwise matrix are covered; malformed frames are not.",
            "Static IR counts include non-PvP assets and do not establish runtime reachability or full-card behavior.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output),
        "inventory": {name: {"callsite_count": data["callsite_count"], "asset_count": data["asset_count"]} for name, data in result["inventory"].items()},
        "regression_cases": result["semantics"]["regression_cases"],
        "native_executable_matches_fixture": capture["exe_sha256"] == fixture["exe_sha256"],
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
