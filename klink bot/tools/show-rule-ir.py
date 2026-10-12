"""Render extracted rule IR compactly; evidence files remain read-only."""
import argparse
import json
from pathlib import Path


def operand(value):
    if not isinstance(value, dict):
        return str(value)
    if "var" in value:
        prefix = operand(value["ctx"]) + "." if "ctx" in value else ""
        return prefix + value["var"]
    for key in ("int", "bool", "str", "name", "obj"):
        if key in value:
            return str(value[key])
    if "none" in value:
        return "null"
    for key in ("math", "call"):
        if key in value:
            return value[key] + "(" + ",".join(map(operand, value.get("args", []))) + ")"
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=Path)
    parser.add_argument("--min", type=int, default=0)
    parser.add_argument("--max", type=int, default=2**31 - 1)
    parser.add_argument("--calls", action="store_true")
    args = parser.parse_args()
    steps = json.loads(args.path.read_text(encoding="utf-8"))
    for step in steps:
        if not args.min <= step["i"] <= args.max or (args.calls and step["op"] != "call"):
            continue
        op = step["op"]
        result = f"{step['i']} {op} "
        if op == "set":
            result += step["dst"] + "=" + operand(step["src"])
        elif op == "call":
            result += (operand(step["recv"]) + "." if step.get("recv") else "")
            result += step["fn"] + "(" + ",".join(map(operand, step["args"])) + ")"
        elif op == "setArray":
            result += step["dst"] + "=[" + ",".join(map(operand, step["values"])) + "]"
        else:
            result += str(step.get("to", "")) + " " + operand(step.get("cond", ""))
        print(result)


if __name__ == "__main__":
    main()
