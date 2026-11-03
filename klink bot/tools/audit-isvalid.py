#!/usr/bin/env python3
"""Bind IsValid IR inventory to current-client UObject wrapper evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inventory(ir: dict[str, Any]) -> dict[str, Any]:
    rows: list[dict[str, Any]] = []

    def visit(value: Any, card: str, program: str) -> None:
        if isinstance(value, dict):
            if value.get("op") == "math" and value.get("fn") == "IsValid":
                rows.append({"card": card, "program": program, "instruction_i": value.get("i"), "byte_offset": value.get("bo")})
            if value.get("math") == "IsValid":
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
    parser.add_argument("--capture", type=Path, default=Path("out/audit/systematic-rules/kismet-isvalid-native-capture-current-20261104.json"))
    parser.add_argument("--fixture", type=Path, default=Path("tools/BotSim/Fixtures/kismet-isvalid-native-20261104.json"))
    parser.add_argument("--output", type=Path, default=Path("out/audit/systematic-rules/isvalid-native-contract-20261104.json"))
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
    signature = re.search(r"\bvoid\s+IsValid\(const\s+class\s+UObject\*\s+Object\);", header)
    if signature is None:
        raise ValueError("SDK declaration UKismetSystemLibrary::IsValid not found")
    native = capture.get("function", {})
    if capture.get("owner") != fixture.get("owner") or not native.get("ownership_verified"):
        raise ValueError("Current-client function ownership differs from the fixture")
    if native.get("name") != fixture.get("function") or native.get("exec_rva") != fixture.get("exec_rva"):
        raise ValueError("Current-client function identity differs from the fixture")
    if capture.get("exe_sha256") != fixture.get("exe_sha256"):
        raise ValueError("Capture executable differs from the fixture")
    if sha256(capture_path) != fixture.get("capture_sha256"):
        raise ValueError("Capture SHA-256 differs from the fixture")
    for rva, bytecode in fixture["native_instruction_checks"]:
        if not any(i.get("rva") == rva and i.get("bytes") == bytecode
                   for i in native.get("instructions_through_two_returns", [])):
            raise ValueError(f"Captured instruction differs at {rva}")

    result = {
        "schema_version": 1,
        "report_id": "KISMET-ISVALID-NATIVE-CONTRACT-20261104",
        "status": "partial",
        "scope": "Current-client UKismetSystemLibrary IsValid wrapper structure, SDK declaration, managed CardInstance predicate regression and static IR callsite inventory; not complete UObject lifetime or card certification.",
        "client": {"executable": capture["module"]["path"], "sha256": capture["exe_sha256"]},
        "native_capture": {
            "path": capture_path.relative_to(repo).as_posix(),
            "sha256": sha256(capture_path),
            "owner": capture["owner"],
            "function": {"name": native["name"], "exec_rva": native["exec_rva"], "ownership_verified": native["ownership_verified"]},
            "observed_checks": fixture["native_instruction_checks"],
            "method": "Read-only live UObject class/UFunction ownership and entry-byte capture through both observed Boolean return paths; no native call or game-memory write.",
        },
        "sdk": {"header": str(args.sdk_header.resolve()), "sha256": sha256(args.sdk_header), "declaration": signature.group(0)},
        "simulator": {
            "implementation": "KismetVm.IsValid returns true for CardInstance objects and false for null/non-card values.",
            "regression_cases": ["null", "non-UObject string", "non-UObject integer", "board card", "hand card", "discard card", "NotAvailable card"],
            "interpretation": "The managed predicate deliberately does not use IsAlive/location; removed or out-of-play CardInstance references remain modeled as valid UObject references.",
        },
        "inventory": inventory(ir),
        "source_sha256": {
            "card_ir": sha256(ir_path), "vm": sha256(vm_path), "selftest": sha256(test_path),
            "selftest_registry": sha256(registry_path), "project": sha256(project_path),
            "fixture": sha256(fixture_path), "capture_script": sha256(repo / "out/audit/capture-kismet-isvalid-current-20261104.py"),
            "audit_script": sha256(Path(__file__).resolve()),
        },
        "limitations": fixture["limitations"] + [
            "IR callsite counts include non-PvP assets and do not establish runtime reachability.",
            "The capture shows an object-pointer and object-flag check in this wrapper; it does not map every tested bit to all UE GC/destruction states.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "inventory": result["inventory"], "exe_sha256": result["client"]["sha256"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
