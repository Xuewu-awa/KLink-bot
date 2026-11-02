#!/usr/bin/env python3
"""Inventory GetFactionEnum IR callsites and compare the simulator map with the current SDK enum."""

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


def walk(value: Any, path: str = "") -> Iterator[tuple[dict[str, Any], str]]:
    if isinstance(value, dict):
        yield value, path
        for key, child in value.items():
            yield from walk(child, f"{path}.{key}" if path else key)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from walk(child, f"{path}[{index}]")


def variables(value: Any, name: str, path: str = "") -> Iterator[str]:
    for node, node_path in walk(value, path):
        if node.get("var") == name:
            yield node_path


def read_factions(source: str) -> list[dict[str, Any]]:
    match = re.search(r"enum class EFactionEnum\s*\{(.*?)\};", source, re.S)
    if match is None:
        raise ValueError("Could not find EFactionEnum in the SDK header")
    values = [
        {"name": name, "value": int(value)}
        for name, value in re.findall(r"\b([A-Za-z_]\w*)\s*=\s*(\d+)", match.group(1))
        if not name.endswith("_MAX")
    ]
    if not values:
        raise ValueError("EFactionEnum has no explicit values")
    return values


def find_consumers(steps: list[Any], index: int, destination: str | None) -> list[dict[str, Any]]:
    if not destination:
        return []
    consumers: list[dict[str, Any]] = []
    for candidate in steps[index + 1 :]:
        if not isinstance(candidate, dict):
            continue
        if candidate.get("op") == "set" and candidate.get("dst") == destination:
            break
        if candidate.get("op") != "call":
            continue
        for arg_index, arg in enumerate(candidate.get("args", [])):
            paths = list(variables(arg, destination))
            if paths:
                consumers.append(
                    {
                        "function": candidate.get("fn"),
                        "instruction_i": candidate.get("i"),
                        "argument_index": arg_index,
                        "nested_variable_paths": paths,
                    }
                )
    return consumers


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()

    paths = {
        "card_ir": repo / "klink bot/docs/card-ir.json",
        "sdk_enum": Path(
            r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump\KardsCore_enums.hpp"
        ),
        "sdk_function": Path(
            r"D:\Program Files\KARDS - The WWII Card Game\KARDS\default\game\kards\Binaries\Win64\ue4ss\CXXHeaderDump\kards.hpp"
        ),
        "vm": repo / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs",
        "selftest": repo / "tools/BotSim/SelfTest.KismetMath.cs",
    }
    ir = json.loads(paths["card_ir"].read_text(encoding="utf-8"))
    sdk_enum_source = paths["sdk_enum"].read_text(encoding="utf-8")
    sdk_function_source = paths["sdk_function"].read_text(encoding="utf-8")
    vm_source = paths["vm"].read_text(encoding="utf-8")
    factions = read_factions(sdk_enum_source)
    signature_present = re.search(
        r"void\s+GetFactionEnum\s*\(\s*const\s+FName\s*&\s*faction\s*\)",
        sdk_function_source,
    ) is not None
    if not signature_present:
        raise ValueError("The SDK GetFactionEnum declaration did not match the expected FName signature")

    callsites: list[dict[str, Any]] = []
    for card_name, card in ir.items():
        programs = [("steps", card.get("steps", []))]
        programs.extend((f"locals.{name}", steps) for name, steps in (card.get("locals") or {}).items())
        for program_name, steps in programs:
            if not isinstance(steps, list):
                continue
            for index, step in enumerate(steps):
                if not isinstance(step, dict):
                    continue
                for node, expression_path in walk(step):
                    if node.get("math") != "GetFactionEnum":
                        continue
                    callsites.append(
                        {
                            "card": card_name,
                            "program": program_name,
                            "instruction_i": step.get("i"),
                            "byte_offset": step.get("bo"),
                            "expression_path": expression_path,
                            "destination": step.get("dst"),
                            "direct_consumers_before_next_write": find_consumers(
                                steps, index, step.get("dst")
                            ),
                        }
                    )

    output = args.output or repo / "out/audit/systematic-rules/get-faction-enum-sdk-map-20261103.json"
    output = output.resolve()
    result = {
        "schema_version": 1,
        "report_id": "GET-FACTION-ENUM-SDK-MAP-20261103",
        "scope": "static current card IR callsites and local canonical-name mapping comparison; no native execution claim",
        "sdk": {
            "function_signature": "void GetFactionEnum(const FName& faction)",
            "function_signature_present": signature_present,
            "enum": "EFactionEnum",
            "values": factions,
        },
        "inventory": {
            "callsite_count": len(callsites),
            "asset_count": len({item["card"] for item in callsites}),
            "direct_consumer_counts": {
                name: sum(
                    consumer.get("function") == name
                    for item in callsites
                    for consumer in item["direct_consumers_before_next_write"]
                )
                for name in sorted(
                    {
                        consumer.get("function")
                        for item in callsites
                        for consumer in item["direct_consumers_before_next_write"]
                    }
                )
            },
            "callsites": callsites,
        },
        "vm_source_observation": {
            "summary": "Searches IDs 0..12 using FactionName, compares the suffix after `::` with OrdinalIgnoreCase, and returns FirstOrDefault (0 when unmatched).",
            "native_equivalence_established": False,
        },
        "limitations": [
            "The SDK supplies a declaration and enum values but no native function body.",
            "The self-test validates the simulator's 13 canonical name-to-ordinal projections against the SDK table; it does not validate the client's native output.",
            "Case handling, qualified or numbered FName forms, invalid names, and real runtime input values remain unverified.",
            "Static card IR occurrence and consumer counts do not establish current PvP eligibility or runtime reachability.",
        ],
        "source_sha256": {name: sha256(path) for name, path in paths.items()},
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(
        json.dumps(
            {
                "output": str(output),
                "callsite_count": len(callsites),
                "asset_count": result["inventory"]["asset_count"],
                "direct_consumer_counts": result["inventory"]["direct_consumer_counts"],
                "sdk_enum_value_count": len(factions),
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
