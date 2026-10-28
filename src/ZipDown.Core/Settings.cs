using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ZipDown.Core
{
    /// <summary>設定檔：放在程式旁邊的 settings.ini（免安裝、可攜）。</summary>
    public sealed class Settings
    {
        public string Language = "";          // zh-TW / zh-CN / en；空白 = 跟系統
        public string Engine = "auto";        // auto / bandizip / winrar
        public string BandizipPath = "";
        public string WinRARPath = "";
        public int MaxDepth = 3;
        public List<string> JunkWords = new List<string> { "删", "刪" };
        public string LastFolder = "";

        public string FilePath;

        public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;

        public static Settings Load(string dir = null)
        {
            var s = new Settings { FilePath = Path.Combine(dir ?? AppDir, "settings.ini") };
            if (!File.Exists(s.FilePath)) return s;
            foreach (var raw in File.ReadAllLines(s.FilePath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                var v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "language": s.Language = v; break;
                    case "engine": s.Engine = v.ToLowerInvariant(); break;
                    case "bandizippath": s.BandizipPath = v; break;
                    case "winrarpath": s.WinRARPath = v; break;
                    case "maxdepth": if (int.TryParse(v, out var d)) s.MaxDepth = Math.Max(1, Math.Min(10, d)); break;
                    case "junkwords": s.JunkWords = v.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); break;
                    case "lastfolder": s.LastFolder = v; break;
                }
            }
            return s;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ZipDown (褲鍊解壓) settings");
            sb.AppendLine("Language=" + Language);
            sb.AppendLine("Engine=" + Engine);
            sb.AppendLine("BandizipPath=" + BandizipPath);
            sb.AppendLine("WinRARPath=" + WinRARPath);
            sb.AppendLine("MaxDepth=" + MaxDepth.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("JunkWords=" + string.Join("|", JunkWords));
            sb.AppendLine("LastFolder=" + LastFolder);
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
        }
    }

    /// <summary>
    /// 密碼本：passwords.txt，一行一個。另外用 password_stats.txt 記每個密碼成功幾次，
    /// 成功次數多的排前面先試。
    /// </summary>
    public sealed class PasswordBook
    {
        readonly string _file, _statsFile;
        readonly object _lock = new object();
        public List<string> Passwords { get; private set; } = new List<string>();
        readonly Dictionary<string, int> _stats = new Dictionary<string, int>(StringComparer.Ordinal);

        public string FilePath => _file;

        public PasswordBook(string dir = null)
        {
            dir = dir ?? Settings.AppDir;
            _file = Path.Combine(dir, "passwords.txt");
            _statsFile = Path.Combine(dir, "password_stats.txt");
            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                Passwords = new List<string>();
                if (File.Exists(_file))
                {
                    foreach (var raw in File.ReadAllLines(_file, Encoding.UTF8))
                    {
                        var p = raw.Trim('\r', '\n', '\t', ' ', '﻿');
                        if (p.Length > 0 && !Passwords.Contains(p)) Passwords.Add(p);
                    }
                }
                else
                {
                    try { File.WriteAllText(_file, "", new UTF8Encoding(true)); } catch { }
                }
                _stats.Clear();
                if (File.Exists(_statsFile))
                {
                    foreach (var raw in File.ReadAllLines(_statsFile, Encoding.UTF8))
                    {
                        int tab = raw.IndexOf('\t');
                        if (tab <= 0) continue;
                        if (int.TryParse(raw.Substring(0, tab), out var c)) _stats[raw.Substring(tab + 1)] = c;
                    }
                }
            }
        }

        public void SetPasswords(IEnumerable<string> list)
        {
            lock (_lock)
            {
                Passwords = new List<string>();
                foreach (var raw in list)
                {
                    var p = raw.Trim('\r', '\n', '\t', ' ');
                    if (p.Length > 0 && !Passwords.Contains(p)) Passwords.Add(p);
                }
                File.WriteAllText(_file, string.Join("\r\n", Passwords) + "\r\n", new UTF8Encoding(true));
            }
        }

        public int SuccessCount(string pw) { lock (_lock) return _stats.TryGetValue(pw, out var c) ? c : 0; }

        /// <summary>成功次數多的在前，同次數照密碼本原本順序。</summary>
        public List<string> Ordered()
        {
            lock (_lock)
            {
                return Passwords.Select((p, i) => new { p, i, c = _stats.TryGetValue(p, out var c) ? c : 0 })
                    .OrderByDescending(x => x.c).ThenBy(x => x.i).Select(x => x.p).ToList();
            }
        }

        public void RecordSuccess(string pw)
        {
            if (string.IsNullOrEmpty(pw)) return;
            lock (_lock)
            {
                _stats[pw] = (_stats.TryGetValue(pw, out var c) ? c : 0) + 1;
                try
                {
                    File.WriteAllLines(_statsFile, _stats.OrderByDescending(kv => kv.Value).Select(kv => kv.Value + "\t" + kv.Key), new UTF8Encoding(true));
                }
                catch { }
            }
        }

        /// <summary>解壓成功、但密碼不在密碼本裡（例如從檔名抓到的）→ 自動加進密碼本。</summary>
        public bool AddIfMissing(string pw)
        {
            if (string.IsNullOrEmpty(pw)) return false;
            lock (_lock)
            {
                if (Passwords.Contains(pw)) return false;
                Passwords.Add(pw);
                try { File.WriteAllText(_file, string.Join("\r\n", Passwords) + "\r\n", new UTF8Encoding(true)); } catch { }
                return true;
            }
        }
    }
}
