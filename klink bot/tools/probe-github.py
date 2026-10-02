"""探测走本地代理时哪些 GitHub 端点可达。"""
import ssl
import sys
import urllib.error
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

PROXY = "http://127.0.0.1:7877"
CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE

OPENER = urllib.request.build_opener(
    urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}),
    urllib.request.HTTPSHandler(context=CTX),
)

URLS = [
    "https://api.github.com/repos/CCB-TEAM/kards-sim",
    "https://api.github.com/repos/CCB-TEAM/fyserver",
    "https://github.com/CCB-TEAM/kards-sim",
    "https://raw.githubusercontent.com/CCB-TEAM/kards-sim/main/README.md",
    "https://codeload.github.com/CCB-TEAM/kards-sim/tar.gz/refs/heads/main",
]

for u in URLS:
    try:
        req = urllib.request.Request(u, headers={"User-Agent": "probe/1.0"})
        with OPENER.open(req, timeout=60) as resp:
            body = resp.read()
            print("OK   %-72s %s  %d bytes" % (u, resp.status, len(body)))
            if len(body) < 400:
                print("      ", body[:300])
    except urllib.error.HTTPError as e:
        print("HTTP %-3s %-72s %s" % (e.code, u, e.reason))
        try:
            print("      ", e.read()[:300])
        except Exception:
            pass
    except Exception as e:
        print("ERR  %-72s %s: %s" % (u, type(e).__name__, e))
