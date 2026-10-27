# 生成帮助页顶部的「界面总览」图: 起程序 -> 加样例图纸 -> 截当前界面 -> 按控件真实位置画 1~6 编号
# 用法: python docs\shoot_help.py <程序exe目录> <真实dwg目录> <输出png> [暂存根目录]
# 编号位置取自页面元素的 boundingBox，界面改了以后重跑即可，不用手调坐标。
import json, os, shutil, subprocess, sys, time, tempfile, urllib.request
from playwright.sync_api import sync_playwright
from PIL import Image, ImageDraw, ImageFont

exe_dir, dwg_dir, out_png = sys.argv[1], sys.argv[2], os.path.abspath(sys.argv[3])
base_dir = sys.argv[4] if len(sys.argv) > 4 else None
work = tempfile.mkdtemp(prefix="dwgbp_help_")
stage = os.path.join(base_dir or work, "图纸")
shutil.rmtree(stage, ignore_errors=True)
os.makedirs(os.path.join(stage, "一层"), exist_ok=True)
os.makedirs(os.path.join(stage, "详图"), exist_ok=True)
dwgs = [f for f in os.listdir(dwg_dir) if f.lower().endswith(".dwg")][:3]
for i, f in enumerate(dwgs):
    sub = "一层" if i < 2 else "详图"
    shutil.copy(os.path.join(dwg_dir, f), os.path.join(stage, sub, f))

env = dict(os.environ, DWGBP_SETTINGS=os.path.join(work, "settings.ini"))
env.pop("DWGBP_ROOT", None)
log = open(os.path.join(work, "out.txt"), "w+")
proc = subprocess.Popen([os.path.join(exe_dir, "DwgBatchPlotTool.exe"), "--no-browser"],
                        stdout=log, stderr=subprocess.STDOUT, env=env, cwd=exe_dir)
url = None
for _ in range(60):
    time.sleep(0.5)
    log.flush(); log.seek(0)
    for line in log.read().splitlines():
        if line.startswith("URL: "):
            url = line[5:].strip()
    if url: break
if not url: sys.exit("没拿到 URL")
base = url.rsplit("index.html", 1)[0]

def post(action, body):
    req = urllib.request.Request(base + "api/" + action, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    urllib.request.urlopen(req, timeout=30).read()

post("add", {"dir": stage})

# 编号 -> (锚点选择器, 画法)；btn=画在控件上方正中，panel=画在面板右上角外沿
MARKS = [(1, "#btnStart", "btn"), (2, "#fileWrap", "panel"), (3, "#optPane", "panel"),
         (4, "#logPane", "panel"), (5, "#statusbar", "panel"), (6, "#btnHelp", "btn")]

try:
    with sync_playwright() as p:
        b = p.chromium.launch(channel="msedge", args=["--force-color-profile=srgb"])
        pg = b.new_page(viewport={"width": 1500, "height": 760}, device_scale_factor=2)
        pg.goto(url, wait_until="networkidle")
        pg.wait_for_selector("#files tbody tr", timeout=15000)
        if pg.locator("html").get_attribute("data-theme") != "dark":
            pg.click("#btnTheme")
        # 顶栏上方留一条空白，编号 1、6 才有地方画
        pg.add_style_tag(content="body{padding-top:28px}")
        time.sleep(1.8)
        boxes = {num: pg.locator(sel).first.bounding_box() for num, sel, _ in MARKS}
        pg.screenshot(path=out_png, full_page=True)
        b.close()
finally:
    proc.terminate(); proc.wait(timeout=10)
    log.close()

# 画编号
img = Image.open(out_png).convert("RGBA")
d = ImageDraw.Draw(img)
scale = 2  # device_scale_factor
try:
    font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 26)
except OSError:
    font = ImageFont.load_default()
r = 19
for num, _, kind in MARKS:
    box = boxes.get(num)
    if not box: continue
    if kind == "btn":
        cx = (box["x"] + box["width"] / 2) * scale
        cy = (box["y"] - 16) * scale
    else:
        cx = (box["x"] + box["width"] - 24) * scale
        cy = (box["y"] - 7) * scale
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(45, 128, 214, 255), outline=(255, 255, 255, 220), width=2)
    w = d.textlength(str(num), font=font)
    d.text((cx - w / 2, cy - 17), str(num), font=font, fill="white")
img.convert("RGB").save(out_png, optimize=True)
print("saved", out_png, os.path.getsize(out_png), img.size)
shutil.rmtree(work, ignore_errors=True)
if base_dir: shutil.rmtree(base_dir, ignore_errors=True)
