"""
补齐 kardsim 卡库缺口的三件麻烦事。

  list   —— 算出「我抽到的卡」里 kardsim 没有的那些卡名，写到 out/gap-names.txt
  stage  —— 把这些卡的 .uasset 拷进 out/gap-assets/，作为转译器的 --in
  merge  —— 把转译产物 .g.cs 合并进 ref/kards-sim/KardsSim/Generated/，
            并把新条目并进 _index.g.cs（**不能直接覆盖**：那是我局部转译出来的，
            只有几条，覆盖会把他原有的 1600+ 条索引全弄丢）

为什么不能整目录重转：他的 Generated/ 是在**有 UHT 头文件**的条件下生成的，
我这边没有 --uht，整批重转可能让 1600 多个文件的行为变掉。
只补缺的那批，风险最小。
"""
import json
import os
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(".")
FULL_CARDS = ROOT / "out" / "cards-full.json"          # 我全量抽取的结果（2024 张）
THEIR_CARDS = ROOT / "ref" / "kards-sim" / "cards.json"  # 他的卡库（1906 张）
ASSET_ROOT = ROOT / "klink bot" / "live" / "Cards"
GAP_ASSETS = ROOT / "out" / "gap-assets"
GAP_NAMES = ROOT / "out" / "gap-names.txt"
GEN_THEIRS = ROOT / "ref" / "kards-sim" / "KardsSim" / "Generated"


def load_ids(path):
    d = json.load(open(path, encoding="utf-8"))
    if isinstance(d, dict):
        return set(d.keys())
    return {c.get("id") for c in d if c.get("id")}


def cmd_list():
    mine = load_ids(FULL_CARDS)
    theirs = load_ids(THEIR_CARDS)
    # 已经转译过的也算「不缺」。⚠️ 同样不能用 stem（见 cmd_merge 的注记）。
    if GEN_THEIRS.exists():
        for f in GEN_THEIRS.rglob("*.g.cs"):
            if f.name.startswith("_"):
                continue
            theirs.add(f.name.removesuffix(".g.cs"))
    gap = sorted(mine - theirs)
    GAP_NAMES.write_text("\n".join(gap), encoding="utf-8")
    print(f"我抽到 {len(mine)} 张 / kardsim 有 {len(theirs)} 张")
    print(f"缺口 {len(gap)} 张 → {GAP_NAMES}")
    for n in gap[:20]:
        print(f"    {n}")
    if len(gap) > 20:
        print(f"    … 还有 {len(gap) - 20} 张")
    return gap


def cmd_stage():
    names = [x.strip() for x in GAP_NAMES.read_text(encoding="utf-8").splitlines() if x.strip()]
    if GAP_ASSETS.exists():
        shutil.rmtree(GAP_ASSETS)
    GAP_ASSETS.mkdir(parents=True, exist_ok=True)

    # 建索引：卡名 -> uasset 路径
    index = {}
    for f in ASSET_ROOT.rglob("*.uasset"):
        index.setdefault(f.stem, f)

    copied, missing = 0, []
    for n in names:
        src = index.get(n)
        if src is None:
            missing.append(n)
            continue
        # 保持相对目录结构（转译器会按目录组织输出）
        rel = src.relative_to(ASSET_ROOT)
        dst = GAP_ASSETS / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        copied += 1

    print(f"staging: 拷贝 {copied} 个 .uasset → {GAP_ASSETS}")
    if missing:
        print(f"  ⚠ 找不到资产的 {len(missing)} 张: {missing[:10]}")


def cmd_merge():
    """
    把转译产物搬进 ref/kards-sim/KardsSim/Generated/ 的正确位置。

    ⚠️ 为什么要「搬」而不是「按输出布局直接用」：
       转译器对 `--in` 之下的文件按相对路径建目录，但 `--sigdeps` 里的文件
       它一律写进 `_deps/`（扁平）。而转译同一张卡时，staging 副本会失败
       （ArgumentOutOfRangeException: count 为负 —— 脱离游戏包根目录后引用解析不出来），
       `--sigdeps` 那份反而成功。所以真正可用的是 `_deps/` 里的扁平文件，
       阵营目录要靠抽取结果里的 `asset` 字段反推。
    """
    # ⚠️ 不能用抽取结果里的 `asset` 字段 —— 实测它只存**卡名**（'card_event_pams'），
    #    不是资产路径。路径从真实资产树现推。
    name2rel = {}
    for f in ASSET_ROOT.rglob("*.uasset"):
        name2rel.setdefault(f.stem, f.relative_to(ASSET_ROOT).as_posix())

    src = ROOT / "out" / "Generated-gap2" / "_deps"
    if not src.exists():
        print(f"找不到 {src}")
        return

    names = [x.strip() for x in GAP_NAMES.read_text(encoding="utf-8").splitlines() if x.strip()]
    PREFIX = "kards/Content/Blueprints/Cards/"

    copied, missing_file, missing_asset = 0, [], []
    for n in names:
        f = src / f"{n}.g.cs"
        if not f.exists():
            missing_file.append(n)
            continue
        a = name2rel.get(n)
        if not a:
            missing_asset.append(n)
            continue
        if a.startswith(PREFIX):
            a = a[len(PREFIX):]
        a = a.rsplit(".", 1)[0] + ".g.cs"
        dst = GEN_THEIRS / a
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(f, dst)
        copied += 1

    print(f"搬运 {copied} 个 .g.cs → {GEN_THEIRS}")
    if missing_file:
        print(f"  ⚠ {len(missing_file)} 张没有转译产物: {missing_file[:8]}")
    if missing_asset:
        print(f"  ⚠ {len(missing_asset)} 张查不到 asset 路径: {missing_asset[:8]}")

    # ---- 合并 _index.g.cs ----
    new_index = ROOT / "out" / "Generated-gap2" / "_index.g.cs"
    old_index = GEN_THEIRS / "_index.g.cs"
    if not new_index.exists() or not old_index.exists():
        print("  ⚠ 缺少 _index.g.cs，跳过索引合并")
        return

    entry_re = re.compile(r'^\s*\["([^"]+)"\]\s*=\s*([A-Za-z0-9_]+)\.Registry\.Fns,\s*$')
    old_text = old_index.read_text(encoding="utf-8")
    new_text = new_index.read_text(encoding="utf-8")

    old_entries = {}
    for line in old_text.splitlines():
        m = entry_re.match(line)
        if m:
            old_entries[m.group(1)] = m.group(2)

    # 只补「我已经真搬进 Generated/ 的那些」—— 索引里多一条但文件没有会编译不过
    # ⚠️ 不能用 Path.stem：`card_event_pams.g.cs` 的 stem 是 `card_event_pams.g`
    #    （stem 只剥最后一层后缀）。这个坑让前面几次比对全部失效，误报成「0/118」。
    have_files = {f.name.removesuffix(".g.cs") for f in GEN_THEIRS.rglob("*.g.cs")
                  if not f.name.startswith("_")}
    added = {}
    for line in new_text.splitlines():
        m = entry_re.match(line)
        if m and m.group(1) not in old_entries and m.group(1) in have_files:
            added[m.group(1)] = m.group(2)

    if not added:
        print("  索引没有可补的新条目")
        return

    merged = dict(old_entries)
    merged.update(added)

    # ⚠️ 只在 `Assets` 字典的**收尾处插入新条目**，绝不重建整个块。
    #    第一版用 old_text.rindex("};") 当收尾 —— 那是**整个文件**最后一个 `};`，
    #    结果把 `FnIndex.Find` / `EventParams` 等后面的成员一起吞掉了，
    #    编译报一堆 “FnIndex 未包含 Find 的定义”。
    #    Dictionary 不关心顺序，所以直接追加即可。
    start = old_text.index("= new(StringComparer.Ordinal)")
    open_brace = old_text.index("{", start)
    close_match = re.compile(r'^[ \t]*\};[ \t]*$', re.M).search(old_text, open_brace)
    if close_match is None:
        print("  ⚠ 找不到 Assets 字典的收尾，放弃索引合并")
        return

    insert = "\n".join(f'        ["{k}"] = {v}.Registry.Fns,' for k, v in sorted(added.items()))
    old_index.write_text(
        old_text[: close_match.start()] + insert + "\n" + old_text[close_match.start():],
        encoding="utf-8",
    )
    print(f"  索引 {len(old_entries)} → {len(old_entries) + len(added)} 条（新增 {len(added)}）")
    for k in sorted(added)[:20]:
        print(f"      + {k}")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "list"
    {"list": cmd_list, "stage": cmd_stage, "merge": cmd_merge}[cmd]()
