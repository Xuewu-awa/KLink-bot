"""Read-only capture of current-client faction and side enum comparators."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys


FUNCTIONS = {"EnumCompareFaction", "EnumCompareSide"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--review-root", type=Path, required=True)
    parser.add_argument("--index", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.review_root.resolve()
    sys.path[:0] = [str(root), str(root / "python-tools")]
    from runtime_readonly import Reader, modules, find_pid, decode_fname
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64

    baseline = json.loads(args.index.read_text(encoding="utf-8"))
    pid = find_pid()
    module = next(m for m in modules(pid) if m["name"].lower() == "kards-win64-shipping.exe")
    base = module["base"]
    old_base = baseline["game_module"]["base"]
    pool = base + int(baseline["fname_pool"], 16) - old_base
    objects = base + int(baseline["fuobject_array"]["address"], 16) - old_base
    reader = Reader(pid)
    cache = {}
    dis = Cs(CS_ARCH_X86, CS_MODE_64)

    def name(address):
        if not address:
            return "None"
        if address not in cache:
            index, number = struct.unpack("<II", reader.exact(address + 0x18, 8))
            cache[address] = decode_fname(reader, pool, index, number)
        return cache[address]

    try:
        chunks = reader.u64(objects + 0x10)
        count = reader.i32(objects + 0x24)
        if not 0 < count < 3_000_000:
            raise ValueError("Invalid UObject count; rediscover globals")
        owner = None
        for chunk_index in range((count + 65535) // 65536):
            chunk = reader.u64(chunks + chunk_index * 8)
            items = reader.exact(chunk, min(65536, count - chunk_index * 65536) * 24)
            for offset in range(0, len(items), 24):
                address = struct.unpack_from("<Q", items, offset)[0]
                if not address:
                    continue
                try:
                    if (name(address) == "FunctionLibrary"
                            and name(reader.u64(address + 0x10)) == "Class"
                            and name(reader.u64(address + 0x20)) == "/Script/kards"):
                        owner = address
                        break
                except (OSError, ValueError):
                    continue
            if owner:
                break
        if not owner:
            raise ValueError("Verified /Script/kards.FunctionLibrary class not found")

        functions = {}
        child = reader.u64(owner + 0x48)
        seen = set()
        while child and child not in seen:
            seen.add(child)
            function_name = name(child)
            if function_name in FUNCTIONS:
                if (reader.u64(child + 0x20) != owner
                        or name(reader.u64(child + 0x10)) != "Function"):
                    raise ValueError(f"Function ownership mismatch: {function_name}")
                entry = reader.u64(child + 0xD8)
                if not base <= entry < base + module["size"]:
                    raise ValueError(f"Native entry outside game module: {function_name}")
                raw = reader.exact(entry, 512)
                instructions = []
                call_targets = []
                for ins in dis.disasm(raw, entry):
                    instructions.append({
                        "rva": hex(ins.address - base),
                        "bytes": ins.bytes.hex(),
                        "instruction": f"{ins.mnemonic} {ins.op_str}".strip(),
                    })
                    if ins.mnemonic == "call" and ins.op_str.startswith("0x"):
                        call_targets.append(int(ins.op_str, 16))
                    if ins.mnemonic == "ret":
                        break
                if not call_targets:
                    raise ValueError(f"No direct implementation call found: {function_name}")
                implementation = call_targets[-1]
                if not base <= implementation < base + module["size"]:
                    raise ValueError(f"Implementation target outside game module: {function_name}")
                body = reader.exact(implementation, 128)
                body_instructions = []
                for ins in dis.disasm(body, implementation):
                    body_instructions.append({
                        "rva": hex(ins.address - base),
                        "bytes": ins.bytes.hex(),
                        "instruction": f"{ins.mnemonic} {ins.op_str}".strip(),
                    })
                    if ins.mnemonic == "ret":
                        break
                functions[function_name] = {
                    "ownership_verified": True,
                    "exec_rva": hex(entry - base),
                    "capture_hex": raw.hex(),
                    "capture_sha256": hashlib.sha256(raw).hexdigest(),
                    "instructions_to_first_ret": instructions,
                    "direct_call_targets": [hex(x) for x in call_targets],
                    "implementation_rva": hex(implementation - base),
                    "implementation_capture_hex": body.hex(),
                    "implementation_capture_sha256": hashlib.sha256(body).hexdigest(),
                    "implementation_instructions_to_first_ret": body_instructions,
                }
            child = reader.u64(child + 0x28)

        missing = sorted(FUNCTIONS - set(functions))
        if missing:
            raise ValueError(f"Missing FunctionLibrary UFunctions: {missing}")
        result = {
            "schema_version": 1,
            "read_only": True,
            "pid": pid,
            "module": module,
            "exe_sha256": hashlib.sha256(Path(module["path"]).read_bytes()).hexdigest(),
            "source_index": str(args.index.resolve()),
            "source_index_sha256": hashlib.sha256(args.index.read_bytes()).hexdigest(),
            "global_rvas": {"fname_pool": hex(pool - base), "objects": hex(objects - base)},
            "owner": "/Script/kards.FunctionLibrary",
            "functions": functions,
            "limitations": [
                "Read-only live UFunction ownership and machine-code capture; no function was invoked and no game memory was written.",
                "Each wrapper is followed only through its final direct call target and first return; unrelated helper paths are not interpreted.",
                "The capture does not establish PvP eligibility, call reachability, or complete card behavior.",
            ],
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({
            "output": str(args.output),
            "owner": result["owner"],
            "functions": {
                key: {"exec_rva": value["exec_rva"], "implementation_rva": value["implementation_rva"]}
                for key, value in functions.items()
            },
            "exe_sha256": result["exe_sha256"],
        }))
    finally:
        reader.close()


if __name__ == "__main__":
    main()
