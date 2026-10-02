"""
查 kards-sim 有没有新推送。

直连 GitHub API 之前报 SSL 失败，所以这里把所有可能的走法都试一遍：
  1. 环境里常见的代理端口（7897 / 7877 / 7890 / 10809 / 1080）
  2. 不挂代理直连
  3. 走 curl（它有自己的 TLS 栈和证书处理，经常能过）

只看**最新提交的 sha 和时间**，不下载任何东西 —— 避免覆盖我本地已经合并过
（75 张缺口卡 + _index.g.cs 新增 + cards.json 2023 张）的那份。
"""
import json
import os
import ssl
import subprocess
import urllib.request

API = "https://api.github.com/repos/CCB-TEAM/kards-sim/commits?per_page=5"
CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE

PROXIES = ["http://127.0.0.1:7897", "http://127.0.0.1:7877",
           "http://127.0.0.1:7890", "http://127.0.0.1:10809",
           "http://127.0.0.1:1080", None]


def try_urllib(proxy):
    handlers = [urllib.request.HTTPSHandler(context=CTX)]
    if proxy:
        handlers.insert(0, urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
    else:
        handlers.insert(0, urllib.request.ProxyHandler({}))
    op = urllib.request.build_opener(*handlers)
    op.addheaders = [("User-Agent", "klink-check")]
    with op.open(API, timeout=20) as r:
        return json.loads(r.read().decode("utf-8"))


def try_curl(proxy):
    cmd = ["curl", "-s", "--max-time", "20", "-k", "-A", "klink-check"]
    if proxy:
        cmd += ["-x", proxy]
    cmd.append(API)
    out = subprocess.run(cmd, capture_output=True, text=True).stdout
    return json.loads(out) if out.strip().startswith("[") else None


def report(commits, how):
    print(f"  ✓ 成功（{how}）")
    for c in commits[:5]:
        sha = c["sha"][:10]
        date = c["commit"]["author"]["date"]
        msg = c["commit"]["message"].split("\n")[0][:70]
        print(f"      {date}  {sha}  {msg}")
    return True


print("=== 试各种走法 ===")
ok = False
for p in PROXIES:
    label = p or "直连"
    try:
        r = try_urllib(p)
        if r and report(r, f"urllib + {label}"):
            ok = True
            break
    except Exception as e:
        print(f"  ✗ urllib + {label}: {str(e)[:70]}")

if not ok:
    for p in PROXIES:
        label = p or "直连"
        try:
            r = try_curl(p)
            if r and report(r, f"curl + {label}"):
                ok = True
                break
        except Exception as e:
            print(f"  ✗ curl + {label}: {str(e)[:70]}")

if not ok:
    print("\n  所有走法都不通。检查代理软件是否开着（clash/v2ray）。")
