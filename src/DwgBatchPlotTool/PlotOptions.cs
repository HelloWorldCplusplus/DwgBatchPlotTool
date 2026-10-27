using System;
using System.Collections.Generic;
using System.Globalization;

namespace DwgBatchPlotTool
{
    internal enum OutputMode { PerFile = 0, Merged = 1 }
    internal enum PlotSpace { Model = 0, Layout = 1, Both = 2 }

    /// <summary>打印设置，负责翻译成 ODA 引擎 (ODA\ODADrawingTools.exe) 的命令行参数。</summary>
    internal sealed class PlotOptions
    {
        public string RefName = "";
        public PlotSpace Space = PlotSpace.Both;
        // 一张图纸里有多张图框时的页序 (引擎 --frame-order)
        public string FrameOrder = FrameOrderDefault;
        // 输出精度 (引擎 --dpi)：600 = 内核默认，数值越大曲线/图片/渐变越精细、文件越大
        public int Dpi = DpiDefault;
        public string PaperPreset = "A3";
        public double CustomW = 420, CustomH = 297;
        public bool Portrait;
        public bool Center;
        public double OffsetX, OffsetY;
        public bool Fit;
        public string Scale = "";
        public bool ScaleLineweights;
        public bool NoLineweights;
        public bool PlotStyles;
        public bool Mono;
        public string CtbFile = "";
        public bool Hidden;
        public string Title = "";
        public string Author = "";
        public string OutputDir = "";
        public OutputMode Mode = OutputMode.PerFile;
        public string MergedName = "合并输出";
        public int TimeoutSec = 300;

        // 不指定纸张时，引擎按命中的图框长宽比出纸（长边 420mm≈A3）
        public const string PaperDefault = "按图框(A3)";

        public const string FrameOrderDefault = "xrightydown";

        public const int DpiDefault = 600;

        private static readonly Dictionary<string, double[]> Papers = new Dictionary<string, double[]>
        {
            { "A4", new double[] { 210, 297 } },
            { "A3", new double[] { 297, 420 } },
            { "A2", new double[] { 420, 594 } },
            { "A1", new double[] { 594, 841 } },
            { "A0", new double[] { 841, 1189 } },
        };

        public static string[] PaperNames { get { return new[] { PaperDefault, "A4", "A3", "A2", "A1", "A0", "自定义" }; } }

        /// <summary>默认纸张 (A3) 在 PaperNames 里的下标。</summary>
        public const int DefaultPaperIndex = 2;

        /// <summary>复制一份, 用来给单个文件覆盖参照名等参数。</summary>
        public PlotOptions Clone() { return (PlotOptions)MemberwiseClone(); }

        /// <summary>返回纸张宽高(mm)。返回 false 表示不传 --paper（纸张按命中的图框尺寸决定）。</summary>
        public bool TryGetPaper(out double w, out double h)
        {
            w = h = 0;
            if (PaperPreset == PaperDefault) return false;
            if (PaperPreset == "自定义")
            {
                w = CustomW; h = CustomH;
                return w > 0 && h > 0;
            }
            double[] s;
            if (Papers.TryGetValue(PaperPreset, out s)) { w = s[0]; h = s[1]; return true; }
            return false;
        }

        /// <summary>
        /// 把用户输入的比例规范化成 N:D。空 = 按图框（留空）。
        /// 兼容 N:D、N/D、N-D，以及只写分母（AutoCAD 习惯：100 就是 1:100）。
        /// 无法识别时返回 null。
        /// </summary>
        public static string NormalizeScale(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return "";
            double num, den;
            int ci = s.IndexOfAny(new char[] { ':', '/', '-' });
            if (ci > 0
                && double.TryParse(s.Substring(0, ci), NumberStyles.Float, CultureInfo.InvariantCulture, out num)
                && double.TryParse(s.Substring(ci + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out den)
                && num > 0 && den > 0)
                return Fmt(num) + ":" + Fmt(den);
            if (ci < 0
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out den)
                && den > 0)
                return "1:" + Fmt(den);
            return null;
        }

        /// <summary>生成 --plot 模式的命令行参数。</summary>
        public List<string> BuildPlotArgs(string input, string output)
        {
            var a = new List<string>();
            a.Add("--plot");
            a.Add(input);
            a.Add(output);
            if (RefName.Trim().Length > 0) { a.Add("--ref"); a.Add(RefName.Trim()); }
            if (FrameOrder != FrameOrderDefault) { a.Add("--frame-order"); a.Add(FrameOrder); }
            a.Add("--dpi");
            a.Add(Dpi.ToString(CultureInfo.InvariantCulture));
            a.Add("--space");
            a.Add(Space == PlotSpace.Model ? "model" : Space == PlotSpace.Layout ? "layout" : "both");
            if (Mono) a.Add("--mono");
            if (CtbFile.Trim().Length > 0) { a.Add("--ctb"); a.Add(CtbFile.Trim()); }
            double pw, ph;
            if (TryGetPaper(out pw, out ph)) { a.Add("--paper"); a.Add(Fmt(pw)); a.Add(Fmt(ph)); }
            a.Add("--orientation");
            a.Add(Portrait ? "portrait" : "landscape");
            if (Center) a.Add("--center");
            if (OffsetX != 0 || OffsetY != 0) { a.Add("--offset"); a.Add(Fmt(OffsetX)); a.Add(Fmt(OffsetY)); }
            if (Fit) a.Add("--fit");
            if (Scale.Trim().Length > 0) { a.Add("--scale"); a.Add(Scale.Trim()); }
            if (ScaleLineweights) a.Add("--scale-lineweights");
            if (NoLineweights) a.Add("--no-lineweights");
            if (PlotStyles) a.Add("--plot-styles");
            if (Hidden) a.Add("--hidden");
            if (Title.Trim().Length > 0) { a.Add("--title"); a.Add(Title.Trim()); }
            if (Author.Trim().Length > 0) { a.Add("--author"); a.Add(Author.Trim()); }
            a.Add("--verbose");   // 让引擎输出 STEP/WARN/ERROR 日志，便于界面显示失败原因
            return a;
        }

        private static string Fmt(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
