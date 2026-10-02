"""在 exe 里找 UE 引擎全局符号（GUObjectArray / GMalloc / GNatives）。

背景：UE4SS 只差 GUObjectArray 与 FText 两个 AOB 签名就能跑起来，
而 `UE4SS_Signatures/GUObjectArray.lua` 允许我们自带签名。
`.jmap` 里虽然记了 RVA，但那是**另一个构建**的，上次实测零引用。

本脚本做三件事：
 1. 解析 PE（节表 / ImageBase），把 .jmap 的 RVA 换算核对一遍
 2. 全 .text 扫 RIP 相对寻址，看有没有指向那些 RVA 的（验证 .jmap 是否可用）
 3. 定位已知可用的 StaticConstructObject AOB，并在它周围找
    指向引擎全局区（0x9000000~0x9300000）的 RIP 相对引用 —— 候选 GUObjectArray

用法: python tools/find-engine-globals.py
"""
import re
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8")

EXE = r"..\kds\kards\Binaries\Win64\kards-Win64-Shipping.exe"

# .jmap 里记的（2026-09-11 那个构建）
JMAP_IMAGE_BASE = 0x7FF6D7FB0000
JMAP = {
    "guobject_array": 0x7FF6E11AC450,
    "gmalloc":        0x7FF6E10BCA00,
    "gnatives":       0x7FF6E11AAEE0,
}

# 已知**可用**的签名（本机实测能让 UE4SS 跳过 StaticConstructObject 扫描失败）
SCO_AOB = bytes.fromhex("4C8BDC5553415649" + "8DAB28FEFFFF" + "4881ECC0020000" + "488B")

# RIP 相对寻址的常见前缀（mov/lea reg, [rip+disp32]）
RIP_PREFIXES = [
    (b"\x48\x8b\x05", 7), (b"\x48\x8b\x0d", 7), (b"\x48\x8b\x15", 7), (b"\x48\x8b\x1d", 7),
    (b"\x48\x8b\x25", 7), (b"\x48\x8b\x2d", 7), (b"\x48\x8b\x35", 7), (b"\x48\x8b\x3d", 7),
    (b"\x4c\x8b\x05", 7), (b"\x4c\x8b\x0d", 7), (b"\x4c\x8b\x15", 7), (b"\x4c\x8b\x1d", 7),
    (b"\x48\x8d\x05", 7), (b"\x48\x8d\x0d", 7), (b"\x48\x8d\x15", 7), (b"\x48\x8d\x1d", 7),
]


def parse_pe(data):
    if data[:2] != b"MZ":
        raise ValueError("不是 PE")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("PE 头不对")
    machine, nsec = struct.unpack_from("<HH", data, pe + 4)
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    image_base = struct.unpack_from("<Q", data, opt + 24)[0] if magic == 0x20B \
        else struct.unpack_from("<I", data, opt + 28)[0]
    secs = []
    off = opt + opt_size
    for i in range(nsec):
        s = off + i * 40
        name = data[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, s + 8)
        chars = struct.unpack_from("<I", data, s + 36)[0]
        secs.append((name, vaddr, vsize, rawptr, rawsize, chars))
    return image_base, secs


def main():
    data = open(EXE, "rb").read()
    image_base, secs = parse_pe(data)
    print(f"exe ImageBase = 0x{image_base:x}   节数 {len(secs)}")
    for n, va, vs, rp, rs, ch in secs:
        kind = "可写" if ch & 0x80000000 else "只读"
        print(f"   {n:<10} RVA 0x{va:08x}  vsize 0x{vs:08x}  raw 0x{rs:08x}  {kind}")
    print()

    # ---- 1) .jmap 的 RVA ----
    rvas = {k: v - JMAP_IMAGE_BASE for k, v in JMAP.items()}
    print("=== .jmap 记录的 RVA ===")
    for k, v in rvas.items():
        print(f"   {k:<16} 0x{v:08x}")

    def rva_to_offset(rva):
        for n, va, vs, rp, rs, ch in secs:
            if va <= rva < va + max(vs, rs):
                return rp + (rva - va)
        return None

    # ---- 2) 全 .text 扫 RIP 引用 ----
    text = next((s for s in secs if s[0] == ".text"), None)
    if not text:
        print("找不到 .text")
        return 1
    tname, tva, tvs, trp, trs, _ = text
    blob = data[trp:trp + trs]
    print(f"\n=== 扫 .text（RVA 0x{tva:x}，{trs:,} 字节）里的 RIP 相对引用 ===")

    targets = {v: k for k, v in rvas.items()}
    hits = {k: 0 for k in rvas}
    sample = {k: [] for k in rvas}

    for prefix, length in RIP_PREFIXES:
        start = 0
        while True:
            i = blob.find(prefix, start)
            if i < 0:
                break
            start = i + 1
            if i + 7 > len(blob):
                continue
            disp = struct.unpack_from("<i", blob, i + 3)[0]
            insn_rva = tva + i
            tgt = insn_rva + length + disp
            if tgt in targets:
                name = targets[tgt]
                hits[name] += 1
                if len(sample[name]) < 5:
                    sample[name].append(insn_rva)

    for k in rvas:
        print(f"   {k:<16} RIP 引用数 {hits[k]}")
        for r in sample[k]:
            print(f"        引用点 RVA 0x{r:x}")

    # ---- 3) 定位 StaticConstructObject 并找附近候选 ----
    print("\n=== StaticConstructObject（已知可用签名）===")
    idx = blob.find(SCO_AOB)
    if idx < 0:
        print("   ⚠ 本机 exe 里找不到该 AOB —— 说明 exe 又变了？")
        return 1
    sco_rva = tva + idx
    print(f"   命中 RVA 0x{sco_rva:x}（文件偏移 0x{trp + idx:x}）")

    # 函数体里向引擎全局区（0x9000000~0x9300000）取的 RIP 引用 = GUObjectArray 候选
    win = 0x2000
    cand = {}
    for prefix, length in RIP_PREFIXES:
        start = idx
        end = min(idx + win, len(blob) - 7)
        while True:
            i = blob.find(prefix, start, end)
            if i < 0:
                break
            start = i + 1
            disp = struct.unpack_from("<i", blob, i + 3)[0]
            insn_rva = tva + i
            tgt = insn_rva + length + disp
            if 0x9000000 <= tgt <= 0x9300000:
                cand.setdefault(tgt, []).append(insn_rva)

    print(f"\n=== 函数体内指向 0x9000000~0x9300000 的 RIP 引用（{len(cand)} 个候选）===")
    jmap_g = rvas["guobject_array"]
    for tgt in sorted(cand):
        mark = "  ★ 与 .jmap 的 guobject_array 相同！" if tgt == jmap_g else ""
        print(f"   → RVA 0x{tgt:08x}   来自 {len(cand[tgt])} 处 {[hex(x) for x in cand[tgt][:3]]}{mark}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
