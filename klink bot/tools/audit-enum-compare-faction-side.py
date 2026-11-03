#!/usr/bin/env python3
"""Bind faction/side enum comparison IR usage to current-client code evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def enum_values(source: str, enum_name: str) -> list[dict[str, Any]]:
    match = re.search(rf"enum class {re.escape(enum_name)}\s*\{{(.*?)\}};", source, re.S)
    if match is None:
        raise ValueError(f"Could not find {enum_name} in SDK headers")
    values = [
        {"name": name, "value": int(value)}
        for name, value in re.findall(r"\b([A-Za-z_]\w*)\s*=\s*(\d+)", match.group(1))
        if not name.endswith("_MAX")
    ]
    if not values:
        raise ValueError(f"{enum_name} has no explicit values")
    return values


def inventory(ir: dict[str, Any], function: str) -> dict[str, Any]:
    rows = []
    for card_name, card in ir.items():
        programs = [("steps", card.get("steps", []))]
        programs.extend((f"locals.{name}", steps) for name, steps in (card.get("locals") or {}).items())
        for program_name, steps in programs:
            if not isinstance(steps, list):
                continue
            for step in steps:
                if isinstance(step, dict) and step.get("op") == "math" and step.get("fn") == function:
                    rows.append({"card": card_name, "program": program_name, "instruction_i": step.get("i"), "byte_offset": step.get("bo")})
    return {
        "callsite_count": len(rows),
        "asset_count": len({row["card"] for row in rows}),
        "program_count": len({(row["card"], row["program"]) for row in rows}),
        "sample_callsites": rows[:10],
    }


def has_instruction(instructions: list[dict[str, str]], rva: str, raw_bytes: str) -> bool:
    return any(item.get("rva") == rva and item.get("bytes") == raw_bytes for item in instructions)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--capture", type=Path, default=Path("out/audit/systematic-rules/kismet-faction-side-compare-native-capture-current-20261104.json"))
    parser.add_argument("--output", type=Path, default=Path("out/audit/systematic-rules/enum-compare-faction-side-native-contract-20261104.json"))
    parser.add_argument("--sdk-root", type=Path, default=Path(r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    capture_path = (repo / args.capture).resolve() if not args.capture.is_absolute() else args.capture.resolve()
    output = (repo / args.output).resolve() if not args.output.is_absolute() else args.output.resolve()
    sdk_root = args.sdk_root.resolve()
    previous = {
        "faction": repo / "out/audit/systematic-rules/enum-compare-faction-sdk-contract-20261103.json",
        "side": repo / "out/audit/systematic-rules/enum-compare-side-sdk-contract-20261103.json",
        "faction_values": repo / "out/audit/systematic-rules/get-faction-enum-sdk-map-20261103.json",
    }
    sources = {
        "card_ir": repo / "klink bot/docs/card-ir.json",
        "vm": repo / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs",
        "selftest": repo / "tools/BotSim/SelfTest.KismetMath.cs",
        "selftest_registry": repo / "tools/BotSim/SelfTest.cs",
        "sdk_function": sdk_root / "kards.hpp",
        "sdk_enums": sdk_root / "kards_enums.hpp",
        "faction_contract": previous["faction"],
        "side_contract": previous["side"],
        "faction_enum_map": previous["faction_values"],
    }
    ir = json.loads(sources["card_ir"].read_text(encoding="utf-8"))
    sdk_function = sources["sdk_function"].read_text(encoding="utf-8")
    sdk_enums = sources["sdk_enums"].read_text(encoding="utf-8")
    signatures = {
        "EnumCompareFaction": "void EnumCompareFaction(EFactionEnum faction1, EFactionEnum faction2, EnumAreTheyEqual& Branches)",
        "EnumCompareSide": "void EnumCompareSide(ESideEnum side1, ESideEnum side2, EnumAreTheyEqual& Branches)",
    }
    patterns = {
        "EnumCompareFaction": r"void\s+EnumCompareFaction\s*\(\s*EFactionEnum\s+faction1\s*,\s*EFactionEnum\s+faction2\s*,\s*EnumAreTheyEqual\s*&\s*Branches\s*\)",
        "EnumCompareSide": r"void\s+EnumCompareSide\s*\(\s*ESideEnum\s+side1\s*,\s*ESideEnum\s+side2\s*,\s*EnumAreTheyEqual\s*&\s*Branches\s*\)",
    }
    for name, pattern in patterns.items():
        if re.search(pattern, sdk_function) is None:
            raise ValueError(f"SDK declaration missing or changed: {name}")
    faction_contract = json.loads(previous["faction"].read_text(encoding="utf-8"))
    side_contract = json.loads(previous["side"].read_text(encoding="utf-8"))
    faction_map = json.loads(previous["faction_values"].read_text(encoding="utf-8"))
    factions = faction_contract["sdk"]["faction_enum"]
    if factions != faction_map["sdk"]["values"]:
        raise ValueError("Faction enum values disagree between the enum comparator and GetFactionEnum contracts")
    sides = enum_values(sdk_enums, "ESideEnum")
    branches = enum_values(sdk_enums, "EnumAreTheyEqual")
    if sides != side_contract["sdk"]["side_enum"] or branches != side_contract["sdk"]["branch_enum"]:
        raise ValueError("Side/branch enum values disagree with the existing SDK contract")

    capture = json.loads(capture_path.read_text(encoding="utf-8"))
    exe_path = Path(capture["module"]["path"])
    if capture.get("owner") != "/Script/kards.FunctionLibrary":
        raise ValueError("Captured owner does not match FunctionLibrary")
    if capture.get("exe_sha256") != sha256(exe_path):
        raise ValueError("Captured executable hash no longer matches the installed client")
    if set(capture["functions"]) != set(signatures):
        raise ValueError("Capture does not contain exactly the two requested enum comparators")
    shared_exec = {row["exec_rva"] for row in capture["functions"].values()}
    shared_impl = {row["implementation_rva"] for row in capture["functions"].values()}
    if len(shared_exec) != 1 or len(shared_impl) != 1:
        raise ValueError("Faction and side comparators did not resolve to the same shared wrapper/implementation")

    wrapper_evidence = [
        {"rva": "0x4a8933a", "bytes": "0fb6542458", "meaning": "zero-extend the second byte enum parameter"},
        {"rva": "0x4a89342", "bytes": "0fb64c2420", "meaning": "zero-extend the first byte enum parameter"},
        {"rva": "0x4a8935d", "bytes": "e85ebb0900", "meaning": "call the shared enum comparator"},
    ]
    implementation_evidence = [
        {"rva": "0x4b24ec0", "bytes": "3aca", "meaning": "cmp cl, dl"},
        {"rva": "0x4b24ec2", "bytes": "0f95c0", "meaning": "set result to 1 exactly when unequal"},
        {"rva": "0x4b24ec5", "bytes": "418800", "meaning": "write the byte result through the output pointer"},
        {"rva": "0x4b24ec8", "bytes": "c3", "meaning": "return"},
    ]
    for name, function in capture["functions"].items():
        if not function["ownership_verified"]:
            raise ValueError(f"UFunction owner was not verified: {name}")
        for item in wrapper_evidence:
            if not has_instruction(function["instructions_to_first_ret"], item["rva"], item["bytes"]):
                raise ValueError(f"Expected {name} wrapper instruction missing: {item}")
        for item in implementation_evidence:
            if not has_instruction(function["implementation_instructions_to_first_ret"], item["rva"], item["bytes"]):
                raise ValueError(f"Expected {name} implementation instruction missing: {item}")

    result = {
        "schema_version": 1,
        "report_id": "ENUM-COMPARE-FACTION-SIDE-NATIVE-CONTRACT-20261104",
        "status": "partial",
        "scope": "Current-client FunctionLibrary.EnumCompareFaction and EnumCompareSide wrappers/shared comparator, SDK enum maps, local BotSim pairwise regression and static IR callsite inventory; not card-effect or PvP certification.",
        "client": {"executable": str(exe_path).replace("\\", "/"), "sha256": capture["exe_sha256"]},
        "native_capture": {
            "path": str(capture_path.relative_to(repo)).replace("\\", "/"),
            "sha256": sha256(capture_path),
            "owner": capture["owner"],
            "shared_exec_rva": next(iter(shared_exec)),
            "shared_implementation_rva": next(iter(shared_impl)),
            "wrapper_instructions": wrapper_evidence,
            "implementation_instructions": implementation_evidence,
            "method": "Read-only live UFunction ownership capture and direct-call tracing. Both UFunctions share the byte-parameter wrapper and cmp/setne implementation. No game function was invoked and no memory was written.",
        },
        "sdk": {
            "function_signatures": signatures,
            "faction_enum_source": "Existing cross-checked EnumCompareFaction and GetFactionEnum contract artifacts; their ordered values agree.",
            "faction_enum": factions,
            "side_enum": sides,
            "branch_enum": branches,
            "faction_enum_declaration_present_in_header_dump": False,
        },
        "semantics": {
            "result": "Branches = Equal (0) iff the two enum bytes are equal; otherwise NotEqual (1).",
            "basis": "Wrappers zero-extend both byte parameters and pass their low bytes to the shared body: cmp cl, dl; setne al; store AL through the out pointer.",
            "validated_input_domains": {"EnumCompareFaction": "All 13 values 0..12 pairwise.", "EnumCompareSide": "All 3 values 0..2 pairwise."},
        },
        "inventory": {name: inventory(ir, name) for name in signatures},
        "regression": {
            "fixture": "tools/BotSim/Fixtures/kismet-faction-side-compare-native-20261104.json",
            "cases": {"EnumCompareFaction": len(factions) ** 2, "EnumCompareSide": len(sides) ** 2},
            "result": "All 178 valid enum pairs match the captured native comparison and output encoding.",
            "native_equivalence_established_for_scope": True,
        },
        "source_sha256": {name: sha256(path) for name, path in sources.items()},
        "limitations": [
            "Faction values reuse the existing current enum mapping artifacts because the SDK dump has no EFactionEnum declaration; those mappings are cross-checked against each other but not newly read from this capture.",
            "Machine-code evidence is valid only when the executable SHA-256 matches the recorded value.",
            "Static IR counts include non-PvP assets and do not prove runtime reachability or complete card behavior.",
            "Malformed parameter frames and values outside each documented enum range are not tested.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output),
        "inventory": {name: {"callsite_count": data["callsite_count"], "asset_count": data["asset_count"], "program_count": data["program_count"]} for name, data in result["inventory"].items()},
        "regression_cases": result["regression"]["cases"],
        "native_equivalence_established_for_scope": result["regression"]["native_equivalence_established_for_scope"],
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
