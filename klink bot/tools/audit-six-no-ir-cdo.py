#!/usr/bin/env python3
"""Audit the six card assets that have no card-ir entry.

This is an evidence inventory only.  It checks the authoritative parsed CDO
metadata, the local static card table, the IR index, and the native keyword
table.  An empty Blueprint bytecode export is not treated as proof that all
inherited native behaviour has been certified.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any


CARD_IDS = [
    "card_unit_panzer_ii_a",
    "card_unit_15th_cavalry_regiment",
    "card_unit_ki_61_hien",
    "card_unit_shiden",
    "card_unit_554th_rifles",
    "card_unit_red_devils",
]

COMPARE_FIELDS = (
    "name",
    "title",
    "attack",
    "defense",
    "range",
    "rarity",
    "cardSet",
    "faction",
    "type",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def enum_value(value: Any) -> Any:
    if isinstance(value, str) and "::" in value:
        return value.rsplit("::", 1)[1]
    return value


def locate(root: Path, card_id: str) -> Path:
    matches = sorted(root.rglob(f"{card_id}.metadata.json"))
    if len(matches) != 1:
        raise RuntimeError(f"expected one metadata file for {card_id}, found {len(matches)}")
    return matches[0]


def cdo_data(export: dict[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for item in export.get("data", []):
        if not isinstance(item, dict) or not isinstance(item.get("Name"), str):
            continue
        name = item["Name"]
        value = item.get("Value")
        if isinstance(value, dict) and "AssetPath" in value:
            value = value["AssetPath"]
        result[name] = value
    return result


def cdo_compare_value(export: dict[str, Any], field: str) -> Any:
    """Return a human-readable CDO value for the small static cross-check."""
    target = {"name": "Name", "title": "title", "attack": "attack", "defense": "defense",
              "range": "range", "rarity": "rarity", "cardSet": "cardSet", "faction": "faction",
              "type": "Type"}[field]
    for item in export.get("data", []):
        if not isinstance(item, dict) or item.get("Name") != target:
            continue
        # FText properties carry the localized display text alongside the key.
        if field == "title" and item.get("CultureInvariantString"):
            return item["CultureInvariantString"]
        return enum_value(item.get("Value"))
    return None


def innate_entry(source: str, card_id: str) -> str | None:
    pattern = re.compile(rf'^\s*\["{re.escape(card_id)}"\]\s*=.*$', re.MULTILINE)
    match = pattern.search(source)
    return match.group(0).strip() if match else None


def build_report(args: argparse.Namespace) -> dict[str, Any]:
    cards_path = args.repo / "klink bot/docs/cards.json"
    ir_path = args.repo / "klink bot/docs/card-ir.json"
    innate_path = args.repo / "src/KLink.Bot/Cards/CardInnateTable.cs"
    cards = json.loads(cards_path.read_text(encoding="utf-8"))
    ir = json.loads(ir_path.read_text(encoding="utf-8"))
    innate_source = innate_path.read_text(encoding="utf-8")
    rows: list[dict[str, Any]] = []
    for card_id in CARD_IDS:
        metadata_path = locate(args.parsed_root, card_id)
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        exports = metadata.get("exports", [])
        class_exports = [item for item in exports if item.get("type") == "ClassExport"]
        function_exports = [
            item
            for item in exports
            if any(token in str(item.get("type", "")).lower() for token in ("function", "event"))
            or any(token in str(item.get("name", "")).lower() for token in ("function", "event"))
            or (item.get("bytecode_raw_size") or 0) != 0
        ]
        cdo_exports = [item for item in exports if item.get("type") == "NormalExport"]
        cdo = cdo_data(cdo_exports[0]) if len(cdo_exports) == 1 else {}
        static = cards.get(card_id)
        if not isinstance(static, dict):
            raise RuntimeError(f"missing static card table entry: {card_id}")
        mismatches: dict[str, dict[str, Any]] = {}
        cdo_field_names = {
            "name": "Name",
            "title": "title",
            "attack": "attack",
            "defense": "defense",
            "range": "range",
            "rarity": "rarity",
            "cardSet": "cardSet",
            "faction": "faction",
            "type": "Type",
        }
        for field in COMPARE_FIELDS:
            cdo_value = cdo_compare_value(cdo_exports[0], field) if len(cdo_exports) == 1 else None
            static_value = static.get(field)
            if cdo_value != static_value:
                mismatches[field] = {"cdo": cdo_value, "cards_json": static_value}
        rows.append(
            {
                "name": card_id,
                "metadata_path": str(metadata_path),
                "metadata_sha256": sha256(metadata_path),
                "export_count": len(exports),
                "class_export": {
                    "count": len(class_exports),
                    "names": [item.get("name") for item in class_exports],
                    "super": [item.get("super") for item in class_exports],
                    "super_struct": [item.get("super_struct") for item in class_exports],
                    "bytecode_raw_size": [item.get("bytecode_raw_size", 0) for item in class_exports],
                    "data_lengths": [len(item.get("data", [])) for item in class_exports],
                    "property_lengths": [len(item.get("properties", [])) for item in class_exports],
                },
                "cdo_property_names": list(cdo),
                "cdo_rule_fields": {
                    key: cdo[key]
                    for key in cdo
                    if key in {
                        "hasBlitz",
                        "hasSmokescreen",
                        "hasAmbush",
                        "hasFury",
                        "isReserved",
                        "isHQDamage",
                        "EffectType",
                        "KreditsTax_AsEnemyTarget",
                    }
                },
                "function_or_event_exports": [
                    {
                        "name": item.get("name"),
                        "type": item.get("type"),
                        "bytecode_raw_size": item.get("bytecode_raw_size", 0),
                    }
                    for item in function_exports
                ],
                "card_ir_present": card_id in ir,
                "cards_json_fields": {field: static.get(field) for field in COMPARE_FIELDS},
                "cdo_vs_cards_json_mismatches": mismatches,
                "innate_table_entry": innate_entry(innate_source, card_id),
                "observed_conclusion": (
                    "data-only-or-native-keyword"
                    if len(exports) == 2 and len(class_exports) == 1 and not function_exports and not mismatches
                    else "unclear"
                ),
            }
        )
    return {
        "schema_version": 1,
        "generated_by": "klink bot/tools/audit-six-no-ir-cdo.py",
        "authority": {
            "parsed_metadata_root": str(args.parsed_root),
            "repository": str(args.repo),
            "note": "The parsed CDO metadata and static table are authoritative inputs for this inventory; no replay data is used.",
        },
        "inputs": {
            "cards_json": {"path": str(cards_path), "sha256": sha256(cards_path)},
            "card_ir": {"path": str(ir_path), "sha256": sha256(ir_path)},
            "card_innate_table": {"path": str(innate_path), "sha256": sha256(innate_path)},
        },
        "summary": {
            "card_count": len(rows),
            "missing_ir_count": sum(not row["card_ir_present"] for row in rows),
            "function_or_event_export_count": sum(len(row["function_or_event_exports"]) for row in rows),
            "cdo_static_mismatch_count": sum(bool(row["cdo_vs_cards_json_mismatches"]) for row in rows),
            "candidate_data_only_or_native_keyword_count": sum(
                row["observed_conclusion"] == "data-only-or-native-keyword" for row in rows
            ),
        },
        "cards": rows,
        "limitations": [
            "No function/event export and zero Blueprint bytecode establish only that these packets expose no custom Blueprint body in the parsed metadata.",
            "Inherited BaseCardObject native behaviour, runtime ProcessEvent routing, and cross-card interactions are not certified by this inventory.",
            "The six cards remain open for full per-card and integration certification even when their visible behaviour is represented by CDO fields and native keyword registrations.",
            "red_devils EffectType is retained exactly as the parser emitted it; the invalid enum label is not resolved here.",
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument(
        "--parsed-root",
        type=Path,
        default=Path(r"D:\Codex Workshop\kards-install-review\parsed"),
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().parents[2]
        / "out/audit/systematic-rules/six-no-ir-card-cdo-classification-20261031.json",
    )
    args = parser.parse_args()
    report = build_report(args)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
