"""拖拽添加的端到端自测: 用 Playwright 造一个 drop 事件, 看文件是否进到程序列表里。

用法: python docs\\shoot_drop.py <程序exe目录> <样例dwg> [临时输出目录]
程序目录里要有 ODA\\ 和 Fonts\\ (bin\\ 就是)。
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # 控制台代码页是 936 时中文/✕ 会炸

EXE_DIR = os.path.abspath(sys.argv[1])
EXE = os.path.join(EXE_DIR, "DwgBatchPlotTool.exe")
DWG = os.path.abspath(sys.argv[2])
TMP = sys.argv[3] if len(sys.argv) > 3 else tempfile.mkdtemp(prefix="dwgbp_drop_")


def http(url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read().decode("utf-8")


def main():
    ini = os.path.join(TMP, "settings.ini")
    env = dict(os.environ, DWGBP_SETTINGS=ini)
    env.pop("DWGBP_ROOT", None)
    p = subprocess.Popen([EXE, "--no-browser"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         text=True, encoding="utf-8", errors="replace", env=env, cwd=EXE_DIR)
    url = None
    for _ in range(200):
        line = p.stdout.readline()
        if not line:
            break
        m = re.match(r"URL: (\S+)", line.strip())
        if m:
            url = m.group(1)
            break
    if not url:
        raise SystemExit("没拿到界面地址")
    base = url[: url.index("index.html")]
    api = base + "api/"

    from playwright.sync_api import sync_playwright

    with sync_playwright() as pw:
        br = pw.chromium.launch(channel="msedge")
        pg = br.new_page(viewport={"width": 1280, "height": 800})
        pg.goto(url)
        pg.wait_for_selector("#files")
        with open(DWG, "rb") as f:
            data = list(f.read())
        name = "拖入试验.dwg"
        pg.evaluate(
            """([name, data]) => {
                 const dt = new DataTransfer();
                 dt.items.add(new File([new Uint8Array(data)], name, { type: 'application/octet-stream' }));
                 for (const type of ['dragenter', 'dragover', 'drop']) {
                   const ev = new DragEvent(type, { dataTransfer: dt, bubbles: true, cancelable: true });
                   window.dispatchEvent(ev);
                 }
               }""",
            [name, data],
        )
        pg.wait_for_function(
            "(n) => Array.from(document.querySelectorAll('#files tbody tr')).some(r => r.textContent.indexOf(n) >= 0)",
            arg=name, timeout=60000,
        )
        rows = pg.eval_on_selector_all("#files tbody tr", "els => els.map(e => e.textContent)")
        br.close()

    print("列表行:", rows)
    st = json.loads(http(api + "state"))
    hit = [i for i in st["items"] if i["name"] == name]
    print("state 命中:", hit)
    assert hit, "拖入的文件没进列表"
    assert "dwgbp-drop" in hit[0]["dir"], "落盘位置不是临时目录: " + hit[0]["dir"]
    print("拖拽添加 OK")
    try:
        http(api + "quit", data=b"{}", headers={"Content-Type": "application/json"})
    except Exception:
        pass
    p.terminate()
    shutil.rmtree(hit[0]["dir"], ignore_errors=True)   # 只清这次拖拽落盘的目录


if __name__ == "__main__":
    main()
