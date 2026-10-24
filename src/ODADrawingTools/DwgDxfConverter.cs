// DWG/DXF 格式互相转换 (基于 Teigha 4.02, 与 dwg2pdf 共享同一 ODA 运行时)
//   单文件: DwgDxfConverter.Convert(input, output, version)
//   批量:   DwgDxfConverter.ConvertBatch(files, outputDir, outputExt, version, progress)
//   版本:   DwgDxfConverter.ParseVersion("ACAD2013" / "AC1027" / "2013" / "newest")
using System;
using System.Collections.Generic;
using System.IO;
using Teigha.Runtime;
using Teigha.DatabaseServices;

namespace ODADrawingTools
{
  public static class DwgDxfConverter
  {
    static Services _dd;
    static bool _inited;

    // ---------- ODA 运行时 (进程内只需初始化一次) ----------
    public static void EnsureRuntime()
    {
      if (_inited) return;
      _dd = new Services();
      SystemObjects.DynamicLinker.LoadApp("PlotSettingsValidator", false, false);
      HostApplicationServices.Current = new HostApp();
      _inited = true;
    }

    public static void Shutdown()
    {
      if (!_inited) return;
      // 注意: 不能先置空 HostApplicationServices.Current, Services.UnInit 依赖它
      _dd.Dispose();
      _dd = null;
      _inited = false;
    }

    // ---------- 单文件转换 ----------
    // 方向由输出文件扩展名决定: .dwg -> 存 DWG; .dxf -> 存 DXF
    public static void Convert(string inputFile, string outputFile, DwgVersion version)
    {
      if (inputFile == null) throw new ArgumentNullException("inputFile");
      if (outputFile == null) throw new ArgumentNullException("outputFile");
      if (!File.Exists(inputFile))
        throw new FileNotFoundException("输入文件不存在: " + inputFile);

      string inExt = Path.GetExtension(inputFile).ToUpperInvariant();
      string outExt = Path.GetExtension(outputFile).ToUpperInvariant();
      bool fromDwg = (inExt == ".DWG");
      bool toDwg = (outExt == ".DWG");
      if (inExt != ".DWG" && inExt != ".DXF")
        throw new ArgumentException("不支持的输入格式 (仅 .dwg/.dxf): " + inputFile);
      if (outExt != ".DWG" && outExt != ".DXF")
        throw new ArgumentException("不支持的输出格式 (仅 .dwg/.dxf): " + outputFile);
      if (inExt == outExt)
        throw new ArgumentException("输入输出格式相同, 无需转换: " + inputFile);

      EnsureRuntime();

      string outDir = Path.GetDirectoryName(Path.GetFullPath(outputFile));
      if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

      using (Database db = new Database(false, false))
      {
        if (fromDwg)
          db.ReadDwgFile(inputFile, FileOpenMode.OpenForReadAndAllShare, false, "");
        else
          db.DxfIn(inputFile, "");
        HostApplicationServices.WorkingDatabase = db;
        try
        {
          if (toDwg)
            db.SaveAs(outputFile, version);
          else
            db.DxfOut(outputFile, 0, version);
        }
        finally
        {
          HostApplicationServices.WorkingDatabase = null;
        }
      }
    }

    public static void DxfToDwg(string inputDxf, string outputDwg, DwgVersion version)
    {
      Convert(inputDxf, outputDwg, version);
    }

    public static void DwgToDxf(string inputDwg, string outputDxf, DwgVersion version)
    {
      Convert(inputDwg, outputDxf, version);
    }

    // ---------- 批量转换 ----------
    public sealed class BatchResult
    {
      public string Input;
      public string Output;
      public bool Ok;
      public string Error;
    }

    // inputFiles : 输入文件列表 (.dwg/.dxf 可混合, 自动按源扩展名读入)
    // outputExt  : 目标扩展名 ".dwg" 或 ".dxf"
    // outputDir  : 输出目录; 传 null/"" 则输出到各自源文件同目录
    // progress   : 可选进度回调 (收到 "i/n: 文件名" 形式的文本)
    public static List<BatchResult> ConvertBatch(
        IEnumerable<string> inputFiles, string outputDir, string outputExt,
        DwgVersion version, Action<string> progress)
    {
      if (inputFiles == null) throw new ArgumentNullException("inputFiles");
      string ext = (outputExt ?? "").Trim().ToLowerInvariant();
      if (ext != ".dwg" && ext != ".dxf")
        throw new ArgumentException("outputExt 必须为 \".dwg\" 或 \".dxf\"");

      List<BatchResult> results = new List<BatchResult>();
      EnsureRuntime();

      List<string> files = new List<string>(inputFiles);
      int n = files.Count, i = 0;
      foreach (string input in files)
      {
        i++;
        BatchResult r = new BatchResult();
        r.Input = input;
        results.Add(r);
        try
        {
          string baseName = Path.GetFileNameWithoutExtension(input);
          string outDir = string.IsNullOrEmpty(outputDir)
              ? Path.GetDirectoryName(Path.GetFullPath(input))
              : outputDir;
          string output = Path.Combine(outDir, baseName + ext);
          r.Output = output;
          if (progress != null) progress(i + "/" + n + ": " + Path.GetFileName(input));
          Convert(input, output, version);
          r.Ok = true;
        }
        catch (System.Exception ex)
        {
          r.Ok = false;
          r.Error = ex.Message;
        }
      }
      return results;
    }

    // ---------- 版本解析 ----------
    // 支持: "newest"/"current", "ACAD2013"/"2013"/"13", "AC1027"
    public static DwgVersion ParseVersion(string text)
    {
      DwgVersion v;
      if (ParseVersion(text, out v)) return v;
      throw new ArgumentException("无法识别的版本: " + text +
          " (支持 newest, 2000, 2004, 2007, 2010, 2013 或 AC1015/AC1018/AC1021/AC1024/AC1027)");
    }

    public static bool TryParseVersion(string text, out DwgVersion version)
    {
      return ParseVersion(text, out version);
    }

    static bool ParseVersion(string text, out DwgVersion version)
    {
      version = DwgVersion.Newest;
      if (string.IsNullOrEmpty(text)) return true; // 默认 newest

      string s = text.Trim().ToLowerInvariant();
      if (s == "newest" || s == "latest" || s == "max") { version = DwgVersion.Newest; return true; }
      if (s == "current") { version = DwgVersion.Current; return true; }

      // 年份别名 (注意: ODA 2004 版内部名为 AC1800, 无 AC1018)
      switch (s)
      {
        case "2000": case "acad2000": s = "ac1015"; break;
        case "2004": case "acad2004": s = "ac1800"; break;
        case "2007": case "acad2007": s = "ac1021"; break;
        case "2010": case "acad2010": s = "ac1024"; break;
        case "2013": case "acad2013": s = "ac1027"; break;
        default: break;
      }
      if (s == "ac" || s.StartsWith("acad")) s = s.Substring(4); // "acad1015" -> "1015"

      // 纯年份数字 (2000+) 按 ODA 内部名换算
      int year;
      if (s.Length == 4 && int.TryParse(s, out year) && year >= 2000 && year <= 2013)
      {
        switch (year)
        {
          case 2000: s = "ac1015"; break;
          case 2004: s = "ac1800"; break;
          case 2007: s = "ac1021"; break;
          case 2010: s = "ac1024"; break;
          case 2013: s = "ac1027"; break;
        }
      }

      if (!s.StartsWith("ac")) s = "ac" + s;
      return Enum.TryParse(s, true, out version) && version != DwgVersion.Unknown;
    }
  }
}
