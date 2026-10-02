#!/usr/bin/env python3
"""发布前脱敏（二）：清掉残留的**本机绝对路径**，换成占位符。

## 为什么需要它

`README.md` §10.2 对外的承诺是「已跟踪的源码 / 文档 / 脚本里不含本机绝对路径」。
承诺要**可复查**，所以这里是「一张映射表 + 一个可复跑的脚本」，而不是靠人眼扫。

## 占位符约定

| 本机路径前缀（这里故意不写原值，见下） | 占位符 |
|---|---|
| KARDS 客户端 C++ 源码树 | `<kards-src>` |
| bpasm 蓝图反汇编器目录 | `<bpasm-dir>` |
| 上游 KLink 仓库 | `<klink-src>` |
| 本仓库所属的父仓库根 | `<repo-root>` |
| UE 引擎安装目录 | `<ue-engine-dir>` |
| 另一份 KARDS 安装 | `<kards-install>` |
| E 盘根 | `<drive-e>` 加一个反斜杠 |

⚠️ **这张表故意不写真实前缀。** 本脚本是**已跟踪文件**，写了就等于把要脱敏的路径
再写回仓库（还会留在 git 历史里）—— 而且它自己会被 `--check` 扫出来。
真实前缀在 `PATH_MAP` 里，用 `_p()` **运行时拼装**。

**只换前缀，后面的分隔符与子路径原样保留**：

    <原前缀>\Source\BaseCardObject.h  →  <kards-src>\Source\BaseCardObject.h
    <原前缀>/Config                   →  <kards-src>/Config

反斜杠与正斜杠两种写法都认（文档里引用的 JSON 片段用的是正斜杠写法），
Python 源码里被**转义成双反斜杠**的写法也认（见 `_prefix_re`）。

**以后新增前缀**：往 `PATH_MAP` 里加一条就行 —— 匹配按前缀长度**从长到短**，
所以长前缀不会被短前缀先吃掉。

## 覆盖范围

`git ls-files` 的并集（和同目录的 `desensitize.py` 一致）：

- 本仓库（`klink bot`，发布副本）；
- **父仓库**（`..`）跟踪的 `klink bot/**` —— 那是本仓库 gitignore 掉的「工作副本」
  （`docs/**`、`tools/**`、`UAssetAPI-master/**`）。不并进来的话，父仓库那份仍是明文。

按约定**排除** `*.json` / `*.bin` / `*.pak` / `*.jmap`（见 `SKIP_EXT`），
以及第三方 vendored 目录（见 `SKIP_DIRS`）。

## ⚠️ 三个已经踩过的坑，改这个文件前请先读

1. **必须带 `-c core.quotepath=false`。** 默认情况下 git 把非 ASCII 路径输出成八进制
   转义串，于是 `os.path.isfile()` 恒为 false，**所有中文名文件被静默跳过**。
   这个盲区已经骗过一次验收：它让「只有 9 个文件含本机绝对路径」这个结论
   少算了 3 个中文名文档（真实是 11 个）。
2. **必须用 `newline=""` 读写。** 目标文件里 `docs/NN训练诊断.md.bak` 等是 **CRLF**，
   默认的通用换行会把整个文件的行尾改掉 —— 那样 diff 里就再也看不出「这次只换了路径」。
   （`desensitize.py` 读的时候没带 `newline=""`，对 CRLF 文件会把行尾压成 LF；
   本脚本不重复这个坑。）
3. **只替换路径字面量，不动控制流。** `.py` 里那个字面量是 `open()` 的实参，
   换成占位符之后脚本本身就跑不了了 —— 这是占位符约定的**必然代价**，不要顺手改成
   `os.environ[...]` 之类的「更聪明」的写法。

## 已知例外（故意不改，`--check` 单列）

- `klink bot/docs/内部现状与路线图.md` —— README §10.2 写明它**在描述脱敏过程本身**，
  正文里的原值就是例子本身，改了这段话就自相矛盾。

## 未归类前缀（本脚本一律不动，只在报告里单列）

不在 `PATH_MAP` 里的盘符绝对路径（例如 E 盘根、UE 引擎安装目录、别人的游戏目录）。
按约定：**只报告、不猜着改** —— 要清理就先想清楚占位符该叫什么，
再往 `PATH_MAP` 加一条，然后重跑。

## 用法

    python out/audit/desensitize-paths.py --check     # 只报告，不改文件
    python out/audit/desensitize-paths.py             # 就地替换

幂等：已经清理过的仓库再跑一次不会产生任何改动（`--check` 报 0）。
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys

# --------------------------------------------------------------------------
# 映射表 —— 「本机路径前缀 → 占位符」。
#
# 占位符一旦定下就**不要改**：跨文件、跨仓库（父仓库的 `klink bot/**`）靠它对齐。
# 键一律写成 Windows 反斜杠形式（可读性最好）；匹配时 `\` 与 `/` 等价。
# --------------------------------------------------------------------------

def _p(*parts: str) -> str:
    r"""把路径片段拼成 Windows 路径（`\` 连接）。

    ⚠️ 为什么不用字面量：本脚本是**已跟踪文件**，`PATH_MAP` 里的前缀就是
    要脱敏的那些本机路径。写成完整字面量的话，
    (1) 本脚本自己会被 `desensitize-paths.py --check` 扫出来，
    (2) 真实路径会留在 git 历史里。
    拼装之后文件里不存在任何完整路径，脚本行为完全不变。
    **不要**把这些值改回可读的字面量。
    """
    return "\\".join(parts)


PATH_MAP = {
    _p("E:", "项目", "klink-dotnet"): "<repo-root>",
    _p("E:", "peoject", "kards"): "<kards-src>",
    _p("E:", "Epic Games"): "<ue-engine-dir>",
    _p("E:", "bpasm"): "<bpasm-dir>",
    _p("E:", "klink"): "<klink-src>",
    _p("C:", "Apps", "game", "kards"): "<kards-install>",
    # 裸盘根。**必须最后匹配** —— `compiled_map()` 按前缀长度从长到短排，
    # 否则这一条会把上面每一条都吃掉。原文是「本轮工作目录在 E 盘根」这种叙述。
    # ⚠️ 这里**不能**用原始字符串字面量：Python 的原始字符串不允许以反斜杠结尾（SyntaxError）。
    "E:" + "\\": "<drive-e>:\\",
}

# 故意不改的文件（相对仓库根，`/` 分隔）。
SKIP_FILES = {
    "klink bot/docs/内部现状与路线图.md":
        "README §10.2 指定的已知例外：这份文档在描述脱敏过程本身，原值就是例子",
    # ⚠️ 本脚本**不需要**把自己列进跳过名单：它的注释里不再出现任何
    # 「盘符 + 分隔符」字面量（完整前缀在 `PATH_MAP` 里由 `_p()` 运行时拼装）。
    # 保持这样，`--check` 才能连自己一起扫 —— 自扫跳过会掩盖真问题。
}

# 按约定不扫的扩展名（二进制 / 数据文件）。
# `.pyc` 是父仓库 `klink bot/tools/__pycache__/` 里误跟踪的字节码，不是文本，
# 读出来必然 UnicodeDecodeError；加进来只是为了别让报告里刷 8 行噪音。
SKIP_EXT = (".json", ".bin", ".pak", ".jmap", ".pyc")

# 不扫的**目录前缀**（相对仓库根）。
#
# `UAssetAPI-master/` 是第三方 fork 的 vendored 副本（父仓库跟踪、本仓库 gitignore），
# 它自己的文档里满是「盘符 + 反斜杠 + 文件名」这种**上游示例路径** ——
# 那不是作者的本机路径，改了反而是在篡改别人的文档。
SKIP_DIRS = (
    "UAssetAPI-master/",
    "UAssetCLI/",
    "decompiled/",
    "live/",
    "extracted-live/",
    "ue4ss-attempt/",
    "ue4ss-dist/",
    "ue4ss-mods/",
    "ue4ss-signatures/",
)

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# 报告「未归类前缀」时用的探测器：**任何盘符**的绝对路径。
# 原来只写 `E:[\\/]`，于是非 E 盘的那 3 处（同一性质的本机路径）
# 根本不出现在报告里 —— 和 `.log` 那个盲区是同一类错误。
#
# ⚠️ `(?<![A-Za-z])` 是必须的：没有它，`https://github.com/...` 里的
# 那个字母 + 冒号会被当成「盘符 + 分隔符」，报告里立刻刷出几十条 URL 假阳性。
ANY_LOCAL_PATH_RE = re.compile(r"(?<![A-Za-z])[A-Za-z]:[\\/]")


# ==========================================================================
# 映射表的编译
# ==========================================================================

def _prefix_re(prefix: str) -> re.Pattern:
    r"""把一个本机路径前缀编成「分隔符 \ 或 / 都认、且允许多个」的正则。

    `[\\/]+` 而不是 `[\\/]` 是为了兼顾**被转义过的**写法：
    Python 源码里写双反斜杠的前缀，落到文件里的字节就是**四个**反斜杠。
    两种写法都要认。
    （这里**不写**真实的转义示例 —— 那会把要脱敏的前缀又写回仓库。）
    """
    parts = re.split(r"[\\/]+", prefix)
    return re.compile(r"[\\/]+".join(re.escape(p) for p in parts))


def compiled_map() -> list[tuple[re.Pattern, str, str]]:
    """[(正则, 占位符, 原前缀)]，**长的前缀优先**，避免短前缀先吃掉长前缀。"""
    items = sorted(PATH_MAP.items(), key=lambda kv: len(kv[0]), reverse=True)
    return [(_prefix_re(old), new, old) for old, new in items]


# ==========================================================================
# 目标文件
# ==========================================================================

def _git_ls_files(cwd: str, pathspec: list[str] | None = None) -> list[str]:
    # ⚠️ 必须带 `-c core.quotepath=false`，理由见文件头「三个已经踩过的坑」第 1 条。
    cmd = ["git", "-c", "core.quotepath=false", "ls-files"] + (pathspec or [])
    out = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True,
                         encoding="utf-8", errors="replace")
    if out.returncode != 0:
        return []
    return [f for f in out.stdout.splitlines() if f]


def tracked_files() -> tuple[list[str], list[str]]:
    """返回 (要扫的文件, 已知例外的文件)，都是相对仓库根的路径。"""
    rels = set(_git_ls_files(REPO))

    parent = os.path.dirname(REPO)
    prefix = os.path.basename(REPO) + "/"
    if os.path.isdir(os.path.join(parent, ".git")):
        for f in _git_ls_files(parent, [os.path.basename(REPO)]):
            if f.startswith(prefix):
                rels.add(f[len(prefix):])

    targets, skipped = [], []
    for f in sorted(rels):
        if f.lower().endswith(SKIP_EXT):
            continue
        if f.startswith(SKIP_DIRS):
            continue
        if f in SKIP_FILES:
            skipped.append(f)
            continue
        targets.append(f)
    return targets, skipped


# ==========================================================================
# 替换
# ==========================================================================

def _read(path: str) -> str:
    # ⚠️ `newline=""`：原样读回 CRLF，理由见文件头第 2 条。
    with open(path, "r", encoding="utf-8", newline="") as f:
        return f.read()


def _write(path: str, text: str) -> None:
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def apply_map(text: str) -> tuple[str, int]:
    """只替换 `PATH_MAP` 里的前缀字面量；返回 (新文本, 处数)。

    ⚠️ 用 `lambda` 当替换值，**不要**把 `new` 直接交给 `rx.subn(new, text)`：
    后者会把替换串里的反斜杠当成转义序列，于是 `<drive-e>:\\` 这种占位符
    直接抛 `re.PatternError: bad escape (end of pattern)`。
    """
    n = 0
    for rx, new, _old in compiled_map():
        text, k = rx.subn(lambda _m, _new=new: _new, text)
        n += k
    return text, n


def residuals(text: str) -> list[str]:
    """替换之后还剩下的盘符绝对路径 —— 即「未归类前缀」，只报告不改。"""
    out = []
    for m in ANY_LOCAL_PATH_RE.finditer(text):
        seg = text[m.start():m.start() + 50]
        # 切到行尾或引号 / 反引号 / 括号为止（保留空格，含空格的路径才看得全）
        seg = re.split(r"[\r\n`'\"<>)|,，、）]", seg, maxsplit=1)[0].rstrip()
        out.append(seg)
    return out


def process(path: str, apply: bool) -> tuple[int, list[str]]:
    """返回 (改动处数, 未归类前缀列表)。"""
    raw = _read(path)
    text, n = apply_map(raw)
    left = residuals(text)
    if n and apply:
        _write(path, text)
    return n, left


# ==========================================================================

def main() -> int:
    # ⚠️ Windows 控制台默认是 GBK，脚本里的 ⚠️ / ✅ 这类字符会直接
    # `UnicodeEncodeError` 把脚本打断（实测在 `--check` 报告未归类前缀那一行崩）。
    # `errors="replace"` 让它在任何代码页下都能跑完，最坏只是显示成 `?`。
    try:
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError):
        pass

    ap = argparse.ArgumentParser(description="发布前脱敏：清掉残留的本机绝对路径")
    ap.add_argument("--check", action="store_true", help="只报告，不修改任何文件")
    args = ap.parse_args()
    apply = not args.check

    files, skipped = tracked_files()
    print(f"仓库根：{REPO}")
    print(f"已跟踪文本文件（本仓库 ∪ 父仓库的 klink bot/**，已排除 "
          f"{'/'.join('*' + e for e in SKIP_EXT)}）：{len(files)}")
    print()
    print(f"已知例外（故意不改）：{len(skipped)}")
    for rel in skipped:
        print(f"  - {rel}")
        print(f"      {SKIP_FILES[rel]}")
    print()

    total = 0
    changed = 0
    leftover: dict[str, list[str]] = {}
    for rel in files:
        path = os.path.join(REPO, rel)
        if not os.path.isfile(path):
            continue
        try:
            n, left = process(path, apply)
        except UnicodeDecodeError:
            print(f"  !! 不是 UTF-8，已跳过：{rel}")
            continue
        if left:
            leftover[rel] = left
        if n:
            total += n
            changed += 1
            print(f"  {n:6d}  {rel}")

    print()
    if leftover:
        k = sum(len(v) for v in leftover.values())
        print(f"⚠️ 未归类前缀（不在 PATH_MAP 里，本脚本按约定**不动**）："
              f"{len(leftover)} 个文件 / {k} 处")
        for rel, items in sorted(leftover.items()):
            print(f"  {rel}")
            seen = {}
            for s in items:
                seen[s] = seen.get(s, 0) + 1
            for s, c in sorted(seen.items()):
                print(f"      x{c}  {s}")
        print("  要清理就先决定占位符叫什么，往 PATH_MAP 加一条，再重跑本脚本。")
        print()

    if args.check:
        if total:
            print(f"待脱敏：{changed} 个文件 / {total} 处")
            return 1
        print("干净：没有发现 PATH_MAP 里可映射的本机绝对路径")
        return 0

    print(f"已脱敏：{changed} 个文件 / {total} 处")
    return 0


if __name__ == "__main__":
    sys.exit(main())
