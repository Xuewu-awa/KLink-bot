"""定出 GUObjectArray：跟着 StaticConstructObject 的 call 目标，找它引用的结构体全局。

理由：`StaticConstructObject_Internal` → `StaticAllocateObject` 会**直接访问 GUObjectArray**
（分配 UObject 索引）。所以在它的调用树里，被多个函数以 `lea` 方式引用的 `.data`
结构体全局，就是 GUObjectArray。

输出按「**被多少个不同函数引用**」排序 —— 这个指标比总引用次数更能区分
「引擎核心全局」和「某个模块的局部表」。

用法: python tools/resolve-guobjectarray.py
"""
import struct
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

EXE = r"..\kds\kards\Binaries\Win64\kards-Win64-Shipping.exe"
SCO_AOB = bytes.fromhex("4C8BDC5553415649" + "8DAB28FEFFFF" + "4881ECC0020000" + "488B")

MOV = [(b"\x48\x8b\x05", 7), (b"\x48\x8b\x0d", 7), (b"\x48\x8b\x15", 7), (b"\x48\x8b\x1d", 7),
       (b"\x48\x8b\x25", 7), (b"\x48\x8b\x2d", 7), (b"\x48\x8b\x35", 7), (b"\x48\x8b\x3d", 7),
       (b"\x4c\x8b\x05", 7), (b"\x4c\x8b\x0d", 7), (b"\x4c\x8b\x15", 7), (b"\x4c\x8b\x1d", 7),
       (b"\x4c\x8b\x25", 7), (b"\x4c\x8b\x2d", 7), (b"\x4c\x8b\x35", 7), (b"\x4c\x8b\x3d", 7)]
LEA = [(b"\x48\x8d\x05", 7), (b"\x48\x8d\x0d", 7), (b"\x48\x8d\x15", 7), (b"\x48\x8d\x1d", 7),
       (b"\x48\x8d\x25", 7), (b"\x48\x8d\x2d", 7), (b"\x48\x8d\x35", 7), (b"\x48\x8d\x3d", 7),
       (b"\x4c\x8d\x05", 7), (b"\x4c\x8d\x0d", 7), (b"\x4c\x8d\x15", 7), (b"\x4c\x8d\x1d", 7),
       (b"\x4c\x8d\x25", 7), (b"\x4c\x8d\x2d", 7), (b"\x4c\x8d\x35", 7), (b"\x4c\x8d\x3d", 7)]


def load():
    data = open(EXE, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    osz = struct.unpack_from("<H", data, pe + 20)[0]
    secs = {}
    for i in range(nsec):
        s = pe + 24 + osz + i * 40
        name = data[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vs, va, rs, rp = struct.unpack_from("<IIII", data, s + 8)
        secs[name] = (va, vs, rp, rs)
    tva, tvs, trp, trs = secs[".text"]
    dva, dvs = secs[".data"][0], secs[".data"][1]
    return data, tva, trp, trs, dva, dvs


def main():
    data, tva, trp, trs, dva, dvs = load()
    blob = data[trp:trp + trs]

    idx = blob.find(SCO_AOB)
    if idx < 0:
        print("找不到 SCO 签名")
        return 1
    sco_file_rva = tva + idx
    print(f"StaticConstructObject  RVA 0x{sco_file_rva:x}")

    # ---- SCO 函数体里的 call 目标 ----
    calls = []
    end = min(idx + 0x8000, len(blob) - 5)
    i = idx
    while i < end:
        if blob[i] == 0xE8:
            rel = struct.unpack_from("<i", blob, i + 1)[0]
            tgt = tva + i + 5 + rel
            if tva <= tgt < tva + trs:
                calls.append(tgt)
            i += 5
        else:
            i += 1
    uniq = sorted(set(calls))
    print(f"函数体内 call 目标 {len(calls)} 个（去重 {len(uniq)} 个）")

    # ---- 在每个被调函数里找 .data 结构体引用 ----
    lea_by_func = defaultdict(set)     # target -> {caller}
    mov_by_func = defaultdict(set)
    for c in uniq:
        o = c - tva
        if o < 0 or o >= len(blob):
            continue
        fend = min(o + 0x2000, len(blob) - 7)
        for prefixes, bucket in ((LEA, lea_by_func), (MOV, mov_by_func)):
            for pre, ln in prefixes:
                st = o
                while True:
                    j = blob.find(pre, st, fend)
                    if j < 0:
                        break
                    st = j + 1
                    disp = struct.unpack_from("<i", blob, j + 3)[0]
                    tg = tva + j + ln + disp
                    if dva <= tg < dva + dvs:
                        bucket[tg].add(c)

    print()
    print("=== 按「被多少个不同函数引用」排序：结构体（lea）===")
    print("%-14s %10s %10s   %s" % ("地址", "函数数", "总次数", "备注"))
    for tg, funcs in sorted(lea_by_func.items(), key=lambda kv: -len(kv[1]))[:12]:
        note = ""
        if tg == 0x093dde80:
            note = "★ 之前怀疑的候选"
        print("0x%08x %10d %10s   %s" % (tg, len(funcs), "-", note))

    print()
    print("=== 对照：指针全局（mov）被多少函数引用 ===")
    for tg, funcs in sorted(mov_by_func.items(), key=lambda kv: -len(kv[1]))[:6]:
        print("0x%08x %10d" % (tg, len(funcs)))

    # ---- 专门看 0x093dde80 在不在 SCO 的调用树里 ----
    print()
    t = 0x093dde80
    if t in lea_by_func:
        print(f"★ 0x093dde80 出现在 SCO 调用树的 {len(lea_by_func[t])} 个函数里 ——")
        print("   与「StaticAllocateObject 访问 GUObjectArray」吻合")
    else:
        print("0x093dde80 **不在** SCO 的调用树里 —— 它更可能是 FNamePool 之类")

    return 0


if __name__ == "__main__":
    sys.exit(main())
