"""区分候选是「指针全局」还是「结构体全局」，用来判定 GMalloc 与 GUObjectArray。

判据（UE 的实际访问方式）：
  GMalloc         是 `FMalloc*`  → 代码是 `mov rax, [GMalloc]`（48 8B 05），**读指针**
  GUObjectArray   是 `FUObjectArray`（结构体，按值）→ 代码多为 `lea rax, [GUObjectArray]`
                  （48 8D 05），再按偏移取字段

所以同一个地址的引用里：mov 占绝大多数 → 指针全局；lea 占相当比例 → 结构体全局。

另外：`mov rax, [GUObjectArray + 0x10]`（取 ObjObjects 的块表）会让位移指向
候选地址 **+0x10**，所以要看候选附近 ±0x20 是否也有引用聚集。
"""
import struct
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

EXE = r"..\kds\kards\Binaries\Win64\kards-Win64-Shipping.exe"

MOV = [(b"\x48\x8b\x05", 7), (b"\x48\x8b\x0d", 7), (b"\x48\x8b\x15", 7), (b"\x48\x8b\x1d", 7),
       (b"\x48\x8b\x25", 7), (b"\x48\x8b\x2d", 7), (b"\x48\x8b\x35", 7), (b"\x48\x8b\x3d", 7),
       (b"\x4c\x8b\x05", 7), (b"\x4c\x8b\x0d", 7), (b"\x4c\x8b\x15", 7), (b"\x4c\x8b\x1d", 7),
       (b"\x4c\x8b\x25", 7), (b"\x4c\x8b\x2d", 7), (b"\x4c\x8b\x35", 7), (b"\x4c\x8b\x3d", 7)]
LEA = [(b"\x48\x8d\x05", 7), (b"\x48\x8d\x0d", 7), (b"\x48\x8d\x15", 7), (b"\x48\x8d\x1d", 7),
       (b"\x48\x8d\x25", 7), (b"\x48\x8d\x2d", 7), (b"\x48\x8d\x35", 7), (b"\x48\x8d\x3d", 7),
       (b"\x4c\x8d\x05", 7), (b"\x4c\x8d\x0d", 7), (b"\x4c\x8d\x15", 7), (b"\x4c\x8d\x1d", 7)]


def main():
    data = open(EXE, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    secs = []
    for i in range(nsec):
        s = pe + 24 + opt_size + i * 40
        name = data[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, s + 8)
        secs.append((name, vaddr, vsize, rawptr, rawsize))

    tva, trp, trs = next((s[1], s[3], s[4]) for s in secs if s[0] == ".text")
    dva, dvs = next((s[1], s[2]) for s in secs if s[0] == ".data")
    blob = data[trp:trp + trs]

    mov_c = Counter()
    lea_c = Counter()

    def scan(prefixes, counter):
        for prefix, length in prefixes:
            start = 0
            while True:
                i = blob.find(prefix, start)
                if i < 0:
                    break
                start = i + 1
                disp = struct.unpack_from("<i", blob, i + 3)[0]
                tgt = tva + i + length + disp
                if dva <= tgt < dva + dvs:
                    counter[tgt] += 1

    scan(MOV, mov_c)
    scan(LEA, lea_c)

    print("=== Top 12：mov（读指针） vs lea（取结构体地址）===")
    print("%-14s %10s %10s   %s" % ("地址", "mov", "lea", "推断"))
    top = [t for t, _ in (mov_c + lea_c).most_common(12)]
    for tgt in top:
        m, l = mov_c[tgt], lea_c[tgt]
        ratio = l / max(1, m + l)
        guess = "结构体（GUObjectArray？）" if ratio > 0.15 else "指针（GMalloc？）"
        print("0x%08x %10d %10d   %s  (lea 占比 %.1f%%)" % (tgt, m, l, guess, ratio * 100))

    print()
    print("=== 候选附近的引用聚集（±0x20）===")
    for tgt in top[:6]:
        print("  0x%08x:" % tgt)
        for off in range(-0x20, 0x24, 4):
            a = tgt + off
            m, l = mov_c[a], lea_c[a]
            if m or l:
                tag = ""
                if off == 0x10:
                    tag = "   ← +0x10 = FUObjectArray::ObjObjects"
                print("      %+#06x  0x%08x  mov %-6d lea %-6d%s" % (off, a, m, l, tag))

    return 0


if __name__ == "__main__":
    sys.exit(main())
