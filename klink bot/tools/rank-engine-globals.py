"""按「被引用次数」排出 exe 里的引擎全局变量，用来定位 GMalloc / GUObjectArray。

思路：GMalloc / GLog / GConfig / GUObjectArray 这类引擎全局被成千上万处代码读取，
所以「被 RIP 相对寻址引用最多的 .data 地址」几乎一定是它们。

而且 `.jmap`（旧构建）给了这三个符号的**相对位置**：
    guobject_array - gmalloc = 0xEFA50
    gnatives       - gmalloc = 0xEE4E0
    guobject_array - gnatives = 0x1570
如果新构建的 .data 布局顺序没变，这个相对关系应该还在 —— 可以直接交叉验证。
"""
import struct
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")

EXE = r"..\kds\kards\Binaries\Win64\kards-Win64-Shipping.exe"
JMAP_IMAGE_BASE = 0x7FF6D7FB0000
JMAP = {"guobject_array": 0x7FF6E11AC450, "gmalloc": 0x7FF6E10BCA00, "gnatives": 0x7FF6E11AAEE0}

PREFIXES = [(b"\x48\x8b\x05", 7), (b"\x48\x8b\x0d", 7), (b"\x48\x8b\x15", 7), (b"\x48\x8b\x1d", 7),
            (b"\x48\x8b\x25", 7), (b"\x48\x8b\x2d", 7), (b"\x48\x8b\x35", 7), (b"\x48\x8b\x3d", 7),
            (b"\x4c\x8b\x05", 7), (b"\x4c\x8b\x0d", 7), (b"\x4c\x8b\x15", 7), (b"\x4c\x8b\x1d", 7),
            (b"\x4c\x8b\x25", 7), (b"\x4c\x8b\x2d", 7), (b"\x4c\x8b\x35", 7), (b"\x4c\x8b\x3d", 7),
            (b"\x48\x8d\x05", 7), (b"\x48\x8d\x0d", 7), (b"\x48\x8d\x15", 7), (b"\x48\x8d\x1d", 7)]


def parse_pe(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    secs = []
    for i in range(nsec):
        s = opt + opt_size + i * 40
        name = data[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, s + 8)
        secs.append((name, vaddr, vsize, rawptr, rawsize))
    return secs


def main():
    data = open(EXE, "rb").read()
    secs = parse_pe(data)
    text = next(s for s in secs if s[0] == ".text")
    data_sec = next(s for s in secs if s[0] == ".data")
    tva, trp, trs = text[1], text[3], text[4]
    dva, dvs = data_sec[1], data_sec[2]
    blob = data[trp:trp + trs]

    print(f".text RVA 0x{tva:x} ({trs:,} 字节)   .data RVA 0x{dva:x} 大小 0x{dvs:x}")
    print(f".data 范围: 0x{dva:08x} .. 0x{dva + dvs:08x}")
    print()

    counter = Counter()
    for prefix, length in PREFIXES:
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

    print(f"=== .data 里被引用最多的 25 个地址（共 {len(counter):,} 个不同地址）===")
    for tgt, cnt in counter.most_common(25):
        print(f"   0x{tgt:08x}   被引用 {cnt:,} 次")

    # 相对位置交叉验证
    off_g = JMAP["guobject_array"] - JMAP["gmalloc"]
    off_n = JMAP["gnatives"] - JMAP["gmalloc"]
    print()
    print(f"=== 用旧构建的相对位置反查（guobject_array = gmalloc + 0x{off_g:x}）===")
    found = 0
    for gmall, cnt in counter.most_common(300):
        for name, off in (("guobject_array", off_g), ("gnatives", off_n)):
            cand = gmall + off
            if cand in counter:
                print(f"   ★ gmalloc 候选 0x{gmall:08x}（引用 {cnt:,}）"
                      f" → {name} 0x{cand:08x}（引用 {counter[cand]:,}）")
                found += 1
    if not found:
        print("   没有命中 —— 说明 .data 布局顺序也变了，相对位置法不可用")

    return 0


if __name__ == "__main__":
    sys.exit(main())
