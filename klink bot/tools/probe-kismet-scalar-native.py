"""Capture ownership-checked scalar Kismet wrappers without executing game code."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--review-root', type=Path, required=True)
    parser.add_argument('--index', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = args.review_root.resolve()
    sys.path[:0] = [str(root), str(root / 'python-tools')]
    from runtime_readonly import Reader, modules, find_pid, decode_fname
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    baseline = json.loads(args.index.read_text(encoding='utf-8'))
    pid = find_pid()
    module = next(m for m in modules(pid) if m['name'].lower() == 'kards-win64-shipping.exe')
    base = module['base']
    old_base = baseline['game_module']['base']
    pool = base + int(baseline['fname_pool'], 16) - old_base
    objects = base + int(baseline['fuobject_array']['address'], 16) - old_base
    reader = Reader(pid)
    cache = {}
    wanted = {'FTrunc', 'BreakVector', 'SelectInt', 'Multiply_IntInt', 'Clamp', 'clamp'}
    dis = Cs(CS_ARCH_X86, CS_MODE_64)

    def name(address):
        if not address:
            return 'None'
        if address not in cache:
            index, number = struct.unpack('<II', reader.exact(address + 0x18, 8))
            cache[address] = decode_fname(reader, pool, index, number)
        return cache[address]

    try:
        chunks = reader.u64(objects + 0x10)
        count = reader.i32(objects + 0x24)
        if not 0 < count < 3000000:
            raise ValueError('Invalid UObject count; rediscover globals')
        owner = None
        for chunk_index in range((count + 65535) // 65536):
            chunk = reader.u64(chunks + chunk_index * 8)
            items = reader.exact(chunk, min(65536, count - chunk_index * 65536) * 24)
            for j in range(len(items) // 24):
                address = struct.unpack_from('<Q', items, j * 24)[0]
                if not address:
                    continue
                try:
                    if name(address) == 'KismetMathLibrary' and name(reader.u64(address + 0x10)) == 'Class' and name(reader.u64(address + 0x20)) == '/Script/Engine':
                        owner = address
                        break
                except (OSError, ValueError):
                    continue
            if owner:
                break
        if not owner:
            raise ValueError('Verified /Script/Engine.KismetMathLibrary class not found')
        child = reader.u64(owner + 0x48)
        seen = set()
        functions = {}
        while child and child not in seen:
            seen.add(child)
            fn = name(child)
            if fn in wanted:
                if reader.u64(child + 0x20) != owner or name(reader.u64(child + 0x10)) != 'Function':
                    raise ValueError('Function ownership mismatch')
                entry = reader.u64(child + 0xD8)
                if not base <= entry < base + module['size']:
                    raise ValueError('Native entry outside game module')
                raw = reader.exact(entry, 1024)
                instructions = []
                # Capture window is not a claim of an exact function boundary.
                for ins in dis.disasm(raw, entry):
                    instructions.append({'rva': hex(ins.address - base), 'bytes': ins.bytes.hex(), 'instruction': f'{ins.mnemonic} {ins.op_str}'})
                functions[fn] = {'ownership_verified': True, 'exec_rva': hex(entry - base), 'capture_hex': raw.hex(), 'capture_sha256': hashlib.sha256(raw).hexdigest(), 'instructions': instructions}
            child = reader.u64(child + 0x28)
        if not functions:
            raise ValueError('No requested native functions found')
        result = {'schema_version': 1, 'read_only': True, 'pid': pid, 'module': module, 'exe_sha256': hashlib.sha256(Path(module['path']).read_bytes()).hexdigest(), 'source_index': str(args.index.resolve()), 'source_index_sha256': hashlib.sha256(args.index.read_bytes()).hexdigest(), 'global_rvas': {'fname_pool': hex(pool - base), 'objects': hex(objects - base)}, 'owner': '/Script/Engine.KismetMathLibrary', 'functions': functions, 'missing': sorted(wanted - set(functions)), 'limitations': ['Read-only machine-code capture; no native calls, injection or game-memory writes.', 'The source UObject index supplies RVA seeds and is from an earlier capture; live class/function pointers and UFunction ownership are rediscovered and checked during this run.', '1024-byte windows may extend beyond a function. Establish instruction/control-flow boundaries before semantic conclusions.', 'Function declarations, captures and IR presence alone do not certify full card behavior or PvP reachability.']}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        print(json.dumps({'output': str(args.output), 'functions': sorted(functions), 'exe_sha256': result['exe_sha256'], 'missing': result['missing']}))
    finally:
        reader.close()


if __name__ == '__main__':
    main()
