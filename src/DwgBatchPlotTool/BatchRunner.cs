using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DwgBatchPlotTool
{
    internal sealed class FileResult
    {
        public int Index;
        public string Input;
        public bool Ok;
        public string Output;
        public string Message;
        public long ElapsedMs;
        public int Pages = -1;   // 实际生成的页数 (按参照图框分页时 = 命中的图框数), -1 = 未知
    }

    internal sealed class BatchResult
    {
        public int Ok;
        public int Fail;
        public int Pages;
        public string MergedPath;
        public string Error;
    }

    /// <summary>
    /// 单个文件对全局参数的覆盖, 与 inputs 一一对应。
    /// 每个字段留空 = 这一项沿用全局值; 以后列表里再加覆盖列, 只加字段即可, 不用再改 Run 的签名。
    /// </summary>
    internal sealed class FileOverrides
    {
        public string RefName = "";     // 参照名
        public string Scale = "";       // 打印比例
        public string PlotStyle = "";   // 打印样式表 (.ctb/.stb) 的路径或文件名
        public string RelDir = "";      // 独立 PDF 输出时, 按源目录结构建的相对子目录
    }

    /// <summary>批量转换核心: 每个文件一个子进程, 单个文件失败不影响其它文件。</summary>
    internal static class BatchRunner
    {
        /// <summary>overrides 与 inputs 一一对应 (可为 null / 比 inputs 短): 逐文件覆盖参照名、比例、打印样式, 以及独立 PDF 的相对子目录。</summary>
        public static BatchResult Run(PlotOptions opt, IList<string> inputs, Action<FileResult> onFile,
            Action<string> log, CancellationToken token, IList<FileOverrides> overrides = null)
        {
            var r = new BatchResult();
            string tempDir = null;
            var produced = new List<string>();
            try
            {
                Directory.CreateDirectory(opt.OutputDir);
                if (opt.Mode == OutputMode.Merged)
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "dwgbp_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                }
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < inputs.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    string input = inputs[i];
                    var ov = (overrides != null && i < overrides.Count) ? overrides[i] : null;
                    string outPdf;
                    if (opt.Mode == OutputMode.Merged)
                    {
                        outPdf = Path.Combine(tempDir, i.ToString("D5") + ".pdf");
                    }
                    else
                    {
                        string baseName = Path.GetFileNameWithoutExtension(input);
                        string sub = ov != null ? (ov.RelDir ?? "").Trim() : "";
                        string dir = sub.Length > 0 ? Path.Combine(opt.OutputDir, sub) : opt.OutputDir;
                        Directory.CreateDirectory(dir);
                        string name = baseName + ".pdf";
                        outPdf = Path.Combine(dir, name);
                        int k = 2;
                        while (!used.Add(outPdf)) { name = baseName + " (" + k + ").pdf"; k++; outPdf = Path.Combine(dir, name); }
                    }

                    var sw = Stopwatch.StartNew();
                    // 单个文件指定的参照名/比例/打印样式优先, 没指定就用全局的
                    var o = ApplyOverrides(opt, ov, input, log);
                    var res = OdaRunner.Run(o.BuildPlotArgs(input, outPdf), opt.TimeoutSec, token);
                    sw.Stop();

                    bool ok = res.Ok && File.Exists(outPdf) && new FileInfo(outPdf).Length > 0;
                    var fr = new FileResult { Index = i, Input = input, Ok = ok, ElapsedMs = sw.ElapsedMilliseconds, Pages = res.Pages };
                    if (ok)
                    {
                        r.Ok++;
                        r.Pages += res.Pages > 0 ? res.Pages : 0;
                        produced.Add(outPdf);
                        fr.Output = outPdf;
                        fr.Message = "成功";
                        Log(log, "成功: " + Path.GetFileName(input) + " -> " + outPdf + " (" + sw.ElapsedMilliseconds + " ms" + (res.Pages > 0 ? ", " + res.Pages + " 页" : "") + ")");
                    }
                    else
                    {
                        r.Fail++;
                        fr.Message = res.Describe();
                        Log(log, "失败: " + Path.GetFileName(input) + " -> " + fr.Message);
                        foreach (var m in res.ExtractMessages()) Log(log, "      " + m);
                    }
                    if (onFile != null) onFile(fr);
                }

                if (opt.Mode == OutputMode.Merged && !token.IsCancellationRequested)
                {
                    if (produced.Count == 0)
                    {
                        r.Error = "合并失败: 没有成功生成的 PDF";
                        Log(log, r.Error);
                    }
                    else
                    {
                        string merged = Path.Combine(opt.OutputDir, SafeName(opt.MergedName) + ".pdf");
                        string listFile = Path.Combine(tempDir, "merge-list.txt");
                        File.WriteAllLines(listFile, produced, new UTF8Encoding(false));
                        var res = OdaRunner.Run(new List<string> { "--merge-list", merged, listFile }, 600, token);
                        if (res.Ok && File.Exists(merged))
                        {
                            r.MergedPath = merged;
                            Log(log, "合并完成: " + merged + " (" + produced.Count + " 个文件, 共 " + r.Pages + " 页, " + new FileInfo(merged).Length + " 字节)");
                        }
                        else
                        {
                            r.Error = "合并失败: " + res.Describe();
                            Log(log, r.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                Log(log, "异常: " + ex.Message);
            }
            finally
            {
                if (tempDir != null) { try { Directory.Delete(tempDir, true); } catch { } }
            }
            return r;
        }

        /// <summary>
        /// 把单文件覆盖项应用到全局参数。没有任何覆盖时直接返回全局对象 (不复制)。
        /// 打印样式找不到时回退全局样式, 并在日志里写明; 找到时强制打开 --plot-styles,
        /// 否则即使传了 --ctb, 引擎也会因为「不按样式打印」而忽略样式表。
        /// </summary>
        internal static PlotOptions ApplyOverrides(PlotOptions opt, FileOverrides ov, string input, Action<string> log)
        {
            if (ov == null || opt == null) return opt;
            string refN = (ov.RefName ?? "").Trim();
            string sc = (ov.Scale ?? "").Trim();
            string ps = (ov.PlotStyle ?? "").Trim();
            if (refN.Length == 0 && sc.Length == 0 && ps.Length == 0) return opt;

            var o = opt.Clone();
            if (refN.Length > 0) o.RefName = refN;
            if (sc.Length > 0) o.Scale = sc;
            if (ps.Length > 0)
            {
                bool fallback;
                string ctb = ResolvePlotStyle(ps, opt.CtbFile, input, out fallback);
                if (fallback)
                {
                    // 完全沿用全局 (包括「不按样式打印」), 不因为一次失败的选择把样式打印打开
                    Log(log, "WARN: " + Path.GetFileName(input ?? "") + " 的打印样式 \"" + ps + "\" 找不到, 已回退全局打印样式");
                }
                else if (ctb.Length > 0)
                {
                    o.CtbFile = ctb;
                    o.PlotStyles = true;
                }
            }
            return o;
        }

        /// <summary>
        /// 解析单文件打印样式: 不选 (空) 或最终找不到文件 -> 用全局打印样式。
        /// 相对名的查找顺序: 程序 PlotStyles\ -> 全局样式所在目录 -> 图纸所在目录。
        /// fallback=true 表示「选了但没找到」, 供调用方记日志; 空选择不算回退。
        /// </summary>
        public static string ResolvePlotStyle(string chosen, string globalCtb, string drawing, out bool fallback)
        {
            fallback = false;
            string global = (globalCtb ?? "").Trim();
            chosen = (chosen ?? "").Trim();
            if (chosen.Length == 0) return global;

            var tried = new List<string>();
            if (Path.IsPathRooted(chosen)) tried.Add(chosen);
            else
            {
                tried.Add(Path.Combine(OdaRunner.PlotStylesDir, chosen));
                string gd = global.Length > 0 ? SafeDir(global) : null;
                if (!string.IsNullOrEmpty(gd)) tried.Add(Path.Combine(gd, chosen));
                string dd = SafeDir(drawing);
                if (!string.IsNullOrEmpty(dd)) tried.Add(Path.Combine(dd, chosen));
            }
            foreach (var p in tried)
            {
                try { if (File.Exists(p)) return Path.GetFullPath(p); } catch { }
            }
            fallback = true;
            return global;
        }

        private static string SafeDir(string file)
        {
            try { return string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(Path.GetFullPath(file)); }
            catch { return null; }
        }

        public static string SafeName(string n)
        {
            var s = string.IsNullOrWhiteSpace(n) ? "合并输出" : n.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static void Log(Action<string> log, string msg)
        {
            if (log != null) log(msg);
        }
    }
}

// 本程序开源免费使用，严禁转卖，否则生儿子没屁眼。
