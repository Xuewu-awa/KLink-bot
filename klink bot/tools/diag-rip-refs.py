"""诊断：验证 RIP 相对引用扫描器，并检查 GUObjectArray RVA 是否真的被 .text 引用。

用法：python diag-rip-refs.py <jmap> <exe>
"""
import re
import struct
import sys
from pathlib import Path

MODRM_OPCODES = {0x8B, 0x8D, 0x89, 0x03, 0x2B, 0x33, 0x3B, 0x39, 0x85, 0x0B, 0x23}


def parse_pe(data):
    e = struct.unpack_from("<I", data, 0x3C)[0]
    coff = e + 4
    nsec = struct.unpack_from("<H", data, coff + 2)[0]
    size_opt = struct.unpack_from("<H", data, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    image_base = struct.unpack_from("<Q", data, opt + 24)[0] if magic == 0x20B else struct.unpack_from("<I", data, opt + 28)[0]
    secs = []
    for i in range(nsec):
        off = opt + size_opt + i * 40
        name = data[off:off + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, rsize, roff = struct.unpack_from("<IIII", data, off + 8)
        secs.append((name, va, roff, rsize, vsize))
    return image_base, secs


def main():
    jmap, exe = Path(sys.argv[1]), Path(sys.argv[2])
    head = jmap.open("r", encoding="utf-8", errors="replace").read(8192)
    base = int(re.search(r'"image_base_address"\s*:\s*"(0x[0-9a-fA-F]+)"', head).group(1), 16)
    offs = {m.group(1): int(m.group(2), 16) for m in re.finditer(r'"(\w+)"\s*:\s*"(0x[0-9a-fA-F]+)"', head)}

    print("jmap image base:", hex(base))
    for k in ("guobject_array", "gmalloc", "gnatives", "process_event", "process_internal", "fname_to_string"):
        if k in offs:
            print(f"  {k:<18} {offs[k]:#x}   RVA={offs[k]-base:#x}")

    data = exe.read_bytes()
    image_base, secs = parse_pe(data)
    print("\nexe preferred base:", hex(image_base))
    print("sections:")
    for (name, va, roff, rsize, vsize) in secs:
        print(f"  {name:<10} RVA={va:#010x} raw={roff:#010x} size={rsize:#x} vsize={vsize:#x}")

    text = next(s for s in secs if s[0] == ".text")
    _, tva, troff, trsize, _ = text
    buf = data[troff:troff + trsize]
    print(f"\n.text 扫描中（{trsize/1e6:.1f} MB）...")

    target = offs["guobject_array"] - base
    refs = []
    n = len(buf)
    i = 0
    total_rip = 0
    while i < n - 8:
        b = buf[i]
        k = i + 1 if 0x40 <= b <= 0x4F else i
        op = buf[k]
        if op in MODRM_OPCODES and (buf[k + 1] & 0xC7) == 0x05:
            total_rip += 1
            doff = k + 2
            disp = struct.unpack_from("<i", buf, doff)[0]
            tgt = tva + doff + 4 + disp
            refs.append(tgt)
            i = doff + 4
            continue
        i += 1

    print(f"RIP 相对引用总数: {total_rip}")
    if not refs:
        print("一条都没扫到 —— 扫描器有问题")
        return 1

    refs.sort()
    import bisect
    pos = bisect.bisect_left(refs, target)
    print(f"\n目标 GUObjectArray RVA = {target:#x}")
    print("最接近该地址的 12 个引用目标:")
    for j in range(max(0, pos - 6), min(len(refs), pos + 6)):
        d = refs[j] - target
        mark = "   <<< 命中" if d == 0 else ""
        print(f"  {refs[j]:#012x}   差 {d:+#x}{mark}")

    # 模块范围内的引用目标分布（看是否落在合理区间）
    mod_lo, mod_hi = 0, max(s[1] + s[4] for s in secs)
    in_mod = sum(1 for r in refs if mod_lo <= r < mod_hi)
    print(f"\n落在模块范围内({mod_lo:#x}..{mod_hi:#x})的引用: {in_mod}/{len(refs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
