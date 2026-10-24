# 截 README 用的界面截图: 起程序 -> 加入几张样例图纸 -> 用 Edge 截图
# 用法: python docs\shoot.py <程序exe目录> <真实dwg目录> <输出png> [暂存根目录]
# 暂存根目录用于让截图里的路径好看 (不传就用系统临时目录)
import json, os, shutil, subprocess, sys, tempfile, time, urllib.request
from playwright.sync_api import sync_playwright

exe_dir, dwg_dir, out_png = sys.argv[1], sys.argv[2], os.path.abspath(sys.argv[3])
base_dir = sys.argv[4] if len(sys.argv) > 4 else None
work = tempfile.mkdtemp(prefix="dwgbp_shot_")           # 设置文件和日志放临时目录
stage = os.path.join(base_dir or work, "图纸")           # 样例图纸放在这里, 截图里就是它
shutil.rmtree(stage, ignore_errors=True)
os.makedirs(os.path.join(stage, "住宅"), exist_ok=True)
os.makedirs(os.path.join(stage, "办公"), exist_ok=True)
dwgs = [f for f in os.listdir(dwg_dir) if f.lower().endswith(".dwg")][:4]
names = ["一层平面.dwg", "二层平面.dwg", "立面图.dwg", "总平面.dwg"]
for i, f in enumerate(dwgs):
    sub = "住宅" if i < 2 else "办公"
    shutil.copy(os.path.join(dwg_dir, f), os.path.join(stage, sub, names[i]))

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
if not url:
    sys.exit("没拿到 URL")
base = url.rsplit("index.html", 1)[0]

def post(action, body):
    req = urllib.request.Request(base + "api/" + action, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    urllib.request.urlopen(req, timeout=30).read()

post("add", {"dir": stage})
post("save-settings", {"outputDir": os.path.join(work, "输出")})

try:
    with sync_playwright() as p:
        b = p.chromium.launch(channel="msedge", args=["--force-color-profile=srgb"])
        pg = b.new_page(viewport={"width": 1440, "height": 900}, device_scale_factor=2)
        pg.goto(url, wait_until="networkidle")
        pg.wait_for_selector("#files tbody tr", timeout=15000)
        time.sleep(1.5)
        os.makedirs(os.path.dirname(out_png), exist_ok=True)
        pg.screenshot(path=out_png, full_page=True)
        print("saved", out_png, os.path.getsize(out_png))
        b.close()
finally:
    proc.terminate(); proc.wait(timeout=10)
    log.close()
    shutil.rmtree(work, ignore_errors=True)
