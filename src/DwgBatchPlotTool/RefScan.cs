using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DwgBatchPlotTool
{
    /// <summary>
    /// 块参照扫描器: 把已加入图纸里的块参照 (参照图框名) 读出来, 供参照名下拉框挑选。
    /// 扫描在后台线程做, 不阻塞界面; 结果按文件缓存, 读过一次不再读。
    /// </summary>
    internal sealed class RefScanner
    {
        private readonly Dictionary<string, List<RefInfo>> _cache =
            new Dictionary<string, List<RefInfo>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _errors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <summary>是否有扫描在跑。</summary>
        public volatile bool Scanning;

        /// <summary>扫描结束(后台线程)时触发。</summary>
        public event Action Changed;

        /// <summary>把没读过的文件排进后台扫描, 立即返回。</summary>
        public void Request(IEnumerable<string> paths)
        {
            var pending = new List<string>();
            lock (_sync)
            {
                foreach (var p in paths)
                    if (!string.IsNullOrEmpty(p) && !_cache.ContainsKey(p)) pending.Add(p);
            }
            if (pending.Count == 0) return;
            Scanning = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var p in pending)
                {
                    string err;
                    var refs = OdaRunner.ListRefs(p, 120, CancellationToken.None, out err);
                    lock (_sync) { _cache[p] = refs; _errors[p] = err ?? ""; }
                    if (!string.IsNullOrEmpty(err)) OnError(p, err);
                }
                Scanning = false;
                var h = Changed;
                if (h != null) h();
            });
        }

        /// <summary>扫描失败时回调 (文件路径, 错误原因)。</summary>
        public event Action<string, string> Error;

        private void OnError(string path, string err)
        {
            var h = Error;
            if (h != null) h(path, err);
        }

        /// <summary>最近一次扫描失败的原因 (没有则空串)。</summary>
        public string LastError()
        {
            lock (_sync)
            {
                foreach (var kv in _errors)
                    if (!string.IsNullOrEmpty(kv.Value))
                        return Path.GetFileName(kv.Key) + ": " + kv.Value;
            }
            return "";
        }

        /// <summary>忘掉所有缓存, 下次 Request 会重新读一遍。</summary>
        public void Clear()
        {
            lock (_sync) { _cache.Clear(); _errors.Clear(); }
        }

        /// <summary>同步读一个文件 (立刻要结果时用), 并进缓存。</summary>
        public List<RefInfo> ReadNow(string path, int timeoutSec, out string error)
        {
            var refs = OdaRunner.ListRefs(path, timeoutSec, CancellationToken.None, out error);
            lock (_sync) { _cache[path] = refs; }
            return refs;
        }

        /// <summary>该文件是否已读过。</summary>
        public bool Has(string path)
        {
            lock (_sync) { return _cache.ContainsKey(path); }
        }

        /// <summary>所有参照名 (去重, 按名称排序)。</summary>
        public List<string> Names()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                foreach (var kv in _cache)
                    foreach (var r in kv.Value) set.Add(r.Name);
            }
            return new List<string>(set);
        }

        /// <summary>参照行 (每行 = 某张图纸里的一个参照名), 按给定文件顺序。</summary>
        public List<RefInfo> Rows(IEnumerable<string> paths)
        {
            var rows = new List<RefInfo>();
            lock (_sync)
            {
                foreach (var p in paths)
                {
                    List<RefInfo> refs;
                    if (!_cache.TryGetValue(p, out refs)) continue;
                    rows.AddRange(refs);
                }
            }
            return rows;
        }

        /// <summary>丢掉不再需要的文件缓存。</summary>
        public void Forget(IEnumerable<string> keep)
        {
            var alive = new HashSet<string>(keep ?? new string[0], StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                var dead = new List<string>();
                foreach (var kv in _cache) if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (var d in dead) _cache.Remove(d);
            }
        }
    }
}
