"""Audit the live BP_CardFunctions extract against the KLink runtime.

Run from the project root:
    python "klink bot/tools/audit-bp-cardfunctions.py"

The report keeps three concepts separate:
the 294 top-level Blueprint functions, names recognized by the runtime,
and LocalVirtualFunction references found inside the Blueprint bytecode.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


HERE = Path(__file__).resolve().parent
# HERE is .../klink bot/tools; its second parent is the repository root.
PROJECT_ROOT = HERE.parents[1]
WORKSHOP_ROOT = HERE.parents[3] if len(HERE.parents) > 3 else PROJECT_ROOT.parent


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def first_existing(paths: Iterable[Path]) -> Path | None:
    for path in paths:
        if path.exists():
            return path
    return None


def walk(value: Any) -> Iterable[dict[str, Any]]:
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def collect_refs(bytecode: Any) -> dict[str, Counter[str]]:
    refs = {
        "local_virtual": Counter(),
        "final_function": Counter(),
        "call_math": Counter(),
        "contexts": Counter(),
    }
    for node in walk(bytecode):
        inst = node.get("Inst")
        if inst == "LocalVirtualFunction" and node.get("FunctionName"):
            refs["local_virtual"][str(node["FunctionName"])] += 1
        elif inst == "FinalFunction" and node.get("Function"):
            refs["final_function"][str(node["Function"])] += 1
        elif inst == "CallMath" and node.get("Function"):
            refs["call_math"][str(node["Function"])] += 1
        if node.get("ContextClass"):
            refs["contexts"][str(node["ContextClass"])] += 1
    return refs


def variable_names(bytecode: Any) -> list[str]:
    names: set[str] = set()
    for node in walk(bytecode):
        if node.get("Inst") in {"LocalVariable", "LocalOutVariable"}:
            name = node.get("Variable Name")
            if isinstance(name, str) and name:
                names.add(name)
    return sorted(names)


def parameter_candidates(names: list[str]) -> list[str]:
    prefixes = ("CallFunc_", "Temp_", "K2Node_", "Local_")
    return [name for name in names if not name.startswith(prefixes)]


def dispatch_names(*paths: Path) -> set[str]:
    names: set[str] = set()
    key_pattern = re.compile(r'\[\s*"([A-Za-z_][A-Za-z0-9_]*)"\s*\]\s*=')
    case_pattern = re.compile(r'case\s+"([A-Za-z_][A-Za-z0-9_]*)"')
    for path in paths:
        if not path.exists():
            continue
        source = path.read_text(encoding="utf-8")
        names.update(key_pattern.findall(source))
        names.update(case_pattern.findall(source))
    return names


def counter_dict(counter: Counter[str]) -> dict[str, int]:
    return dict(sorted(counter.items(), key=lambda item: (-item[1], item[0])))


def resolve_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    default_bp = first_existing(
        [
            WORKSHOP_ROOT / "kards-live-extract" / "BP_CardFunctions.json",
            PROJECT_ROOT.parent / "kards-live-extract" / "BP_CardFunctions.json",
            PROJECT_ROOT / ".." / ".." / "kards-live-extract" / "BP_CardFunctions.json",
        ]
    )
    parser.add_argument("--bp-json", type=Path, default=default_bp)
    parser.add_argument("--bp-names", type=Path)
    parser.add_argument(
        "--dispatch",
        type=Path,
        default=PROJECT_ROOT / "src/KLink.Bot/Effects/CardApiDispatch.cs",
    )
    parser.add_argument(
        "--vm",
        type=Path,
        default=PROJECT_ROOT / "src/KLink.Bot/Effects/Blueprint/KismetVm.cs",
    )
    parser.add_argument("--output-dir", type=Path, default=PROJECT_ROOT / "out/audit")
    parser.add_argument("--no-write", action="store_true")
    args = parser.parse_args()
    if args.bp_json is None:
        parser.error("找不到 BP_CardFunctions.json，请用 --bp-json 指定路径")
    if args.bp_names is None:
        args.bp_names = args.bp_json.with_name("BP_CardFunctions.names.txt")
    return args


def build_report(args: argparse.Namespace) -> dict[str, Any]:
    bp_json = args.bp_json.resolve()
    bp_names = args.bp_names.resolve()
    with bp_json.open("r", encoding="utf-8") as handle:
        bp = json.load(handle)
    if not isinstance(bp, dict):
        raise ValueError("BP_CardFunctions.json 根结构必须是对象")

    json_names = list(bp.keys())
    text_names = [line.strip() for line in bp_names.read_text(encoding="utf-8").splitlines() if line.strip()]
    refs_total = {
        "local_virtual": Counter(),
        "final_function": Counter(),
        "call_math": Counter(),
        "contexts": Counter(),
    }
    functions: list[dict[str, Any]] = []
    for name, payload in bp.items():
        bytecode = payload.get("bytecode") or []
        refs = collect_refs(bytecode)
        for key, counter in refs.items():
            refs_total[key].update(counter)
        names = variable_names(bytecode)
        flags = str(payload.get("function_flags") or "")
        functions.append(
            {
                "name": name,
                "function_flags": flags,
                "expression_count": int(payload.get("expression_count") or 0),
                "bytecode_length": len(bytecode),
                "event": "FUNC_Event" in flags,
                "pure": "FUNC_BlueprintPure" in flags,
                "has_out_params": "FUNC_HasOutParms" in flags,
                "has_defaults": "FUNC_HasDefaults" in flags,
                "variable_names": names,
                "parameter_candidates": parameter_candidates(names),
                "local_virtual_calls": counter_dict(refs["local_virtual"]),
                "final_function_calls": counter_dict(refs["final_function"]),
                "call_math_calls": counter_dict(refs["call_math"]),
            }
        )

    dispatch = dispatch_names(args.dispatch, args.vm)
    bp_set = set(json_names)
    local_virtual = set(refs_total["local_virtual"])
    return {
        "schema_version": 1,
        "generated_by": str(Path(__file__).relative_to(PROJECT_ROOT)),
        "inputs": {
            "bp_json": str(bp_json),
            "bp_names": str(bp_names),
            "dispatch": str(args.dispatch.resolve()),
            "vm": str(args.vm.resolve()),
        },
        "integrity": {
            "bp_json_sha256": sha256(bp_json),
            "bp_names_sha256": sha256(bp_names),
            "bp_json_bytes": bp_json.stat().st_size,
            "bp_names_bytes": bp_names.stat().st_size,
            "json_function_count": len(json_names),
            "names_line_count": len(text_names),
            "names_unique_count": len(set(text_names)),
            "names_match_json_order": text_names == json_names,
            "names_only": sorted(set(text_names) - bp_set),
            "json_only": sorted(bp_set - set(text_names)),
        },
        "summary": {
            "function_count": len(functions),
            "expression_count": sum(item["expression_count"] for item in functions),
            "event_count": sum(item["event"] for item in functions),
            "pure_count": sum(item["pure"] for item in functions),
            "out_param_count": sum(item["has_out_params"] for item in functions),
            "defaults_count": sum(item["has_defaults"] for item in functions),
            "bytecode_min": min((item["bytecode_length"] for item in functions), default=0),
            "bytecode_max": max((item["bytecode_length"] for item in functions), default=0),
            "bytecode_avg": round(sum(item["bytecode_length"] for item in functions) / len(functions), 2)
            if functions
            else 0,
        },
        "references": {key: counter_dict(value) for key, value in refs_total.items()},
        "dispatch": {
            "recognized_name_count": len(dispatch),
            "bp_names_in_dispatch": sorted(bp_set & dispatch),
            "dispatch_names_in_bp": sorted(dispatch & bp_set),
            "bp_names_not_in_dispatch": sorted(bp_set - dispatch),
            "dispatch_names_not_in_bp": sorted(dispatch - bp_set),
            "local_virtual_candidates_not_in_dispatch": sorted(local_virtual - dispatch),
        },
        "functions": sorted(functions, key=lambda item: item["name"]),
    }


def markdown(report: dict[str, Any]) -> str:
    integrity = report["integrity"]
    summary = report["summary"]
    refs = report["references"]
    dispatch = report["dispatch"]
    top_complex = sorted(report["functions"], key=lambda item: (-item["bytecode_length"], item["name"]))[:20]
    lines = [
        "# BP_CardFunctions 审计报告",
        "",
        f"生成脚本：{report['generated_by']}",
        f"蓝图 JSON：{report['inputs']['bp_json']}",
        "",
        "## 完整性",
        "",
        f"- JSON 函数数：{integrity['json_function_count']}",
        f"- names.txt 行数：{integrity['names_line_count']}（唯一值 {integrity['names_unique_count']}）",
        f"- JSON 与 names.txt 顺序一致：**{integrity['names_match_json_order']}**",
        f"- JSON SHA-256：{integrity['bp_json_sha256']}",
        f"- names.txt SHA-256：{integrity['bp_names_sha256']}",
        "",
        "## 函数统计",
        "",
        f"- 表达式总数：{summary['expression_count']}",
        f"- Event：{summary['event_count']}；Pure：{summary['pure_count']}",
        f"- Out 参数：{summary['out_param_count']}；带默认值：{summary['defaults_count']}",
        f"- 字节码长度：{summary['bytecode_min']} ~ {summary['bytecode_max']}，平均 {summary['bytecode_avg']}",
        "",
        "## 复杂函数 TOP 20",
        "",
        "| 函数 | 字节码 | Event | Out | Defaults |",
        "|---|---:|:---:|:---:|:---:|",
    ]
    for item in top_complex:
        lines.append(
            f"| {item['name']} | {item['bytecode_length']} | "
            f"{item['event']} | {item['has_out_params']} | {item['has_defaults']} |"
        )
    lines.extend(["", "## 调用热点", "", "### LocalVirtualFunction", ""])
    for name, count in list(refs["local_virtual"].items())[:30]:
        lines.append(f"- {name}：{count}")
    lines.extend(
        [
            "",
            "## 派发关系",
            "",
            f"- CardApiDispatch + KismetVm 可识别名字：{dispatch['recognized_name_count']}",
            f"- 与 BP_CardFunctions 同名：{len(dispatch['bp_names_in_dispatch'])}",
            f"- LocalVirtualFunction 中未出现在派发表的候选名：{len(dispatch['local_virtual_candidates_not_in_dispatch'])}",
            "",
            "注意：候选名不是自动缺口结论。Blueprint 成员函数、Kismet 数学函数和卡牌局部函数需要结合 IR、事件契约和 CardApi 语义继续分类。",
            "",
            "### 候选名 TOP 40",
            "",
        ]
    )
    for name in dispatch["local_virtual_candidates_not_in_dispatch"][:40]:
        lines.append(f"- {name}")
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    args = resolve_args()
    report = build_report(args)
    print(
        json.dumps(
            {
                "json_function_count": report["integrity"]["json_function_count"],
                "names_match_json_order": report["integrity"]["names_match_json_order"],
                "bp_json_sha256": report["integrity"]["bp_json_sha256"],
                "bp_names_sha256": report["integrity"]["bp_names_sha256"],
                "dispatch_recognized_name_count": report["dispatch"]["recognized_name_count"],
                "local_virtual_candidate_count": len(
                    report["dispatch"]["local_virtual_candidates_not_in_dispatch"]
                ),
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    if not args.no_write:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        json_path = args.output_dir / "bp-cardfunctions-report.json"
        md_path = args.output_dir / "bp-cardfunctions-report.md"
        json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        md_path.write_text(markdown(report), encoding="utf-8")
        print(f"已写出: {json_path}")
        print(f"已写出: {md_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
