using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DwgBatchPlotTool
{
    internal sealed class EngineResult
    {
        public int ExitCode;
        public string Output = "";
        public string Error = "";
        public bool TimedOut;
        public bool Cancelled;
        public bool StartFailed;
        public int Pages = -1;   // 引擎输出的 "PAGES: n", -1 = 未知
        public bool Ok { get { return !TimedOut && !Cancelled && ExitCode == 0; } }

        /// <summary>把引擎输出整理成一行可读的失败原因。</summary>
        public string Describe()
        {
            if (Cancelled) return "已取消";
            if (TimedOut) return "超时";
            if (StartFailed) return Error.Length > 0 ? Error : "引擎启动失败";
            var lines = ExtractMessages();
            if (lines.Count > 0) return string.Join("; ", lines.ToArray());
            if (ExitCode == 0) return "成功";
            if (ExitCode < 0) return "引擎异常退出 (" + ExitCode + ")";
            return "失败 (退出码 " + ExitCode + ")";
        }

        /// <summary>从引擎日志里挑出值得展示的行 (去掉大量 INFO 噪声)。</summary>
        public List<string> ExtractMessages()
        {
            var res = new List<string>();
            foreach (var raw in (Output + "\n" + Error).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("INFO:")) continue;
                if (line.StartsWith("STEP:")) continue;
                if (line.StartsWith("OK:")) continue;
                if (line.StartsWith("WARN:") || line.StartsWith("ERROR:") || line.StartsWith("FAIL:"))
                {
                    if (res.Count < 4) res.Add(line);
                }
            }
            return res;
        }
    }

    /// <summary>图纸里的一个块参照 (可作为参照图框名)。</summary>
    internal sealed class RefInfo
    {
        public string Name = "";
        public int Count;
        public string Space = "";
        public double W, H;
        public string File = "";   // 该参照所在图纸
        public bool IsXref;         // true = 这是外部参照(被参照的 dwg/dxf 文件), 不是块名
    }

    /// <summary>
    /// 引擎调用层。引擎是独立 exe, 崩溃只影响单个文件的转换, 不会带走整个批处理。
    /// </summary>
    internal static class OdaRunner
    {
        /// <summary>程序目录 (exe 所在目录, 存放 ODA\ / Fonts\ / PlotStyles\)。环境变量 DWGBP_ROOT 可覆盖, 供自检程序使用。</summary>
        public static string RootDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("DWGBP_ROOT");
                if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);

                // 只用 exe 同目录下的 ODA\ / Fonts\ / PlotStyles\, 不再往上层找
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        public static string EngineDir { get { return Path.Combine(RootDir, "ODA"); } }
        public static string EngineExe { get { return Path.Combine(EngineDir, "ODADrawingTools.exe"); } }
        public static string PlotStylesDir { get { return Path.Combine(RootDir, "PlotStyles"); } }
        public static string DefaultCtb { get { return Path.Combine(PlotStylesDir, "monochrome.ctb"); } }
        public static bool EngineAvailable { get { return File.Exists(EngineExe); } }

        /// <summary>字体目录 (可多个, ; 分隔)。默认程序根目录下的 Fonts\，界面可改。</summary>
        public static string FontsDir = Path.Combine(RootDir, "Fonts");

        public static EngineResult Run(IList<string> args, int timeoutSec, CancellationToken token)
        {
            var res = new EngineResult();
            var psi = new ProcessStartInfo();
            psi.FileName = EngineExe;
            psi.Arguments = BuildCommandLine(args);
            psi.WorkingDirectory = EngineDir;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(FontsDir)) psi.EnvironmentVariables["DWGBP_FONTS"] = FontsDir;

            using (var p = new Process())
            {
                p.StartInfo = psi;
                try { p.Start(); }
                catch (Exception ex)
                {
                    res.Error = "无法启动引擎: " + ex.Message;
                    res.ExitCode = -1;
                    res.StartFailed = true;
                    return res;
                }

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                using (token.Register(delegate { try { if (!p.HasExited) p.Kill(); } catch { } }))
                {
                    bool exited = p.WaitForExit(timeoutSec <= 0 ? int.MaxValue : timeoutSec * 1000);
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        p.WaitForExit();
                        res.TimedOut = !token.IsCancellationRequested;
                        res.Cancelled = token.IsCancellationRequested;
                    }
                    else
                    {
                        p.WaitForExit();   // 确保重定向流读完
                    }
                }

                res.Output = Safe(() => outTask.Result);
                res.Error = Safe(() => errTask.Result);
                res.Pages = ParsePages(res.Output);
                if (!res.TimedOut && !res.Cancelled) res.ExitCode = p.ExitCode;
                else res.ExitCode = -1;
            }
            return res;
        }

        /// <summary>列出图纸里的块参照 (名称/实例数/空间/尺寸), 供界面挑选参照图框名。</summary>
        public static List<RefInfo> ListRefs(string file, int timeoutSec, CancellationToken token, out string error)
        {
            var list = new List<RefInfo>();
            error = "";
            var res = Run(new List<string> { "--list-refs", file, "--verbose" }, timeoutSec, token);
            if (!res.Ok)
            {
                error = res.Describe();
                return list;
            }
            foreach (string raw in (res.Output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                if (f.Length < 2) continue;
                var ri = new RefInfo { Name = f[0], File = file };
                int c;
                if (int.TryParse(f[1], out c)) ri.Count = c;
                if (f.Length > 2) ri.Space = f[2];
                double d;
                if (f.Length > 3 && double.TryParse(f[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d)) ri.W = d;
                if (f.Length > 4 && double.TryParse(f[4], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d)) ri.H = d;
                // 第 6 列 = "外部参照" 表示这一行是被参照的 dwg/dxf 文件, 不是块名
                if (f.Length > 5 && f[5].Trim() == "外部参照") ri.IsXref = true;
                list.Add(ri);
            }
            return list;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        /// <summary>解析引擎输出的 "PAGES: n" 行 (实际生成的页数)。</summary>
        public static int ParsePages(string output)
        {
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("PAGES:")) continue;
                int n;
                if (int.TryParse(line.Substring(6).Trim(), out n)) return n;
            }
            return -1;
        }

        /// <summary>
        /// 解析引擎日志里的布局顺序: "INFO: 布局[tab=3] 总图(一)LV2"。
        /// 引擎按 AutoCAD 的布局标签页顺序出图, 这个顺序即 PDF 的页序。
        /// </summary>
        public static List<KeyValuePair<int, string>> ParseLayoutTabs(string log)
        {
            var res = new List<KeyValuePair<int, string>>();
            foreach (string raw in (log ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("INFO: 布局[tab=")) continue;
                int close = line.IndexOf(']');
                if (close < 0) continue;
                string tabStr = line.Substring("INFO: 布局[tab=".Length, close - "INFO: 布局[tab=".Length);
                int tab;
                if (!int.TryParse(tabStr, out tab)) continue;
                string name = line.Substring(close + 1).Trim();
                res.Add(new KeyValuePair<int, string>(tab, name));
            }
            return res;
        }

        /// <summary>按 Windows 命令行规则拼参数 (与 CreateProcess 解析规则一致)。</summary>
        public static string BuildCommandLine(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            if (s.Length > 0 && s.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
                if (slashes > 0) { sb.Append('\\', slashes); slashes = 0; }
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
