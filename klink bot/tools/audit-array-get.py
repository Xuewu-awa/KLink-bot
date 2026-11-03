#!/usr/bin/env python3
"""Bind Array_Get IR inventory and simulator dispatch checks to current-client evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path
from typing import Any


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inventory(ir: dict[str, Any]) -> dict[str, Any]:
    rows: list[dict[str, Any]] = []

    def visit(value: Any, card: str, program: str) -> None:
        if isinstance(value, dict):
            if value.get("fn") == "Array_Get" or value.get("call") == "Array_Get":
                args = value.get("args") or []
                rows.append({
                    "card": card,
                    "program": program,
                    "instruction_i": value.get("i"),
                    "byte_offset": value.get("bo"),
                    "argument_shapes": [sorted(arg) if isinstance(arg, dict) else None for arg in args],
                    "output_parameter_indices": [out.get("param") for out in value.get("outs", []) if isinstance(out, dict)],
                    "output_slots": [out.get("slot") for out in value.get("outs", []) if isinstance(out, dict)],
                })
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
        "arity_counts": dict(Counter(str(len(row["argument_shapes"])) for row in rows)),
        "output_parameter_shapes": dict(Counter(str(row["output_parameter_indices"]) for row in rows)),
        "sample_callsites": rows[:12],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--capture", type=Path, default=Path("out/audit/systematic-rules/kismet-array-get-native-capture-current-20261104.json"))
    parser.add_argument("--fixture", type=Path, default=Path("tools/BotSim/Fixtures/kismet-array-get-native-20261104.json"))
    parser.add_argument("--output", type=Path, default=Path("out/audit/systematic-rules/array-get-native-contract-20261104.json"))
    parser.add_argument("--sdk-header", type=Path, default=Path(r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump\Engine.hpp"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    resolve = lambda path: (repo / path).resolve() if not path.is_absolute() else path.resolve()
    capture_path, fixture_path, output = map(resolve, (args.capture, args.fixture, args.output))
    ir_path = repo / "klink bot/docs/card-ir.json"
    dispatch_path = repo / "src/KLink.Bot/Effects/CardApiDispatch.cs"
    test_path = repo / "tools/BotSim/SelfTest.KismetMath.cs"
    registry_path = repo / "tools/BotSim/SelfTest.cs"
    project_path = repo / "tools/BotSim/BotSim.csproj"
    capture = json.loads(capture_path.read_text(encoding="utf-8-sig"))
    fixture = json.loads(fixture_path.read_text(encoding="utf-8-sig"))
    ir = json.loads(ir_path.read_text(encoding="utf-8-sig"))
    header = args.sdk_header.read_text(encoding="utf-8", errors="replace")
    signature = re.search(r"\bvoid\s+Array_Get\(const\s+TArray<int32>&\s+TargetArray,\s*int32\s+Index,\s*int32&\s+Item\);", header)
    if signature is None:
        raise ValueError("SDK declaration UKismetArrayLibrary::Array_Get not found")
    native = capture.get("function", {})
    if capture.get("owner") != fixture.get("owner") or not native.get("ownership_verified"):
        raise ValueError("Current-client UFunction ownership differs from the fixture")
    if native.get("name") != fixture.get("function") or native.get("exec_rva") != fixture.get("exec_rva"):
        raise ValueError("Current-client UFunction identity differs from the fixture")
    if capture.get("exe_sha256") != fixture.get("exe_sha256"):
        raise ValueError("Capture executable differs from the fixture")
    if sha256(capture_path) != fixture.get("capture_sha256"):
        raise ValueError("Capture SHA-256 differs from the fixture")
    for rva, bytecode in fixture["native_instruction_checks"]:
        if not any(i.get("rva") == rva and i.get("bytes") == bytecode
                   for i in native.get("instructions_through_two_returns", [])):
            raise ValueError(f"Captured instruction differs at {rva}")
    inv = inventory(ir)
    dispatch_text = dispatch_path.read_text(encoding="utf-8")
    dispatch_match = re.search(r'\["Array_Get"\]\s*=\s*\(c,\s*r,\s*a\)\s*=>\s*\{(?P<body>[^}]+)\}', dispatch_text)
    if dispatch_match is None:
        raise ValueError("CardApiDispatch Array_Get handler was not found")

    result = {
        "schema_version": 1,
        "report_id": "KISMET-ARRAY-GET-NATIVE-CONTRACT-20261104",
        "status": "partial",
        "scope": "Current-client UKismetArrayLibrary Array_Get ownership, SDK declaration, local CardApiDispatch/output-slot regression and static IR input inventory; not a full generic property ABI or card certification.",
        "client": {"executable": capture["module"]["path"], "sha256": capture["exe_sha256"]},
        "native_capture": {
            "path": capture_path.relative_to(repo).as_posix(),
            "sha256": sha256(capture_path),
            "owner": capture["owner"],
            "function": {"name": native["name"], "exec_rva": native["exec_rva"], "ownership_verified": native["ownership_verified"]},
            "observed_instruction_checks": fixture["native_instruction_checks"],
            "method": "Read-only live UObject/UFunction identity and entry-code capture; no native invocation or process-memory write.",
        },
        "sdk": {"header": str(args.sdk_header.resolve()), "sha256": sha256(args.sdk_header), "declaration": signature.group(0)},
        "simulator": {
            "implementation": dispatch_match.group("body").strip(),
            "regression_cases": ["int array valid first/last index", "object array valid index", "string-valued element", "negative index", "past-end index", "Dictionary fallback", "string fallback", "null fallback"],
            "native_comparison": "The valid indexed read and output-slot route are represented by captured instructions and the SDK declaration. Invalid-index output values and malformed non-array fallbacks are simulator behavior, not native equivalence claims.",
        },
        "inventory": inv,
        "source_sha256": {
            "card_ir": sha256(ir_path), "dispatch": sha256(dispatch_path),
            "selftest": sha256(test_path), "selftest_registry": sha256(registry_path),
            "project": sha256(project_path), "fixture": sha256(fixture_path),
            "capture_script": sha256(repo / "out/audit/capture-kismet-array-get-current-20261104.py"),
            "audit_script": sha256(Path(__file__).resolve()),
        },
        "limitations": fixture["limitations"] + [
            "Static IR counts do not establish PvP eligibility, runtime reachability, or complete-card behavior.",
            "The capture does not independently reconstruct all generic property-copy paths or every element type's output handling.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "inventory": {k: inv[k] for k in ("callsite_count", "asset_count", "program_count", "arity_counts", "output_parameter_shapes")}, "exe_sha256": capture["exe_sha256"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
