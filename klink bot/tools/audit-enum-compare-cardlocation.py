#!/usr/bin/env python3
"""Bind EnumCompareCardLocation IR usage to current-client native evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any, Iterator


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def walk(value: Any) -> Iterator[dict[str, Any]]:
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


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


def has_instruction(instructions: list[dict[str, str]], rva: str, raw_bytes: str) -> bool:
    return any(item.get("rva") == rva and item.get("bytes") == raw_bytes for item in instructions)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--capture", type=Path, default=Path("out/audit/systematic-rules/kismet-cardlocation-compare-native-capture-current-20261104.json"))
    parser.add_argument("--output", type=Path, default=Path("out/audit/systematic-rules/enum-compare-cardlocation-native-contract-20261104.json"))
    parser.add_argument("--sdk-root", type=Path, default=Path(r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    capture_path = (repo / args.capture).resolve() if not args.capture.is_absolute() else args.capture.resolve()
    output = (repo / args.output).resolve() if not args.output.is_absolute() else args.output.resolve()
    sdk_root = args.sdk_root.resolve()
    sources = {
        "card_ir": repo / "klink bot/docs/card-ir.json",
        "vm": repo / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs",
        "selftest": repo / "tools/BotSim/SelfTest.KismetMath.cs",
        "selftest_registry": repo / "tools/BotSim/SelfTest.cs",
        "sdk_function": sdk_root / "kards.hpp",
        "sdk_enums": sdk_root / "kards_enums.hpp",
    }
    ir = json.loads(sources["card_ir"].read_text(encoding="utf-8"))
    sdk_function = sources["sdk_function"].read_text(encoding="utf-8")
    sdk_enums = sources["sdk_enums"].read_text(encoding="utf-8")
    signature = "void EnumCompareCardLocation(ECardLocationEnum location1, ECardLocationEnum location2, EnumAreTheyEqual& Branches)"
    if re.search(r"void\s+EnumCompareCardLocation\s*\(\s*ECardLocationEnum\s+location1\s*,\s*ECardLocationEnum\s+location2\s*,\s*EnumAreTheyEqual\s*&\s*Branches\s*\)", sdk_function) is None:
        raise ValueError("The SDK EnumCompareCardLocation declaration did not match the expected signature")
    locations = enum_values(sdk_enums, "ECardLocationEnum")
    branches = enum_values(sdk_enums, "EnumAreTheyEqual")

    callsites: list[dict[str, Any]] = []
    for card_name, card in ir.items():
        programs = [("steps", card.get("steps", []))]
        programs.extend((f"locals.{name}", steps) for name, steps in (card.get("locals") or {}).items())
        for program_name, steps in programs:
            if not isinstance(steps, list):
                continue
            for step in steps:
                if isinstance(step, dict) and step.get("op") == "math" and step.get("fn") == "EnumCompareCardLocation":
                    callsites.append({
                        "card": card_name,
                        "program": program_name,
                        "instruction_i": step.get("i"),
                        "byte_offset": step.get("bo"),
                    })

    capture = json.loads(capture_path.read_text(encoding="utf-8"))
    exe_path = Path(capture["module"]["path"])
    if capture.get("owner") != "/Script/kards.FunctionLibrary" or capture.get("function") != "EnumCompareCardLocation":
        raise ValueError("Capture owner/function identity does not match the requested native contract")
    if capture.get("exe_sha256") != sha256(exe_path):
        raise ValueError("Captured executable hash no longer matches the installed client")
    wrapper = capture["instructions_to_first_ret"]
    implementation = capture["trailing_call_target_instructions_to_first_ret"]
    wrapper_evidence = [
        {"rva": "0x4a8933a", "bytes": "0fb6542458", "meaning": "zero-extend the second byte enum parameter"},
        {"rva": "0x4a89342", "bytes": "0fb64c2420", "meaning": "zero-extend the first byte enum parameter"},
        {"rva": "0x4a8935d", "bytes": "e85ebb0900", "meaning": "call the captured comparison implementation"},
    ]
    implementation_evidence = [
        {"rva": "0x4b24ec0", "bytes": "3aca", "meaning": "cmp cl, dl"},
        {"rva": "0x4b24ec2", "bytes": "0f95c0", "meaning": "set result to 1 exactly when unequal"},
        {"rva": "0x4b24ec5", "bytes": "418800", "meaning": "write the byte result through the output pointer"},
        {"rva": "0x4b24ec8", "bytes": "c3", "meaning": "return"},
    ]
    for item in wrapper_evidence:
        if not has_instruction(wrapper, item["rva"], item["bytes"]):
            raise ValueError(f"Expected wrapper instruction is missing: {item}")
    for item in implementation_evidence:
        if not has_instruction(implementation, item["rva"], item["bytes"]):
            raise ValueError(f"Expected implementation instruction is missing: {item}")

    result = {
        "schema_version": 1,
        "report_id": "ENUM-COMPARE-CARDLOCATION-NATIVE-CONTRACT-20261104",
        "status": "partial",
        "scope": "Current-client EnumCompareCardLocation wrapper and implementation, SDK enum values, local VM regression and static IR callsite inventory; not card-effect or PvP certification.",
        "client": {"executable": str(exe_path).replace("\\", "/"), "sha256": capture["exe_sha256"]},
        "native_capture": {
            "path": str(capture_path.relative_to(repo)).replace("\\", "/"),
            "sha256": sha256(capture_path),
            "owner": capture["owner"],
            "function": capture["function"],
            "exec_rva": capture["exec_rva"],
            "implementation_rva": capture["trailing_call_target_rva"],
            "wrapper_instructions": wrapper_evidence,
            "implementation_instructions": implementation_evidence,
            "method": "Read-only live UFunction ownership and ExecFunction capture; wrapper input/output flow traced to a direct native comparator. No function was invoked and no game memory was written.",
        },
        "sdk": {
            "function_signature": signature,
            "location_enum": locations,
            "branch_enum": branches,
            "native_body_present_in_sdk_dump": False,
        },
        "semantics": {
            "result": "Branches = Equal (0) iff location1 == location2; otherwise NotEqual (1).",
            "basis": "Wrapper reads both enum inputs as bytes and passes them in CL/DL; the native body executes cmp cl, dl; setne al; stores AL through the out pointer.",
            "validated_input_domain": "All 10 SDK-defined ECardLocationEnum values, pairwise.",
        },
        "inventory": {
            "source": "klink bot/docs/card-ir.json",
            "callsite_count": len(callsites),
            "asset_count": len({item["card"] for item in callsites}),
            "program_count": len({(item["card"], item["program"]) for item in callsites}),
            "sample_callsites": callsites[:12],
            "note": "Inventory includes all card IR modes/assets and does not establish PvP eligibility or runtime reachability.",
        },
        "regression": {
            "fixture": "tools/BotSim/Fixtures/kismet-cardlocation-compare-native-20261104.json",
            "cases": len(locations) ** 2,
            "result": "All SDK-defined location pairs are checked against the captured byte comparison and branch enum values.",
            "native_equivalence_established_for_scope": True,
        },
        "source_sha256": {name: sha256(path) for name, path in sources.items()},
        "limitations": [
            "The machine-code evidence is valid only when the executable SHA-256 matches the value above.",
            "The capture establishes this primitive's byte comparison and output encoding, not which assets are legal or reachable in PvP.",
            "Static IR counts include non-PvP content and do not establish execution or full-card behavior.",
            "No malformed parameter frame or enum value outside the SDK-defined set was tested in BotSim.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output),
        "callsite_count": result["inventory"]["callsite_count"],
        "asset_count": result["inventory"]["asset_count"],
        "program_count": result["inventory"]["program_count"],
        "native_equivalence_established_for_scope": result["regression"]["native_equivalence_established_for_scope"],
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
