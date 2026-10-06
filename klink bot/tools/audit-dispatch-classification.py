#!/usr/bin/env python3
"""Classify Blueprint IR callsites for the headless KLink simulator.

This report deliberately counts callsites, rather than only comparing names.
An unregistered name can still be a card-local function and therefore execute
correctly through KismetVm's local-function fallback.  UI, campaign, tutorial,
and client-only calls are reported separately because they are outside the
headless rules kernel's scope.
"""

from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Iterable


HERE = Path(__file__).resolve().parent
PROJECT_ROOT = HERE.parents[1]


def walk(value: Any) -> Iterable[dict[str, Any]]:
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def existing(*paths: Path) -> Path:
    for path in paths:
        if path.exists():
            return path
    raise FileNotFoundError("none of the candidate paths exists: " + ", ".join(map(str, paths)))


def direct_dispatch_names(path: Path) -> set[str]:
    """Read only dictionary registrations, excluding switch/case diagnostics."""
    pattern = re.compile(r'^\s*\[\s*"([A-Za-z_][A-Za-z0-9_]*)"\s*\]\s*=')
    return {
        match.group(1)
        for line in path.read_text(encoding="utf-8").splitlines()
        if (match := pattern.match(line))
    }


def vm_case_names(*paths: Path) -> set[str]:
    pattern = re.compile(r'case\s+"([A-Za-z_][A-Za-z0-9_]*)"')
    names: set[str] = set()
    for path in paths:
        if path.exists():
            names.update(pattern.findall(path.read_text(encoding="utf-8")))
    return names


def iter_calls(card_name: str, card: dict[str, Any]):
    for source, programs in (("entrypoint", {"__entrypoint__": card.get("steps", [])}),
                             ("local", card.get("locals", {}))):
        if isinstance(programs, dict):
            for program_name, steps in programs.items():
                if not isinstance(steps, list):
                    continue
                for step in steps:
                    if not isinstance(step, dict) or step.get("op") != "call":
                        continue
                    fn = step.get("fn")
                    if isinstance(fn, str) and fn:
                        yield fn, source, program_name


def scope_for_card(card_name: str) -> str:
    """Identify IR owners that never participate in headless rules execution."""
    lowered = card_name.lower()
    if card_name.startswith(("BP_", "WBP_", "createCard_")):
        return "ui"
    if ("campaign" in lowered or "tutorial" in lowered
            or re.search(r"_(?:cam|scen)\d*(?:_|$)", lowered)):
        return "campaign"
    return "gameplay"


def classify(name: str, stat: dict[str, Any], bp_names: set[str], dispatch: set[str], vm: set[str]) -> str:
    if name in dispatch:
        return "dispatch"
    if stat["local_fallback_calls"] == stat["calls"]:
        return "local_fallback"
    lowered = name.lower()
    if any(token in lowered for token in (
        "campaign", "tutorial", "helpbubble", "verticalbox", "widget", "focus",
        "visibility", "brush", "button", "slate", "umg", "hud", "menu",
        "client", "animation", "settext", "gettext", "tooltip",
    )):
        return "campaign_ui"
    if name in vm:
        return "vm_builtin"
    if name in bp_names:
        return "blueprint_function"
    if stat["recv_calls"] > 0:
        return "blueprint_member"
    return "unknown_gameplay"


def resolve_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ir", type=Path, default=PROJECT_ROOT / "klink bot/docs/card-ir.json")
    parser.add_argument("--bp-json", type=Path, default=PROJECT_ROOT.parent / "kards-live-extract/BP_CardFunctions.json")
    parser.add_argument("--dispatch", type=Path, default=PROJECT_ROOT / "src/KLink.Bot/Effects/CardApiDispatch.cs")
    parser.add_argument("--vm", type=Path, default=PROJECT_ROOT / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs")
    parser.add_argument("--output-dir", type=Path, default=PROJECT_ROOT / "out/audit")
    parser.add_argument("--no-write", action="store_true")
    return parser.parse_args()


def build_report(args: argparse.Namespace) -> dict[str, Any]:
    ir_path = existing(args.ir)
    bp_path = existing(args.bp_json)
    dispatch_path = existing(args.dispatch)
    vm_path = existing(args.vm)
    ir = json.loads(ir_path.read_text(encoding="utf-8"))
    bp = json.loads(bp_path.read_text(encoding="utf-8"))
    if not isinstance(ir, dict) or not isinstance(bp, dict):
        raise ValueError("card-ir.json and BP_CardFunctions.json must contain objects")

    dispatch = direct_dispatch_names(dispatch_path)
    vm_names = vm_case_names(vm_path)
    bp_names = set(bp)
    stats: dict[str, dict[str, Any]] = defaultdict(lambda: {
        "calls": 0, "cards": set(), "entrypoint_calls": 0, "local_calls": 0,
        "local_fallback_calls": 0, "recv_calls": 0, "sample_cards": set(),
        "scope_calls": Counter(), "scope_cards": defaultdict(set),
        "scope_fallback_calls": Counter(),
    })
    total_cards = 0
    total_calls = 0
    for card_name, card in ir.items():
        if not isinstance(card, dict):
            continue
        total_cards += 1
        local_names = set((card.get("locals") or {}).keys())
        for fn, source, _program in iter_calls(card_name, card):
            stat = stats[fn]
            stat["calls"] += 1
            stat["cards"].add(card_name)
            scope = scope_for_card(card_name)
            stat["scope_calls"][scope] += 1
            stat["scope_cards"][scope].add(card_name)
            if len(stat["sample_cards"]) < 8:
                stat["sample_cards"].add(card_name)
            if source == "entrypoint":
                stat["entrypoint_calls"] += 1
            else:
                stat["local_calls"] += 1
            if fn in local_names:
                stat["local_fallback_calls"] += 1
                stat["scope_fallback_calls"][scope] += 1
            # A receiver means this is a member/library-shaped callsite.
            # The distinction is useful even when the name is unresolved.
            # Find the step again cheaply only for this boolean by convention:
            # card IR stores recv on the call object, handled below in a second pass.
            total_calls += 1

    # Re-read call objects to count receiver-shaped unresolved calls precisely.
    for card_name, card in ir.items():
        if not isinstance(card, dict):
            continue
        for source, programs in (("entrypoint", {"__entrypoint__": card.get("steps", [])}),
                                 ("local", card.get("locals", {}))):
            for _program, steps in programs.items() if isinstance(programs, dict) else []:
                if not isinstance(steps, list):
                    continue
                for step in steps:
                    if isinstance(step, dict) and step.get("op") == "call" and isinstance(step.get("fn"), str):
                        if step.get("recv") is not None:
                            stats[step["fn"]]["recv_calls"] += 1

    rows = []
    category_counts = Counter()
    for name, stat in stats.items():
        if stat["scope_calls"]["gameplay"] == 0 and name not in dispatch:
            category = "campaign_ui"
        else:
            category = classify(name, stat, bp_names, dispatch, vm_names)
        category_counts[category] += 1
        rows.append({
            "name": name,
            "category": category,
            "calls": stat["calls"],
            "distinct_cards": len(stat["cards"]),
            "entrypoint_calls": stat["entrypoint_calls"],
            "local_calls": stat["local_calls"],
            "local_fallback_calls": stat["local_fallback_calls"],
            "uncovered_calls": stat["calls"] - stat["local_fallback_calls"] if name not in dispatch else 0,
            "gameplay_fallback_calls": stat["scope_fallback_calls"]["gameplay"],
            "gameplay_gap_calls": stat["scope_calls"]["gameplay"] - stat["scope_fallback_calls"]["gameplay"] if name not in dispatch else 0,
            "receiver_calls": stat["recv_calls"],
            "gameplay_calls": stat["scope_calls"]["gameplay"],
            "campaign_calls": stat["scope_calls"]["campaign"],
            "ui_calls": stat["scope_calls"]["ui"],
            "gameplay_cards": len(stat["scope_cards"]["gameplay"]),
            "excluded_cards": sorted(stat["scope_cards"]["campaign"] | stat["scope_cards"]["ui"])[:8],
            "sample_cards": sorted(stat["sample_cards"]),
        })
    rows.sort(key=lambda row: (-row["calls"], row["name"]))
    unresolved = [row for row in rows if row["gameplay_gap_calls"] > 0]
    return {
        "schema_version": 1,
        "generated_by": str(Path(__file__).relative_to(PROJECT_ROOT)),
        "headless_scope": {
            "included": "card rules, gameplay state, triggers, targeting and resolution",
            "excluded_categories": ["campaign_ui"],
            "note": "UI, campaign, tutorial and client-only calls are inventory only; they are not P0 kernel gaps.",
        },
        "inputs": {"ir": str(ir_path), "bp_json": str(bp_path), "dispatch": str(dispatch_path), "vm": str(vm_path)},
        "summary": {
            "card_count": total_cards,
            "callsite_count": total_calls,
            "unique_function_count": len(rows),
            "direct_dispatch_count": len(dispatch),
            "bp_function_count": len(bp_names),
            "vm_builtin_count": len(vm_names),
            "category_function_counts": dict(sorted(category_counts.items())),
            "headless_gap_calls": sum(row["gameplay_gap_calls"] for row in rows),
            "headless_gap_functions": sum(row["gameplay_gap_calls"] > 0 for row in rows),
            "unknown_gameplay_calls": sum(row["gameplay_gap_calls"] for row in rows if row["category"] == "unknown_gameplay"),
            "unknown_gameplay_functions": sum(row["category"] == "unknown_gameplay" and row["gameplay_gap_calls"] > 0 for row in rows),
        },
        "functions": rows,
        "headless_candidates": [row for row in unresolved if row["category"] in {"unknown_gameplay", "blueprint_function", "blueprint_member", "vm_builtin"}],
    }


def markdown(report: dict[str, Any]) -> str:
    summary = report["summary"]
    lines = [
        "# 无头模拟器派分类审计", "",
        "UI、战役、教程和客户端专用调用只做库存记录，不进入核心原语修复队列。", "",
        f"- 卡/程序条目：{summary['card_count']}",
        f"- IR call 调用点：{summary['callsite_count']}",
        f"- 唯一调用名：{summary['unique_function_count']}",
        f"- 无头规则真缺口：{summary['headless_gap_functions']} 种 / {summary['headless_gap_calls']} 个调用点",
        f"- 其中未知语义 unknown_gameplay：{summary['unknown_gameplay_functions']} 种 / {summary['unknown_gameplay_calls']} 个调用点", "",
        "## 分类统计", "", "| 分类 | 函数数 |", "|---|---:|",
    ]
    for category, count in summary["category_function_counts"].items():
        lines.append(f"| `{category}` | {count} |")
    lines += ["", "## 无头规则候选 TOP 100", "", "| 调用名 | 分类 | 调用点 | 卡数 | 未被本地函数覆盖 | 示例卡 |", "|---|---|---:|---:|---:|---|"]
    candidates = sorted(report["headless_candidates"], key=lambda row: (-row["gameplay_calls"], row["name"]))
    for row in candidates[:100]:
        lines.append(f"| `{row['name']}` | `{row['category']}` | {row['gameplay_gap_calls']} | {row['gameplay_cards']} | {row['uncovered_calls']} | {', '.join(row['sample_cards'][:3])} |")
    return "\n".join(lines) + "\n"


def main() -> int:
    args = resolve_args()
    report = build_report(args)
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))
    if not args.no_write:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        (args.output_dir / "dispatch-classification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        (args.output_dir / "dispatch-classification.md").write_text(markdown(report), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
