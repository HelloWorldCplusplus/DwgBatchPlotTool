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

    /// <summary>批量转换核心: 每个文件一个子进程, 单个文件失败不影响其它文件。</summary>
    internal static class BatchRunner
    {
        /// <summary>refNames/scales/relDirs 与 inputs 一一对应: 单文件指定的参照名/比例, 独立PDF 时的相对子目录 (空 = 平铺); 为空则用 opt 里的全局值。</summary>
        public static BatchResult Run(PlotOptions opt, IList<string> inputs, Action<FileResult> onFile, Action<string> log, CancellationToken token, IList<string> refNames = null, IList<string> scales = null, IList<string> relDirs = null)
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
                    string outPdf;
                    if (opt.Mode == OutputMode.Merged)
                    {
                        outPdf = Path.Combine(tempDir, i.ToString("D5") + ".pdf");
                    }
                    else
                    {
                        string baseName = Path.GetFileNameWithoutExtension(input);
                        string sub = (relDirs != null && i < relDirs.Count) ? (relDirs[i] ?? "").Trim() : "";
                        string dir = sub.Length > 0 ? Path.Combine(opt.OutputDir, sub) : opt.OutputDir;
                        Directory.CreateDirectory(dir);
                        string name = baseName + ".pdf";
                        outPdf = Path.Combine(dir, name);
                        int k = 2;
                        while (!used.Add(outPdf)) { name = baseName + " (" + k + ").pdf"; k++; outPdf = Path.Combine(dir, name); }
                    }

                    var sw = Stopwatch.StartNew();
                    // 单个文件指定的参照名/比例优先, 没指定就用全局的
                    var o = opt;
                    string refN = (refNames != null && i < refNames.Count) ? refNames[i] : null;
                    string sc = (scales != null && i < scales.Count) ? scales[i] : null;
                    if (!string.IsNullOrWhiteSpace(refN) || !string.IsNullOrWhiteSpace(sc))
                    {
                        o = opt.Clone();
                        if (!string.IsNullOrWhiteSpace(refN)) o.RefName = refN.Trim();
                        if (!string.IsNullOrWhiteSpace(sc)) o.Scale = sc.Trim();
                    }
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
