# -*- coding: utf-8 -*-
"""重新生成本项目文档的全部界面配图（一次启动程序，连拍三张）。

用法:
    python docs\\shoot_docs.py [真实图纸目录(可选)]

输出:
    docs\\screenshot.png                       README 首图
    src\\DwgBatchPlotTool\\ui\\help-ui.png       帮助页「界面总览」(按控件真实位置画 1~6 编号)
    src\\DwgBatchPlotTool\\ui\\help-ctxmenu.png  帮助页 / README 的「右键批量菜单」实拍

不依赖本机有真实 DWG：不给图纸目录时，脚本会生成几份体积不同、带图框的最小 DXF
（用 .dwg 扩展名，尾部填充到接近真实图纸的大小）作为样例，界面只显示文件名/大小，
不出图、不解析文件内容。给了真实图纸目录就复制前 4 份 .dwg 并改成中文名。

编号位置取自页面元素的 boundingBox，界面列宽/布局改了以后重跑即可，不用手调坐标。
依赖: Python 3.14 那份装了 playwright + pillow 的环境，Edge 或 Playwright Chromium。
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request

from playwright.sync_api import sync_playwright
from PIL import Image, ImageDraw, ImageFont
from watermark import stamp_png   # 重拍后把隐藏声明写回 PNG 元数据

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE_DIR = os.path.join(ROOT, "bin")
EXE = os.path.join(EXE_DIR, "DwgBatchPlotTool.exe")
UI = os.path.join(ROOT, "src", "DwgBatchPlotTool", "ui")
SHOT_DIR = os.path.join(ROOT, "docs", "_shot")
STAGE = os.path.join(SHOT_DIR, "图纸")
OUT = {
    "readme": os.path.join(ROOT, "docs", "screenshot.png"),
    "help": os.path.join(UI, "help-ui.png"),
    "menu": os.path.join(UI, "help-ctxmenu.png"),
}
SCALE = 2                       # device_scale_factor
WIDTH = 1600                    # 比旧的 1440/1500 宽一点, 才放得下新增的「打印样式」列
SAMPLES = [                     # (子目录, 文件名, 目标字节数)
    ("住宅", "一层平面.dwg", 399 * 1024),
    ("住宅", "二层平面.dwg", 748 * 1024),
    ("办公", "立面图.dwg", 137 * 1024),
    ("办公", "总平面.dwg", 206 * 1024),
]
# 一个合法的最小 DXF（图框 + 闭合多段线）；EOF 之后的填充 ODA 不会读，只用来让「大小」列好看
MINI_DXF = (
    b"0\nSECTION\n2\nENTITIES\n"
    b"0\nLWPOLYLINE\n8\n0\n90\n5\n70\n1\n"
    b"10\n0\n20\n0\n10\n420\n20\n0\n10\n420\n20\n297\n10\n0\n20\n297\n10\n0\n20\n0\n"
    b"0\nENDSEC\n0\nEOF\n"
)

# 编号 -> (锚点选择器, 画法)；btn = 控件上方正中，panel = 面板右上角外沿
MARKS = [(1, "#btnStart", "btn"), (2, "#fileWrap", "panel"), (3, "#optPane", "panel"),
         (4, "#logPane", "panel"), (5, "#statusbar", "panel"), (6, "#btnHelp", "btn")]


def make_sample(path, size):
    with open(path, "wb") as f:
        f.write(MINI_DXF)
        if size > len(MINI_DXF):
            f.write(b"\n" + b" " * (size - len(MINI_DXF) - 1))


def prepare_stage(real_dwg_dir):
    shutil.rmtree(SHOT_DIR, ignore_errors=True)
    real = []
    if real_dwg_dir and os.path.isdir(real_dwg_dir):
        real = [os.path.join(real_dwg_dir, f) for f in os.listdir(real_dwg_dir)
                if f.lower().endswith(".dwg")][:len(SAMPLES)]
    for i, (sub, name, size) in enumerate(SAMPLES):
        d = os.path.join(STAGE, sub)
        os.makedirs(d, exist_ok=True)
        dst = os.path.join(d, name)
        if i < len(real):
            shutil.copy(real[i], dst)
        else:
            make_sample(dst, size)
    print("样例图纸:", len(SAMPLES), "份;", "真实" if real else "合成")


def start_server(env, log_path):
    log = open(log_path, "w+", encoding="utf-8", errors="replace")
    # 大小写重复的代理变量会让 .NET 的 EnvironmentVariables 抛异常, 启动前一律清掉
    for k in list(env):
        if k.lower() in ("http_proxy", "https_proxy", "all_proxy", "no_proxy"):
            env.pop(k, None)
    proc = subprocess.Popen([EXE, "--no-browser"], stdout=log,
                            stderr=subprocess.STDOUT, env=env, cwd=EXE_DIR)
    url = None
    for _ in range(120):
        time.sleep(0.5)
        log.flush()
        log.seek(0)
        for line in log.read().splitlines():
            if line.startswith("URL: "):
                url = line[5:].strip()
        if url:
            break
    if not url:
        proc.terminate()
        log.close()
        raise SystemExit("没拿到界面 URL, 看 " + log_path)
    return proc, log, url


def make_post(base):
    def post(action, body):
        req = urllib.request.Request(base + "api/" + action,
                                     data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
        return urllib.request.urlopen(req, timeout=30).read().decode()
    return post


def open_page(browser, url, height, pad=False):
    pg = browser.new_page(viewport={"width": WIDTH, "height": height},
                          device_scale_factor=SCALE)
    pg.goto(url, wait_until="domcontentloaded")
    pg.wait_for_selector("#files tbody tr", timeout=20000)
    pg.wait_for_function(
        "n => document.querySelectorAll('#files tbody tr').length === n",
        arg=len(SAMPLES), timeout=20000)
    if pad:
        pg.add_style_tag(content="body{padding-top:28px}")   # 顶栏上方留白, 编号 1 / 6 有地方画
    time.sleep(1.6)                                          # 等参数回填、参照扫描、样式候选稳定
    return pg


def draw_marks(png, boxes):
    img = Image.open(png).convert("RGBA")
    d = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 26)
    except OSError:
        font = ImageFont.load_default()
    r = 19
    for num, _, kind in MARKS:
        box = boxes.get(num)
        if not box:
            continue
        if kind == "btn":
            cx = (box["x"] + box["width"] / 2) * SCALE
            cy = (box["y"] - 16) * SCALE
        else:
            cx = (box["x"] + box["width"] - 24) * SCALE
            cy = (box["y"] - 7) * SCALE
        d.ellipse([cx - r, cy - r, cx + r, cy + r],
                  fill=(45, 128, 214, 255), outline=(255, 255, 255, 220), width=2)
        w = d.textlength(str(num), font=font)
        d.text((cx - w / 2, cy - 17), str(num), font=font, fill="white")
    img.convert("RGB").save(png, optimize=True)


def main():
    real_dwg_dir = sys.argv[1] if len(sys.argv) > 1 else None
    # 本机同时有 HTTP_PROXY 与 http_proxy (大小写重复): .NET 会崩, urllib / 浏览器也可能被代理劫持
    for k in list(os.environ):
        if k.lower() in ("http_proxy", "https_proxy", "all_proxy", "no_proxy"):
            os.environ.pop(k, None)
    prepare_stage(real_dwg_dir)
    work = tempfile.mkdtemp(prefix="dwgbp_docs_")
    env = dict(os.environ, DWGBP_SETTINGS=os.path.join(work, "settings.ini"))
    env.pop("DWGBP_ROOT", None)
    proc, log, url = start_server(env, os.path.join(work, "out.txt"))
    base = url.rsplit("index.html", 1)[0]
    post = make_post(base)

    try:
        post("save-settings", {"options": {"theme": "dark"}})   # 三张图统一深色, 和旧图一致
        post("add", {"dir": STAGE})

        with sync_playwright() as p:
            try:
                browser = p.chromium.launch(channel="msedge", args=["--force-color-profile=srgb"])
            except Exception:
                browser = p.chromium.launch(headless=True, args=["--force-color-profile=srgb"])

            # 1) README 首图: 完整窗口
            pg = open_page(browser, url, 900)
            pg.screenshot(path=OUT["readme"], full_page=True)
            pg.close()

            # 2) 帮助页总览: 顶部留白 + 1~6 编号
            pg = open_page(browser, url, 760, pad=True)
            boxes = {num: pg.locator(sel).first.bounding_box() for num, sel, _ in MARKS}
            pg.screenshot(path=OUT["help"], full_page=True)
            pg.close()
            draw_marks(OUT["help"], boxes)

            # 3) 右键批量菜单实拍: 选中两行 -> 右键 -> 裁一块「列表 + 菜单」出来
            pg = open_page(browser, url, 900)
            rows = pg.locator("#files tbody tr")
            rows.nth(0).locator("td").first.click()
            rows.nth(1).locator("td").first.click(modifiers=["Control"])
            rows.nth(1).locator("td").first.click(button="right")
            menu = pg.locator("#ctxMenu")
            menu.wait_for(state="visible", timeout=5000)
            mb = menu.bounding_box()
            tb = pg.locator("#files").bounding_box()
            # 从页面左上角起截: 顶栏/表头完整, 右边界到表格末尾 (11 列都进画面, 不切半列)
            w = min(WIDTH, int(tb["x"] + tb["width"]) + 12)
            h = min(900, max(520, int(mb["y"] + mb["height"]) + 48))
            pg.screenshot(path=OUT["menu"], clip={"x": 0, "y": 0, "width": w, "height": h})
            pg.close()
            browser.close()

        for key, path in OUT.items():
            # 所有裁剪/画编号都做完了再盖水印, 否则 PIL 另存会把 tEXt 丢掉
            stamp_png(path)
            with Image.open(path) as im:
                print("%-6s %-58s %s  %d bytes" % (key, os.path.relpath(path, ROOT), im.size,
                                                   os.path.getsize(path)))
    finally:
        try:
            post("quit", {})
        except Exception:
            pass
        try:
            proc.terminate()
            proc.wait(timeout=10)
        except Exception:
            pass
        log.close()
        shutil.rmtree(work, ignore_errors=True)
        shutil.rmtree(SHOT_DIR, ignore_errors=True)


if __name__ == "__main__":
    main()

# 本程序开源免费使用，严禁转卖，否则生儿子没屁眼。
