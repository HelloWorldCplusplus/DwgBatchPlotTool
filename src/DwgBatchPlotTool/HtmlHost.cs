using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DwgBatchPlotTool
{
    /// <summary>列表里的一行 (待转换的图纸)。</summary>
    internal sealed class HostItem
    {
        public string Path = "", Name = "", Dir = "", Status = "待处理", OutPath = "";
        public string Rel = "";   // 相对「添加时的文件夹」的子目录, 独立PDF按它建目录 (单个添加的文件为空 = 平铺)
        public long Size, Elapsed;
        public bool Checked = true;   // 是否参与本次转换
        public string RefName = "";   // 单个文件指定的参照名, 空 = 用全局参照名
        public string Scale = "";      // 单个文件指定的比例, 空 = 用全局比例
    }

    /// <summary>界面状态: 文件列表 / 日志 / 进度。服务端权威, 页面轮询取。</summary>
    internal sealed class HostState
    {
        public readonly object Sync = new object();
        public readonly List<HostItem> Items = new List<HostItem>();
        public readonly List<string> Log = new List<string>();
        public bool Running;
        public int Done, Total;
        public string Summary = "就绪";
    }

    /// <summary>
    /// HTML 界面的宿主: 在本机开一个 HTTP 服务, 页面通过 JSON 接口操作批量转换。
    /// 不引入任何新依赖 (JSON 用 .NET 自带的 JavaScriptSerializer, 界面用浏览器渲染)。
    /// </summary>
    internal sealed class HtmlHost
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly string _token = Guid.NewGuid().ToString("N");
        private readonly HostState _state = new HostState();
        private readonly RefScanner _refs = new RefScanner();
        private readonly Settings _settings;
        private readonly DialogPump _dialogs = new DialogPump();
        private readonly ManualResetEvent _exit = new ManualResetEvent(false);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly string _sysTheme;   // 系统深浅色 (settings.ini 没写主题时用它)

        private CancellationTokenSource _cts;
        private DateTime _lastPing = DateTime.UtcNow;
        private DateTime _byeAt = DateTime.MinValue;
        private string _prefix = "";

        public HtmlHost()
        {
            _json.MaxJsonLength = 32 * 1024 * 1024;
            // DWGBP_SETTINGS 可把设置文件指到别处 (自检用临时文件, 不动用户的 settings.ini)
            string sfile = Environment.GetEnvironmentVariable("DWGBP_SETTINGS");
            if (string.IsNullOrEmpty(sfile))
            {
                // 设置文件跟 exe 放一起 (exe 在 bin\ 里, 用户容易找到、也方便整目录拷走)
                sfile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
                string old = Path.Combine(OdaRunner.RootDir, "settings.ini");
                if (!string.Equals(old, sfile, StringComparison.OrdinalIgnoreCase) && !File.Exists(sfile) && File.Exists(old))
                {
                    try { File.Copy(old, sfile); } catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            _settings = new Settings(sfile);
            _sysTheme = SystemIsDark() ? "dark" : "light";
            _refs.Error += delegate(string path, string err) { Log("参照名识别失败: " + path + " — " + err); };
            ApplyFontsDir(CurrentOptions());
        }

        // 字体目录: 决定引擎去哪里找非系统字体(方正/华文/THFont 等)
        private static void ApplyFontsDir(Dictionary<string, object> opts)
        {
            string dir = Str(opts, "fontsDir", Path.Combine(OdaRunner.RootDir, "Fonts"), true).Trim();
            OdaRunner.FontsDir = dir;
        }

        // ------------------------------------------------------------ 启动 / 退出

        /// <summary>监听本机端口; 成功返回界面地址。</summary>
        public bool Start(out string url)
        {
            url = null;
            for (int port = 17845; port < 17890; port++)
            {
                string prefix = "http://127.0.0.1:" + port + "/t/" + _token + "/";
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add(prefix);
                    _listener.Start();
                    _prefix = prefix;
                    url = prefix + "index.html";
                    break;
                }
                catch (HttpListenerException) { /* 端口被占, 换下一个 */ }
                catch (Exception) { break; }
            }
            if (url == null) return false;

            _listener.BeginGetContext(OnContext, null);
            ThreadPool.QueueUserWorkItem(delegate { WatchIdle(); });
            return true;
        }

        /// <summary>打开界面: 优先用 Edge/Chrome 的 app 窗口 (无地址栏, 最像原生程序)。</summary>
        public bool OpenBrowser(string url)
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            };
            foreach (var exe in candidates)
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo(exe);
                    psi.Arguments = "--app=\"" + url + "\" --window-size=1400,900 --new-window";
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    return true;
                }
                catch { /* 试下一个 */ }
            }
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        /// <summary>阻塞直到程序退出。</summary>
        public void WaitForExit() { _exit.WaitOne(); }

        private void Quit()
        {
            try { _settings.Save(); } catch { }   // 退出前把设置落盘
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _dialogs.Dispose(); } catch { }
            _exit.Set();
        }

        /// <summary>页面定时 ping; 长时间没人理 (浏览器被强杀) 就自动退出。</summary>
        private void WatchIdle()
        {
            while (true)
            {
                Thread.Sleep(500);
                if (_exit.WaitOne(0)) return;
                bool bye = _byeAt != DateTime.MinValue && (DateTime.UtcNow - _byeAt).TotalSeconds > 3;
                if (bye || (DateTime.UtcNow - _lastPing).TotalMinutes > 10) { Quit(); return; }
            }
        }

        // ------------------------------------------------------------ HTTP

        private void OnContext(IAsyncResult ar)
        {
            HttpListenerContext ctx = null;
            try
            {
                ctx = _listener.EndGetContext(ar);
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
            catch { return; }
            try { _listener.BeginGetContext(OnContext, null); } catch { }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath;
                string root = "/t/" + _token + "/";
                if (!path.StartsWith(root, StringComparison.Ordinal)) { Fail(ctx, 403, "forbidden"); return; }
                string rel = path.Substring(root.Length);

                if (rel == "" || rel == "index.html") { Send(ctx, "text/html; charset=utf-8", Asset("index.html").Replace("__THEME__", Theme()).Replace("__VERSION__", Version)); return; }
                if (rel == "app.js") { Send(ctx, "application/javascript; charset=utf-8", Asset("app.js")); return; }
                if (rel == "style.css") { Send(ctx, "text/css; charset=utf-8", Asset("style.css")); return; }
                if (rel == "icon.svg") { Send(ctx, "image/svg+xml", Asset("icon.svg")); return; }
                if (rel == "help.html") { Send(ctx, "text/html; charset=utf-8", Asset("help.html").Replace("__VERSION__", Version)); return; }
                if (rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { byte[] img = AssetBytes(rel); if (img != null) { SendBytes(ctx, "image/png", img); return; } }
                if (!rel.StartsWith("api/", StringComparison.Ordinal)) { Fail(ctx, 404, "not found"); return; }

                string action = rel.Substring(4);
                if (action == "state") { GetState(ctx); return; }
                if (action == "ping") { _lastPing = DateTime.UtcNow; _byeAt = DateTime.MinValue; Send(ctx, "application/json; charset=utf-8", "{\"ok\":true}"); return; }
                if (action == "bye") { _byeAt = DateTime.UtcNow; Send(ctx, "application/json; charset=utf-8", "{\"ok\":true}"); return; }
                if (action == "upload") { Upload(ctx); return; }

                Dictionary<string, object> body = ReadJson(ctx);
                Dictionary<string, object> resp;
                switch (action)
                {
                    case "add": Add(body); resp = null; break;
                    case "remove": Remove(body); resp = null; break;
                    case "check": Check(body); resp = null; break;
                    case "set-ref": SetRef(body); resp = null; break;
                    case "set-scale": SetScale(body); resp = null; break;
                    case "clear": Clear(); resp = null; break;
                    case "rescan": Rescan(); resp = null; break;
                    case "start": resp = StartBatch(body); break;
                    case "cancel": Cancel(); resp = null; break;
                    case "save-settings": SaveSettingsCore(body); resp = null; break;
                    case "pick-files": resp = PickFiles(); break;
                    case "pick-folder": resp = PickFolder(); break;
                    case "pick-ctb": resp = PickCtb(); break;
                    case "open-dir": OpenDir(body); resp = null; break;
                    case "open-file": OpenFile(body); resp = null; break;
                    case "quit":
                        Send(ctx, "application/json; charset=utf-8", "{\"ok\":true,\"quit\":true}");
                        ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(300); Quit(); });
                        return;
                    default: Fail(ctx, 404, "unknown action"); return;
                }
                Send(ctx, "application/json; charset=utf-8", _json.Serialize(resp ?? new Dictionary<string, object> { { "ok", true } }));
            }
            catch (Exception ex)
            {
                try { Send(ctx, "application/json; charset=utf-8", "{\"ok\":false,\"error\":" + JsonString(ex.Message) + "}"); } catch { }
            }
        }

        private static void Fail(HttpListenerContext ctx, int code, string msg)
        {
            ctx.Response.StatusCode = code;
            Send(ctx, "text/plain; charset=utf-8", msg);
        }

        private static void Send(HttpListenerContext ctx, string contentType, string body)
        {
            SendBytes(ctx, contentType, Encoding.UTF8.GetBytes(body ?? ""));
        }

        private static void SendBytes(HttpListenerContext ctx, string contentType, byte[] data)
        {
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
        }

        private Dictionary<string, object> ReadJson(HttpListenerContext ctx)
        {
            try
            {
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    string s = sr.ReadToEnd();
                    if (s.Trim().Length == 0) return new Dictionary<string, object>();
                    var d = _json.Deserialize<Dictionary<string, object>>(s);
                    return d ?? new Dictionary<string, object>();
                }
            }
            catch { return new Dictionary<string, object>(); }
        }

        private string JsonString(string s) { return _json.Serialize(s ?? ""); }

        // ------------------------------------------------------------ 状态

        private void GetState(HttpListenerContext ctx)
        {
            var d = new Dictionary<string, object>();
            lock (_state.Sync)
            {
                d["running"] = _state.Running;
                d["done"] = _state.Done;
                d["total"] = _state.Total;
                d["summary"] = _state.Summary;
                d["items"] = _state.Items.Select(i => new Dictionary<string, object>
                {
                    { "path", i.Path }, { "name", i.Name }, { "dir", i.Dir },
                    { "size", i.Size }, { "status", i.Status }, { "elapsed", i.Elapsed },
                    { "out", i.OutPath }, { "checked", i.Checked }, { "refName", i.RefName },
                    { "scale", i.Scale }
                }).ToList();
                d["log"] = _state.Log.ToList();
                d["refNames"] = _refs.Names();
                d["refDetails"] = _refs.Rows(_state.Items.Select(i => i.Path)).Select(r => new Dictionary<string, object>
                {
                    { "name", r.Name }, { "file", r.File }, { "count", r.Count },
                    { "space", r.Space }, { "w", r.W }, { "h", r.H }, { "xref", r.IsXref }
                }).ToList();
                d["options"] = CurrentOptions();
                d["engine"] = OdaRunner.EngineAvailable;
                d["engineExe"] = OdaRunner.EngineExe;
                d["refError"] = _refs.LastError();
                d["refScanning"] = _refs.Scanning;
            }
            Send(ctx, "application/json; charset=utf-8", _json.Serialize(d));
        }

        // ------------------------------------------------------------ 文件列表

        private static bool IsDrawing(string f)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            return e == ".dwg" || e == ".dxf";
        }

        private void Add(Dictionary<string, object> body)
        {
            var paths = new List<string>();
            string dir = Str(body, "dir", null);
            string rootDir = string.IsNullOrEmpty(dir) ? null : Path.GetFullPath(dir);
            if (rootDir != null)
            {
                var opt = SearchOption.AllDirectories;   // 添加文件夹一律含子目录
                foreach (var pat in new[] { "*.dwg", "*.dxf" })
                    try { paths.AddRange(Directory.GetFiles(rootDir, pat, opt)); } catch { }
            }
            else
            {
                var arr = body.ContainsKey("paths") ? body["paths"] as ArrayList : null;
                if (arr != null) foreach (var o in arr) if (o != null) paths.Add(o.ToString());
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            int added = 0;
            lock (_state.Sync)
            {
                var known = new HashSet<string>(_state.Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var p in paths)
                {
                    if (!IsDrawing(p) || !known.Add(p)) continue;
                    var fi = new FileInfo(p);
                    string rel = "";
                    if (rootDir != null)
                    {
                        string pd = Path.GetFullPath(Path.GetDirectoryName(p));
                        if (pd.Length > rootDir.Length && pd.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                            rel = pd.Substring(rootDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    _state.Items.Add(new HostItem
                    {
                        Path = p,
                        Name = Path.GetFileName(p),
                        Dir = Path.GetDirectoryName(p),
                        Rel = rel,
                        Size = fi.Exists ? fi.Length : 0
                    });
                    added++;
                }
                _state.Summary = "已添加 " + _state.Items.Count + " 个文件 (本次 +" + added + ")";
            }
            if (added > 0) _refs.Request(paths);
        }

        /// <summary>
        /// 接收拖进界面的文件。浏览器不给真实路径，只能把内容传上来落到临时目录,
        /// 之后前端按「添加文件夹」收集一次, 子目录结构就由临时目录里的相对层级还原。
        /// </summary>
        private void Upload(HttpListenerContext ctx)
        {
            // 名字走请求头 (UTF-8 百分号编码), 走 query 会被 HttpListener 按系统代码页解错中文
            string raw = ctx.Request.Headers["X-Name"];
            string name = string.IsNullOrEmpty(raw) ? null : Uri.UnescapeDataString(raw);
            byte[] data = ReadAllBytes(ctx.Request.InputStream);
            string rel = SafeRelName(name);
            if (!IsDrawing(rel)) throw new Exception("只接受 dwg / dxf: " + name);
            string root = Path.Combine(Path.GetTempPath(), "dwgbp-drop", _token);
            string full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllBytes(full, data);
            Send(ctx, "application/json; charset=utf-8", _json.Serialize(new Dictionary<string, object>
            {
                { "ok", true }, { "path", full }, { "root", root }
            }));
        }

        /// <summary>把浏览器给的相对名变成安全的相对路径 (去掉 .. 与非法字符, 保留子目录层级)。</summary>
        internal static string SafeRelName(string name)
        {
            var parts = new List<string>();
            foreach (var raw in (name ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s == "." || s == "..") continue;
                foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
                s = s.TrimEnd('.', ' ');
                if (s.Length > 0) parts.Add(s);
            }
            if (parts.Count == 0) throw new Exception("文件名无效");
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts.ToArray());
        }

        private static byte[] ReadAllBytes(Stream s)
        {
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private void Remove(Dictionary<string, object> body)
        {
            var kill = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arr = body.ContainsKey("paths") ? body["paths"] as ArrayList : null;
            if (arr != null) foreach (var o in arr) if (o != null) kill.Add(o.ToString());
            lock (_state.Sync)
            {
                _state.Items.RemoveAll(i => kill.Contains(i.Path));
                _state.Summary = "已移除, 剩余 " + _state.Items.Count + " 个文件";
            }
        }

        /// <summary>勾选/取消勾选某个文件 (决定它是否参与转换)。</summary>
        private void Check(Dictionary<string, object> body)
        {
            string path = Str(body, "path", "").Trim();
            bool on = Bool(body, "checked", true);
            lock (_state.Sync)
            {
                foreach (var i in _state.Items)
                    if (string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) { i.Checked = on; break; }
            }
        }

        /// <summary>给单个文件指定参照名 (空 = 用全局参照名)。</summary>
        private void SetRef(Dictionary<string, object> body)
        {
            string path = Str(body, "path", "").Trim();
            string name = Str(body, "refName", "").Trim();
            lock (_state.Sync)
            {
                foreach (var i in _state.Items)
                    if (string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) { i.RefName = name; break; }
            }
        }

        /// <summary>给单个文件指定比例 (空 = 用全局比例)。存规范化后的 N:D, 格式不对直接抛回界面。</summary>
        private void SetScale(Dictionary<string, object> body)
        {
            string path = Str(body, "path", "").Trim();
            string s = Str(body, "scale", "", true).Trim();
            string norm = PlotOptions.NormalizeScale(s);
            if (norm == null)
                throw new ArgumentException("比例格式不对: \"" + s + "\" (应为 1:100；只写分母如 100 也可以；留空 = 按图框)");
            lock (_state.Sync)
            {
                foreach (var i in _state.Items)
                    if (string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) { i.Scale = norm; break; }
            }
        }

        private void Clear()
        {
            lock (_state.Sync)
            {
                _state.Items.Clear();
                _state.Log.Clear();
                _state.Done = 0;
                _state.Total = 0;
                _state.Summary = "就绪";
            }
            _refs.Forget(new string[0]);
        }

        /// <summary>重新识别所有已加入图纸里的参照名 (清缓存后重扫)。</summary>
        private void Rescan()
        {
            _refs.Clear();
            var paths = _state.Items.Select(i => i.Path).ToList();
            if (paths.Count == 0) { Log("没有图纸可识别参照名"); return; }
            Log("正在重新识别参照名 (" + paths.Count + " 个图纸)…");
            _refs.Request(paths);
        }

        // ------------------------------------------------------------ 转换

        private Dictionary<string, object> StartBatch(Dictionary<string, object> body)
        {
            if (!OdaRunner.EngineAvailable) throw new Exception("找不到 ODA 引擎: " + OdaRunner.EngineExe);

            // 参数先校验, 出错就直接抛回去, 免得界面卡在"转换中"
            var merged = CurrentOptions();
            var bodyOpts = body.ContainsKey("options") ? body["options"] as Dictionary<string, object> : null;
            if (bodyOpts != null)
                foreach (var k in bodyOpts.Keys) merged[k] = bodyOpts[k];
            var opt = ToOptions(merged);
            merged["scale"] = opt.Scale;        // 回传规范化后的比例, 界面显示真正生效的值
            SaveOptions(merged);
            ApplyFontsDir(merged);              // 字体目录: 用户改了立刻生效

            var items = new List<HostItem>();
            var inputs = new List<string>();
            lock (_state.Sync)
            {
                if (_state.Running) throw new Exception("正在转换中");
                foreach (var i in _state.Items) if (i.Checked) { items.Add(i); inputs.Add(i.Path); }
                if (items.Count == 0) throw new Exception("请先添加并勾选要转换的文件");
                _state.Running = true;
                _state.Done = 0;
                _state.Total = items.Count;
                _state.Summary = "转换中...";
                foreach (var i in items) { i.Status = "待处理"; i.OutPath = ""; i.Elapsed = 0; }
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(delegate { RunBatch(opt, items, inputs, token); });

            return new Dictionary<string, object> { { "ok", true }, { "options", merged } };
        }

        private void RunBatch(PlotOptions opt, List<HostItem> items, List<string> inputs, CancellationToken token)
        {
            Action<FileResult> onFile = delegate(FileResult fr)
            {
                lock (_state.Sync)
                {
                    var it = items[fr.Index];
                    it.Status = fr.Ok ? (fr.Pages > 0 ? "成功 (" + fr.Pages + " 页)" : "成功") : "失败: " + fr.Message;
                    it.OutPath = fr.Output;
                    it.Elapsed = fr.ElapsedMs;
                    _state.Done = fr.Index + 1;
                }
            };

            var refNames = items.Select(i => i.RefName).ToList();   // 单个文件自己的参照名 (空 = 用全局)
            var scales = items.Select(i => i.Scale).ToList();       // 单个文件自己的比例 (空 = 用全局)
            var relDirs = items.Select(i => i.Rel).ToList();         // 独立PDF: 按源目录结构输出的相对子目录
            var result = BatchRunner.Run(opt, inputs, onFile, Log, token, refNames, scales, relDirs);

            if (result.MergedPath != null)
            {
                lock (_state.Sync) foreach (var it in items) it.OutPath = result.MergedPath;
            }

            string summary = token.IsCancellationRequested
                ? "已取消 (成功 " + result.Ok + ", 失败 " + result.Fail + ")"
                : (result.Error != null ? "完成: 成功 " + result.Ok + ", 失败 " + result.Fail + " — " + result.Error
                                        : "完成: 成功 " + result.Ok + ", 失败 " + result.Fail + ", 共 " + result.Pages + " 页");
            lock (_state.Sync)
            {
                _state.Summary = summary;
                _state.Running = false;
            }
        }

        private void Cancel()
        {
            if (_cts != null) _cts.Cancel();
            lock (_state.Sync) _state.Summary = "正在停止...";
        }

        private void Log(string msg)
        {
            lock (_state.Sync)
            {
                _state.Log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + msg);
                if (_state.Log.Count > 500) _state.Log.RemoveRange(0, _state.Log.Count - 500);
            }
        }

        // ------------------------------------------------------------ 参数

        private Dictionary<string, object> DefaultOptions()
        {
            return new Dictionary<string, object>
            {
                { "refName", "图框" }, { "scale", "" },
                { "frameOrder", PlotOptions.FrameOrderDefault },
                { "dpi", PlotOptions.DpiDefault },
                { "offsetX", "0" }, { "offsetY", "0" }, { "customW", "420" }, { "customH", "297" },
                { "mergedName", "合并输出" }, { "outputDir", Path.Combine(OdaRunner.RootDir, "output") },
                { "ctb", OdaRunner.DefaultCtb }, { "timeout", "300" },
                { "fontsDir", Path.Combine(OdaRunner.RootDir, "Fonts") },
                { "space", 2 }, { "paper", PlotOptions.DefaultPaperIndex }, { "lineweight", 0 },
                { "orientation", 0 }, { "mode", 0 },
                { "center", false }, { "fit", false }, { "mono", false }, { "plotstyles", true },
                { "hidden", false },
                { "theme", _sysTheme }
            };
        }

        // 深浅色主题：settings.ini 里的值为准，没有就跟随系统
        private string Theme()
        {
            string t = _settings.Get("theme", _sysTheme);
            return (t == "dark" || t == "light") ? t : _sysTheme;
        }

        private static bool SystemIsDark()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return v != null && Convert.ToInt32(v) == 0;
            }
            catch { return false; }
        }

        private Dictionary<string, object> CurrentOptions()
        {
            var d = DefaultOptions();
            foreach (var k in _settings.Map.Keys)
                if (d.ContainsKey(k) && (!string.IsNullOrEmpty(_settings.Map[k]) || EmptyOkKeys.Contains(k)))
                    d[k] = _settings.Map[k];   // 空值一般当没设置, 走默认值; 但这几个键的空值本身有意义
            // 路径类: 存的值已经不存在(程序挪了位置/文件被删)时回落默认值
            string ctb = d["ctb"] as string;
            if (!string.IsNullOrEmpty(ctb) && !File.Exists(ctb)) d["ctb"] = DefaultOptions()["ctb"];
            string fonts = d["fontsDir"] as string;
            if (!string.IsNullOrEmpty(fonts) && !Directory.Exists(fonts)) d["fontsDir"] = DefaultOptions()["fontsDir"];
            return d;
        }

        // 路径类参数: 默认值跟着程序目录走, 用户改了别的值才保存
        private static readonly HashSet<string> PathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ctb", "outputDir", "fontsDir" };

        // 这些键允许存空值: 参照名留空 = 整张图, 比例留空 = 按图框
        private static readonly HashSet<string> EmptyOkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "refName", "scale" };

        private void SaveSettingsCore(Dictionary<string, object> body)
        {
            var o = body.ContainsKey("options") ? body["options"] as Dictionary<string, object> : null;
            SaveOptions(o);
        }

        private void SaveOptions(Dictionary<string, object> o)
        {
            if (o == null) return;
            var defs = DefaultOptions();
            foreach (var k in defs.Keys)
            {
                object v;
                if (!o.TryGetValue(k, out v) || v == null) continue;
                string s = v.ToString();
                if (s.Length == 0 && !EmptyOkKeys.Contains(k)) continue;   // 空值不覆盖已有设置
                if (k == "scale")                       // 比例存规范化后的值 (2 -> 1:2)
                {
                    string n = PlotOptions.NormalizeScale(s);
                    if (n == null) continue;
                    s = n;
                }
                // 路径类: 等于默认值就不落盘, 这样程序挪位置后仍跟着程序目录走
                if (PathKeys.Contains(k) && string.Equals(s, defs[k] as string, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Remove(k);
                    continue;
                }
                _settings.Set(k, s);
            }
            _settings.Save();
        }

        private static PlotOptions ToOptions(Dictionary<string, object> d)
        {
            var o = new PlotOptions();
            if (d == null) d = new Dictionary<string, object>();
            o.RefName = Str(d, "refName", "图框", true).Trim();
            int space = (int)Dbl(d, "space", 2);
            o.Space = space == 0 ? PlotSpace.Model : space == 1 ? PlotSpace.Layout : PlotSpace.Both;
            string fo = Str(d, "frameOrder", PlotOptions.FrameOrderDefault).Trim();
            o.FrameOrder = fo.Length > 0 ? fo : PlotOptions.FrameOrderDefault;
            int dpi = (int)Dbl(d, "dpi", PlotOptions.DpiDefault);
            o.Dpi = dpi >= 100 && dpi <= 4800 ? dpi : PlotOptions.DpiDefault;
            int paper = (int)Dbl(d, "paper", PlotOptions.DefaultPaperIndex);
            o.PaperPreset = paper >= 0 && paper < PlotOptions.PaperNames.Length ? PlotOptions.PaperNames[paper] : "A3";
            o.Portrait = (int)Dbl(d, "orientation", 0) == 1;
            o.CustomW = Dbl(d, "customW", 420);
            o.CustomH = Dbl(d, "customH", 297);
            o.Center = Bool(d, "center", false);
            o.Fit = Bool(d, "fit", false);
            o.Scale = Str(d, "scale", "", true).Trim();
            string norm = PlotOptions.NormalizeScale(o.Scale);
            if (norm == null)
                throw new ArgumentException("比例格式不对: \"" + o.Scale + "\" (应为 1:100；只写分母如 100 也可以；留空 = 按图框)");
            o.Scale = norm;
            o.OffsetX = Dbl(d, "offsetX", 0);
            o.OffsetY = Dbl(d, "offsetY", 0);
            o.Hidden = Bool(d, "hidden", false);
            o.Mono = Bool(d, "mono", false);
            o.PlotStyles = Bool(d, "plotstyles", true);
            o.CtbFile = o.PlotStyles ? Str(d, "ctb", "").Trim() : "";
            int lw = (int)Dbl(d, "lineweight", 0);
            o.ScaleLineweights = lw == 1;
            o.NoLineweights = lw == 2;
            o.OutputDir = Str(d, "outputDir", "").Trim();
            if (o.OutputDir.Length == 0) throw new Exception("输出目录不能为空");
            o.Mode = (int)Dbl(d, "mode", 0) == 1 ? OutputMode.Merged : OutputMode.PerFile;
            o.MergedName = Str(d, "mergedName", "合并输出");
            o.TimeoutSec = (int)Dbl(d, "timeout", 300);
            if (o.TimeoutSec <= 0) o.TimeoutSec = 300;
            return o;
        }

        private static string Str(Dictionary<string, object> d, string k, string def, bool emptyOk = false)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                string s = o.ToString();
                if (s.Length > 0 || emptyOk) return s;   // emptyOk: 空字符串是有效值 (如"留空=整张图")
            }
            return def;
        }

        private static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                if (o is bool) return (bool)o;
                string s = o.ToString().ToLowerInvariant();
                if (s == "1" || s == "true") return true;
                if (s == "0" || s == "false") return false;
            }
            return def;
        }

        private static double Dbl(Dictionary<string, object> d, string k, double def)
        {
            object o;
            double v;
            if (d != null && d.TryGetValue(k, out o) && o != null &&
                double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return v;
            return def;
        }

        // ------------------------------------------------------------ 原生对话框 / 其它

        private Dictionary<string, object> PickFiles()
        {
            var files = _dialogs.PickFiles("图纸文件 (*.dwg;*.dxf)|*.dwg;*.dxf|DWG (*.dwg)|*.dwg|DXF (*.dxf)|*.dxf|所有文件 (*.*)|*.*");
            var arr = new List<object>();
            foreach (var f in files) arr.Add(f);
            return new Dictionary<string, object> { { "ok", true }, { "paths", arr } };
        }

        private Dictionary<string, object> PickFolder()
        {
            string dir = _dialogs.PickFolder();
            return new Dictionary<string, object> { { "ok", true }, { "dir", dir ?? "" } };
        }

        private Dictionary<string, object> PickCtb()
        {
            string file = _dialogs.PickCtb();
            return new Dictionary<string, object> { { "ok", true }, { "file", file ?? "" } };
        }

        private void OpenDir(Dictionary<string, object> body)
        {
            string dir = Str(body, "dir", "").Trim();
            if (dir.Length == 0) return;
            try { Directory.CreateDirectory(dir); } catch { }
            try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        /// <summary>打开输出文件 (列表里点输出列的路径)。</summary>
        private void OpenFile(Dictionary<string, object> body)
        {
            string path = Str(body, "path", "").Trim();
            if (path.Length == 0) throw new Exception("没有指定文件");
            if (!File.Exists(path)) throw new Exception("文件不存在: " + path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        // ------------------------------------------------------------ 静态资源 (嵌入 exe)

        /// <summary>程序版本，取自 csproj 的 &lt;Version&gt;（如 1.0.1）。</summary>
        internal static string Version
        {
            get { return typeof(HtmlHost).Assembly.GetName().Version.ToString(3); }
        }

        internal static string Asset(string name)
        {
            var asm = typeof(HtmlHost).Assembly;
            foreach (var n in asm.GetManifestResourceNames())
            {
                if (!n.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                using (var s = asm.GetManifestResourceStream(n))
                using (var sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            return "<!-- 资源缺失: " + name + " -->";
        }

        internal static byte[] AssetBytes(string name)
        {
            var asm = typeof(HtmlHost).Assembly;
            foreach (var n in asm.GetManifestResourceNames())
            {
                if (!n.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                using (var s = asm.GetManifestResourceStream(n))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            return new byte[0];
        }
    }

    /// <summary>原生文件/文件夹选择框 (浏览器拿不到真实路径, 只能服务端弹窗)。</summary>
    internal sealed class DialogPump : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private Form _form;

        public DialogPump()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "dialogs" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.WaitOne(5000);
        }

        private void Run()
        {
            _form = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0,
                Size = new Size(1, 1),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000)
            };
            _form.Load += delegate { _form.Hide(); };
            _ready.Set();
            Application.Run(_form);
        }

        private object Invoke(Func<object> f)
        {
            if (_form == null) return null;
            var done = new ManualResetEvent(false);
            object result = null;
            try
            {
                _form.BeginInvoke((Action)(delegate
                {
                    try { result = f(); } catch { result = null; }
                    done.Set();
                }));
            }
            catch { return null; }
            done.WaitOne(600000);
            return result;
        }

        /// <summary>
        /// 原生对话框由后台进程弹出, 而前台是浏览器窗口 —— 新窗口会被压在浏览器下面。
        /// 办法: 造一个 1x1 全透明、置顶的隐藏窗当 owner, 再用 ShowDialog(owner) 打开对话框,
        /// 对话框就落在置顶带里, 一定盖在浏览器上面。
        /// </summary>
        private Form _owner;

        private void EnsureOwner()
        {
            if (_owner != null) return;
            var scr = Screen.FromPoint(Cursor.Position);
            var wa = scr.WorkingArea;
            _owner = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0,
                Size = new Size(1, 1),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2),
                TopMost = true
            };
            _owner.Show();
        }

        public string[] PickFiles(string filter)
        {
            var r = Invoke(delegate
            {
                EnsureOwner();
                using (var dlg = new OpenFileDialog { Filter = filter, Multiselect = true })
                    return dlg.ShowDialog(_owner) == DialogResult.OK ? dlg.FileNames : new string[0];
            }) as string[];
            return r ?? new string[0];
        }

        public string PickFolder()
        {
            return Invoke(delegate
            {
                EnsureOwner();
                using (var dlg = new FolderBrowserDialog())
                    return dlg.ShowDialog(_owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }) as string;
        }

        public string PickCtb()
        {
            return Invoke(delegate
            {
                EnsureOwner();
                using (var dlg = new OpenFileDialog { Filter = "打印样式表 (*.ctb)|*.ctb|所有文件 (*.*)|*.*" })
                    return dlg.ShowDialog(_owner) == DialogResult.OK ? dlg.FileName : null;
            }) as string;
        }

        public void Dispose()
        {
            try { if (_form != null) _form.BeginInvoke((Action)(delegate { Application.ExitThread(); })); } catch { }
        }
    }
}
