"""把 CCB-TEAM/kards-sim 拉下来做参照。

背景：本机 git 走 schannel 过不了代理（SEC_E_NO_CREDENTIALS），
但 Python 的 ssl 是另一套实现、能正常访问（同 tools 目录下的 fetch_nuget.py）。

用法:
  python tools/fetch-kards-sim.py            # 默认下 main 分支 tarball 并解包到 ref/kards-sim
  python tools/fetch-kards-sim.py --branch dev
  python tools/fetch-kards-sim.py --list     # 只列目录树，不下载
"""
import argparse
import io
import json
import os
import shutil
import ssl
import sys
import tarfile
import time
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = "CCB-TEAM/kards-sim"
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT = os.path.join(ROOT, "ref", "kards-sim")

# 本机 git 的 http.proxy 指向这个本地代理（clash/v2ray 之类）。
# Python 默认不读 git 配置，所以这里显式接上 —— 不接就是直连被 RST。
PROXY = os.environ.get("KARDS_REF_PROXY", "http://127.0.0.1:7877")

CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE  # 代理是 MITM 的，环境证书链不受信

if PROXY:
    OPENER = urllib.request.build_opener(
        urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}),
        urllib.request.HTTPSHandler(context=CTX),
    )
else:
    OPENER = urllib.request.build_opener(urllib.request.HTTPSHandler(context=CTX))


def fetch(url: str, tries: int = 8) -> bytes:
    """代理不太稳定（同一个 URL 时好时坏），所以必须重试。"""
    last = None
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={
                "User-Agent": "klink-ref-fetch/1.0",
                "Accept": "application/vnd.github+json",
            })
            with OPENER.open(req, timeout=180) as resp:
                body = resp.read()
                if body:
                    return body
                last = Exception("空响应")
        except Exception as ex:
            last = ex
        time.sleep(1.0 + i * 0.5)
    raise RuntimeError(f"{tries} 次都失败: {type(last).__name__}: {last}")


def list_tree(branch: str):
    """用 git trees API 列出整个仓库（递归）。"""
    url = f"https://api.github.com/repos/{REPO}/git/trees/{branch}?recursive=1"
    data = json.loads(fetch(url).decode("utf-8"))
    if "tree" not in data:
        print("API 返回异常：", json.dumps(data, ensure_ascii=False)[:500])
        return
    entries = data["tree"]
    print(f"共 {len(entries)} 个条目（truncated={data.get('truncated')}）")
    for e in sorted(entries, key=lambda x: x["path"]):
        if e["type"] == "blob":
            print("  %-80s %8d" % (e["path"], e.get("size", 0)))
        else:
            print("  %s/" % e["path"])


def download(branch: str):
    url = f"https://codeload.github.com/{REPO}/tar.gz/refs/heads/{branch}"
    print(f"下载 {url}")
    blob = fetch(url)
    print(f"  收到 {len(blob):,} 字节")

    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT, exist_ok=True)

    with tarfile.open(fileobj=io.BytesIO(blob), mode="r:gz") as tf:
        members = tf.getmembers()
        # tarball 顶层是 kards-sim-<branch>/，剥掉它
        top = members[0].name.split("/")[0] + "/"
        count = 0
        for m in members:
            if not m.name.startswith(top):
                continue
            m.name = m.name[len(top):]
            if not m.name:
                continue
            tf.extract(m, OUT, filter="data")
            count += 1
    print(f"  解包 {count} 个条目 → {OUT}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--branch", default="main")
    ap.add_argument("--list", action="store_true", help="只列目录树")
    args = ap.parse_args()

    try:
        if args.list:
            list_tree(args.branch)
        else:
            download(args.branch)
    except Exception as ex:
        print(f"失败: {type(ex).__name__}: {ex}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
