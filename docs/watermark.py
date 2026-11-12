# -*- coding: utf-8 -*-
"""DwgBatchPlotTool 隐藏水印: 注入(幂等)与检查。

用法:
    python docs/watermark.py apply   # 给源码/文档/PNG 补上隐藏水印
    python docs/watermark.py check   # 检查所有位置是否还在, 缺一项退出码 1

水印是同一句声明, 分散藏在:
  * 源码 / 文档 / 脚本的注释行 (界面与控制台都看不到)
  * 两个 C# 项目的 [assembly: AssemblyMetadata] -> 编译进两个 exe (见各项目 Program.cs)
  * index.html / help.html 的 hidden DOM 节点 (DOM 里有, 页面不渲染)
  * 四张 PNG 的文本元数据 tEXt / iTXt (像素不变, 显示不变)
输出的 PDF、settings.ini 等用户数据不写水印。
"""
import os
import sys
from pathlib import Path

NOTICE = "本程序开源免费使用，严禁转卖，否则生儿子没屁眼。"
KEY = "Comment"
ROOT = Path(__file__).resolve().parent.parent

# 需要以注释形式藏一份的文件 (相对项目根; 不存在的自动跳过)
TEXT_FILES = """
README.md
build.bat
docs/shoot_docs.py
docs/shoot_drop.py
docs/watermark.py
src/DwgBatchPlotTool/DwgBatchPlotTool.csproj
src/DwgBatchPlotTool/Program.cs
src/DwgBatchPlotTool/HtmlHost.cs
src/DwgBatchPlotTool/BatchRunner.cs
src/DwgBatchPlotTool/OdaRunner.cs
src/DwgBatchPlotTool/PlotOptions.cs
src/DwgBatchPlotTool/RefScan.cs
src/DwgBatchPlotTool/Settings.cs
src/DwgBatchPlotTool/ui/app.js
src/DwgBatchPlotTool/ui/index.html
src/DwgBatchPlotTool/ui/style.css
src/DwgBatchPlotTool/ui/help.html
src/ODADrawingTools/ODADrawingTools.csproj
src/ODADrawingTools/Program.cs
src/ODADrawingTools/DwgDxfConverter.cs
tests/Selftest/Program.cs
tests/Selftest/_ctx_check.py
tests/Selftest/_col_check.py
""".split()

PNG_FILES = """
docs/screenshot.png
src/DwgBatchPlotTool/ui/help-ui.png
src/DwgBatchPlotTool/ui/help-ctxmenu.png
src/DwgBatchPlotTool/ui/donate.png
""".split()

EXE_FILES = """
bin/DwgBatchPlotTool.exe
ODA/ODADrawingTools.exe
""".split()


def line_for(path):
    """按文件类型返回一整行注释; 判断"已注入"时也用这行做标记。"""
    s = path.suffix.lower()
    if s in (".cs", ".js"):
        return "// " + NOTICE
    if s == ".py":
        return "# " + NOTICE
    if s == ".bat":
        return "rem " + NOTICE
    if s == ".css":
        return "/* " + NOTICE + " */"
    if s in (".html", ".md", ".csproj"):
        return "<!-- " + NOTICE + " -->"
    raise ValueError("未知文件类型: " + str(path))


def apply_text(rel):
    p = ROOT / rel
    if not p.exists():
        return "跳过(文件不存在)"
    data = p.read_bytes()
    line = line_for(p).encode("utf-8")
    if line in data:
        return "已有"
    eol = b"\r\n" if b"\r\n" in data else b"\n"
    if p.name == "index.html" and b"<head>" in data:
        # doctype 之前不能插注释; 放 head 里最稳
        data = data.replace(b"<head>", b"<head>" + eol + line, 1)
    else:
        # 一律追加到文件末尾: 不破坏任何语言的语法、字符串或已有缩进
        data = data.rstrip(b"\r\n") + eol + eol + line + eol
    p.write_bytes(data)
    return "已注入"


def stamp_png(path):
    """给 PNG 写文本元数据; 非 Latin-1 内容 Pillow 自动用 iTXt 存, 保留其它字段, 像素不变。"""
    from PIL import Image, PngImagePlugin
    with Image.open(path) as im:
        im.load()
        img = im.copy()
        extra = {k: v for k, v in im.info.items()
                 if isinstance(v, str) and k != KEY}
    meta = PngImagePlugin.PngInfo()
    for k, v in extra.items():
        meta.add_text(k, v)
    meta.add_text(KEY, NOTICE)
    tmp = str(path) + ".wm.tmp"
    img.save(tmp, format="PNG", pnginfo=meta)
    os.replace(tmp, path)


def check_text(rel):
    p = ROOT / rel
    if not p.exists():
        return True, "跳过(文件不存在)"
    return line_for(p).encode("utf-8") in p.read_bytes(), ""


def check_png(rel):
    from PIL import Image
    p = ROOT / rel
    if not p.exists():
        return True, "跳过(文件不存在)"
    with Image.open(p) as im:
        ok = any(isinstance(v, str) and NOTICE in v for v in im.info.values())
    return ok, ""


def check_exe(rel):
    p = ROOT / rel
    if not p.exists():
        return True, "跳过(未构建)"
    b = p.read_bytes()
    return (NOTICE.encode("utf-8") in b or NOTICE.encode("utf-16-le") in b), ""


def run_check():
    groups = [("源文件/文档注释", TEXT_FILES, check_text),
              ("PNG 文本元数据", PNG_FILES, check_png),
              ("exe 内嵌声明", EXE_FILES, check_exe)]
    missing = []
    total = 0
    for title, files, fn in groups:
        print("[%s]" % title)
        for rel in files:
            total += 1
            ok, note = fn(rel)
            if not ok:
                missing.append(rel)
            print("  %s %-58s %s" % ("[OK]  " if ok else "[MISS]", rel, note))
    print("\n水印检查: %d 处, 缺失 %d 处" % (total, len(missing)))
    if missing:
        for rel in missing:
            print("  - " + rel)
        print("先跑: python docs/watermark.py apply  (exe 缺失时先跑 build.bat)")
        return 1
    print("全部到位")
    return 0


def run_apply():
    for rel in TEXT_FILES:
        p = ROOT / rel
        if p.exists():
            print("  %-58s %s" % (rel, apply_text(rel)))
    for rel in PNG_FILES:
        p = ROOT / rel
        if not p.exists():
            print("  %-58s 跳过(文件不存在)" % rel)
            continue
        stamp_png(p)
        print("  %-58s 已写入文本元数据" % rel)
    print("\napply 完成; 接着跑 build.bat 把程序集元数据与内嵌资源编进 exe, 再跑 check。")
    return 0


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "check"
    if cmd == "apply":
        sys.exit(run_apply())
    if cmd == "check":
        sys.exit(run_check())
    print("用法: python docs/watermark.py apply|check")
    sys.exit(2)
# 本程序开源免费使用，严禁转卖，否则生儿子没屁眼。
