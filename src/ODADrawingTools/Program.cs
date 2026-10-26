// ODADrawingTools — ODA/Teigha 4.02 出图引擎 (控制台程序)
// 作者: 唐明龙 (QQ 979123550); 基于 ODA 官方 ODADrawingTools 示例
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using Teigha.Runtime;
using Teigha.DatabaseServices;
using Teigha.Export_Import;
using Teigha.GraphicsSystem;
using Teigha.Geometry;

namespace ODADrawingTools
{
  // 最小化的宿主服务实现：仅解析 .ctb/.stb 打印样式表路径，其余交给默认行为。
  class HostApp : HostApplicationServices
  {
    // 字体文件名 → {英文面名, 中文名} 映射表
    // （复制自主程序 DxfWriter.cpp 出图时写 DXF 的 F2FMap 表，两端保持一致；
    //   PDF 转换端用它在系统字体目录/注册表中把样式引用的 TTF 文件名解析为实际字体文件）
    static readonly Dictionary<string, string[]> FontFileFaceMap =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
      { "simsun.ttf",         new[] { "SimSun",        "宋体" } },
      { "simhei.ttf",         new[] { "SimHei",        "黑体" } },
      { "simkai.ttf",         new[] { "KaiTi_GB2312",  "楷体_GB2312" } },
      { "simli.ttf",          new[] { "LiSu",          "隶书" } },
      { "stliti.ttf",         new[] { "STLiti",        "华文隶书" } },
      { "simfang_0.ttf",      new[] { "FangSong_GB2312", "仿宋_GB2312" } },
      { "simyou.ttf",         new[] { "YouYuan",       "幼圆" } },
      { "stcaiyun.ttf",       new[] { "STCaiyun",      "华文彩云" } },
      { "stfangso.ttf",       new[] { "STFangsong",    "华文仿宋" } },
      { "stxihei.ttf",        new[] { "STXihei",       "华文细黑" } },
      { "stxingka.ttf",       new[] { "STXingkai",     "华文行楷" } },
      { "stxinwei.ttf",       new[] { "STXinwei",      "华文新魏" } },
      { "stzhongs.ttf",       new[] { "STZhongsong",   "华文中宋" } },
      { "fzstk.ttf",          new[] { "FZShuTi",       "方正舒体" } },
      { "fzytk.ttf",          new[] { "FZYaoTi",       "方正姚体" } },
      { "sursong.ttf",        new[] { "Simsun (Founder Extended)", "宋体-方正超大字符集" } },
      { "nsimsun.ttf",        new[] { "NSimSun",       "新宋体" } },
      { "microsoft yahei ui", new[] { "Microsoft YaHei UI", "Microsoft YaHei UI" } }
    };

    // 额外字体搜索目录（多个用 ; 分隔）：
    // 由主程序通过环境变量 DWGBP_FONTS 传入 —— 界面上的「字体目录」选项，
    // 用来指定非系统字体（方正/华文/THFont 等 SHX、TTF）所在文件夹。
    public static readonly List<string> ExtraFontDirs = new List<string>();

    // 按映射表把 TTF 文件名解析为系统实际字体文件：
    // 1) 英文面名直接当文件名试 (SimSun.ttf → SimSun.ttf/.ttc)
    // 2) 查注册表 HKLM\...\Fonts：值名以英文面名或中文名开头 → 字体文件
    //    (宋体 → simsun.ttc, Microsoft YaHei UI → msyh.ttc 等)
    // 找不到返回 null
    static string ResolveSystemFontByFace(string file)
    {
      try
      {
        string key = Path.GetFileNameWithoutExtension(file).Trim().ToLowerInvariant();
        string[] face;
        if (!FontFileFaceMap.TryGetValue(key, out face))
          return null;

        string winFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        if (!Directory.Exists(winFonts)) winFonts = @"C:\Windows\Fonts";

        // 1) 英文面名直接当文件名
        foreach (string e in new[] { ".ttf", ".ttc", ".otf" })
        {
          string p = Path.Combine(winFonts, face[0] + e);
          if (File.Exists(p)) return p;
        }

        // 2) 注册表 面名/中文名 → 字体文件
        //    第一遍按前缀精确匹配("宋体 (TrueType)"、"SimSun & NSimSun (TrueType)")；
        //    第二遍按包含匹配(注册表里族名次序不定，如
        //    "Microsoft YaHei & Microsoft YaHei UI (TrueType)" 中 UI 是第二名字)
        using (Microsoft.Win32.RegistryKey rk =
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
        {
          if (rk == null) return null;
          string best = null;
          foreach (int pass in new[] { 0, 1 })
          {
            foreach (string name in rk.GetValueNames())
            {
              string data = rk.GetValue(name) as string;
              if (string.IsNullOrEmpty(data)) continue;
              string e = Path.GetExtension(data).ToLowerInvariant();
              if (e != ".ttf" && e != ".ttc" && e != ".otf") continue;
              foreach (string f in face)
              {
                bool hit = (pass == 0)
                    ? (name.StartsWith(f + " (", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith(f + " &", StringComparison.OrdinalIgnoreCase))
                    : name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) { best = data; break; }
              }
              if (best != null) break;
            }
            if (best != null) break;
          }
          if (best != null)
          {
            string full = Path.Combine(winFonts, best);
            if (File.Exists(full))
            {
              Console.Error.WriteLine("INFO: TTF '" + file + "' 经字体映射表解析为 " + full);
              return full;
            }
          }
        }
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("WARN: 字体映射表解析失败: " + ex.Message);
      }
      return null;
    }

    public override string FindFile(string file, Database db, FindFileHint hint)
    {
      // 按 hint 判断文件类别, 不依赖扩展名 (字体查找时文件名可能不带扩展名)
      bool isFont = hint == FindFileHint.FontFile
                 || hint == FindFileHint.CompiledShapeFile
                 || hint == FindFileHint.TrueTypeFontFile;
      bool isPlotStyle = false;
      if (!isFont)
      {
        string ext = Path.GetExtension(file).ToUpperInvariant();
        isPlotStyle = (ext == ".CTB" || ext == ".STB");
      }

      if (isPlotStyle)
      {
        string env = Environment.GetEnvironmentVariable("DDPLOTSTYLEPATHS") ?? "";
        foreach (string dir in env.Split(';'))
        {
          if (dir.Length == 0) continue;
          string p = Path.Combine(dir, file);
          if (File.Exists(p)) return p;
        }
      }
      else if (isFont)
      {
        // 字体搜索: 界面指定的字体目录(DWGBP_FONTS) → exe 同目录及其 Fonts 子目录
        // → 主程序 exe 目录下的 Fonts 子目录 → exe 同目录
        // (工具部署于 主程序路径/ODA/ 下, 主程序字体位于 主程序路径/Fonts/)
        // 含大小写不敏感兜底
        string exeDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        string mainDir = "";
        try { mainDir = Path.GetDirectoryName(exeDir) ?? ""; } catch { }
        List<string> dirs = new List<string>();
        foreach (string d in ExtraFontDirs) if (d.Length > 0) dirs.Add(d);
        dirs.Add(Path.Combine(exeDir, "Fonts"));
        dirs.Add(Path.Combine(mainDir, "Fonts"));
        dirs.Add(exeDir);
        foreach (string dir in dirs)
        {
          string p = Path.Combine(dir, file);
          if (File.Exists(p)) return p;
        }
        foreach (string dir in dirs)
        {
          if (!Directory.Exists(dir)) continue;
          foreach (string f in Directory.GetFiles(dir))
          {
            if (string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase))
              return f;
          }
        }
        // TTF 找不到时回退 Windows 系统字体目录：
        // 图纸常写 "SimSun.ttf"，而系统里实际是 simsun.ttc（扩展名不同），
        // 按基名匹配 ttf/ttc/otf，避免 TTF 文字因找不到字体而乱码/丢字
        Console.Error.WriteLine("INFO: [FindFile] font请求: '" + file + "'");
        string ext2 = Path.GetExtension(file).ToLowerInvariant();
        if (ext2 == ".ttf" || ext2 == ".ttc" || ext2 == ".otf")
        {
          string winFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
          if (!Directory.Exists(winFonts)) winFonts = @"C:\Windows\Fonts";
          if (Directory.Exists(winFonts))
          {
            string baseName = Path.GetFileNameWithoutExtension(file);
            foreach (string f in Directory.GetFiles(winFonts, baseName + ".*"))
            {
              string e = Path.GetExtension(f).ToLowerInvariant();
              if (e == ".ttf" || e == ".ttc" || e == ".otf") return f;
            }
          }
        }
        // 基名仍找不到 → 按"字体文件名→字体族"映射表（复制自主程序 DxfWriter 的 F2FMap）
        // 经注册表把英文面名/中文名解析为系统实际字体文件
        // （可处理 "SimSun.ttf"↔simsun.ttc、"Microsoft YaHei UI"(无扩展名)↔msyh.ttc 等文件名不一致的情形）
        string byFace = ResolveSystemFontByFace(file);
        if (byFace != null) return byFace;
      }
      // 其它文件类型（外部参照 xref、图像等）：
      // 1) 先按传入路径（通常为图纸中记录的绝对路径）直接查找
      if (File.Exists(file)) return file;
      // 2) 再按被加载图纸所在目录查找（仅取文件名）
      string dbFile = "";
      try { dbFile = db != null ? db.Filename : ""; } catch { }
      string dbDir = "";
      try { dbDir = Path.GetDirectoryName(dbFile) ?? ""; } catch { }
      if (dbDir.Length > 0)
      {
        string p = Path.Combine(dbDir, Path.GetFileName(file));
        if (File.Exists(p)) return p;
      }
      // 3) ODA 默认实现为抽象方法不可调用；未找到返回空串
      return "";
    }
    public override bool GetPassword(string dwgName, PasswordOptions options, out string pass) { pass = ""; return false; }
  }

  class Program
  {
    static int Main(string[] args)
    {
      Console.OutputEncoding = Encoding.UTF8;

      // 默认静默所有控制台输出(STEP/INFO/WARN/OK/ERROR 等日志全部关闭，
      // 成功失败仅以退出码表达，便于被主程序静默调用)。
      // 需要查看日志时加 --verbose；--help/--h 仍打印用法。
      bool verbose = Array.Exists(args, a => a == "--verbose" || a == "--log"
          || a == "--help" || a == "-h" || a == "--list-refs");
      if (!verbose)
      {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
      }

      // 字体目录（可选，多个用 ; 分隔）：主程序界面上的「字体目录」选项传进来
      try
      {
        string fonts = Environment.GetEnvironmentVariable("DWGBP_FONTS") ?? "";
        foreach (string d in fonts.Split(';'))
        {
          string t = d.Trim();
          if (t.Length > 0) HostApp.ExtraFontDirs.Add(t);
        }
      }
      catch { }

      // ---------- 格式转换子命令 ----------
      // ODADrawingTools.exe --convert <in.dwg|.dxf> <out.dxf|.dwg> [--version ACAD2013]
      // ODADrawingTools.exe --convert-batch <输入目录|通配符> <输出目录> --ext dwg|dxf [--version ACAD2013]
      if (args.Length > 0)
      {
        string c0 = args[0].ToLowerInvariant();
        if (c0 == "--convert" || c0 == "-c")
        {
          RunConvert(args);
          return 0;
        }
        if (c0 == "--convert-batch" || c0 == "-cb")
        {
          RunConvertBatch(args);
          return 0;
        }
        // ---------- 导出文字样式表子命令 ----------
        // ODADrawingTools.exe --dump-styles <in.dwg|.dxf> <out.tsv>
        // 输出每行 "样式名<TAB>宽度因子"，供主程序在 ODA DxfOut 丢失样式宽度因子后恢复
        // （实测 图框_Ref.dwg 的 TEXT1 样式 XScale=0.75，转出的 2004 DXF 中组码41变为1）。
        if (c0 == "--dump-styles" || c0 == "-ds")
        {
          return RunDumpStyles(args);
        }
        // ---------- 导出文字实体表子命令 ----------
        // ODADrawingTools.exe --dump-texts <in.dwg|.dxf> <out.tsv>
        // 输出 TEXT 实体的对齐/位置/宽度因子，用于核对 ODA DxfOut 是否丢失对齐信息
        if (c0 == "--dump-texts" || c0 == "-dt")
        {
          return RunDumpTexts(args);
        }
        // ---------- 按参照图框分页打印子命令 ----------
        // 单文件:  ODADrawingTools.exe --plot <in.dwg|.dxf> <out.pdf> [--ref 名称] [--space model|layout|both] [--mono] [--ctb x] [--paper W H] [--title T] [--author A]
        // 批量(每文件一PDF): ODADrawingTools.exe --plot-batch <输出目录> [--ref 名称] [--space ...] [--mono] [--ctb x] <in1> <in2> ...
        // 批量(合并一PDF):   ODADrawingTools.exe --plot-merge <out.pdf> [--ref 名称] [--space ...] [--mono] [--ctb x] <in1> <in2> ...
        if (c0 == "--plot")
        {
          return RunPlot(args);
        }
        if (c0 == "--plot-batch")
        {
          return RunPlotBatch(args);
        }
        if (c0 == "--plot-merge")
        {
          return RunPlotMerge(args);
        }
        // ---------- 合并已生成的 PDF ----------
        // ODADrawingTools.exe --merge-list <输出.pdf> <列表文件>
        // 列表文件每行一个 PDF 路径（UTF-8，# 开头为注释）。
        // 用文件传参避免 Windows 命令行 32K 长度上限，且不需要加载 ODA 运行时。
        if (c0 == "--merge-list")
        {
          return RunMergeList(args);
        }
        // ---------- 列出图纸中的块参照（供界面选择参照图框名） ----------
        // ODADrawingTools.exe --list-refs <in.dwg|.dxf>
        // 每行: 名称<TAB>实例数<TAB>所在空间<TAB>宽<TAB>高（图纸单位）
        if (c0 == "--list-refs")
        {
          return RunListRefs(args);
        }
      }
      try
      {
        // ---------- 解析命令行 ----------
        string input = null, output = null, ctb = "", title = "DWG to PDF", author = "dwg2pdf";
        bool mono = false, embedTtf = false, layers = true, dxf = false;
        double pw = 0, ph = 0;

        for (int i = 0; i < args.Length; i++)
        {
          string a = args[i];
          if (a == "--mono") mono = true;
          else if (a == "--embed-ttf") embedTtf = true;
          else if (a == "--no-layers") layers = false;
          else if (a == "--dxf") dxf = true;
          else if (a == "--ctb" && i + 1 < args.Length) ctb = args[++i];
          else if (a == "--title" && i + 1 < args.Length) title = args[++i];
          else if (a == "--author" && i + 1 < args.Length) author = args[++i];
          else if (a == "--paper" && i + 2 < args.Length)
          {
            double.TryParse(args[++i], out pw);
            double.TryParse(args[++i], out ph);
          }
          else if (a == "--verbose" || a == "--log") { /* 仅控制日志开关, 此处忽略 */ }
          else if (a == "--help" || a == "-h")
          {
            PrintUsage();
            return 0;
          }
          else if (input == null) input = a;
          else if (output == null) output = a;
          else { Console.Error.WriteLine("未知参数: " + a); PrintUsage(); return 2; }
        }

        if (input == null || output == null)
        {
          PrintUsage();
          return 2;
        }
        if (!File.Exists(input))
        {
          Console.Error.WriteLine("ERROR: 输入文件不存在: " + input);
          return 1;
        }

        // --mono 没有显式 ctb 时, 尝试找 monochrome.ctb / monochrome.stb
        if (mono && ctb.Length == 0)
        {
          string m = FindMonoStyle();
          if (m.Length > 0) ctb = m;
          else Console.Error.WriteLine("WARN: --mono 已设置但未找到 monochrome.ctb/monochrome.stb, 将输出彩色 PDF。");
        }

        // ---------- 初始化 ODA/DWGdirect 运行时 ----------
        Console.Error.WriteLine("STEP: 初始化 ODA 运行时...");
        Services dd = new Services();
        SystemObjects.DynamicLinker.LoadApp("PlotSettingsValidator", false, false);
        HostApplicationServices.Current = new HostApp();

        // ---------- 读取 DWG / DXF ----------
        using (Database database = new Database(false, false))
        {
          if (dxf || input.ToUpperInvariant().EndsWith(".DXF"))
          {
            Console.Error.WriteLine("STEP: DxfIn 读取中...");
            database.DxfIn(input, "");
            Console.Error.WriteLine("STEP: DxfIn 完成");
          }
          else
          {
            Console.Error.WriteLine("STEP: ReadDwgFile 读取中...");
            database.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, false, "");
            Console.Error.WriteLine("STEP: ReadDwgFile 完成");
          }

          HostApplicationServices.WorkingDatabase = database;
          ApplyNonPlotLayersOff(database);
          FixEmptyTextStyles(database);
          NormalizeTextJustification(database);

          // ---------- 应用 CTB / STB 打印样式表 ----------
          if (ctb.Length > 0)
          {
            string dir = Path.GetDirectoryName(Path.GetFullPath(ctb));
            string cur = Environment.GetEnvironmentVariable("DDPLOTSTYLEPATHS") ?? "";
            if (cur.Length > 0 && cur.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
              cur = dir + ";" + cur;
            else if (cur.Length == 0)
              cur = dir;
            Environment.SetEnvironmentVariable("DDPLOTSTYLEPATHS", cur);

            string ctbName = Path.GetFileName(ctb);
            using (DBDictionary layoutDict = (DBDictionary)database.LayoutDictionaryId.GetObject(OpenMode.ForRead))
            {
              foreach (DBDictionaryEntry entry in layoutDict)
              {
                try
                {
                  ObjectId layId = layoutDict.GetAt(entry.Key);
                  using (Layout lay = (Layout)layId.GetObject(OpenMode.ForWrite))
                  {
                    // CurrentStyleSheet 在 PlotSettings/Layout 上只读, 必须通过
                    // PlotSettingsValidator 实例方法设置 (CTB/STB 才能生效)。
                    PlotSettingsValidator.Current.SetCurrentStyleSheet(lay, ctbName);
                  }
                }
                catch (System.Exception ex)
                {
                  Console.Error.WriteLine("WARN: 无法为布局 '" + entry.Key + "' 应用样式表: " + ex.Message);
                }
              }
            }
          }

          // ---------- 配置并导出 PDF ----------
          using (mPDFExportParams param = new mPDFExportParams())
          {
            param.Database = database;

            using (FileStreamBuf fs = new FileStreamBuf(output, false, FileShareMode.DenyNo, FileCreationDisposition.CreateAlways))
            {
              param.OutputStream = fs;

              PDFExportFlags flags = 0;
              if (embedTtf) flags |= PDFExportFlags.EmbededTTF;
              if (layers)    flags |= PDFExportFlags.EnableLayers;
              flags |= PDFExportFlags.ZoomToExtentsMode;     // 缩放到图纸范围, 内容适配纸张
              flags |= PDFExportFlags.SimpleGeomOptimization;
              // SHX/大字体文字转几何：SHX(如 Fsdb.shx/Fsdb_e.shx)无法以文本方式
              // 正确映射到 PDF 字体，按文本导出会乱码/丢字，转为矢量轮廓则显示正确
              flags |= PDFExportFlags.SHXTextAsGeometry;
              // TTF 文字同样转几何：实测 ODA 嵌入 TTF 子集时字形映射错乱
              // (内容数据正确但显示为拉丁乱码)，转矢量轮廓彻底规避
              flags |= PDFExportFlags.TTFTextAsGeometry;
              param.SearchTextType = SearchableTextType.NoSearch; // 所有文字转几何(不以可搜索文本导出)
              param.Flags = flags;

              param.Title = title;
              param.Author = author;

              // 枚举所有布局 (模型空间在前)
              StringCollection layouts = new StringCollection();
              using (DBDictionary layoutDict = (DBDictionary)database.LayoutDictionaryId.GetObject(OpenMode.ForRead))
              {
                foreach (DBDictionaryEntry entry in layoutDict)
                  layouts.Add(entry.Key);
              }
              param.Layouts = layouts;

              // 纸张尺寸
              double w = (pw > 0) ? pw : 210.0;   // 默认 A4
              double h = (ph > 0) ? ph : 297.0;
              PageParamsCollection ppc = new PageParamsCollection();
              for (int k = 0; k < layouts.Count; k++)
              {
                PageParams pp = new PageParams();
                pp.setParams(w, h);
                ppc.Add(pp);
              }
              param.PageParams = ppc;

              Console.Error.WriteLine("STEP: ExportPDF 导出中 (布局数: " + layouts.Count + ")...");
              Export_Import.ExportPDF(param);
              Console.Error.WriteLine("STEP: ExportPDF 完成");
            }
          }
        }

        Console.WriteLine("OK: " + output);
        // 显式释放 ODA 原生资源, 防止进程收尾时终结器访问已卸载资源导致退出码异常
        dd.Dispose();
        Environment.Exit(0);
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        Console.Error.WriteLine("STACK: " + ex.StackTrace);
        System.Exception inner = ex.InnerException;
        while (inner != null)
        {
          Console.Error.WriteLine("INNER: " + inner.Message);
          Console.Error.WriteLine("  " + inner.StackTrace);
          inner = inner.InnerException;
        }
        Environment.Exit(1);
      }
      return 0;
    }

    // ---------- --convert: 单文件格式转换 ----------
    static void RunConvert(string[] args)
    {
      // 用法: --convert <in> <out> [--version <ver>]
      string ver = "newest";
      string input = null, output = null;
      for (int i = 1; i < args.Length; i++)
      {
        string a = args[i];
        if ((a == "--version" || a == "-v") && i + 1 < args.Length) ver = args[++i];
        else if (input == null) input = a;
        else if (output == null) output = a;
        else { Console.Error.WriteLine("多余参数: " + a); Environment.Exit(2); }
      }
      if (input == null || output == null)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --convert <in.dwg|.dxf> <out.dxf|.dwg> [--version ACAD2013]");
        Environment.Exit(2);
      }
      try
      {
        DwgVersion version = DwgDxfConverter.ParseVersion(ver);
        Console.Error.WriteLine("STEP: 转换 " + input + " -> " + output + " (" + version + ")");
        DwgDxfConverter.Convert(input, output, version);
        Console.WriteLine("OK: " + output);
        DwgDxfConverter.Shutdown();
        Environment.Exit(0);
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        DwgDxfConverter.Shutdown();
        Environment.Exit(1);
      }
    }

    // ---------- --dump-styles: 导出文字样式表（样式名 + 宽度因子） ----------
    // 背景: Teigha 的 DwgRead→DxfOut 会丢失文字样式的宽度因子
    // (实测 图框_Ref.dwg 的 TEXT1 样式 XScale=0.75, 转出的 2004 DXF 组码41=1)，
    // 导致主程序渲染参照图框文字时宽度错误。此处把 DWG 里真实的宽度因子
    // 导出为 TSV(每行 "样式名\t宽度因子")，供主程序在转换后回填。
    static int RunDumpStyles(string[] args)
    {
      // 用法: --dump-styles <in.dwg|.dxf> <out.tsv>
      string input = null, output = null;
      for (int i = 1; i < args.Length; i++)
      {
        string a = args[i];
        if (input == null) input = a;
        else if (output == null) output = a;
        else { Console.Error.WriteLine("多余参数: " + a); return 2; }
      }
      if (input == null || output == null)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --dump-styles <in.dwg|.dxf> <out.tsv>");
        return 2;
      }
      if (!File.Exists(input))
      {
        Console.Error.WriteLine("ERROR: 输入文件不存在: " + input);
        return 1;
      }
      try
      {
        Services dd = InitRuntime();
        try
        {
          using (Database database = new Database(false, false))
          {
            if (input.ToUpperInvariant().EndsWith(".DXF"))
              database.DxfIn(input, "");
            else
              database.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, false, "");
            HostApplicationServices.WorkingDatabase = database;
            try
            {
              using (TextStyleTable st = (TextStyleTable)database.TextStyleTableId.GetObject(OpenMode.ForRead))
              using (StreamWriter w = new StreamWriter(output, false, Encoding.UTF8))
              {
                foreach (ObjectId id in st)
                {
                  try
                  {
                    using (TextStyleTableRecord tsr = (TextStyleTableRecord)id.GetObject(OpenMode.ForRead))
                    {
                      double wf = 1.0;
                      try { wf = tsr.XScale; } catch { }
                      if (wf <= 0.0 || wf > 16.0) wf = 1.0; // 异常值防御
                      w.WriteLine(tsr.Name + "\t" + wf.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    }
                  }
                  catch { }
                }
              }
            }
            finally
            {
              HostApplicationServices.WorkingDatabase = null;
            }
          }
        }
        finally
        {
          dd.Dispose();
        }
        Console.WriteLine("OK: " + output);
        return 0;
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        return 1;
      }
    }

    // ---------- --dump-texts: 导出 TEXT 实体对齐信息 ----------
    // 每行: 文本\t图层\t样式\tx10\ty10\tx11\ty11\thalign\tvalign\t宽度因子\t字高
    // 用于核对 DWG 里文字的真实对齐方式(ODA DxfOut 对部分实体可能丢失 72/73/11)
    static int RunDumpTexts(string[] args)
    {
      string input = null, output = null;
      for (int i = 1; i < args.Length; i++)
      {
        string a = args[i];
        if (input == null) input = a;
        else if (output == null) output = a;
        else { Console.Error.WriteLine("多余参数: " + a); return 2; }
      }
      if (input == null || output == null)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --dump-texts <in.dwg|.dxf> <out.tsv>");
        return 2;
      }
      if (!File.Exists(input))
      {
        Console.Error.WriteLine("ERROR: 输入文件不存在: " + input);
        return 1;
      }
      try
      {
        Services dd = InitRuntime();
        try
        {
          using (Database database = new Database(false, false))
          {
            if (input.ToUpperInvariant().EndsWith(".DXF"))
              database.DxfIn(input, "");
            else
              database.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, false, "");
            HostApplicationServices.WorkingDatabase = database;
            try
            {
              using (BlockTable bt = (BlockTable)database.BlockTableId.GetObject(OpenMode.ForRead))
              using (BlockTableRecord ms = (BlockTableRecord)bt[BlockTableRecord.ModelSpace].GetObject(OpenMode.ForRead))
              using (StreamWriter w = new StreamWriter(output, false, Encoding.UTF8))
              {
                foreach (ObjectId id in ms)
                {
                  if (id.ObjectClass.Name != "AcDbText")
                    continue;
                  try
                  {
                    using (DBText t = (DBText)id.GetObject(OpenMode.ForRead))
                    {
                      double wf = 1.0;
                      try { wf = t.WidthFactor; } catch { }
                      // 首列为实体句柄(十六进制)，与转换后 DXF 的组码5对应，供主程序按句柄回填真值
                      w.WriteLine("{0}\t{1}\t{2}\t{3:R}\t{4:R}\t{5:R}\t{6:R}\t{7}\t{8}\t{9:R}\t{10:R}",
                          t.Handle.ToString(), t.TextString, t.Layer,
                          t.Position.X, t.Position.Y,
                          t.AlignmentPoint.X, t.AlignmentPoint.Y,
                          (int)t.HorizontalMode, (int)t.VerticalMode,
                          wf, t.Height);
                    }
                  }
                  catch { }
                }
              }
            }
            finally
            {
              HostApplicationServices.WorkingDatabase = null;
            }
          }
        }
        finally
        {
          dd.Dispose();
        }
        Console.WriteLine("OK: " + output);
        return 0;
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        return 1;
      }
    }

    // ---------- --convert-batch: 批量格式转换 ----------
    static void RunConvertBatch(string[] args)
    {
      // 用法: --convert-batch <目录|目录\*.dxf> <输出目录> --ext dwg|dxf [--version <ver>]
      string ver = "newest", ext = null;
      string src = null, outDir = null;
      for (int i = 1; i < args.Length; i++)
      {
        string a = args[i];
        if ((a == "--version" || a == "-v") && i + 1 < args.Length) ver = args[++i];
        else if ((a == "--ext" || a == "-e") && i + 1 < args.Length) ext = args[++i];
        else if (src == null) src = a;
        else if (outDir == null) outDir = a;
        else { Console.Error.WriteLine("多余参数: " + a); Environment.Exit(2); }
      }
      if (src == null || outDir == null || ext == null)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --convert-batch <输入目录|通配符> <输出目录> --ext dwg|dxf [--version ACAD2013]");
        Environment.Exit(2);
      }
      ext = ext.Trim().ToLowerInvariant();
      if (!ext.StartsWith(".")) ext = "." + ext;
      if (ext != ".dwg" && ext != ".dxf")
      {
        Console.Error.WriteLine("ERROR: --ext 只支持 dwg 或 dxf");
        Environment.Exit(2);
      }

      try
      {
        // 收集输入文件: 支持 "目录"、"目录\*.dxf" 通配符
        List<string> files = new List<string>();
        string dir = Path.GetDirectoryName(src);
        string pattern = Path.GetFileName(src);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        if (pattern.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
          foreach (string f in Directory.GetFiles(dir, pattern))
            files.Add(f);
        }
        else if (Directory.Exists(src))
        {
          foreach (string f in Directory.GetFiles(src))
          {
            string e = Path.GetExtension(f).ToLowerInvariant();
            if (e == ".dwg" || e == ".dxf") files.Add(f);
          }
        }
        else files.Add(src);

        if (files.Count == 0)
        {
          Console.Error.WriteLine("ERROR: 未找到输入文件");
          Environment.Exit(1);
        }

        // 排除与目标扩展名相同的文件
        List<string> todo = new List<string>();
        foreach (string f in files)
          if (Path.GetExtension(f).ToLowerInvariant() != ext) todo.Add(f);

        DwgVersion version = DwgDxfConverter.ParseVersion(ver);
        Console.Error.WriteLine("STEP: 批量转换 " + todo.Count + " 个文件 -> " + ext + " (" + version + ")");
        List<DwgDxfConverter.BatchResult> results =
            DwgDxfConverter.ConvertBatch(todo, outDir, ext, version,
                delegate(string msg) { Console.Error.WriteLine("  " + msg); });

        int ok = 0;
        foreach (DwgDxfConverter.BatchResult r in results)
        {
          if (r.Ok) { ok++; Console.WriteLine("OK: " + r.Output); }
          else Console.Error.WriteLine("FAIL: " + r.Input + " -> " + r.Error);
        }
        Console.Error.WriteLine("STEP: 完成 " + ok + "/" + results.Count);
        DwgDxfConverter.Shutdown();
        Environment.Exit(ok == results.Count ? 0 : 1);
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        DwgDxfConverter.Shutdown();
        Environment.Exit(1);
      }
    }

    static string FindMonoStyle()
    {
      string env = Environment.GetEnvironmentVariable("DDPLOTSTYLEPATHS") ?? "";
      string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
      string[] dirs = env.Split(';');
      string[] names = { "monochrome.ctb", "monochrome.stb" };
      foreach (string dir in dirs)
      {
        if (dir.Length == 0) continue;
        foreach (string n in names)
        {
          string p = Path.Combine(dir, n);
          if (File.Exists(p)) return p;
        }
      }
      foreach (string n in names)
      {
        string p = Path.Combine(exeDir, n);
        if (File.Exists(p)) return p;
      }
      return "";
    }

    static void PrintUsage()
    {
      Console.WriteLine("用法: ODADrawingTools.exe <输入.dwg|.dxf> <输出.pdf> [选项]");
      Console.WriteLine("  --mono          单色输出 (需 monochrome.ctb/monochrome.stb, 或配合 --ctb)");
      Console.WriteLine("  --ctb <文件>    应用 CTB/STB 打印样式表 (控制颜色/线宽)");
      Console.WriteLine("  --embed-ttf     嵌入 TTF 字体");
      Console.WriteLine("  --no-layers     不在 PDF 中保留图层");
      Console.WriteLine("  --dxf           强制按 DXF 读取");
      Console.WriteLine("  --paper W H     自定义纸张尺寸 (毫米)");
      Console.WriteLine("  --title <文本>  设置 PDF 标题");
      Console.WriteLine("  --author <文本> 设置 PDF 作者");
    }

    // ===================== 按参照图框分页打印 =====================

    // 一个打印页：对应某个布局（或模型空间）的一个打印范围
    class PlotJob
    {
      public string LayoutName;     // 布局名（"Model" 表示模型空间）
      public Extents2d? Window;     // 打印窗口（参照图框范围）；null=整布局缩放打印
      public double PageW;          // 纸张宽(mm)，0=自适应
      public double PageH;          // 纸张高(mm)，0=自适应
    }

    // 对应 AutoCAD 打印对话框的高级选项（打印偏移/比例/线宽/图形方向等）
    class PlotExtras
    {
      public bool center = false;              // 居中打印(C)
      public double offX = 0, offY = 0;        // 打印偏移 X/Y (mm)
      public string orientation = "landscape"; // 图形方向: landscape=横向 / portrait=纵向
      public bool fit = false;                 // 布满图纸(I)
      public bool hasScale = false;            // 是否指定自定义打印比例
      public double scaleNum = 1, scaleDen = 1;// 自定义比例 N:D（如 1:100）
      public bool scaleLineweights = false;    // 缩放线宽(L)
      public bool plotLineweights = true;      // 打印对象线宽(W)，默认开
      public bool plotStyles = false;          // 按样式打印(E)
      public bool hidden = false;              // 消隐打印(H)
      // 一张图纸里有多张参照图框时的页面顺序:
      // xrightydown=先左→右、再上→下(默认) / xrightyup=先左→右、再下→上
      // ydownxright=先上→下、再左→右 / yupxright=先下→上、再左→右
      public string frameOrder = "xrightydown";
    }

    // 初始化 ODA/Teigha 运行时（每次进程内调用一次）
    static Services InitRuntime()
    {
      Services dd = new Services();
      SystemObjects.DynamicLinker.LoadApp("PlotSettingsValidator", false, false);
      HostApplicationServices.Current = new HostApp();
      return dd;
    }

    // 解析 --mono 为 monochrome.ctb/monochrome.stb（若未显式指定 --ctb）
    static string ResolveCtb(string ctb, bool mono)
    {
      if (mono && ctb.Length == 0)
      {
        string m = FindMonoStyle();
        if (m.Length > 0) return m;
        Console.Error.WriteLine("WARN: --mono 已设置但未找到 monochrome.ctb/monochrome.stb");
      }
      return ctb;
    }

    // 遵守图层"不打印"属性：把不打印的图层临时关闭(IsOff)。
    // ODA 的 PDF 导出不检查图层 Plot 标志，按可见性渲染即可正确排除（仅改内存，不回写文件）。
    static void ApplyNonPlotLayersOff(Database database)
    {
      using (LayerTable lt = (LayerTable)database.LayerTableId.GetObject(OpenMode.ForRead))
      {
        foreach (ObjectId id in lt)
        {
          try
          {
            using (LayerTableRecord ltr = (LayerTableRecord)id.GetObject(OpenMode.ForWrite))
            {
              if (!ltr.IsPlottable && !ltr.IsOff)
              {
                ltr.IsOff = true;
                Console.Error.WriteLine("INFO: 图层 '" + ltr.Name + "' 为不打印图层，已临时关闭");
              }
            }
          }
          catch (System.Exception ex)
          {
            Console.Error.WriteLine("WARN: 处理图层不打印属性失败: " + ex.Message);
          }
        }
      }
    }

    // 修复无字体定义的文字样式：
    // 部分图框文件(如 图框_Ref.dwg)的文字样式(如 TEXT1)字体名为空，渲染时回退到单字节
    // SHX 逐字节渲染中文 → 标题栏出现乱码(CAD 有自己的回退机制所以显示正常)。
    // 这里把无字体定义的样式映射为宋体(经 FindFile 解析到系统 simsun.ttc)。
    // 注意: TTF 样式的字体名不在 FileName 里(那是 SHX 的字段), 而在 Font.TypeFace;
    // 只看 FileName 会把正常的 TTF 样式误判成"无字体"并改成宋体, 所以三处都为空才算无字体。
    static void FixEmptyTextStyles(Database database)
    {
      using (TextStyleTable st = (TextStyleTable)database.TextStyleTableId.GetObject(OpenMode.ForRead))
      {
        foreach (ObjectId id in st)
        {
          try
          {
            using (TextStyleTableRecord tsr = (TextStyleTableRecord)id.GetObject(OpenMode.ForWrite))
            {
              string fn = "", big = "", face = "";
              try { fn = tsr.FileName ?? ""; } catch { }
              try { big = tsr.BigFontFileName ?? ""; } catch { }
              try { if (tsr.Font != null) face = tsr.Font.TypeFace ?? ""; } catch { }
              if (fn.Trim().Length == 0 && big.Trim().Length == 0 && face.Trim().Length == 0)
              {
                tsr.FileName = "SimSun.ttf";
                Console.Error.WriteLine("INFO: 文字样式 '" + tsr.Name + "' 无字体定义，已映射为 SimSun.ttf");
              }
              else
              {
                Console.Error.WriteLine("INFO: 样式 '" + tsr.Name + "' font=[" + fn + "] bigfont=[" + big + "] typeface=[" + face + "]");
              }
            }
          }
          catch (System.Exception ex)
          {
            Console.Error.WriteLine("WARN: 处理文字样式失败: " + ex.Message);
          }
        }
      }
    }

    // 修正非左对齐文字的位置：
    // DXF 中 h72/h73≠0 的 TEXT 以对齐点(11,21)定位，插入点(10,20)只是估算位置；
    // ODA 的文字转几何渲染按 Position(插入点) 做 Justify 偏移，导致居中/右对齐文字
    // 整体偏左半个字宽(表头"混凝土"压到"总重"列、表格标题/标注文字偏位)。
    // 这里把 Position 统一设置为 AlignmentPoint(真实对齐点)，ODA 渲染中心即与 CAD 一致。
    static void NormalizeTextJustification(Database database)
    {
      using (BlockTable bt = (BlockTable)database.BlockTableId.GetObject(OpenMode.ForRead))
      {
        foreach (ObjectId btrId in bt)
        {
          using (BlockTableRecord btr = (BlockTableRecord)btrId.GetObject(OpenMode.ForRead))
          {
            foreach (ObjectId id in btr)
            {
              try
              {
                using (DBObject obj = id.GetObject(OpenMode.ForWrite))
                {
                  DBText t = obj as DBText;
                  if (t == null) continue;
                  if (t.IsDefaultAlignment) continue; // 默认对齐(基线左): 按 Position 渲染即正确
                  Point3d ap = t.AlignmentPoint;
                  t.Position = ap;
                }
              }
              catch (System.Exception ex)
              {
                Console.Error.WriteLine("WARN: 文字对齐修正失败: " + ex.Message);
              }
            }
          }
        }
      }
    }

    // 应用 CTB/STB 打印样式表到数据库所有布局
    static void ApplyCtb(Database database, string ctb)
    {
      if (ctb.Length == 0) return;
      string dir = Path.GetDirectoryName(Path.GetFullPath(ctb));
      string cur = Environment.GetEnvironmentVariable("DDPLOTSTYLEPATHS") ?? "";
      if (cur.Length > 0 && cur.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
        cur = dir + ";" + cur;
      else if (cur.Length == 0)
        cur = dir;
      Environment.SetEnvironmentVariable("DDPLOTSTYLEPATHS", cur);

      string ctbName = Path.GetFileName(ctb);
      using (DBDictionary layoutDict = (DBDictionary)database.LayoutDictionaryId.GetObject(OpenMode.ForRead))
      {
        foreach (DBDictionaryEntry entry in layoutDict)
        {
          try
          {
            ObjectId layId = layoutDict.GetAt(entry.Key);
            using (Layout lay = (Layout)layId.GetObject(OpenMode.ForWrite))
              PlotSettingsValidator.Current.SetCurrentStyleSheet(lay, ctbName);
          }
          catch (System.Exception ex)
          {
            Console.Error.WriteLine("WARN: 无法为布局 '" + entry.Key + "' 应用样式表: " + ex.Message);
          }
        }
      }
    }

    // 块名与传入模式匹配：支持 '*'/'?' 通配（大小写不敏感），否则按相等匹配
    static bool NameMatches(string blockName, string pattern)
    {
      if (string.IsNullOrEmpty(pattern)) return true;
      if (pattern.IndexOfAny(new[] { '*', '?' }) >= 0)
        return DosMatch(blockName, pattern);
      return string.Equals(blockName, pattern, StringComparison.OrdinalIgnoreCase);
    }
    static bool DosMatch(string text, string pattern)
    {
      string rx = "^" + Regex.Escape(pattern).Replace("\\*", "[\\s\\S]*").Replace("\\?", ".") + "$";
      return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase);
    }

    // 取块参照所属块表的记录路径（外部参照 xref 就是被参照的 dwg/dxf 文件路径）。
    // BlockReference 本身没有路径属性，路径挂在它引用的 BlockTableRecord 上。
    static string XRefPath(BlockReference br)
    {
      try
      {
        using (BlockTableRecord btr = (BlockTableRecord)br.BlockTableRecord.GetObject(OpenMode.ForRead))
        {
          if (!btr.IsFromExternalReference) return "";
          return btr.PathName == null ? "" : btr.PathName;
        }
      }
      catch { return ""; }
    }

    // 参照名匹配：块名匹配，或该块是外部参照时按参照文件名/完整路径匹配。
    // 这样 --ref 图框_Ref.dwg 就能一次命中所有引用了同一张图的外部参照实例。
    static bool RefMatches(BlockReference br, string pattern)
    {
      if (NameMatches(br.Name, pattern)) return true;
      string xpath = XRefPath(br);
      if (xpath.Length == 0) return false;
      if (NameMatches(xpath, pattern)) return true;
      string fn = System.IO.Path.GetFileName(xpath);
      return fn.Length > 0 && NameMatches(fn, pattern);
    }

    // 收集打印任务：根据空间过滤 + 参照名匹配，每个匹配参照图框 → 一页
    static List<PlotJob> CollectPlotJobs(Database db, string refer, string space, string frameOrder)
    {
      var jobs = new List<PlotJob>();
      bool scanModel = (space == "model" || space == "both");
      bool scanLayout = (space == "layout" || space == "both");

      using (BlockTable bt = (BlockTable)db.BlockTableId.GetObject(OpenMode.ForRead))
      using (DBDictionary layoutDict = (DBDictionary)db.LayoutDictionaryId.GetObject(OpenMode.ForRead))
      {
        // 模型空间
        if (scanModel)
        {
          string layoutName = "Model";
          if (refer.Length == 0)
            jobs.Add(new PlotJob { LayoutName = layoutName });
          else
            AddBlockRefJobs(jobs, bt[BlockTableRecord.ModelSpace], layoutName, refer, frameOrder);
        }
        // 各布局（图纸空间）
        // 按 AutoCAD 的布局标签页顺序(TabOrder)遍历：DWG/DXF 里布局字典的存储顺序是乱的
        // （DXF 里 LAYOUT 实体甚至按 20,21,22,2,11,12... 这样的顺序存放），
        // 直接按字典顺序出图，页序会和图纸里标注的「第N页 共21页」完全对不上。
        if (scanLayout)
        {
          var lays = new List<KeyValuePair<int, string>>();
          foreach (DBDictionaryEntry entry in layoutDict)
          {
            if (string.Equals(entry.Key, "Model", StringComparison.OrdinalIgnoreCase)) continue;
            int tab = 0;
            try
            {
              ObjectId layId0 = layoutDict.GetAt(entry.Key);
              using (Layout lay0 = (Layout)layId0.GetObject(OpenMode.ForRead)) tab = lay0.TabOrder;
            }
            catch { }
            lays.Add(new KeyValuePair<int, string>(tab, entry.Key));
          }
          lays.Sort(delegate (KeyValuePair<int, string> a, KeyValuePair<int, string> b)
          {
            if (a.Key != b.Key) return a.Key.CompareTo(b.Key);
            return string.CompareOrdinal(a.Value, b.Value);
          });

          foreach (KeyValuePair<int, string> kv in lays)
          {
            string layoutName = kv.Value;
            Console.Error.WriteLine("INFO: 布局[tab=" + kv.Key + "] " + layoutName);
            ObjectId layId = layoutDict.GetAt(layoutName);
            using (Layout lay = (Layout)layId.GetObject(OpenMode.ForRead))
            {
              ObjectId btrId = lay.BlockTableRecordId;
              if (refer.Length == 0)
                jobs.Add(new PlotJob { LayoutName = layoutName });
              else
                AddBlockRefJobs(jobs, btrId, layoutName, refer, frameOrder);
            }
          }
        }
      }
      return jobs;
    }

    // 多图框页序：主轴（X 或 Y）相同即视为同一行/列，再按次轴排。
    // 坐标量化成「半个图框」的整数格再做键值排序 —— 直接带容差比较会破坏传递性，
    // List.Sort 会抛 "IComparer.Compare() method returns inconsistent results"。
    static void SortFrameJobs(List<PlotJob> jobs, string order)
    {
      if (jobs.Count < 2) return;
      double sumW = 0, sumH = 0;
      foreach (PlotJob j in jobs)
      {
        Extents2d w = j.Window.Value;
        sumW += w.MaxPoint.X - w.MinPoint.X;
        sumH += w.MaxPoint.Y - w.MinPoint.Y;
      }
      double tolX = Math.Max(sumW / jobs.Count * 0.5, 1e-9);
      double tolY = Math.Max(sumH / jobs.Count * 0.5, 1e-9);
      var keyed = jobs.Select(j => new
      {
        j,
        x = (long)Math.Round(j.Window.Value.MinPoint.X / tolX),
        y = (long)Math.Round(j.Window.Value.MinPoint.Y / tolY)
      }).ToList();

      // xrightydown 这种写法是「先 X 向右逐行读完，再 Y 向下换行」= 逐行阅读顺序，
      // 所以 X 是行内键(内层)，Y 是换行键(外层)；ydownxright 反过来是逐列。
      bool xInner = order.StartsWith("x");
      bool yDesc = order == "xrightydown" || order == "ydownxright";   // Y 向下 = Y 递减
      IEnumerable<PlotJob> sorted;
      if (xInner)
        sorted = yDesc ? keyed.OrderByDescending(k => k.y).ThenBy(k => k.x).Select(k => k.j)
                       : keyed.OrderBy(k => k.y).ThenBy(k => k.x).Select(k => k.j);
      else
        sorted = yDesc ? keyed.OrderBy(k => k.x).ThenByDescending(k => k.y).Select(k => k.j)
                       : keyed.OrderBy(k => k.x).ThenBy(k => k.y).Select(k => k.j);
      jobs.Clear();
      jobs.AddRange(sorted);
    }

    // 扫描某空间（块表记录）下的块参照/外部参照，按名匹配，每个实例生成一个窗口打印页
    static void AddBlockRefJobs(List<PlotJob> jobs, ObjectId btrId, string layoutName, string refer, string frameOrder)
    {
      var mine = new List<PlotJob>();
      using (BlockTableRecord btr = (BlockTableRecord)btrId.GetObject(OpenMode.ForRead))
      {
        foreach (ObjectId id in btr)
        {
          using (DBObject obj = id.GetObject(OpenMode.ForRead))
          {
            BlockReference br = obj as BlockReference;
            if (br == null) continue;
            if (!RefMatches(br, refer)) continue;
            try
            {
              Extents3d ext = br.GeometricExtents;
              double minX = ext.MinPoint.X, minY = ext.MinPoint.Y;
              double maxX = ext.MaxPoint.X, maxY = ext.MaxPoint.Y;
              double w = maxX - minX, h = maxY - minY;
              if (w <= 1e-6 || h <= 1e-6) continue;
              string xpath = XRefPath(br);
              // 窗口向内收缩 1mm：图框文件的几何范围通常撑满整张纸，
              // 其内部自带的图纸边界线恰压在窗口边缘上，会被打印成页面边缘的细线；
              // 内缩后裁掉这些边界线（图框可见内容距页边有较大内边距，不受影响）。
              // 注意这 1mm 是**图纸上的** mm，不是模型单位：模型坐标按图框比例放大过
              // （1:150 时 1 图纸 mm = 150 模型单位），直接内缩 1 个模型单位等于没缩，
              // 边界线照样打印在页面边缘。下面按「长边 = 420mm」折算成模型单位。
              double inset = Math.Min(Math.Max(w, h) / 420.0, Math.Min(w, h) / 4.0);
              minX += inset; minY += inset; maxX -= inset; maxY -= inset;
              w = maxX - minX; h = maxY - minY;
              Console.Error.WriteLine("INFO: 参照 '" + br.Name + "'" +
                  (xpath.Length > 0 ? " (外部参照: " + xpath + ")" : "") + " 打印窗口=(" +
                  minX.ToString("0.##") + "," + minY.ToString("0.##") + ")-(" +
                  maxX.ToString("0.##") + "," + maxY.ToString("0.##") + ")");
              // 纸张长边固定为 420mm(A3 长边)，短边按图框宽高比自适应（避免图形拉伸变形）。
              // 长度取 A3 是因为 ODA 只能设置“规范纸张”(ISO_A3_(297.00_x_420.00_MM) 这类)，
              // 导出页面尺寸由布局纸张决定，非标准尺寸无法落到页面上。
              double longer = 420.0;
              double pw, ph;
              if (w >= h) { pw = longer; ph = longer * h / w; }
              else { ph = longer; pw = longer * w / h; }
              mine.Add(new PlotJob
              {
                LayoutName = layoutName,
                Window = new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY)),
                PageW = pw,
                PageH = ph
              });
            }
            catch (System.Exception ex)
            {
              Console.Error.WriteLine("WARN: 无法获取参照 '" + br.Name + "' 的范围: " + ex.Message);
            }
          }
        }
      }
      SortFrameJobs(mine, frameOrder);
      jobs.AddRange(mine);
    }

    // 将单个打印任务导出为 PDF（窗口打印或整布局缩放打印）
    static void ExportLayoutToPdf(Database database, string layoutName, Extents2d? window,
        double pageWmm, double pageHmm, string title, string author, bool layers, bool embedTtf, string outPdf,
        PlotExtras ex)
    {
      // 纸张宽高与图形方向保持一致：横向确保 w>=h，纵向确保 h>=w
      double w = (pageWmm > 0) ? pageWmm : 210.0;
      double h = (pageHmm > 0) ? pageHmm : 297.0;
      if (ex.orientation == "portrait" ? w > h : h > w)
      {
        double t = w; w = h; h = t;
      }
      using (mPDFExportParams param = new mPDFExportParams())
      {
        param.Database = database;
        using (FileStreamBuf fs = new FileStreamBuf(outPdf, false, FileShareMode.DenyNo, FileCreationDisposition.CreateAlways))
        {
          param.OutputStream = fs;
          PDFExportFlags flags = 0;
          if (embedTtf) flags |= PDFExportFlags.EmbededTTF;
          if (layers) flags |= PDFExportFlags.EnableLayers;
          if (window == null) flags |= PDFExportFlags.ZoomToExtentsMode; // 整布局：缩放到范围
          flags |= PDFExportFlags.SimpleGeomOptimization;
          // SHX/大字体文字转几何：SHX(如 Fsdb.shx/Fsdb_e.shx)无法以文本方式
          // 正确映射到 PDF 字体，按文本导出会乱码/丢字，转为矢量轮廓则显示正确
          flags |= PDFExportFlags.SHXTextAsGeometry;
          // TTF 文字同样转几何：实测 ODA 嵌入 TTF 子集时字形映射错乱
          // (内容数据正确但显示为拉丁乱码)，转矢量轮廓彻底规避
          flags |= PDFExportFlags.TTFTextAsGeometry;
          param.SearchTextType = SearchableTextType.NoSearch; // 所有文字转几何(不以可搜索文本导出)
          param.Flags = flags;
          param.Title = title;
          param.Author = author;

          // 设置该布局的打印窗口（或重置为范围打印，避免上一任务残留 Window 设置）
          using (DBDictionary layoutDict = (DBDictionary)database.LayoutDictionaryId.GetObject(OpenMode.ForRead))
          {
            ObjectId layId = layoutDict.GetAt(layoutName);
            using (Layout lay = (Layout)layId.GetObject(OpenMode.ForWrite))
            {
              // PlotType / PlotWindowArea 在本机 Teigha 4.02 为只读属性，
              // 必须通过 PlotSettingsValidator 设置（Layout 继承自 PlotSettings）。
              PlotSettingsValidator psv = PlotSettingsValidator.Current;
              // 高级打印选项逐项套 try/catch：个别选项被运行时拒绝时降级为 WARN，
              // 不中断整个 PDF 导出（本机 Teigha 4.02 的 SetPlotPaperUnits 会抛 eInvalidInput，
              // 故不强制纸张单位，布局默认即为毫米）。
              bool psvBroken = false;
              Action<string, Action> Guard = (name, act) =>
              {
                try { act(); }
                catch (System.Exception e2)
                {
                  // 布局的打印配置缺失/损坏时（本例指向未安装的打印机 "A3 某某打印机.pc3"），
                  // PlotSettingsValidator 的所有方法都抛 eNullObjectId：纸张、方向、比例、
                  // 打印区域全都设不上。此时导出仍按 param.PageParams 的请求尺寸出图（实测正确），
                  // 所以只提示一次，不再逐项刷屏。
                  if (e2.Message.IndexOf("eNullObjectId", StringComparison.OrdinalIgnoreCase) >= 0)
                  {
                    if (!psvBroken)
                    {
                      psvBroken = true;
                      Console.Error.WriteLine("WARN: [" + layoutName + "] 打印配置无效(可能指向未安装的打印机)，该布局部分打印设置沿用图纸自带配置");
                    }
                    return;
                  }
                  Console.Error.WriteLine("WARN: [" + layoutName + "] " + name + " 设置失败: " + e2.Message);
                }
              };
              // 布局的打印配置缺失/损坏时（本例指向未安装的打印机 "A3 某某打印机.pc3"），
              // PlotSettingsValidator 的所有方法都抛 eNullObjectId —— 纸张、方向、比例、打印区域
              // 全部设置不上，PDF 就按布局自带的老配置出图。
              // 这里不重置打印配置：实测重置也不解除 eNullObjectId，而且会丢掉布局原有的
              // 页边距/打印区域，让本来正常的图纸内容发生变化。改为让每项设置各自降级。
              // 纸张：导出的 PDF 页面尺寸由「布局自带的纸张」决定——设置 param.Layouts 时
              // param.PageParams 会被忽略，页面按布局的 PlotPaperSize 出。
              // 若布局还是 A4 而请求的是 A3，内容就会按 A4 的可打印区排版，
              // 结果是纸小、图也小、没占满页面。这里把布局纸张改成请求的纸张。
              // 用 SetClosestMediaName(宽,高) 直接按毫米换算，不走 GetCanonicalMediaNameList：
              // 自定义纸张的布局（如整条图纸拼成的长条纸）读列表会抛 eInvalidInput，
              // 那样就会保留布局自带的纸张，导致同一批 PDF 里前后页大小不一致。
              Guard("纸张", delegate
              {
                // 布局自带的打印配置可能指向未安装的打印机（本例："A3 某某打印机.pc3"），
                // 此时设置纸张一律抛 eInvalidInput，布局就保留自带纸张 → 同一批 PDF 里前后页大小
                // 不一致（整条图纸拼成的长条布局会按 3360mm 长的纸出图）。
                // 失败时先重置为默认配置(None)再选纸张。
                // 换用带「无边距纸张」的绘图设备。
                // ODA 的 ScaleToFit 只把窗口缩到设备的**可打印区**：设备 "None" 的页边距是
                // 7.5/20mm，A3 横向可打印区只剩 380x282，于是图框只占纸宽 90%、四周留一圈白边
                // （用户截图里的"图1"）。"DWG To PDF.pc3"(AutoCAD/ODA 自带) 的纸张表里有
                // ISO_full_bleed_A3 (页边距 0.79mm)，配合下面的 matchPrintableArea=true 会被选中，
                // 图框就能缩到 99.4% 纸面（"图2"）。
                // 设备不存在（未装 AutoCAD 的机器）就静默保持原样，不影响出图。
                string pdfDev = Environment.GetEnvironmentVariable("DWGBP_DEV");
                if (pdfDev == null) pdfDev = "DWG To PDF.pc3";
                if (pdfDev.Length > 0)
                {
                  try { psv.SetPlotConfigurationName(lay, pdfDev, lay.CanonicalMediaName); }
                  catch (System.Exception) { }
                }
                string sheet = lay.CurrentStyleSheet;
                // matchPrintableArea: true=按设备的可打印区(扣页边距)选纸张, false=整张纸
                bool mpa = Environment.GetEnvironmentVariable("DWGBP_MPA") != "0";
                try
                {
                  // 纸张按“纵向”规格选（短边在前），页面方向由下面的 SetPlotRotation 决定：
                  // 横向输出 = 纵向纸张 + 旋转 90°，与 AutoCAD「横向=旋转」语义一致。
                  psv.SetClosestMediaName(lay, Math.Min(w, h), Math.Max(w, h), PlotPaperUnit.Millimeters, mpa);
                }
                catch (System.Exception)
                {
                  psv.SetDefaultPlotConfig(lay);
                  psv.SetClosestMediaName(lay, Math.Min(w, h), Math.Max(w, h), PlotPaperUnit.Millimeters, mpa);
                }
                if (!string.IsNullOrEmpty(sheet)) psv.SetCurrentStyleSheet(lay, sheet);
                Console.Error.WriteLine("INFO: [媒体] " + lay.CanonicalMediaName + " 纸张=" + lay.PlotPaperSize
                  + " 页边距=" + lay.PlotPaperMargins + " mpa=" + mpa);
              });
              // 图形方向：本机 Teigha 的媒体为纵向定义(0°=纵向页)，
              // 横向需旋转90°得到横向页（与 AutoCAD "横向=旋转"语义一致）
              Guard("图形方向", delegate
              {
                psv.SetPlotRotation(lay, ex.orientation == "portrait" ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
              });
              // 打印偏移（原点设置在可打印区域）与居中打印
              Guard("打印偏移", delegate { psv.SetPlotOrigin(lay, new Point2d(ex.offX, ex.offY)); });
              Guard("居中打印", delegate { psv.SetPlotCentered(lay, ex.center); });
              // 打印选项
              Guard("打印对象线宽", delegate { lay.PrintLineweights = ex.plotLineweights; });
              Guard("缩放线宽", delegate { lay.ScaleLineweights = ex.scaleLineweights; });
              Guard("按样式打印", delegate { lay.PlotPlotStyles = ex.plotStyles; });
              Guard("消隐打印", delegate { lay.PlotHidden = ex.hidden; });
              // 打印比例：布满图纸 / 自定义 N:D / 按图框
              // 缩放目标恒为「设备可打印区」——要让图框铺满纸张，靠的是把设备页边距换小
              // （见上面「纸张」里切换到 DWG To PDF.pc3 的无边距纸张），而不是改这里。
              if (ex.fit)
                Guard("布满图纸", delegate
                {
                  psv.SetUseStandardScale(lay, true);
                  psv.SetStdScaleType(lay, StdScaleType.ScaleToFit);
                });
              else if (ex.hasScale)
                Guard("打印比例", delegate
                {
                  psv.SetUseStandardScale(lay, false);
                  psv.SetCustomPrintScale(lay, new CustomScale(ex.scaleNum, ex.scaleDen));
                });
              else
                // 没给比例也没给 --fit：按图框算 —— 把打印窗口（参照图框）缩放到纸张大小
                Guard("打印比例(按图框)", delegate
                {
                  psv.SetUseStandardScale(lay, true);
                  psv.SetStdScaleType(lay, StdScaleType.ScaleToFit);
                });
              if (window != null)
              {
                // 注意顺序：Teigha 4.02 的 SetPlotType(kWindow) 会校验当前窗口区域，
                // 未设置有效窗口时抛 eInvalidInput，必须先 SetPlotWindowArea 再 SetPlotType。
                // 另：图纸空间布局若打印配置损坏/缺失，PlotSettingsValidator 全部方法抛
                // eNullObjectId（连 SetPlotRotation 都会失败），此时回退为布局自带打印区域，
                // 由下面的 ZoomToExtentsMode 兜底缩放，不中断整个 PDF 导出。
                Guard("打印窗口", delegate { psv.SetPlotWindowArea(lay, window.Value); });
                Guard("打印区域(窗口)", delegate { psv.SetPlotType(lay, PlotType.Window); });
              }
              else
              {
                Guard("打印区域(范围)", delegate { psv.SetPlotType(lay, PlotType.Extents); });
              }
              Console.Error.WriteLine("INFO: [" + layoutName + "] 纸张=" + lay.CanonicalMediaName
                  + " 尺寸=" + lay.PlotPaperSize + " 页边距=" + lay.PlotPaperMargins
                  + " 原点=" + lay.PlotOrigin + " 居中=" + lay.PlotCentered
                  + " 比例方式=" + lay.StdScaleType + " 标准比例=" + lay.StdScale
                  + " 打印区域=" + lay.PlotType + " 窗口=" + lay.PlotWindowArea);
            }
          }

          StringCollection layouts = new StringCollection();
          layouts.Add(layoutName);
          param.Layouts = layouts;

          PageParamsCollection ppc = new PageParamsCollection();
          PageParams pp = new PageParams();
          pp.setParams(w, h);
          ppc.Add(pp);
          param.PageParams = ppc;

          Export_Import.ExportPDF(param);
        }
      }
    }

    // 将一批打印任务逐个导出为临时 PDF，返回临时文件路径列表
    static List<string> ExportJobsToTemps(Database database, List<PlotJob> jobs,
        string title, string author, bool layers, bool embedTtf, double overrideW, double overrideH, PlotExtras ex)
    {
      var temps = new List<string>();
      string tmpDir = Path.GetTempPath();
      int pageNo = 0;
      foreach (PlotJob job in jobs)
      {
        string tmp = Path.Combine(tmpDir, "dwg2pdf_" + Guid.NewGuid().ToString("N") + ".pdf");
        double useW = (overrideW > 0 && overrideH > 0) ? overrideW : job.PageW;
        double useH = (overrideW > 0 && overrideH > 0) ? overrideH : job.PageH;
        if (useW <= 0) useW = 210.0;
        if (useH <= 0) useH = 297.0;
        pageNo++;
        // 机器可读: 页序 + 打印窗口，供界面/自检核对多图框排序
        Console.Error.WriteLine("PAGE: " + pageNo + "/" + jobs.Count + " " + job.LayoutName +
            (job.Window.HasValue
              ? " (" + job.Window.Value.MinPoint.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," +
                    job.Window.Value.MinPoint.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")"
              : " 整布局"));
        ExportLayoutToPdf(database, job.LayoutName, job.Window, useW, useH, title, author, layers, embedTtf, tmp, ex);
        temps.Add(tmp);
      }
      return temps;
    }

    // 合并多个 PDF 为一个。
    // 说明：本机 Teigha 4.02 运行时未提供 Teigha.Pdf.PDFDocument 托管类型，
    // 这里用纯 C# 实现最小可用的 PDF 拼接：对每个输入文件的所有间接对象整体偏移重新编号，
    // 收集各文件的 /Page 页面对象，重建顶层 /Pages 与 /Catalog 后写出。
    // 适用前提：Teigha 导出的是标准（非对象流压缩）PDF；若启用了对象流/压缩，需改用外部 PDF 库。
    // xref 表的第 n 项必须指向「n 0 obj」的实际字节偏移，两条硬约束：
    //  1) 偏移要用 fs.Position 实测（PDF 头 %PDF-1.4\n 是 9 字节，不是 8）；
    //  2) 对象号必须与 xref 表的位置一一对应 —— Teigha 导出时对象是乱序写出的
    //     （实测顺序 1,2,4,5,11,3,6,...），所以不能按文件出现顺序编号，
    //     必须按源对象号排序后连续重编号。违反任一条 Acrobat 就报
    //     「载入对象错误：未对齐的对象」。
    static void MergePdfs(List<string> inputs, string output)
    {
      var latin1 = System.Text.Encoding.GetEncoding("ISO-8859-1");
      var ascii = System.Text.Encoding.ASCII;
      var bodies = new List<KeyValuePair<int, string>>();   // 新对象号 → 对象体，按新号升序
      var pages = new List<int>();
      int next = 0;
      foreach (string f in inputs)
        ParsePdfObjects(f, ref next, latin1, bodies, pages);
      int topPages = next + 1;
      int catalog = next + 2;
      var pageSet = new HashSet<int>(pages);

      using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
      {
        Action<byte[]> wr = b => fs.Write(b, 0, b.Length);
        wr(ascii.GetBytes("%PDF-1.4\n"));
        var offsets = new Dictionary<int, long>();
        foreach (var kv in bodies)
        {
          // 顶层 Pages 号此时才确定，页面的 /Parent 在这里统一改指它
          string body = pageSet.Contains(kv.Key)
              ? System.Text.RegularExpressions.Regex.Replace(kv.Value, @"/Parent\s+\d+\s+\d+\s+R", "/Parent " + topPages + " 0 R")
              : kv.Value;
          offsets[kv.Key] = fs.Position;
          wr(ascii.GetBytes(kv.Key + " 0 obj\n"));
          wr(latin1.GetBytes(body));
          wr(ascii.GetBytes("\nendobj\n"));
        }
        var kids = new System.Text.StringBuilder();
        foreach (int p in pages) { kids.Append(p); kids.Append(" 0 R "); }
        offsets[topPages] = fs.Position;
        wr(ascii.GetBytes(topPages + " 0 obj\n<< /Type /Pages /Kids [ " + kids + "] /Count " + pages.Count + " >>\nendobj\n"));
        offsets[catalog] = fs.Position;
        wr(ascii.GetBytes(catalog + " 0 obj\n<< /Type /Catalog /Pages " + topPages + " 0 R >>\nendobj\n"));

        long xrefPos = fs.Position;
        wr(ascii.GetBytes("xref\n0 " + (catalog + 1) + "\n0000000000 65535 f \n"));
        for (int n = 1; n <= catalog; n++)
          wr(ascii.GetBytes(offsets[n].ToString("D10") + " 00000 n \n"));
        wr(ascii.GetBytes("trailer\n<< /Size " + (catalog + 1) + " /Root " + catalog + " 0 R >>\nstartxref\n" + xrefPos + "\n%%EOF\n"));
      }
    }

    // 取 PDF 字典里某个键的原始值文本（间接引用 / 数组 / 数字），取不到返回 null。
    static string PdfVal(string dict, string key)
    {
      var m = System.Text.RegularExpressions.Regex.Match(dict, @"/" + key + @"\s+(\d+\s+\d+\s+R)");
      if (m.Success) return m.Groups[1].Value;
      m = System.Text.RegularExpressions.Regex.Match(dict, @"/" + key + @"\s*(\[[^\]]*\])");
      if (m.Success) return m.Groups[1].Value;
      m = System.Text.RegularExpressions.Regex.Match(dict, @"/" + key + @"\s+(-?\d+)");
      return m.Success ? m.Groups[1].Value : null;
    }

    // 改写对象体里的间接引用（源对象号 → 新对象号）。
    // 流数据（stream 关键字之后）必须原样保留：内容流里的坐标数字序列也长得像
    // "n 0 R"，误改会让实际数据与 /Length 不一致，PDF 直接读坏。
    static string PdfRewriteRefs(string body, Dictionary<int, int> map)
    {
      int s = body.IndexOf("stream", StringComparison.Ordinal);
      string head = s < 0 ? body : body.Substring(0, s);
      string tail = s < 0 ? "" : body.Substring(s);
      head = System.Text.RegularExpressions.Regex.Replace(head, @"(\d+)\s+(\d+)\s+R",
          r =>
          {
            int rn;
            if (!map.TryGetValue(int.Parse(r.Groups[1].Value), out rn)) return r.Value;
            return rn + " " + r.Groups[2].Value + " R";
          });
      return head + tail;
    }

    // 解析单个 PDF：按源对象号升序把对象连续重编号（nextNum 递增），改写文件内引用；
    // 收集重编号后的 /Page 对象号。/Parent 留给 MergePdfs 在顶层 Pages 号确定后改写。
    static void ParsePdfObjects(string file, ref int nextNum, System.Text.Encoding latin1,
        List<KeyValuePair<int, string>> bodies, List<int> pages)
    {
      string text = latin1.GetString(File.ReadAllBytes(file));
      var re = new System.Text.RegularExpressions.Regex(@"(?m)^(\d+)\s+(\d+)\s+obj(.*?)endobj",
          System.Text.RegularExpressions.RegexOptions.Singleline);
      var src = new List<System.Text.RegularExpressions.Match>();
      foreach (System.Text.RegularExpressions.Match m in re.Matches(text)) src.Add(m);
      src.Sort((a, b) => int.Parse(a.Groups[1].Value).CompareTo(int.Parse(b.Groups[1].Value)));

      var map = new Dictionary<int, int>();              // 源对象号 → 新对象号
      int prev = -1;
      foreach (var m in src)
      {
        int old = int.Parse(m.Groups[1].Value);
        if (old == prev) continue;                       // 同号重复定义只保留一份
        prev = old;
        map[old] = ++nextNum;
      }

      var byNum = new Dictionary<int, string>();         // 新对象号 → 对象体（引用已改写）
      var pageSet = new List<int>();
      prev = -1;
      foreach (var m in src)
      {
        int old = int.Parse(m.Groups[1].Value);
        if (old == prev) continue;
        prev = old;
        int num = map[old];
        string body = PdfRewriteRefs(m.Groups[3].Value, map);
        byNum[num] = body;
        if (System.Text.RegularExpressions.Regex.IsMatch(body, @"/Type\s*/\s*Page\b")) pageSet.Add(num);
      }

      // 继承属性拍平：Teigha 把 /Resources 挂在 /Pages 节点上，页面对象本身没有。
      // 合并后顶层 /Pages 是新建的、不含这些属性，不补进去等于丢掉资源字典，
      // 内容流里的 /OC0 之类的资源名全都解析不到，页面就画不出来了。
      foreach (int p in pageSet)
      {
        string body = byNum[p];
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
          if (System.Text.RegularExpressions.Regex.IsMatch(body, @"/" + k + @"[\s/<]")) have.Add(k);
        var attrs = new System.Text.StringBuilder();
        string cur = body;
        for (int depth = 0; depth < 8 && have.Count < 4; depth++)
        {
          var pm = System.Text.RegularExpressions.Regex.Match(cur, @"/Parent\s+(\d+)\s+\d+\s+R");
          string parent;
          if (!pm.Success || !byNum.TryGetValue(int.Parse(pm.Groups[1].Value), out parent)) break;
          foreach (var k in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
          {
            if (have.Contains(k)) continue;
            string v = PdfVal(parent, k);
            if (v == null) continue;
            have.Add(k);
            attrs.Append('/').Append(k).Append(' ').Append(v).Append(' ');
          }
          cur = parent;
        }
        int at = body.IndexOf("<<", StringComparison.Ordinal);
        if (attrs.Length > 0 && at >= 0)
          byNum[p] = body.Substring(0, at + 2) + attrs + body.Substring(at + 2);
      }

      // 页面顺序按 trailer → /Root → /Pages → /Kids 递归走：Teigha 的多布局文件是把
      // 每个布局各自完整的 PDF 追加到同一个文件里，只有最后一个 trailer 的 /Root 是权威页面树，
      // 直接按对象号排会把布局顺序搞乱。
      var ordered = new List<int>();
      var rm = System.Text.RegularExpressions.Regex.Match(text, @"/Root\s+(\d+)\s+\d+\s+R");
      int rootNum;
      if (rm.Success && map.TryGetValue(int.Parse(rm.Groups[1].Value), out rootNum))
        CollectPdfPages(byNum, rootNum, pageSet, ordered, 0);
      pageSet.Sort();
      foreach (int n in pageSet) if (!ordered.Contains(n)) ordered.Add(n);
      pages.AddRange(ordered);

      var all = new List<int>(byNum.Keys);
      all.Sort();
      foreach (int n in all) bodies.Add(new KeyValuePair<int, string>(n, byNum[n]));
    }

    // 从 catalog 顺着 /Pages → /Kids 收集页面对象号（保持文档顺序）。
    static void CollectPdfPages(Dictionary<int, string> byNum, int num, List<int> pageSet, List<int> ordered, int depth)
    {
      if (depth > 32) return;
      string body;
      if (!byNum.TryGetValue(num, out body)) return;
      if (pageSet.Contains(num)) { if (!ordered.Contains(num)) ordered.Add(num); return; }
      var km = System.Text.RegularExpressions.Regex.Match(body, @"/Kids\s*\[([^\]]*)\]");
      if (!km.Success) return;
      foreach (System.Text.RegularExpressions.Match r in
          System.Text.RegularExpressions.Regex.Matches(km.Groups[1].Value, @"(\d+)\s+\d+\s+R"))
        CollectPdfPages(byNum, int.Parse(r.Groups[1].Value), pageSet, ordered, depth + 1);
    }

    // 解析打印相关的通用选项，返回位置参数（第一个为输出路径，其余为输入文件）
    static List<string> ParsePlotFlags(string[] args, int start,
        out bool mono, out string ctb, out string title, out string author,
        out string space, out string refer, out double pw, out double ph, out PlotExtras pex)
    {
      mono = false; ctb = ""; title = "DWG to PDF"; author = "dwg2pdf";
      space = "both"; refer = ""; pw = 0; ph = 0;
      pex = new PlotExtras();
      var positional = new List<string>();
      for (int i = start; i < args.Length; i++)
      {
        string a = args[i];
        if (a == "--mono") mono = true;
        else if (a == "--ctb" && i + 1 < args.Length) ctb = args[++i];
        else if (a == "--title" && i + 1 < args.Length) title = args[++i];
        else if (a == "--author" && i + 1 < args.Length) author = args[++i];
        else if (a == "--space" && i + 1 < args.Length) space = args[++i].ToLowerInvariant();
        else if (a == "--ref" && i + 1 < args.Length) refer = args[++i];
        else if (a == "--frame-order" && i + 1 < args.Length)               // 多图框打印顺序
        {
          string fo = args[++i].ToLowerInvariant();
          if (fo == "xrightydown" || fo == "xrightyup" || fo == "ydownxright" || fo == "yupxright")
            pex.frameOrder = fo;
          else
            Console.Error.WriteLine("WARN: 无效的 --frame-order: " + fo + " (可选 xrightydown/xrightyup/ydownxright/yupxright)");
        }
        else if (a == "--paper" && i + 2 < args.Length)
        {
          double.TryParse(args[++i], out pw);
          double.TryParse(args[++i], out ph);
        }
        // ---- 打印对话框高级选项 ----
        else if (a == "--center") pex.center = true;                       // 居中打印
        else if (a == "--offset" && i + 2 < args.Length)                   // 打印偏移 X Y (mm)
        {
          double.TryParse(args[++i], out pex.offX);
          double.TryParse(args[++i], out pex.offY);
        }
        else if (a == "--orientation" && i + 1 < args.Length)              // 图形方向
          pex.orientation = args[++i].ToLowerInvariant();
        else if (a == "--fit") pex.fit = true;                             // 布满图纸
        else if (a == "--scale" && i + 1 < args.Length)                    // 打印比例 N:D
        {
          string s = args[++i];
          double num = 0, den = 0;
          bool ok = false;
          // 兼容 N:D、N/D、N-D，以及只写分母（AutoCAD 习惯：100 = 1:100）
          int ci = s.IndexOfAny(new char[] { ':', '/', '-' });
          if (ci > 0 && double.TryParse(s.Substring(0, ci), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out num)
                    && double.TryParse(s.Substring(ci + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out den)
                    && num > 0 && den > 0) ok = true;
          else if (ci < 0 && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out den) && den > 0)
          { num = 1; ok = true; }
          if (ok) { pex.scaleNum = num; pex.scaleDen = den; pex.hasScale = true; }
          else Console.Error.WriteLine("WARN: 无效的 --scale 参数: " + s + " (应为 N:D，如 1:100)");
        }
        else if (a == "--scale-lineweights") pex.scaleLineweights = true;  // 缩放线宽
        else if (a == "--no-lineweights") pex.plotLineweights = false;     // 关闭打印对象线宽
        else if (a == "--plot-styles") pex.plotStyles = true;              // 按样式打印
        else if (a == "--hidden") pex.hidden = true;                       // 消隐打印
        else if (a == "--verbose" || a == "--log") { /* 仅控制日志开关, 此处忽略 */ }
        else if (a == "--help" || a == "-h") { PrintPlotUsage(); Environment.Exit(0); }
        else positional.Add(a);
      }
      return positional;
    }

    // 块参照统计（--list-refs 用）
    class RefStat
    {
      public string Name = "";
      public int Count;
      public string Space = "";
      public double W, H;
      public bool IsXref;   // true = 这一行是外部参照（被参照的 dwg/dxf 文件），不是块名
    }

    // ---------- 列出图纸中的块参照（供界面选择参照图框名） ----------
    // ODADrawingTools.exe --list-refs <in.dwg|.dxf>
    // 每行输出: 名称<TAB>实例数<TAB>所在空间<TAB>宽<TAB>高（图纸单位）[TAB>外部参照]
    // 外部参照的行: 名称是被参照的 dwg/dxf 文件名, 第 6 列固定为 "外部参照"
    static int RunListRefs(string[] args)
    {
      if (args.Length < 2)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --list-refs <输入.dwg|.dxf>");
        return 2;
      }
      string input = args[1];
      if (!File.Exists(input)) { Console.Error.WriteLine("ERROR: 输入文件不存在: " + input); return 1; }
      try
      {
        Services dd = InitRuntime();
        var map = new Dictionary<string, RefStat>(StringComparer.OrdinalIgnoreCase);
        var xmap = new Dictionary<string, RefStat>(StringComparer.OrdinalIgnoreCase);
        using (Database database = new Database(false, false))
        {
          if (input.ToUpperInvariant().EndsWith(".DXF")) database.DxfIn(input, "");
          else database.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, false, "");
          HostApplicationServices.WorkingDatabase = database;
          using (BlockTable bt = (BlockTable)database.BlockTableId.GetObject(OpenMode.ForRead))
          using (DBDictionary layoutDict = (DBDictionary)database.LayoutDictionaryId.GetObject(OpenMode.ForRead))
          {
            ScanRefs(map, xmap, bt[BlockTableRecord.ModelSpace], "模型");
            foreach (DBDictionaryEntry entry in layoutDict)
            {
              if (string.Equals(entry.Key, "Model", StringComparison.OrdinalIgnoreCase)) continue;
              ObjectId layId = layoutDict.GetAt(entry.Key);
              using (Layout lay = (Layout)layId.GetObject(OpenMode.ForRead))
                ScanRefs(map, xmap, lay.BlockTableRecordId, entry.Key);
            }
          }
        }
        dd.Dispose();
        var list = new List<RefStat>(map.Values);
        list.Sort(delegate(RefStat a, RefStat b)
        {
          int c = b.Count.CompareTo(a.Count);
          return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (RefStat s in list)
          Console.WriteLine(s.Name + "\t" + s.Count + "\t" + s.Space + "\t" +
            s.W.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "\t" +
            s.H.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        // 外部参照（被参照的 dwg/dxf）单独列出，作为图纸范围取值基准
        var xlist = new List<RefStat>(xmap.Values);
        xlist.Sort(delegate(RefStat a, RefStat b)
        {
          int c = b.Count.CompareTo(a.Count);
          return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (RefStat s in xlist)
          Console.WriteLine(s.Name + "\t" + s.Count + "\t" + s.Space + "\t" +
            s.W.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "\t" +
            s.H.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "\t外部参照");
        return 0;
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        return 1;
      }
    }

    // 扫描某空间下的块参照, 按名累计实例数与最大外接尺寸
    // xmap 单独累计外部参照(被参照的 dwg/dxf 文件) —— 外部参照的块名没意义,
    // 有意义的是被参照的文件名, 所以它单独作为一类候选列出。
    static void ScanRefs(Dictionary<string, RefStat> map, Dictionary<string, RefStat> xmap, ObjectId btrId, string space)
    {
      using (BlockTableRecord btr = (BlockTableRecord)btrId.GetObject(OpenMode.ForRead))
      {
        foreach (ObjectId id in btr)
        {
          using (DBObject obj = id.GetObject(OpenMode.ForRead))
          {
            BlockReference br = obj as BlockReference;
            if (br == null) continue;
            string name = br.Name == null ? "" : br.Name;
            if (name.Length == 0 || name.StartsWith("*")) continue;   // 匿名/动态块不作为参照名候选
            RefStat st;
            if (!map.TryGetValue(name, out st)) { st = new RefStat(); st.Name = name; map[name] = st; }
            st.Count++;
            if (st.Space.Length == 0) st.Space = space;
            else if (st.Space != space) st.Space = st.Space + "," + space;
            try
            {
              Extents3d ext = br.GeometricExtents;
              double w = ext.MaxPoint.X - ext.MinPoint.X, h = ext.MaxPoint.Y - ext.MinPoint.Y;
              if (w > st.W) st.W = w;
              if (h > st.H) st.H = h;
            }
            catch { }

            // 外部参照：按被参照的文件名单独累计一份
            string xpath = XRefPath(br);
            if (xpath.Length > 0)
            {
              string fn = System.IO.Path.GetFileName(xpath);
              if (fn.Length > 0)
              {
                RefStat xs;
                if (!xmap.TryGetValue(fn, out xs))
                { xs = new RefStat(); xs.Name = fn; xs.IsXref = true; xmap[fn] = xs; }
                xs.Count++;
                if (xs.Space.Length == 0) xs.Space = space;
                else if (xs.Space != space && xs.Space.Split(',').Length < 4)
                  xs.Space = xs.Space + "," + space;
                try
                {
                  Extents3d ext = br.GeometricExtents;
                  double w = ext.MaxPoint.X - ext.MinPoint.X, h = ext.MaxPoint.Y - ext.MinPoint.Y;
                  if (w > xs.W) xs.W = w;
                  if (h > xs.H) xs.H = h;
                }
                catch { }
              }
            }
          }
        }
      }
    }


    static int RunPlot(string[] args)
    {
      List<string> pos = ParsePlotFlags(args, 1, out bool mono, out string ctb, out string title,
          out string author, out string space, out string refer, out double pw, out double ph, out PlotExtras pex);
      if (pos.Count < 2)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --plot <输入.dwg|.dxf> <输出.pdf> [--ref 名称] [--space model|layout|both] [--mono] [--ctb x.ctb] [--paper W H] [--title T] [--author A]");
        return 2;
      }
      string input = pos[0], output = pos[1];
      if (!File.Exists(input)) { Console.Error.WriteLine("ERROR: 输入文件不存在: " + input); return 1; }
      ctb = ResolveCtb(ctb, mono);
      try
      {
        Services dd = InitRuntime();
        using (Database database = new Database(false, false))
        {
          if (input.ToUpperInvariant().EndsWith(".DXF")) database.DxfIn(input, "");
          else database.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, false, "");
          HostApplicationServices.WorkingDatabase = database;
          ApplyNonPlotLayersOff(database);
          FixEmptyTextStyles(database);
          NormalizeTextJustification(database);
          ApplyCtb(database, ctb);
          List<PlotJob> jobs = CollectPlotJobs(database, refer, space, pex.frameOrder);
          if (jobs.Count == 0)
          {
            Console.Error.WriteLine("ERROR: 未找到可打印内容 (参照: " + (refer.Length == 0 ? "<整图>" : refer) + ", 空间: " + space + ")");
            dd.Dispose();
            return 1;
          }
          Console.Error.WriteLine("STEP: 生成 " + jobs.Count + " 页...");
          List<string> temps = ExportJobsToTemps(database, jobs, title, author, true, false, pw, ph, pex);
          if (temps.Count == 1) File.Copy(temps[0], output, true);
          else MergePdfs(temps, output);
          foreach (var t in temps) { try { File.Delete(t); } catch { } }
          // 机器可读: 实际生成的页数（按参照图框分页时 = 命中的图框个数），供界面显示
          Console.WriteLine("PAGES: " + temps.Count);
        }
        dd.Dispose();
        Console.WriteLine("OK: " + output);
        Environment.Exit(0);
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        Console.Error.WriteLine("STACK: " + ex.StackTrace);
        Environment.Exit(1);
      }
      return 0;
    }

    static int RunPlotBatch(string[] args)
    {
      List<string> pos = ParsePlotFlags(args, 1, out bool mono, out string ctb, out string title,
          out string author, out string space, out string refer, out double pw, out double ph, out PlotExtras pex);
      if (pos.Count < 1)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --plot-batch <输出目录> [--ref 名称] [--space model|layout|both] [--mono] [--ctb x] <输入1> [输入2] ...");
        return 2;
      }
      string outDir = pos[0];
      var inputs = pos.Skip(1).ToList();
      if (inputs.Count == 0) { Console.Error.WriteLine("ERROR: 未提供输入文件"); return 2; }
      ctb = ResolveCtb(ctb, mono);
      Directory.CreateDirectory(outDir);
      Services dd = InitRuntime();
      try
      {
        int okCount = 0;
        foreach (string input in inputs)
        {
          if (!File.Exists(input)) { Console.Error.WriteLine("WARN: 跳过不存在的文件: " + input); continue; }
          string baseName = Path.GetFileNameWithoutExtension(input);
          string outPdf = Path.Combine(outDir, baseName + ".pdf");
          try
          {
            Console.Error.WriteLine("STEP: 处理 " + input);
            using (Database database = new Database(false, false))
            {
              if (input.ToUpperInvariant().EndsWith(".DXF")) database.DxfIn(input, "");
              else database.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, false, "");
              HostApplicationServices.WorkingDatabase = database;
              ApplyNonPlotLayersOff(database);
              FixEmptyTextStyles(database);
              NormalizeTextJustification(database);
              ApplyCtb(database, ctb);
              List<PlotJob> jobs = CollectPlotJobs(database, refer, space, pex.frameOrder);
              if (jobs.Count == 0) { Console.Error.WriteLine("WARN: " + input + " 无匹配打印内容，跳过"); continue; }
              List<string> temps = ExportJobsToTemps(database, jobs, title, author, true, false, pw, ph, pex);
              if (temps.Count == 1) File.Copy(temps[0], outPdf, true);
              else MergePdfs(temps, outPdf);
              foreach (var t in temps) { try { File.Delete(t); } catch { } }
              Console.WriteLine("OK: " + outPdf);
              okCount++;
            }
          }
          catch (System.Exception ex)
          {
            Console.Error.WriteLine("FAIL: " + input + " -> " + ex.Message);
          }
        }
        dd.Dispose();
        Console.Error.WriteLine("STEP: 完成 " + okCount + "/" + inputs.Count);
        Environment.Exit(okCount == inputs.Count ? 0 : 1);
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        dd.Dispose();
        Environment.Exit(1);
      }
      return 0;
    }

    static int RunPlotMerge(string[] args)
    {
      List<string> pos = ParsePlotFlags(args, 1, out bool mono, out string ctb, out string title,
          out string author, out string space, out string refer, out double pw, out double ph, out PlotExtras pex);
      if (pos.Count < 2)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --plot-merge <输出.pdf> [--ref 名称] [--space model|layout|both] [--mono] [--ctb x] <输入1> [输入2] ...");
        return 2;
      }
      string output = pos[0];
      var inputs = pos.Skip(1).ToList();
      if (inputs.Count == 0) { Console.Error.WriteLine("ERROR: 未提供输入文件"); return 2; }
      ctb = ResolveCtb(ctb, mono);
      Services dd = InitRuntime();
      var allTemps = new List<string>();
      try
      {
        int okCount = 0;
        foreach (string input in inputs)
        {
          if (!File.Exists(input)) { Console.Error.WriteLine("WARN: 跳过不存在的文件: " + input); continue; }
          try
          {
            Console.Error.WriteLine("STEP: 处理 " + input);
            using (Database database = new Database(false, false))
            {
              if (input.ToUpperInvariant().EndsWith(".DXF")) database.DxfIn(input, "");
              else database.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, false, "");
              HostApplicationServices.WorkingDatabase = database;
              ApplyNonPlotLayersOff(database);
              FixEmptyTextStyles(database);
              NormalizeTextJustification(database);
              ApplyCtb(database, ctb);
              List<PlotJob> jobs = CollectPlotJobs(database, refer, space, pex.frameOrder);
              if (jobs.Count == 0) { Console.Error.WriteLine("WARN: " + input + " 无匹配打印内容，跳过"); continue; }
              allTemps.AddRange(ExportJobsToTemps(database, jobs, title, author, true, false, pw, ph, pex));
              okCount++;
            }
          }
          catch (System.Exception ex)
          {
            Console.Error.WriteLine("FAIL: " + input + " -> " + ex.Message);
          }
        }
        if (allTemps.Count == 0) { Console.Error.WriteLine("ERROR: 未生成任何打印页"); dd.Dispose(); return 1; }
        MergePdfs(allTemps, output);
        foreach (var t in allTemps) { try { File.Delete(t); } catch { } }
        dd.Dispose();
        Console.WriteLine("OK: " + output);
        Environment.Exit(0);
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        dd.Dispose();
        Environment.Exit(1);
      }
      return 0;
    }

    static int RunMergeList(string[] args)
    {
      if (args.Length < 3)
      {
        Console.Error.WriteLine("用法: ODADrawingTools.exe --merge-list <输出.pdf> <列表文件>");
        return 2;
      }
      string output = args[1], listFile = args[2];
      if (!File.Exists(listFile)) { Console.Error.WriteLine("ERROR: 列表文件不存在: " + listFile); return 1; }
      var inputs = new List<string>();
      foreach (string line in File.ReadAllLines(listFile, Encoding.UTF8))
      {
        string t = line.Trim();
        if (t.Length == 0 || t.StartsWith("#")) continue;
        if (File.Exists(t)) inputs.Add(t);
        else Console.Error.WriteLine("WARN: 跳过不存在的文件: " + t);
      }
      if (inputs.Count == 0) { Console.Error.WriteLine("ERROR: 列表中没有可合并的 PDF"); return 1; }
      try
      {
        MergePdfs(inputs, output);
        Console.WriteLine("OK: " + output);
        return 0;
      }
      catch (System.Exception ex)
      {
        Console.Error.WriteLine("ERROR: 合并失败: " + ex.Message);
        return 1;
      }
    }

    static void PrintPlotUsage()
    {
      Console.WriteLine("按参照图框分页打印:");
      Console.WriteLine("  --plot <输入.dwg|.dxf> <输出.pdf> [--ref 名称] [--space model|layout|both]");
      Console.WriteLine("  --plot-batch <输出目录> [选项] <输入1> [输入2] ...");
      Console.WriteLine("  --plot-merge <输出.pdf> [选项] <输入1> [输入2> ...");
      Console.WriteLine("  选项:");
      Console.WriteLine("    --ref <名称>   按块名/外部参照名过滤（支持 * ? 通配），每个实例一页；省略则按整布局打印");
      Console.WriteLine("    --space <值>   model=仅模型空间, layout=仅布局空间, both=全部(默认)");
      Console.WriteLine("    --frame-order <值>  多图框页序: xrightydown=先左→右再上→下(默认) /");
      Console.WriteLine("                        xrightyup=先左→右再下→上 / ydownxright=先上→下再左→右 /");
      Console.WriteLine("                        yupxright=先下→上再左→右");
      Console.WriteLine("    --mono         单色输出 (需 monochrome.ctb/monochrome.stb)");
      Console.WriteLine("    --ctb <文件>   应用 CTB/STB 打印样式表");
      Console.WriteLine("    --paper W H    自定义纸张尺寸(mm)，覆盖自适应尺寸");
      Console.WriteLine("    --center             居中打印");
      Console.WriteLine("    --offset X Y         打印偏移(mm)，原点在可打印区域");
      Console.WriteLine("    --orientation <值>   landscape=横向(默认) / portrait=纵向");
      Console.WriteLine("    --fit                布满图纸");
      Console.WriteLine("    --scale N:D          自定义打印比例，如 1:100 (与 --fit 同给时 --fit 优先)");
      Console.WriteLine("    --scale-lineweights  缩放线宽");
      Console.WriteLine("    --no-lineweights     不打印对象线宽(默认打印)");
      Console.WriteLine("    --plot-styles        按样式打印(配合 --ctb)");
      Console.WriteLine("    --hidden             消隐打印");
      Console.WriteLine("    --title <文本> 设置 PDF 标题");
      Console.WriteLine("    --author <文本> 设置 PDF 作者");
    }
  }
}
