#!/usr/bin/env python3
"""Inventory EnumCompareSide IR callsites and bind the SDK enum contract."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path
from typing import Any, Iterator


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def walk(value: Any, path: str = "") -> Iterator[tuple[dict[str, Any], str]]:
    if isinstance(value, dict):
        yield value, path
        for key, child in value.items():
            yield from walk(child, f"{path}.{key}" if path else key)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from walk(child, f"{path}[{index}]")


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


def consumers_after(steps: list[Any], start: int, slot: str | None) -> list[dict[str, Any]]:
    if not slot:
        return []
    for candidate in steps[start + 1 :]:
        if not isinstance(candidate, dict):
            continue
        if candidate.get("op") == "math" and candidate.get("fn") == "EnumCompareSide":
            continue
        referenced = any(node.get("var") == slot for node, _ in walk(candidate))
        if not referenced:
            if candidate.get("op") == "set" and candidate.get("dst") == slot:
                break
            continue
        math_names = sorted(
            {
                str(node.get("math") or node.get("fn"))
                for node, _ in walk(candidate)
                if (node.get("math") or node.get("fn"))
                and any(child.get("var") == slot for child, _ in walk(node))
            }
        )
        return [
            {
                "instruction_i": candidate.get("i"),
                "operation": candidate.get("op"),
                "math_names": math_names,
            }
        ]
    return []


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    sdk_root = Path(
        r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump"
    )
    sources = {
        "card_ir": repo / "klink bot/docs/card-ir.json",
        "vm": repo / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs",
        "selftest": repo / "tools/BotSim/SelfTest.KismetMath.cs",
        "selftest_registry": repo / "tools/BotSim/SelfTest.cs",
        "sdk_function": sdk_root / "kards.hpp",
        "sdk_side_enum": sdk_root / "kards_enums.hpp",
        "sdk_compare_enum": sdk_root / "kards_enums.hpp",
    }
    ir = json.loads(sources["card_ir"].read_text(encoding="utf-8"))
    sdk_function = sources["sdk_function"].read_text(encoding="utf-8")
    enum_header = sources["sdk_side_enum"].read_text(encoding="utf-8")
    signature_present = re.search(
        r"void\s+EnumCompareSide\s*\(\s*ESideEnum\s+side1\s*,\s*ESideEnum\s+side2\s*,\s*EnumAreTheyEqual\s*&\s*Branches\s*\)",
        sdk_function,
    ) is not None
    if not signature_present:
        raise ValueError("The SDK EnumCompareSide declaration did not match the expected signature")

    sides = enum_values(enum_header, "ESideEnum")
    branch_values = enum_values(enum_header, "EnumAreTheyEqual")
    callsites: list[dict[str, Any]] = []
    consumer_counts: Counter[str] = Counter()
    for card_name, card in ir.items():
        programs = [("steps", card.get("steps", []))]
        programs.extend((f"locals.{name}", steps) for name, steps in (card.get("locals") or {}).items())
        for program_name, steps in programs:
            if not isinstance(steps, list):
                continue
            for index, step in enumerate(steps):
                if not isinstance(step, dict) or step.get("op") != "math" or step.get("fn") != "EnumCompareSide":
                    continue
                outs = step.get("outs", [])
                branch_slot = next(
                    (item.get("slot") for item in outs if isinstance(item, dict) and item.get("param") == 2),
                    None,
                )
                consumers = consumers_after(steps, index, branch_slot)
                for consumer in consumers:
                    for name in consumer["math_names"]:
                        consumer_counts[name] += 1
                callsites.append(
                    {
                        "card": card_name,
                        "program": program_name,
                        "instruction_i": step.get("i"),
                        "byte_offset": step.get("bo"),
                        "args": step.get("args", []),
                        "outs": outs,
                        "branch_output_slot": branch_slot,
                        "first_local_consumer": consumers,
                    }
                )

    output = args.output or repo / "out/audit/systematic-rules/enum-compare-side-sdk-contract-20261103.json"
    output = output.resolve()
    result = {
        "schema_version": 1,
        "report_id": "ENUM-COMPARE-SIDE-SDK-CONTRACT-20261103",
        "scope": "current card IR callsites, SDK declarations/enumerators, and local VM regression only; no native execution or PvP reachability claim",
        "sdk": {
            "function_signature": "void EnumCompareSide(ESideEnum side1, ESideEnum side2, EnumAreTheyEqual& Branches)",
            "function_signature_present": signature_present,
            "side_enum": sides,
            "branch_enum": branch_values,
            "native_body_present_in_sdk_dump": False,
        },
        "inventory": {
            "callsite_count": len(callsites),
            "asset_count": len({item["card"] for item in callsites}),
            "program_count": len({(item["card"], item["program"]) for item in callsites}),
            "first_local_consumer_math_counts": dict(sorted(consumer_counts.items())),
            "callsites": callsites,
        },
        "simulator_observation": {
            "summary": "EvalMath returns 0 when its first two inputs convert to the same integer and 1 otherwise; the self-test exercises all 9 pairs from SDK ESideEnum ordinals 0..2.",
            "native_equivalence_established": False,
        },
        "limitations": [
            "SDK declarations provide parameter types and output enum values but no EnumCompareSide implementation body.",
            "The VM regression validates the local mapping against enum labels only; it is not a client runtime oracle.",
            "Current IR occurrence and first-consumer counts do not establish PvP eligibility, execution, or full call reachability.",
            "Sides outside the SDK ESideEnum ordinal range and malformed values are not modeled by the 3x3 regression.",
        ],
        "source_sha256": {name: sha256(path) for name, path in sources.items()},
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(output),
        "callsite_count": len(callsites),
        "asset_count": result["inventory"]["asset_count"],
        "program_count": result["inventory"]["program_count"],
        "first_local_consumer_math_counts": result["inventory"]["first_local_consumer_math_counts"],
        "sdk_side_enum_count": len(sides),
        "sdk_branch_enum_count": len(branch_values),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
