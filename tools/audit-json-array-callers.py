"""Replay RULE-419 loaded-script reference scan from retained read-only evidence.

This checks a captured corpus, not current process state or unloaded/native calls.
Usage: python tools/audit-json-array-callers.py --evidence PATH
"""
import argparse
import gzip
import hashlib
import json
import struct
from pathlib import Path


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def walk(value):
    if isinstance(value, dict):
        yield value
        for item in value.values():
            yield from walk(item)
    elif isinstance(value, list):
        for item in value:
            yield from walk(item)


def verify(root):
    interfaces = load(root / "rule419-json-array-interfaces-native-20261025.json")
    inventory = load(root / "rule419-loaded-json-array-callers-native-20261025.json")
    proof = load(root / "rule419-json-array-caller-proof-20261025.json")
    for capture in (inventory, proof):
        require(capture["exe_sha256"] == interfaces["exe_sha256"], "mixed executable versions")
        require(capture["pid"] == interfaces["pid"] and capture["game_module"] == interfaces["game_module"], "mixed process captures")
    methods = {"JSON_" + operation + kind + "Array"
               for kind in ("Bool", "String")
               for operation in ("Get", "Set", "AddTo", "RemoveFrom")}
    implementations = {f["name"]: f for f in interfaces["functions"] if f["script_size"]}
    require(set(implementations) == methods, "unexpected implementation inventory")
    require(len(interfaces["functions"]) == 16 and len(implementations) == 8, "duplicate or missing declarations")
    for name in methods:
        stubs = [f for f in interfaces["functions"] if f["name"] == name and not f["script_size"]]
        require(len(stubs) == 1 and stubs[0]["owner"] == "CardFunctionsStub", "stub declaration mismatch")
    patterns = {name: set() for name in sorted(methods)}
    for f in interfaces["functions"]:
        header = bytes.fromhex(f["header_hex"])
        patterns[f["name"]].update((header[0x18:0x20], struct.pack("<Q", int(f["address"], 16))))
        script = bytes.fromhex(f["script_hex"])
        require(len(script) == f["script_size"] == struct.unpack_from("<i", header, 0x68)[0], "script size mismatch")
        require(hashlib.sha256(script).hexdigest() == f["script_sha256"], "interface script hash mismatch")
        if script:
            require(f["owner"] == "BP_CardFunctions_C" and f["decode_complete"], "implementation not completely decoded")
    require({k: set(v) for k, v in proof["target_patterns"].items()} ==
            {k: {p.hex() for p in v} for k, v in patterns.items()}, "target pattern mismatch")
    require(set(inventory["patterns_hex"]) == {p.hex() for ps in patterns.values() for p in ps}, "first scan pattern mismatch")
    corpus_path = root / "rule419-loaded-script-corpus-20261025.bin.gz"
    require(hashlib.sha256(corpus_path.read_bytes()).hexdigest() == proof["corpus_sha256"], "corpus hash mismatch")
    hits = dict.fromkeys(sorted(methods), 0)
    positive = dict.fromkeys(proof["control_patterns"], 0)
    controls = {k: [bytes.fromhex(p) for p in ps] for k, ps in proof["control_patterns"].items()}
    scalar_capture = load(root / "rule415-json-scalars-native-20261025.json")
    require(scalar_capture["pid"] == interfaces["pid"] and scalar_capture["exe_sha256"] == interfaces["exe_sha256"]
            and scalar_capture["game_module"] == interfaces["game_module"], "mixed positive control captures")
    for name, ps in controls.items():
        callee = next(f for f in scalar_capture["functions"] if f["name"] == name and f["script_size"])
        expected = {bytes.fromhex(callee["header_hex"])[0x18:0x20],
                    struct.pack("<Q", int(callee["address"], 16))}
        require(set(ps) == expected, "control callee identity pattern mismatch")
    examples = {int(x["function"]["address"], 16): x for x in proof["examples"]}
    seen_examples = set()
    seen_addresses = set()
    total = 0
    with gzip.open(corpus_path, "rb") as corpus:
        require(corpus.read(8) == b"KR419S1\0", "corpus magic mismatch")
        for record in inventory["inventory"]:
            frame = corpus.read(20)
            require(len(frame) == 20, "truncated corpus record")
            address, pointer, size = struct.unpack("<QQI", frame)
            header = corpus.read(0xe0)
            require(len(header) == 0xe0, "truncated function header")
            raw = corpus.read(size)
            require(address == int(record["address"], 16) and address not in seen_addresses, "record address mismatch or duplicate")
            seen_addresses.add(address)
            require((pointer, size) == struct.unpack_from("<Qi", header, 0x60), "script header mismatch")
            require(size == len(raw) == record["script_size"] and size <= struct.unpack_from("<i", header, 0x6c)[0], "script capacity mismatch")
            require(hashlib.sha256(raw).hexdigest() == record["sha256"], "corpus script hash mismatch")
            require(inventory["fnames"]["%s:%s" % struct.unpack_from("<II", header, 0x18)] == record["name"], "function FName mismatch")
            for name, ps in patterns.items():
                hits[name] += int(any(p in raw for p in ps))
            for name, ps in controls.items():
                positive[name] += int(any(p in raw for p in ps))
            if address in examples:
                example = examples[address]
                require(raw.hex() == example["script_hex"] and header.hex() == example["header_hex"], "control example mismatch")
                require(any(p in raw for p in controls[example["target"]]), "control has no raw hit")
                require(any(n.get("Function", n.get("FunctionName")) == example["target"]
                            and n.get("Inst") in ("LocalVirtualFunction", "FinalFunction", "CallMath")
                            for n in walk(example["decoded"])), "control has no decoded call")
                seen_examples.add(address)
            total += size
        require(not corpus.read(1), "unaccounted corpus tail")
    count = len(seen_addresses)
    require(count == inventory["nonempty_scripts"] == proof["scripts"], "corpus count mismatch")
    require(total == inventory["script_bytes"] == proof["bytes"], "corpus byte mismatch")
    require(not inventory["read_errors"] and not inventory["candidates"] and not inventory["direct_calls"], "scan not an error-free negative")
    require(hits == proof["raw_hit_functions"] and not any(hits.values()), "negative scan mismatch")
    require(positive == proof["positive_controls"] and all(positive.values()), "positive scan mismatch")
    require(seen_examples == set(examples) and {x["target"] for x in examples.values()} == set(controls), "missing positive examples")
    assignments = {}
    for name, f in sorted(implementations.items()):
        assignments[name] = [{"statement_index": r["StatementIndex"], "expression": n["Expression"]}
                             for r in f["decoded"] for n in walk(r)
                             if n.get("Inst") == "LetBool" and n.get("Variable", {}).get("Variable Name") == "Found"]
    return dict(rule="RULE-419", scripts=count, script_bytes=total,
                implementation_count=len(implementations), implementation_bytes=sum(f["script_size"] for f in implementations.values()),
                raw_hit_functions=hits, positive_controls=positive, found_assignments=assignments,
                kernel_changed=False, fully_certified_cards=0,
                scope="Captured loaded scripts only; unloaded/native/dynamic dispatch not excluded. Found assignments are syntax evidence, not complete semantic execution.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.evidence), ensure_ascii=False, indent=2))
