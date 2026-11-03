#!/usr/bin/env python3
"""Bind Array_Length IR inventory to current-client generic array wrapper evidence."""

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
            if value.get("fn") == "Array_Length" or value.get("call") == "Array_Length":
                args = value.get("args") or []
                first = args[0] if args else None
                rows.append({
                    "card": card,
                    "program": program,
                    "instruction_i": value.get("i"),
                    "byte_offset": value.get("bo"),
                    "input_expression_keys": sorted(first) if isinstance(first, dict) else None,
                    "input_element_type": first.get("element_type") if isinstance(first, dict) else None,
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
        "input_expression_shapes": dict(Counter(
            ",".join(row["input_expression_keys"] or []) for row in rows
        )),
        "input_element_types": dict(Counter(str(row["input_element_type"]) for row in rows)),
        "sample_callsites": rows[:12],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--capture", type=Path, default=Path("out/audit/systematic-rules/kismet-array-length-native-capture-current-20261104.json"))
    parser.add_argument("--fixture", type=Path, default=Path("tools/BotSim/Fixtures/kismet-array-length-native-20261104.json"))
    parser.add_argument("--output", type=Path, default=Path("out/audit/systematic-rules/array-length-native-contract-20261104.json"))
    parser.add_argument("--sdk-header", type=Path, default=Path(r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump\Engine.hpp"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    resolve = lambda path: (repo / path).resolve() if not path.is_absolute() else path.resolve()
    capture_path, fixture_path, output = map(resolve, (args.capture, args.fixture, args.output))
    ir_path = repo / "klink bot/docs/card-ir.json"
    vm_path = repo / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs"
    test_path = repo / "tools/BotSim/SelfTest.KismetMath.cs"
    registry_path = repo / "tools/BotSim/SelfTest.cs"
    project_path = repo / "tools/BotSim/BotSim.csproj"
    capture = json.loads(capture_path.read_text(encoding="utf-8-sig"))
    fixture = json.loads(fixture_path.read_text(encoding="utf-8-sig"))
    ir = json.loads(ir_path.read_text(encoding="utf-8-sig"))
    header = args.sdk_header.read_text(encoding="utf-8", errors="replace")
    signature = re.search(r"\bvoid\s+Array_Length\(const\s+TArray<int32>&\s+TargetArray\);", header)
    if signature is None:
        raise ValueError("SDK declaration UKismetArrayLibrary::Array_Length not found")
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
                   for i in native.get("instructions_to_first_ret", [])):
            raise ValueError(f"Captured instruction differs at {rva}")
    inv = inventory(ir)
    if inv["callsite_count"] != 1144 or inv["asset_count"] != 762:
        raise ValueError(f"Unexpected IR inventory: {inv['callsite_count']} sites / {inv['asset_count']} assets")

    result = {
        "schema_version": 1,
        "report_id": "KISMET-ARRAY-LENGTH-NATIVE-CONTRACT-20261104",
        "status": "partial",
        "scope": "Current-client UKismetArrayLibrary Array_Length ownership, wrapper output field, SDK declaration, managed IList regression and static IR input-shape inventory; not complete generic property ABI or card certification.",
        "client": {"executable": capture["module"]["path"], "sha256": capture["exe_sha256"]},
        "native_capture": {
            "path": capture_path.relative_to(repo).as_posix(),
            "sha256": sha256(capture_path),
            "owner": capture["owner"],
            "function": {"name": native["name"], "exec_rva": native["exec_rva"], "ownership_verified": native["ownership_verified"]},
            "observed_output_instructions": fixture["native_instruction_checks"],
            "method": "Read-only live UFunction ownership and entry-byte capture. The code reads a 32-bit value at array-data offset +8 and stores it to the integer output; no native call or process-memory write.",
        },
        "sdk": {"header": str(args.sdk_header.resolve()), "sha256": sha256(args.sdk_header), "declaration": signature.group(0)},
        "simulator": {
            "implementation": "KismetVm.Array_Length returns IList.Count; null and non-IList values use the existing zero fallback.",
            "regression_cases": ["empty int[]", "nonempty int[]", "heterogeneous List<object?>", "Dictionary fallback", "string fallback", "null fallback"],
            "native_comparison": "Valid array count is consistent with the captured read/store and SDK function declaration. Malformed non-array behavior is not compared to the native error path.",
        },
        "inventory": inv,
        "source_sha256": {
            "card_ir": sha256(ir_path), "vm": sha256(vm_path), "selftest": sha256(test_path),
            "selftest_registry": sha256(registry_path), "project": sha256(project_path),
            "fixture": sha256(fixture_path), "capture_script": sha256(repo / "out/audit/capture-kismet-array-length-current-20261104.py"),
            "audit_script": sha256(Path(__file__).resolve()),
        },
        "limitations": fixture["limitations"] + [
            "Static IR use counts and input expression shapes do not establish current PvP eligibility or runtime reachability.",
            "The machine-code capture does not independently execute or trace the virtual property helper used to validate the generic array parameter.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "inventory": {k: inv[k] for k in ("callsite_count", "asset_count", "program_count", "input_expression_shapes", "input_element_types")}, "exe_sha256": capture["exe_sha256"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
