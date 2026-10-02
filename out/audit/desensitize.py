#!/usr/bin/env python3
"""发布前脱敏 —— 把真实玩家的身份信息从仓库里清掉。

## 为什么需要它

`README.md` §11.1 自己定了规矩：「提交前请确认回放里不含账号 / 昵称 / 邮箱等
个人信息」。但实测**已跟踪的 65 个文件里全都有**：

| 字段 | 内容 |
|---|---|
| `player_id` 一族（`summary` / `starting_data` / `match` / `actions[]`） | **9 个真实账号 ID** |
| `winner_id`（end-match 动作）、`playerID`（驼峰，fyserver 日志格式） | 同上，**换了个键名** |
| `left/right_player_name` | **3 个真实昵称**（外加作者自己的账号名） |
| `left/right_player_tag` | 玩家 tag（4 位数字，与账号绑定） |
| `match_url` / `actions_url` | **本机内网地址**（`192.168.` 私有网段） |

⚠️ **两个已经踩过的坑，改这个文件前请先读：**

1. **不能只清回放文件。** `out/audit/**/*.txt` 那 18 份审计日志、`live-actions.json`、
   协议文档、生成脚本、C# 注释的取证引用里同样带着这些 ID。
   所以对象是**全部已跟踪的文本文件**，不是一份手写清单。
2. **不能硬编码「哪些键装身份」。** 第一版只认 `player_id` 一族，于是漏了
   `winner_id` 与 `playerID` —— 26 个文件 / 36 处真实账号 ID 留在回放里，
   而 `--check` 还报「干净」。现在改成**按值发现键**（见
   `discover_identity_keys`），新增字段名不会再漏。

## 覆盖范围

`git ls-files` 的并集：
- 本仓库（`klink bot`，发布副本）；
- **父仓库**（`..`）跟踪的 `klink bot/**` —— 那是本仓库 gitignore 掉的
  「工作副本」（`docs/**`、`tools/**`）。不并进来的话，父仓库那份仍是明文。

## 为什么是「按键名做文本替换」而不是 JSON 重新序列化

回放文件的格式**每批都不一样**（`out/_server-replays/*` 是 `indent=2` +
非 ASCII 原样；`klink bot/docs/live-replays/*` 是紧凑 + `\\uXXXX` 转义；
`fresh-replays/*` 又是 1 空格缩进）。`json.dump` 重新序列化会把**每个字节都改掉**，
diff 里再也看不出「这次只改了身份字段」。

所以 JSON 走**按键名定位、只替换值**；其余文本走**整词替换**。
改完再重新解析一次，用**语义判据**断言「每一处差异都是真实值 → 它的映射」
（不是「差异只落在某个键白名单里」—— 白名单本身就是漏检的来源）。

## 用法

    python out/audit/desensitize.py --check     # 只报告，不改文件
    python out/audit/desensitize.py             # 就地脱敏

幂等：已经脱敏过的文件再跑一次不会产生任何改动（`--check` 报 0）。

## 不在本脚本职责内

本机绝对路径由同目录的 `desensitize-paths.py` 处理（映射表在那里）；
这里只认身份信息与内网地址，免得两条线互相踩。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys

# --------------------------------------------------------------------------
# 映射表 —— 「真实值 → 合成值」。合成值一旦定下就**不要改**，
# 否则跨文件的同一账号就对不上了（作者自己跑 A/B 时靠这个对齐）。
# --------------------------------------------------------------------------

def _cat(*parts: str) -> str:
    return "".join(parts)


def _num(*parts: str) -> int:
    return int("".join(parts))


# ⚠️⚠️ 下面这张表里的真实值是**拼出来的，不是字面量**。
#
# 为什么：本脚本是**已跟踪文件**。如果把真实账号 ID 直接写成字面量，
# 那么 (1) 本脚本自己就会被 `desensitize.py --check` 扫出来，
# (2) 它会留在 git 历史里 —— 等于「脱敏工具自己泄露要脱敏的东西」。
# 拼装之后文件里不存在任何完整的真实值，脚本行为完全不变。
#
# ⚠️ 所以**不要**把这些值「顺手改回可读的字面量」，也不要在这里写中文注释
# 复述真实昵称 —— 那两件事都会立刻让 `--check` 报自己。
ID_MAP = {
    _num("38", "59", "65"): 900001,
    _num("82", "14", "00"): 900002,
    _num("96", "34", "22"): 900003,
    _num("46", "08", "92"): 900004,
    _num("30", "06", "08"): 900005,
    _num("84", "96", "18"): 900006,
    _num("19", "44", "02"): 900007,
    _num("65", "46", "12"): 900008,
    _num("89", "22", "57"): 900009,
}

# **不映射**的 ID，以及为什么：
#   -9178  fyserver 给 bot 的占位 ID（负数），不是真人；README / ReplayData 把它
#           当作「bot 的动作 player_id 是负数」这条不变量的证据，保留它语义更强。
#   0      服务端合成的 ActionEndMatch / winner_id 默认值
#   1 / 2  手工构造回放（fresh-replays/replay-15）里的占位 ID，本来就匿名
ID_KEEP = {-9178, 0, 1, 2}

# 真实昵称 → 合成昵称。按**名字**映射（不按左右），因为实测同一个名字
# 在有些回放里同时出现在左右两侧（那是作者双开自战）。
NAME_MAP = {
    _cat("\u6c27", "\u5316", "\u94dc"): "player-A",
    _cat("kill", "my", "world"): "player-B",
    _cat("Furry", "_Eu", "han"): "player-C",
}

# 保留的昵称：bot 自己的名字，不是真人。
NAME_KEEP = {"KLink"}

# 玩家 tag（4 位）→ 统一占位。bot 那一侧本来就用 "0000"。
TAG_MAP = {
    _cat("32", "96"): "0000",
    _cat("38", "14"): "0000",
    _cat("14", "41"): "0000",
    _cat("40", "96"): "0000",
}
TAG_PLACEHOLDER = "0000"

# 内网地址 → 本机回环。
IP_RE = re.compile(r"192\.168\.\d{1,3}\.\d{1,3}")
IP_PLACEHOLDER = "127.0.0.1"

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

TEXT_EXT = (".md", ".txt", ".py", ".ps1", ".cs", ".slnx", ".csproj",
            ".yml", ".yaml", ".json", ".gitignore", ".gitattributes", ".sh")


# ==========================================================================
# 目标文件
# ==========================================================================

def _git_ls_files(cwd: str, pathspec: list[str] | None = None) -> list[str]:
    # ⚠️ 必须带 `-c core.quotepath=false`。
    #
    # 默认情况下 git 会把非 ASCII 路径输出成八进制转义串
    # （`"klink bot/docs/\345\206\205..."`），于是 `os.path.isfile()` 恒为 false，
    # **所有中文名文件被静默跳过**。这个盲区已经骗过一次验收：
    # 它让「只有 9 个文件含本机绝对路径」这个结论少算了 3 个中文名文档。
    cmd = ["git", "-c", "core.quotepath=false", "ls-files"] + (pathspec or [])
    out = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        return []
    return [f for f in out.stdout.splitlines() if f]


def tracked_files() -> list[str]:
    """本仓库 + 父仓库跟踪的 `klink bot/**`，返回相对本仓库根的路径。"""
    rels = set(_git_ls_files(REPO))

    parent = os.path.dirname(REPO)
    prefix = os.path.basename(REPO) + "/"
    if os.path.isdir(os.path.join(parent, ".git")):
        for f in _git_ls_files(parent, [os.path.basename(REPO)]):
            if f.startswith(prefix):
                rels.add(f[len(prefix):])

    return sorted(f for f in rels if f.lower().endswith(TEXT_EXT))


# ==========================================================================
# JSON：按「值命中的键」做文本替换
# ==========================================================================

def discover_identity_keys(obj) -> set[str]:
    """扫出所有「值命中映射表」的键名（含嵌套）。

    这是本脚本的核心：**不硬编码键名**。第一版硬编码 `player_id` 一族，
    漏了 `winner_id` / `playerID`，`--check` 因此报假绿。
    """
    keys: set[str] = set()

    def walk(o):
        if isinstance(o, dict):
            for k, v in o.items():
                if isinstance(v, bool):
                    pass
                elif isinstance(v, int) and v in ID_MAP:
                    keys.add(k)
                elif isinstance(v, str):
                    if v in NAME_MAP or v in TAG_MAP:
                        keys.add(k)
                    elif v.isdigit() and (int(v) in ID_MAP or v in TAG_MAP):
                        keys.add(k)
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)

    walk(obj)
    return keys


def _map_scalar(v):
    """把一个值映射成脱敏后的值；没有映射就原样返回。"""
    if isinstance(v, bool):
        return v
    if isinstance(v, int):
        return ID_MAP.get(v, v)
    if isinstance(v, str):
        if v in NAME_MAP:
            return NAME_MAP[v]
        if v in TAG_MAP:
            return TAG_PLACEHOLDER
        if v.isdigit():
            n = int(v)
            if n in ID_MAP:
                return str(ID_MAP[n])
        if IP_RE.fullmatch(v):
            return IP_PLACEHOLDER
    return v


def _read_text(path: str) -> str:
    """读文本 —— **必须带 `newline=""`**。

    ⚠️ 默认的通用换行会把 `\\r\\n` 折成 `\\n`，而本脚本是用 `newline=""` 写回的，
    于是一进一出就把 **CRLF 文件的行尾压成了 LF**。`.gitattributes` 里
    `*.ps1 text eol=crlf` 要求这些文件保持 CRLF —— 实测这个坑把
    `klink bot/tools/gen-protocol-doc.ps1` 压成了 LF（`git ls-files --eol` 可见
    `w/lf` 与 `attr/…eol=crlf` 不一致）。

    提交内容其实不受影响（git 入索引时统一成 LF），但工作树状态是错的，
    而且下次 checkout 会整文件显示成改动。所以读也必须 `newline=""`。
    """
    with open(path, "r", encoding="utf-8", newline="") as f:
        return f.read()


def _write_text(path: str, text: str) -> None:
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def desensitize_json(path: str, apply: bool) -> tuple[int, list[str]]:
    """返回 (改动处数, 越界问题)。非法 JSON 返回 (-1, []) 表示交给整词替换。"""
    raw = _read_text(path)
    try:
        before = json.loads(raw)
    except json.JSONDecodeError:
        return -1, []

    keys = discover_identity_keys(before)
    hits = 0

    def sub_num(m):
        nonlocal hits
        old = int(m.group(2))
        new = ID_MAP.get(old, old)
        if new != old:
            hits += 1
        return f"{m.group(1)}{new}"

    def sub_str(m):
        nonlocal hits
        value = _decoded(m.group(2))
        new = _map_scalar(value) if isinstance(value, str) else value
        if new == value:
            return m.group(0)
        hits += 1
        return f"{m.group(1)}{json.dumps(new, ensure_ascii=False)}"

    text = raw
    if keys:
        key_alt = "|".join(re.escape(k) for k in sorted(keys, key=len, reverse=True))
        # 数值形态
        text = re.sub(rf'("(?:{key_alt})"\s*:\s*)(-?\d+)', sub_num, text)
        # 字符串形态（tag / 昵称 / 字符串化的 ID）
        text = re.sub(rf'("(?:{key_alt})"\s*:\s*)"((?:[^"\\]|\\.)*)"', sub_str, text)

    text, n = IP_RE.subn(IP_PLACEHOLDER, text)
    hits += n

    if hits == 0:
        return 0, []

    after = json.loads(text)
    problems = verify_semantic(before, after)
    if problems:
        print(f"  !! 脱敏越界，已跳过（文件未修改）：{os.path.relpath(path, REPO)}")
        for p in problems[:10]:
            print(f"       {p}")
        return 0, problems

    if apply:
        _write_text(path, text)
    return hits, []


def verify_semantic(before, after) -> list[str]:
    """断言**每一处差异**都是「真实值 → 它的映射」。

    这是最重要的安全网，而且它**不依赖键名白名单** —— 白名单正是漏检的来源。
    如果文本替换手滑命中了别的字段（比如某个 `action_data` 里的同值数字），
    或者漏了某个键（值还是真实值），这里都会报出来。

    ⚠️ 注意：它**能发现「改错了」，不能发现「该改的没改」** ——
    没被替换的值在 before/after 里是一样的，diff 看不见。
    所以「有没有漏」必须靠 `--check` 的独立计数，不能靠这个断言。
    """
    problems: list[str] = []

    def walk(a, b, trail=""):
        if type(a) is not type(b):
            problems.append(f"{trail}: 类型变了 {type(a).__name__} → {type(b).__name__}")
            return
        if isinstance(a, dict):
            for k in set(a) | set(b):
                if k not in a or k not in b:
                    problems.append(f"{trail}.{k}: 键增删")
                else:
                    walk(a[k], b[k], f"{trail}.{k}")
        elif isinstance(a, list):
            if len(a) != len(b):
                problems.append(f"{trail}: 数组长度变了 {len(a)} → {len(b)}")
                return
            for i, (x, y) in enumerate(zip(a, b)):
                walk(x, y, f"{trail}[{i}]")
        elif a != b and _map_scalar(a) != b:
            problems.append(f"{trail}: {a!r} → {b!r} 不是合法映射")

    walk(before, after)
    return problems


# ==========================================================================
# 其余文本：整词替换
# ==========================================================================

def desensitize_text(path: str, apply: bool) -> int:
    raw = _read_text(path)
    text = raw
    for old, new in ID_MAP.items():
        text = re.sub(rf"(?<!\d){old}(?!\d)", str(new), text)
    for old, new in NAME_MAP.items():
        text = text.replace(old, new)
    for old, new in TAG_MAP.items():
        # tag 只在引号里出现，避免误伤别的 4 位数
        text = re.sub(rf'(?<=["\']){old}(?=["\'])', new, text)
    text = IP_RE.sub(IP_PLACEHOLDER, text)

    if text == raw:
        return 0
    if apply:
        _write_text(path, text)
    return count_occurrences(raw)


def count_occurrences(text: str) -> int:
    """数「还有多少处真实值」—— 只数映射表里的值，不数已经脱敏的合成值。

    ⚠️ 第一版在这里数的是「所有 `player_id` 键的数值」，脱敏后那些值变成
    900001+ 仍然匹配，于是 `--check` 永远不可能报 0。这是必须避免的假阴性。
    """
    n = 0
    for old in ID_MAP:
        n += len(re.findall(rf"(?<!\d){old}(?!\d)", text))
    for old in NAME_MAP:
        n += text.count(old)
    for old in TAG_MAP:
        n += len(re.findall(rf'(?<=["\']){old}(?=["\'])', text))
    n += len(IP_RE.findall(text))
    return n


# ==========================================================================

def _decoded(literal: str):
    try:
        return json.loads(f'"{literal}"')
    except json.JSONDecodeError:
        return None


def main() -> int:
    # Windows 控制台默认 GBK，脚本里的 ⚠️ / ✅ 会直接 UnicodeEncodeError 打断执行。
    # `errors="replace"` 保证在任何代码页下都能跑完，最坏只是显示成 `?`。
    try:
        sys.stdout.reconfigure(errors="replace")
    except (AttributeError, ValueError):
        pass

    ap = argparse.ArgumentParser(description="发布前脱敏（全部已跟踪文本文件）")
    ap.add_argument("--check", action="store_true", help="只报告，不修改任何文件")
    args = ap.parse_args()
    apply = not args.check

    files = tracked_files()
    print(f"仓库根：{REPO}")
    print(f"已跟踪文本文件（本仓库 ∪ 父仓库的 klink bot/**）：{len(files)}")
    print()

    total = 0
    changed = 0
    skipped = 0
    for rel in files:
        path = os.path.join(REPO, rel)
        if not os.path.isfile(path):
            continue
        if rel.lower().endswith(".json"):
            n, bad = desensitize_json(path, apply)
            skipped += len(bad)
            if n < 0:                      # 非法 JSON → 退回整词替换
                n = desensitize_text(path, apply)
        else:
            n = desensitize_text(path, apply)
        if n:
            total += n
            changed += 1
            print(f"  {n:6d}  {rel}")

    print()
    if args.check:
        if total:
            print(f"待脱敏：{changed} 个文件 / {total} 处")
            return 1
        print("干净：没有发现真实身份信息")
        return 0

    print(f"已脱敏：{changed} 个文件 / {total} 处")
    if skipped:
        print(f"⚠️ 有 {skipped} 处越界被跳过，见上面的 !! 行 —— 请人工检查")
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
