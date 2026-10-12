"""Read-only, ownership-verified native rule evidence for the current KARDS build.

python probe-live-rule-native.py <review-root> <output.json>
Requires the review's runtime_readonly.py and python-tools/capstone. Resolves
UFunctions from the running UObject table, verifies their outer/class names,
and records module-relative code. Does not inject, call, or modify game code.
The snapshot supplies global RVAs only; UObject addresses are rediscovered.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys


def probe(root):
    sys.path.insert(0, str(root))
    sys.path.insert(0, str(root / "python-tools"))
    from runtime_readonly import Reader, modules, find_pid, decode_fname
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64

    snapshot = json.loads((root / "runtime-uobject-index.json").read_text(encoding="utf-8"))
    pid = find_pid()
    module = next(m for m in modules(pid) if m["name"].lower() == "kards-win64-shipping.exe")
    base = module["base"]
    old_base = snapshot["game_module"]["base"]
    pool = base + int(snapshot["fname_pool"], 16) - old_base
    objects = base + int(snapshot["fuobject_array"]["address"], 16) - old_base
    reader = Reader(pid)
    dis = Cs(CS_ARCH_X86, CS_MODE_64)
    names = {}
    targets = {}
    base_default = None
    wanted = {"BaseCardObject": ["ResetCardAttributes", "GetPassiveDefenseBuff", "SetExtraPlayTriggers", "UpdateExtraPlayTriggers", "IsValidHandTarget", "FilterCardsToScry"], "KismetMathLibrary": ["RandomIntegerInRangeFromStream", "RandomIntegerFromStream", "FMod"]}

    def name(address):
        if not address:
            return "None"
        if address not in names:
            index, number = struct.unpack("<II", reader.exact(address + 0x18, 8))
            names[address] = decode_fname(reader, pool, index, number)
        return names[address]

    def code(address, size):
        return [{"rva": hex(i.address - base), "instruction": f"{i.mnemonic} {i.op_str}",
                 "bytes": i.bytes.hex()}
                for i in dis.disasm(reader.exact(address, size), address)]

    try:
        chunks = reader.u64(objects + 0x10)
        count = reader.i32(objects + 0x24)
        if not 0 < count < 3000000:
            raise ValueError("Invalid live UObject count; global layout needs rediscovery")
        for chunk_index in range((count + 65535) // 65536):
            chunk = reader.u64(chunks + chunk_index * 8)
            n = min(65536, count - chunk_index * 65536)
            items = reader.exact(chunk, n * 24)
            for j in range(n):
                address = struct.unpack_from("<Q", items, j * 24)[0]
                if not address:
                    continue
                try:
                    h = reader.exact(address, 0x28)
                    klass = struct.unpack_from("<Q", h, 0x10)[0]
                    if name(klass) == "BaseCardObject" and name(address) == "Default__BaseCardObject":
                        if name(reader.u64(address + 0x20)) == "/Script/kards":
                            base_default = (address, klass)
                    if name(klass) != "Class":
                        continue
                    nname = name(address)
                    if nname in wanted:
                        outer = reader.u64(address + 0x20)
                        expected = "/Script/kards" if nname == "BaseCardObject" else "/Script/Engine"
                        if name(outer) != expected:
                            continue
                        targets[nname] = address
                except (OSError, ValueError):
                    continue
            if len(targets) == len(wanted) and base_default:
                break
        result = {"read_only": True, "pid": pid, "module": module,
                  "exe_sha256": hashlib.sha256(Path(module["path"]).read_bytes()).hexdigest(),
                  "global_rvas": {"fname_pool": hex(pool - base), "objects": hex(objects - base)},
                  "functions": {}}
        for classname, methods in wanted.items():
            if classname not in targets:
                raise ValueError(f"Live native class not found: {classname}")
            owner = targets[classname]
            child = reader.u64(owner + 0x48)
            seen = set()
            child_names = []
            while child and child not in seen:
                seen.add(child)
                fname = name(child)
                child_names.append(fname)
                if fname in methods:
                    if reader.u64(child + 0x20) != owner or name(reader.u64(child + 0x10)) != "Function":
                        raise ValueError("Function ownership verification failed")
                    exec_address = reader.u64(child + 0xD8)
                    if not base <= exec_address < base + module["size"]:
                        raise ValueError("Function exec lies outside the game module")
                    entry = {"class": classname, "function": fname, "ownership_verified": True,
                             "exec_rva": hex(exec_address - base), "wrapper": code(exec_address, 768)}
                    if fname in ("SetExtraPlayTriggers", "UpdateExtraPlayTriggers"):
                        # The final direct call before this verified wrapper's
                        # first RET is its native implementation (not a jmap guess).
                        calls = []
                        for instruction in entry["wrapper"]:
                            if instruction["instruction"].startswith("ret"):
                                break
                            if instruction["instruction"].startswith("call 0x"):
                                calls.append(int(instruction["instruction"].split()[1], 16))
                        impl = calls[-1]
                        if not base <= impl < base + module["size"]:
                            raise ValueError("Extra-play implementation outside game module")
                        entry["implementation_rva"] = hex(impl - base)
                        entry["implementation"] = code(impl, 512)
                        literals = []
                        for ins in dis.disasm(reader.exact(impl, 256), impl):
                            if ins.mnemonic == "lea" and "[rip +" in ins.op_str:
                                displacement = struct.unpack("<i", ins.bytes[-4:])[0]
                                raw = reader.exact(ins.address + ins.size + displacement, 96)
                                ascii_text = raw.split(b"\0", 1)[0].decode("ascii", errors="replace")
                                wide_text = raw.decode("utf-16-le", errors="replace").split("\0", 1)[0]
                                literals.append({"instruction_rva": hex(ins.address - base),
                                                 "ascii": ascii_text, "utf16": wide_text})
                        entry["referenced_literals"] = literals
                    if fname in ("GetPassiveDefenseBuff", "IsValidHandTarget", "FilterCardsToScry"):
                        if not base_default or base_default[1] != owner:
                            raise ValueError("BaseCardObject CDO ownership verification failed")
                        vtable = reader.u64(base_default[0])
                        slot = {"GetPassiveDefenseBuff": 0x358, "IsValidHandTarget": 0x2D8,
                                "FilterCardsToScry": 0x368}[fname]
                        if not any(f"[rax + {hex(slot)}]" in i["instruction"] for i in entry["wrapper"]):
                            raise ValueError("Verified wrapper does not call expected virtual slot")
                        impl = reader.u64(vtable + slot)
                        if not base <= impl < base + module["size"]:
                            raise ValueError("Passive-defense virtual target outside game module")
                        entry["virtual_slot"] = hex(slot)
                        entry["implementation_rva"] = hex(impl - base)
                        entry["implementation"] = code(impl, 512)
                    if fname == "ResetCardAttributes":
                        # This build's no-argument wrapper tail-jumps to the implementation.
                        impl = base + 0x4B09020
                        entry["historical_implementation_call_verified"] = any(
                            i["instruction"] in (f"call 0x{impl:x}", f"jmp 0x{impl:x}")
                            for i in entry["wrapper"][:8])
                        if entry["historical_implementation_call_verified"]:
                            entry["implementation_rva"] = hex(impl - base)
                            entry["implementation"] = code(impl, 0x61F)
                    result["functions"][classname + "." + fname] = entry
                child = reader.u64(child + 0x28)
            for method in methods:
                if classname + "." + method not in result["functions"]:
                    raise ValueError(f"Live UFunction not found: {classname}.{method}; children={child_names}")
        return result
    finally:
        reader.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("review_root", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = probe(args.review_root)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: {"exec_rva": v["exec_rva"], "ownership_verified": v["ownership_verified"]}
                      for k, v in result["functions"].items()}, indent=2))
