#!/usr/bin/env python3
"""Bind Array_Add IR inventory and simulator checks to current-client evidence."""

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


def extract_handler(source: str) -> str:
    marker = '["Array_Add"] = (c, r, a) =>'
    start = source.index(marker)
    opening = source.index("{", start)
    depth = 0
    for end in range(opening, len(source)):
        if source[end] == "{":
            depth += 1
        elif source[end] == "}":
            depth -= 1
            if depth == 0:
                return source[opening + 1:end].strip()
    raise ValueError("Unterminated CardApiDispatch Array_Add handler")


def inventory(ir: dict[str, Any]) -> dict[str, Any]:
    rows: list[dict[str, Any]] = []

    def visit(value: Any, card: str, program: str) -> None:
        if isinstance(value, dict):
            if value.get("fn") == "Array_Add" or value.get("call") == "Array_Add":
                args = value.get("args") or []
                rows.append({
                    "card": card,
                    "program": program,
                    "instruction_i": value.get("i"),
                    "byte_offset": value.get("bo"),
                    "argument_shapes": [sorted(arg) if isinstance(arg, dict) else None for arg in args],
                    "element_type": args[0].get("element_type") if args and isinstance(args[0], dict) else None,
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
        "element_type_counts": dict(Counter(str(row["element_type"]) for row in rows)),
        "sample_callsites": rows[:12],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--capture", type=Path, default=Path("out/audit/systematic-rules/kismet-array-add-native-capture-current-20261104.json"))
    parser.add_argument("--fixture", type=Path, default=Path("tools/BotSim/Fixtures/kismet-array-add-native-20261104.json"))
    parser.add_argument("--output", type=Path, default=Path("out/audit/systematic-rules/array-add-native-contract-20261104.json"))
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
    signature = re.search(r"\bvoid\s+Array_Add\(const\s+TArray<int32>&\s+TargetArray,\s*const\s+int32&\s+NewItem\);", header)
    if signature is None:
        raise ValueError("SDK declaration UKismetArrayLibrary::Array_Add not found")
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
    if inv["callsite_count"] != 306 or inv["asset_count"] != 229:
        raise ValueError(f"Unexpected IR inventory: {inv['callsite_count']} sites / {inv['asset_count']} assets")
    dispatch_text = dispatch_path.read_text(encoding="utf-8")
    handler = extract_handler(dispatch_text)

    result = {
        "schema_version": 1,
        "report_id": "KISMET-ARRAY-ADD-NATIVE-CONTRACT-20261104",
        "status": "partial",
        "scope": "Current-client UKismetArrayLibrary Array_Add ownership, SDK declaration, observed wrapper result-frame store, local CardApiDispatch mutation/result regression and static IR inventory; not full helper execution or card certification.",
        "client": {"executable": capture["module"]["path"], "sha256": capture["exe_sha256"]},
        "native_capture": {
            "path": capture_path.relative_to(repo).as_posix(),
            "sha256": sha256(capture_path),
            "owner": capture["owner"],
            "function": {"name": native["name"], "exec_rva": native["exec_rva"], "ownership_verified": native["ownership_verified"]},
            "observed_instruction_checks": fixture["native_instruction_checks"],
            "method": "Read-only live UObject/UFunction identity and entry-code capture; no native invocation or process-memory write.",
            "interpretation": "The wrapper calls an append helper and stores EAX to a 32-bit result-frame location. The called helper was not independently executed, so the stored value's precise semantic meaning remains unverified.",
        },
        "sdk": {"header": str(args.sdk_header.resolve()), "sha256": sha256(args.sdk_header), "declaration": signature.group(0)},
        "simulator": {
            "implementation": handler,
            "regression_cases": ["integer append index and list mutation", "duplicate integer append", "string element preservation", "object element identity and list mutation"],
            "native_comparison": "BotSim verifies its own append and index behavior. The captured result-frame store is consistent with a result path but does not prove the helper returns the newly appended index.",
        },
        "inventory": inv,
        "source_sha256": {
            "card_ir": sha256(ir_path), "dispatch": sha256(dispatch_path),
            "selftest": sha256(test_path), "selftest_registry": sha256(registry_path),
            "project": sha256(project_path), "fixture": sha256(fixture_path),
            "capture_script": sha256(repo / "out/audit/capture-kismet-array-add-current-20261104.py"),
            "audit_script": sha256(Path(__file__).resolve()),
        },
        "limitations": fixture["limitations"] + [
            "Static IR counts do not establish PvP eligibility, runtime reachability, or complete-card behavior.",
            "The SDK header does not expose a named output parameter although captured native code stores through a result-frame pointer; the reflected result property and helper semantics need further verification.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "inventory": {k: inv[k] for k in ("callsite_count", "asset_count", "program_count", "arity_counts", "element_type_counts")}, "exe_sha256": capture["exe_sha256"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
