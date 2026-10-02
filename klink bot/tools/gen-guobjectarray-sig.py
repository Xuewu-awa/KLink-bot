"""从 .jmap 给出的绝对地址反推 GUObjectArray 签名，生成 UE4SS_Signatures/GUObjectArray.lua。

原理
----
UE4SS 找不到 GUObjectArray 时会去 `UE4SS_Signatures/GUObjectArray.lua` 找用户提供的签名。
我们手上没有现成的字节模式，但有更精确的东西：`.jmap` 记录了上次运行时 GUObjectArray 的
**绝对地址**和当时的 **image base**，相减就得到稳定的 **RVA**。

有了 RVA 就能在 exe 里找「谁引用了这个全局变量」：
x64 里访问全局变量走 RIP 相对寻址，形如
    REX?  opcode  ModRM(mod=00, rm=101)  disp32
目标地址 = 指令结束位置 + disp32。把 .text 全扫一遍，凡是算出来正好等于
GUObjectArray RVA 的，就是引用点。取引用点周围的字节（把 disp32 打上通配符）
就是一条可用的 AOB 签名。

用法
----
    python gen-guobjectarray-sig.py <jmap> <exe> [输出目录]
"""

import json
import re
import struct
import sys
from pathlib import Path

# 立即数形式 opcode -> 是否读/写内存（我们只关心会引用全局变量的那些）
MODRM_OPCODES = {
    0x8B,  # mov  r, r/m
    0x8D,  # lea  r, m
    0x89,  # mov  r/m, r
    0x03,  # add  r, r/m
    0x2B,  # sub  r, r/m
    0x33,  # xor  r, r/m
    0x3B,  # cmp  r, r/m
    0x39,  # cmp  r/m, r
    0x85,  # test r/m, r
    0x0B,  # or   r, r/m
    0x23,  # and  r, r/m
}


def parse_pe(data: bytes):
    """返回 (image_base, [(name, va, raw_off, raw_size), ...])"""
    if data[:2] != b"MZ":
        raise ValueError("不是 PE 文件（缺 MZ）")
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise ValueError("不是 PE 文件（缺 PE 签名）")

    coff = e_lfanew + 4
    num_sections = struct.unpack_from("<H", data, coff + 2)[0]
    size_opt = struct.unpack_from("<H", data, coff + 16)[0]

    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    if magic == 0x20B:      # PE32+
        image_base = struct.unpack_from("<Q", data, opt + 24)[0]
    elif magic == 0x10B:    # PE32
        image_base = struct.unpack_from("<I", data, opt + 28)[0]
    else:
        raise ValueError(f"未知的 Optional Header magic: {magic:#x}")

    sections = []
    sec = opt + size_opt
    for i in range(num_sections):
        off = sec + i * 40
        name = data[off:off + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, raw_size, raw_off = struct.unpack_from("<IIII", data, off + 8)
        sections.append((name, va, raw_off, raw_size))
    return image_base, sections


def read_jmap_offsets(jmap_path: Path):
    """只读 jmap 头部，取出 image_base 与 engine_offsets。"""
    with open(jmap_path, "r", encoding="utf-8", errors="replace") as f:
        head = f.read(8192)
    base = int(re.search(r'"image_base_address"\s*:\s*"(0x[0-9a-fA-F]+)"', head).group(1), 16)
    offs = {}
    for m in re.finditer(r'"(\w+)"\s*:\s*"(0x[0-9a-fA-F]+)"', head):
        offs[m.group(1)] = int(m.group(2), 16)
    return base, offs


def find_rip_refs(text: bytes, text_va: int, target_rva: int):
    """找出所有 RIP 相对寻址且目标 == target_rva 的指令。返回 [(va, file_off, length), ...]"""
    hits = []
    n = len(text)
    i = 0
    while i < n - 8:
        b = text[i]
        k = i
        if 0x40 <= b <= 0x4F:      # REX 前缀
            k += 1
            if k >= n:
                break
        op = text[k]
        if op in MODRM_OPCODES:
            modrm = text[k + 1]
            # mod=00, rm=101 -> RIP 相对
            if (modrm & 0xC7) == 0x05:
                disp_off = k + 2
                disp = struct.unpack_from("<i", text, disp_off)[0]
                instr_end_va = text_va + disp_off + 4
                if instr_end_va + disp == target_rva:
                    hits.append((text_va + i, i, disp_off + 4 - i))
                    i = disp_off + 4
                    continue
        i += 1
    return hits


def build_unique_signature(text: bytes, hit_file_off: int, instr_len: int,
                           before: int = 10, after: int = 8):
    """围绕引用点取一段字节，把位移字段打上通配符，并验证在整个 .text 中唯一。"""
    start = max(0, hit_file_off - before)
    end = min(len(text), hit_file_off + instr_len + after)
    window = bytearray(text[start:end])

    # 位移字段位置（相对 window）
    disp_start = (hit_file_off + instr_len - 4) - start
    for j in range(disp_start, disp_start + 4):
        if 0 <= j < len(window):
            window[j] = None  # None 表示通配

    # 验证唯一性
    def matches_at(off):
        for j, wb in enumerate(window):
            if wb is not None and text[off + j] != wb:
                return False
        return True

    count = sum(1 for off in range(0, len(text) - len(window)) if matches_at(off))

    parts = []
    for j, wb in enumerate(window):
        if wb is None:
            parts.append("??")
        else:
            # 位移字段内部也整体通配，但保留首字节以增强区分度（若它不是位移的一部分）
            parts.append(f"{wb:02X}")
    return " ".join(parts), count, start


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    jmap_path = Path(sys.argv[1])
    exe_path = Path(sys.argv[2])
    out_dir = Path(sys.argv[3]) if len(sys.argv) > 3 else Path(".")

    base, offs = read_jmap_offsets(jmap_path)
    if "guobject_array" not in offs:
        print("jmap 里没有 guobject_array")
        return 1

    guobject_rva = offs["guobject_array"] - base
    print(f"jmap image base : {base:#x}")
    print(f"GUObjectArray   : {offs['guobject_array']:#x}")
    print(f"GUObjectArray RVA: {guobject_rva:#x}")
    print()

    data = exe_path.read_bytes()
    image_base, sections = parse_pe(data)
    print(f"exe image base  : {image_base:#x}")
    if image_base != base:
        print("  (与 jmap 的 base 不同 —— 正常，ASLR。我们用的是 RVA，不受影响)")
    print()

    text_sec = next((s for s in sections if s[0] == ".text"), None)
    if not text_sec:
        print("找不到 .text 段")
        return 1
    name, va, raw_off, raw_size = text_sec
    text = data[raw_off:raw_off + raw_size]
    print(f".text: VA={va:#x} 文件偏移={raw_off:#x} 大小={raw_size:#x}")

    hits = find_rip_refs(text, va, guobject_rva)
    print(f"引用 GUObjectArray 的指令: {len(hits)} 条")
    if not hits:
        print("没找到引用点")
        return 1

    best = None
    for (instr_va, foff, ilen) in hits[:200]:
        sig, count, start = build_unique_signature(text, foff, ilen)
        if count == 1:
            best = (sig, instr_va, foff, ilen, count)
            break
        if best is None or count < best[4]:
            best = (sig, instr_va, foff, ilen, count)

    sig, instr_va, foff, ilen, count = best
    print(f"选用引用点 VA={instr_va:#x}（指令长 {ilen} 字节），签名命中 {count} 处")
    print(f"签名: {sig}")
    print()

    lua = f'''-- GUObjectArray 签名（自动生成，请勿手改）
--
-- 生成方式：从 .jmap 的 image_base 反推 GUObjectArray 的 RVA（{guobject_rva:#x}），
-- 再在 kards-Win64-Shipping.exe 的 .text 里找出所有 RIP 相对引用它的指令，
-- 取其中唯一的一条作为签名。生成脚本：tools/gen-guobjectarray-sig.py
--
-- 背景：UE4SS v3.0.1 内置签名针对固定引擎版本，在本作的 UE5.6 fork 上扫不到
-- GUObjectArray，必须由用户提供（见 UE4SS 文档 "Fixing missing AOBs"）。

function Register()
    return "{sig}"
end

function OnMatchFound(MatchAddress)
    -- 签名覆盖的是一条 RIP 相对寻址指令；位移字段已通配。
    -- 指令布局： [可选 REX 1B] [opcode 1B] [ModRM 1B] [disp32 4B]
    -- disp32 位于 MatchAddress + {ilen - 4} 处，RIP 取指令结束地址。
    local dispOffset = {ilen - 4}
    local instrEnd = MatchAddress + {ilen}
    local disp = DerefToInt32(MatchAddress + dispOffset)
    if disp == nil then return nil end
    return instrEnd + disp
end
'''

    out_dir.mkdir(parents=True, exist_ok=True)
    out_file = out_dir / "GUObjectArray.lua"
    out_file.write_text(lua, encoding="utf-8")
    print(f"已写出: {out_file}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
