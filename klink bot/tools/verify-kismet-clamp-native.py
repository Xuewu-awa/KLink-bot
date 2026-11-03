"""Verify captured execClamp against PE bytes and emulate its signed integer tail."""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
import struct
import sys


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--review-root', type=Path, required=True)
    args = parser.parse_args()
    repo = args.repo.resolve()
    sys.path.insert(0, str(args.review_root / 'python-tools'))
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
    from unicorn.x86_const import UC_X86_REG_RSP, UC_X86_REG_RBX, UC_X86_REG_RSI, UC_X86_REG_RDI
    capture_path = repo / 'out/audit/systematic-rules/kismet-scalar-native-capture-20261103.json'
    capture = json.loads(capture_path.read_text())
    exe = Path(capture['module']['path'])
    data = exe.read_bytes()
    if sha(exe) != capture['exe_sha256']:
        raise ValueError('Executable changed since capture')
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    optional = pe + 24
    sections = optional + struct.unpack_from('<H', data, pe + 20)[0]
    entries = []
    for i in range(struct.unpack_from('<H', data, pe + 6)[0]):
        off = sections + 40 * i
        entries.append(struct.unpack_from('<IIII', data, off + 8))

    def raw(rva, size):
        for virtual_size, va, raw_size, pointer in entries:
            if va <= rva and rva + size <= va + raw_size:
                return data[pointer + rva - va:pointer + rva - va + size]
        raise ValueError(f'Unmapped RVA {rva:x}')

    # PE32+ exception directory gives actual wrapper bounds independently of RET.
    pdata_rva, pdata_size = struct.unpack_from('<II', data, optional + 112 + 3 * 8)
    pdata = raw(pdata_rva, pdata_size)
    boundaries = {}
    for name, fn in capture['functions'].items():
        start = int(fn['exec_rva'], 16)
        windows = [struct.unpack_from('<III', pdata, off) for off in range(0, len(pdata) - 11, 12) if struct.unpack_from('<I', pdata, off)[0] == start]
        if len(windows) != 1:
            raise ValueError(f'Expected one pdata entry for {name}')
        begin, end, unwind = windows[0]
        captured = bytes.fromhex(fn['capture_hex'])
        if raw(start, len(captured)) != captured:
            raise ValueError(f'Live/on-disk mismatch for {name}')
        boundaries[name] = {'begin_rva': hex(begin), 'end_rva_exclusive': hex(end), 'unwind_rva': hex(unwind), 'disk_matches_live_window': True}

    fn = capture['functions']['clamp']
    entry = int(fn['exec_rva'], 16)
    first, last = 0x3da038a, 0x3da03b4
    if not entry <= first < last <= int(boundaries['clamp']['end_rva_exclusive'], 16):
        raise ValueError('Selected tail lies outside verified wrapper')
    code = bytes.fromhex(fn['capture_hex'])[first-entry:last-entry]
    # Exact reviewed instruction sequence: Value=stack+50, Min=+58, Max=+48.
    expected_hex = '488b43208b4c24484885c0400f95c74803f88b442458394c245048897b200f4c4c24503bc10f4cc18906'
    if code.hex() != expected_hex:
        raise ValueError('Clamp instruction slice changed; re-review before reuse')
    sdk = Path('D:/Program Files/KARDS - The WWII Card Game/KARDS/default/game/kards/Binaries/Win64/ue4ss/CXXHeaderDump/Engine.hpp')
    if 'clamp(int32 Value, int32 min, int32 max)' not in sdk.read_text(encoding='utf-8'):
        raise ValueError('SDK argument signature changed')
    code_base, stack, frame, output = 0x100000, 0x200000, 0x300000, 0x400000
    machine = Uc(UC_ARCH_X86, UC_MODE_64)
    for address in [code_base, stack, frame, output]:
        machine.mem_map(address, 0x1000)
    machine.mem_write(code_base, code)
    values = [-2147483648, -9, -1, 0, 1, 4, 9, 2147483647]
    cases = []
    for value, minimum, maximum in itertools.product(values, repeat=3):
        machine.reg_write(UC_X86_REG_RSP, stack)
        machine.reg_write(UC_X86_REG_RBX, frame)
        machine.reg_write(UC_X86_REG_RSI, output)
        machine.reg_write(UC_X86_REG_RDI, 0)
        machine.mem_write(frame + 0x20, struct.pack('<Q', 0))
        for offset, argument in [(0x50, value), (0x58, minimum), (0x48, maximum)]:
            machine.mem_write(stack + offset, struct.pack('<i', argument))
        machine.emu_start(code_base, code_base + len(code), count=32)
        result = struct.unpack('<i', machine.mem_read(output, 4))[0]
        if result != max(minimum, min(value, maximum)):
            raise ValueError('Machine-code oracle disagrees with reviewed expression')
        cases.append([value, minimum, maximum, result])
    fixture = {'schema_version': 1, 'source': 'Offline Unicorn execution of ownership-checked current-client execClamp tail, not in-process invocation', 'exe_sha256': capture['exe_sha256'], 'capture_sha256': sha(capture_path), 'tail_begin_rva': hex(first), 'tail_end_rva_exclusive': hex(last), 'tail_hex': code.hex(), 'columns': ['value', 'minimum', 'maximum', 'result'], 'cases': cases}
    fixture_path = repo / 'tools/BotSim/Fixtures/kismet-clamp-native-20261103.json'
    fixture_path.write_text(json.dumps(fixture, indent=2) + '\n', encoding='utf-8')
    ir = json.loads((repo / 'klink bot/docs/card-ir.json').read_text(encoding='utf-8'))
    sites = []
    def walk(node, path):
        if isinstance(node, dict):
            yield node, path
            for key, child in node.items():
                yield from walk(child, path + '.' + key)
        elif isinstance(node, list):
            for i, child in enumerate(node):
                yield from walk(child, path + f'[{i}]')
    for card, body in ir.items():
        for node, path in walk(body, card):
            if node.get('math') == 'Clamp' or (node.get('op') == 'math' and node.get('fn') == 'Clamp'):
                sites.append({'path': path, 'node': node})
    report = {'schema_version': 1, 'report_id': 'KISMET-CLAMP-NATIVE-20261103', 'scope': 'Integer Clamp scalar result for well-typed int32 arguments; reversed bounds included. Does not certify card reachability or complete Kismet argument marshaling.', 'exe_sha256': capture['exe_sha256'], 'native_boundaries': boundaries, 'clamp_tail_begin_rva': hex(first), 'clamp_tail_end_rva_exclusive': hex(last), 'clamp_tail_hex': code.hex(), 'native_expression': 'max(minimum, min(value, maximum)) using signed int32 comparisons', 'offline_machine_code_cases': len(cases), 'reversed_bound_cases': sum(row[1] > row[2] for row in cases), 'observed_simulator_difference': 'Math.Clamp throws when minimum > maximum; captured client result equals minimum.', 'ir_callsites': sites, 'source_sha256': {'capture': sha(capture_path), 'sdk': sha(sdk), 'fixture': sha(fixture_path), 'ir': sha(repo / 'klink bot/docs/card-ir.json')}, 'limitations': ['Only the arithmetic/output tail is emulated, with parameter slots seeded; UFunction argument helpers are not executed.', 'SDK is a separately generated declaration dump, not proof it was generated from this EXE hash. Parameter slot order is also visible in the current wrapper.', 'FTrunc, BreakVector, SelectInt and Multiply_IntInt are captured and PE-checked only in this batch; full semantics and malformed inputs remain separate work.', 'IR Clamp is the simulator adapter key; identifying this native function does not certify all IR lowering or current-pak equivalence.', 'No claim that reversed bounds occur in legal PvP play; three IR sites are inventory candidates only.']}
    report_path = repo / 'out/audit/systematic-rules/kismet-clamp-native-contract-20261103.json'
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'cases': len(cases), 'reversed': report['reversed_bound_cases'], 'ir_sites': len(sites), 'boundaries': boundaries}))


if __name__ == '__main__':
    main()
