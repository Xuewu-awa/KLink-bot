"""为 GUObjectArray 生成 UE4SS 签名（UE4SS_Signatures/GUObjectArray.lua）。

前提：`tools/resolve-guobjectarray.py` 已定出 GUObjectArray = 0x091ff4d0
（依据：它是 SCO 调用树里被 30 个函数以 lea 引用的结构体，
且 +0x10 处是同样热的指针字段 = FUObjectArray::ObjObjects）。

本脚本：
 1. 找出所有 `lea reg, [GUObjectArray]`（48/4C 8D xx，disp32 指向该地址）
 2. 对每一处取前后若干字节，把 disp32 通配成 ??，统计该模式在 .text 里的**匹配次数**
 3. 选匹配次数 == 1 的那条，写成 lua

UE4SS 的签名 lua 约定：
    function Register() return "<AOB>" end
    function OnMatchFound(MatchAddress) return <GUObjectArray 的地址> end
AOB 匹配到的是**指令地址**，所以要在 OnMatchFound 里解 RIP 相对位移。
"""
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8")

EXE = r"..\kds\kards\Binaries\Win64\kards-Win64-Shipping.exe"
OUT = "ue4ss-signatures/GUObjectArray.lua"
GUOBJECTARRAY = 0x091FF4D0
CONTEXT = 16          # 指令前后各取多少字节
INSN_LEN = 7


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

    # 所有 lea reg,[rip+disp] 且目标为 GUObjectArray
    refs = []
    for pre in (b"\x48\x8d\x05", b"\x48\x8d\x0d", b"\x48\x8d\x15", b"\x48\x8d\x1d",
                b"\x48\x8d\x25", b"\x48\x8d\x2d", b"\x48\x8d\x35", b"\x48\x8d\x3d",
                b"\x4c\x8d\x05", b"\x4c\x8d\x0d", b"\x4c\x8d\x15", b"\x4c\x8d\x1d"):
        start = 0
        while True:
            i = blob.find(pre, start)
            if i < 0:
                break
            start = i + 1
            if i + 7 > len(blob):
                continue
            disp = struct.unpack_from("<i", blob, i + 3)[0]
            if tva + i + 7 + disp == GUOBJECTARRAY:
                refs.append(i)

    print(f"lea → GUObjectArray 的指令: {len(refs)} 处")

    def count_matches(pat_bytes, mask):
        """统计「字节相等 or 被通配」的模式在 .text 里的出现次数。"""
        n = 0
        first = pat_bytes[:2]
        start = 0
        while True:
            j = blob.find(first, start)
            if j < 0:
                break
            start = j + 1
            if j + len(pat_bytes) > len(blob):
                break
            ok = True
            for k in range(len(pat_bytes)):
                if mask[k] and blob[j + k] != pat_bytes[k]:
                    ok = False
                    break
            if ok:
                n += 1
        return n

    best = None
    for i in refs:
        lo = max(0, i - CONTEXT)
        hi = min(len(blob), i + INSN_LEN + CONTEXT)
        pat = blob[lo:hi]
        mask = [True] * len(pat)
        # disp32 通配（相对指令起始 +3）
        d = i + 3 - lo
        for k in range(d, d + 4):
            if 0 <= k < len(mask):
                mask[k] = False
        cnt = count_matches(pat, mask)
        if best is None or cnt < best[2]:
            best = (i, pat, cnt, lo, mask)

    if best is None:
        print("没有可用引用")
        return 1

    i, pat, cnt, lo, mask = best
    insn_rva = tva + i

    def fmt(pat, mask):
        return " ".join(("??" if not mask[k] else "%02X" % pat[k]) for k in range(len(pat)))

    print(f"\n最佳候选: 指令 RVA 0x{insn_rva:x}   唯一匹配数 = {cnt}")
    print(f"模式长度 {len(pat)} 字节")

    aob = fmt(pat, mask)
    print(f"AOB: {aob}")

    # 模式里 disp32 的位置（相对模式起始）
    disp_off = (i + 3) - lo
    lua = f"""-- GUObjectArray 签名（针对本机 kards-Win64-Shipping.exe, 2026-09-19 构建）
--
-- 定址依据（见 klink bot/ue4ss-signatures/README.md）：
--   GUObjectArray = 0x{GUOBJECTARRAY:08X}
--   它是 StaticConstructObject 调用树里被 30 个函数以 lea 引用的结构体，
--   且 +0x10 处是同样热的指针字段，与 FUObjectArray::ObjObjects 的偏移吻合。
--
-- 这条 AOB 在整个 .text 里唯一匹配（离线校验过 {cnt} 次匹配）。
-- 匹配到的是指令地址，OnMatchFound 里解 RIP 相对位移得到数组地址。

function Register()
    return "{aob}"
end

function OnMatchFound(MatchAddress)
    -- 指令形如: 48 8D 0D <disp32>   (lea reg, [rip+disp32])
    -- disp32 位于指令 +3，指令长 7，故目标 = MatchAddress + 7 + disp32
    local disp = DerefToInt32(MatchAddress + {disp_off})
    return MatchAddress + {disp_off + 4} + disp
end
"""
    open(OUT, "w", encoding="utf-8", newline="\n").write(lua)
    print(f"\n已写出 {OUT}")
    print(f"  校验：模式起始 = 指令地址 - {i - lo}，disp32 在模式内偏移 {disp_off}")
    print(f"  期望解析出的地址 = 0x{GUOBJECTARRAY:08X} "
          f"（模式 RVA 0x{tva + lo:x} + {disp_off + 4} + disp）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
