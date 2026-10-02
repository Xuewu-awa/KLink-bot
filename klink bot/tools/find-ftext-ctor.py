"""离线找 `FText::FText(FString&&)` 的地址。

依据 UE5 的 FText 布局：`{ FTextData* Data; uint32 Flags; }`（12 字节，按 8 对齐）。
构造函数 `FText::FText(FString&& InString)` 的典型形状是先把自己清零、
再看入参 FString 是否为空：

    mov qword ptr [rcx],    0      ; 48 C7 01 00 00 00 00     Data  = nullptr
    mov dword ptr [rcx+8],  0      ; C7 41 08 00 00 00 00     Flags = 0
    cmp dword ptr [rdx+8],  0      ; 83 7A 08 00              FString::ArrayNum == 0 ?
    je  <ret>                      ; 74 xx

其中 `rdx+8` 就是 `FString`（TArray<TCHAR>）的 ArrayNum 字段。

本脚本按这个模式扫 .text，输出候选及其反汇编预览，再挑唯一/最像的写成 lua。
"""
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8")

EXE = r"..\kds\kards\Binaries\Win64\kards-Win64-Shipping.exe"
OUT = "ue4ss-signatures/FText_Constructor.lua"

# 清零 FText 的两个成员（Data 64 位 + Flags 32 位）
ZERO_FTEXT = bytes.fromhex("48C7010000000" + "0" + "C7410800000000")
# 上面拼写容易错，直接用两段
ZERO_A = bytes.fromhex("48C70100000000")      # mov qword [rcx], 0
ZERO_B = bytes.fromhex("C7410800000000")      # mov dword [rcx+8], 0
CMP_FSTRING_EMPTY = bytes.fromhex("837A0800")  # cmp dword [rdx+8], 0
JE = 0x74
JNE = 0x75


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
    return data, tva, trp, trs


def main():
    data, tva, trp, trs = load()
    blob = data[trp:trp + trs]
    print(f".text {trs:,} 字节")

    # 1) 找「清零 + 后面 32 字节内 cmp [rdx+8],0」
    cands = []
    start = 0
    while True:
        i = blob.find(ZERO_A, start)
        if i < 0:
            break
        start = i + 1
        if blob[i + 7:i + 14] != ZERO_B:
            continue
        win = blob[i + 14:i + 14 + 32]
        j = win.find(CMP_FSTRING_EMPTY)
        if j < 0:
            continue
        # cmp 后面应紧跟条件跳转
        k = j + len(CMP_FSTRING_EMPTY)
        if k < len(win) and win[k] in (JE, JNE):
            cands.append((i, 14 + j))

    print(f"\n=== 「清零 FText + 检查 FString 为空」候选: {len(cands)} 个 ===")
    for off, cmpoff in cands:
        rva = tva + off
        preview = " ".join("%02X" % b for b in blob[off:off + 40])
        print(f"  RVA 0x{rva:x}   (cmp 在 +{cmpoff})")
        print(f"     {preview}")

    # 2) 放宽：只要「清零 FText」且函数很小、紧接着 ret
    print()
    print("=== 放宽：清零 FText 后 24 字节内出现 C3(ret) 的 ===")
    n = 0
    start = 0
    while n < 12:
        i = blob.find(ZERO_A, start)
        if i < 0:
            break
        start = i + 1
        if blob[i + 7:i + 14] != ZERO_B:
            continue
        if 0xC3 in blob[i + 14:i + 38]:
            print(f"  RVA 0x{tva + i:x}  " + " ".join("%02X" % b for b in blob[i:i + 32]))
            n += 1

    if not cands:
        print("\n没有找到符合形状的候选 —— FText 的构造函数可能被内联了。")
        return 1

    # 3) 取第一个候选生成 lua（模式取 24 字节，disp 无关，全是确定字节）
    off, _ = cands[0]
    pat = blob[off:off + 24]
    # 校验唯一性
    cnt = blob.count(pat)
    print(f"\n首选候选 RVA 0x{tva + off:x}，24 字节模式在 .text 出现 {cnt} 次")

    aob = " ".join("%02X" % b for b in pat)
    lua = f"""-- FText::FText(FString&&) 签名（针对本机 exe, 2026-09-19 构建）
--
-- 形状依据：UE5 的 FText = {{ FTextData* Data; uint32 Flags; }}，
-- 构造函数先把两个成员清零，再检查入参 FString 的 ArrayNum([rdx+8]) 是否为空：
--     48 C7 01 00 00 00 00    mov qword [rcx], 0
--     C7 41 08 00 00 00 00    mov dword [rcx+8], 0
--     83 7A 08 00             cmp dword [rdx+8], 0
--     74 ??                   je ...
--
-- 离线校验：24 字节模式在 .text 里出现 {cnt} 次。

function Register()
    return "{aob}"
end

function OnMatchFound(MatchAddress)
    return MatchAddress
end
"""
    open(OUT, "w", encoding="utf-8", newline="\n").write(lua)
    print(f"已写出 {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
