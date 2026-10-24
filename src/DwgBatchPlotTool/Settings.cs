using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DwgBatchPlotTool
{
    /// <summary>参数持久化: settings.ini (key=value), 与旧版 WinForms 界面格式兼容。</summary>
    internal sealed class Settings
    {
        public readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string File { get; private set; }

        public Settings(string file)
        {
            File = file;
            Load();
        }

        public void Load()
        {
            Map.Clear();
            if (!System.IO.File.Exists(File)) return;
            string[] lines;
            try { lines = System.IO.File.ReadAllLines(File); } catch { return; }
            foreach (var line in lines)
            {
                int i = line.IndexOf('=');
                if (i > 0) Map[line.Substring(0, i).Trim()] = line.Substring(i + 1);
            }
        }

        public string Get(string key, string def)
        {
            string v;
            return Map.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        public int GetInt(string key, int def)
        {
            int i;
            return int.TryParse(Get(key, ""), out i) ? i : def;
        }

        public bool GetBool(string key, bool def)
        {
            string v = Get(key, null);
            return v == null ? def : v == "1";
        }

        public void Set(string key, string value) { Map[key] = value ?? ""; }

        public void Remove(string key) { Map.Remove(key); }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var kv in Map) sb.AppendLine(kv.Key + "=" + kv.Value);
            try { System.IO.File.WriteAllText(File, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
    }
}
