import json
import pathlib
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(r"<repo-root>")
SCRIPT = ROOT / "out" / "audit" / "p0-deadpath.py"


def run(label, src, ir):
    out = subprocess.run([sys.executable, str(SCRIPT), "--src", str(src), "--ir", str(ir)],
                         capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    # 每次运行会打印"基线"和"现状"两段；这里取**后一段**（= 被 --src/--ir 指定的那一段）
    star = [l for l in out.splitlines() if "至少一条效果路径是死的" in l]
    ev = [l for l in out.splitlines() if "订阅了从不派发事件的卡" in l]
    pr = [l for l in out.splitlines() if "调用了未进派发表原语的卡" in l]
    print(f"{label}")
    print(f"   ①{ev[-1].split(':',1)[1].strip() if ev else '?'}")
    print(f"   ②{pr[-1].split(':',1)[1].strip() if pr else '?'}")
    print(f"   ★{star[-1].split(':',1)[1].strip() if star else '?'}")


old_src = ROOT / "out" / "_p0-baseline-src"
new_src = ROOT / "src" / "KLink.Bot"
old_ir = ROOT / "klink bot" / "docs" / "card-ir.json.bak-before-locals"
new_ir = ROOT / "klink bot" / "docs" / "card-ir.json"

run("A 真基线（旧源码 + 旧 IR）", old_src, old_ir)
run("B 只加第 3 族（旧源码 + 新 IR）—— 隔离出 locals 的贡献", old_src, new_ir)
run("C 第 1/2/4 族，无第 3 族（新源码 + 旧 IR）", new_src, old_ir)
run("D 全部四族（新源码 + 新 IR）", new_src, new_ir)
