using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZipDown.Core
{
    /// <summary>解壓引擎：回傳外部程式的結束代碼，0 = 成功。取消時要把外部程式關掉並丟出 OperationCanceledException。</summary>
    public interface IArchiveEngine
    {
        string Name { get; }
        string ExePath { get; }
        Task<int> TestAsync(string archive, string password, CancellationToken ct);
        Task<int> ExtractAsync(string archive, string password, string outDir, CancellationToken ct);
        /// <summary>這個結束代碼是不是「密碼錯誤」。</summary>
        bool IsWrongPassword(int exitCode);
    }

    public enum LogLevel { Info, Success, Fail, Skip, Rename, Detail }

    public enum ItemStatus { Pending, AlreadyDone, Testing, Extracting, Done, PartialFail, Skipped, WrongPassword, Damaged, Failed, Stopped }

    public static class StatusExt
    {
        public static bool IsFailure(this ItemStatus s) =>
            s == ItemStatus.Failed || s == ItemStatus.WrongPassword || s == ItemStatus.Damaged || s == ItemStatus.PartialFail;
    }

    public sealed class FileRename
    {
        public string From;
        public string To;
        public FileRename(string from, string to) { From = from; To = to; }
    }

    /// <summary>任務表的一列：一個（最外層的）壓縮檔，含它的分卷。</summary>
    public sealed class PlanItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void On([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        /// <summary>壓縮檔所在資料夾。</summary>
        public string Dir { get; set; }
        /// <summary>使用者加入的資料夾（找資料夾密碼、顯示相對路徑用）。</summary>
        public string RootDir { get; set; }
        public string OriginalName { get; set; }
        public string NewName { get; set; }
        public ArcKind Kind { get; set; }
        public long Size { get; set; }
        /// <summary>要改名的檔案（主卷＋其他分卷）。主卷一定在第一個。</summary>
        public List<FileRename> Renames { get; } = new List<FileRename>();
        public string NamePassword { get; set; }
        public string FolderPassword { get; set; }

        public string OriginalPath => Path.Combine(Dir, OriginalName);
        public string ArchivePath => Path.Combine(Dir, NewName);
        public string SizeText => Fmt.Size(Size);
        /// <summary>完整路徑（資料夾）。</summary>
        public string Location => Dir;

        /// <summary>0 = 最外層；內層壓縮檔是子列，排在父列底下。</summary>
        public int Depth { get; set; }
        public PlanItem Parent { get; set; }
        public string DisplayName => Depth == 0 ? OriginalName : new string('\u3000', Depth) + "└ " + OriginalName;

        public bool IsDescendantOf(PlanItem p)
        {
            for (var x = Parent; x != null; x = x.Parent) if (ReferenceEquals(x, p)) return true;
            return false;
        }

        string _password;
        bool _isResult, _resultNone;
        /// <summary>
        /// 解壓前：優先試的密碼（預設 = 檔名或資料夾抓到的，可在表格修改）。
        /// 解壓後：實際用到的密碼，沒加密顯示「無」。
        /// </summary>
        public string Password
        {
            get => _isResult && _resultNone ? L.T("log.noPw") : _password;
            set { _password = value; _isResult = false; _resultNone = false; On(); }
        }

        /// <summary>手動填（或掃描時抓到）的密碼；顯示的是解壓結果時回傳 null。</summary>
        public string WantedPassword => _isResult ? null : _password;

        /// <summary>null = 沒加密。</summary>
        public void SetResultPassword(string pw)
        {
            _password = pw;
            _isResult = true;
            _resultNone = pw == null;
            On(nameof(Password));
        }

        ItemStatus _status;
        public ItemStatus Status { get => _status; set { _status = value; On(); On(nameof(StatusText)); } }
        public string StatusText => L.T("st." + _status);

        DateTime? _start;
        TimeSpan _elapsed;
        bool _hasTime;
        public string ElapsedText => _hasTime ? Fmt.Time(_elapsed) : "";

        public void BeginTimer() { _start = DateTime.Now; _elapsed = TimeSpan.Zero; _hasTime = true; On(nameof(ElapsedText)); }
        public void StopTimer() { if (_start != null) _elapsed = DateTime.Now - _start.Value; _start = null; On(nameof(ElapsedText)); }
        public void Tick() { if (_start != null) { _elapsed = DateTime.Now - _start.Value; On(nameof(ElapsedText)); } }
        public bool IsRunning => _status == ItemStatus.Testing || _status == ItemStatus.Extracting;

        public void RefreshText() { On(nameof(StatusText)); On(nameof(ElapsedText)); On(nameof(SizeText)); On(nameof(Password)); }
    }

    /// <summary>
    /// 已解過的壓縮檔，重跑時跳過。done_list.txt 每行「大小|檔名 TAB 密碼」，
    /// 沒加密的密碼欄空白；舊格式（沒有 TAB）讀得到，只是不知道密碼。
    /// </summary>
    public sealed class DoneList
    {
        const string Unknown = "\u0001";
        readonly string _file;
        readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly object _lock = new object();
        public string FilePath => _file;

        public DoneList(string dir)
        {
            _file = Path.Combine(dir, "done_list.txt");
            if (!File.Exists(_file)) return;
            foreach (var raw in File.ReadAllLines(_file, System.Text.Encoding.UTF8))
            {
                var l = raw.TrimStart('\uFEFF');
                if (l.Length == 0) continue;
                int tab = l.IndexOf('\t');
                if (tab < 0) { if (!_map.ContainsKey(l)) _map[l] = Unknown; }
                else _map[l.Substring(0, tab)] = l.Substring(tab + 1);
            }
        }

        public static string Key(string path)
        {
            var fi = new FileInfo(path);
            return fi.Length + "|" + fi.Name;
        }

        public static string Key(long size, string name) => size + "|" + name;

        public bool Contains(string key) { lock (_lock) return _map.ContainsKey(key); }

        /// <summary>找得到且知道密碼 → true；pw = null 代表沒加密。</summary>
        public bool TryGetPassword(string key, out string pw)
        {
            pw = null;
            lock (_lock)
            {
                if (!_map.TryGetValue(key, out var v) || v == Unknown) return false;
                pw = v.Length == 0 ? null : v;
                return true;
            }
        }

        /// <summary>pw = null 代表沒加密。</summary>
        public void Mark(string path, string pw)
        {
            var k = Key(path);
            var v = pw ?? "";
            lock (_lock)
            {
                if (_map.TryGetValue(k, out var old) && old == v) return;
                _map[k] = v;
                try { File.AppendAllText(_file, k + "\t" + v + "\r\n", new System.Text.UTF8Encoding(false)); } catch { }
            }
        }
    }

    /// <summary>
    /// 改名紀錄：rename_history.txt，每行「時間 TAB 舊路徑 TAB 新路徑 TAB 檔名裡的密碼」。
    /// 給「復原改名」用；檔名裡的密碼也記下來，改名後重跑還找得回來。
    /// </summary>
    public sealed class RenameJournal
    {
        sealed class Entry { public string Time, From, To, Password; }

        readonly string _file;
        readonly object _lock = new object();
        readonly List<Entry> _entries = new List<Entry>();
        public string FilePath => _file;

        public RenameJournal(string dir)
        {
            _file = Path.Combine(dir, "rename_history.txt");
            if (!File.Exists(_file)) return;
            foreach (var raw in File.ReadAllLines(_file, System.Text.Encoding.UTF8))
            {
                var p = raw.TrimStart('﻿').Split('\t');
                if (p.Length < 3) continue;
                _entries.Add(new Entry { Time = p[0], From = p[1], To = p[2], Password = p.Length > 3 && p[3].Length > 0 ? p[3] : null });
            }
        }

        public int Count { get { lock (_lock) return _entries.Count; } }

        public void Add(string from, string to, string password)
        {
            var e = new Entry { Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), From = from, To = to, Password = password };
            lock (_lock)
            {
                _entries.Add(e);
                try { File.AppendAllText(_file, Line(e) + "\r\n", new System.Text.UTF8Encoding(false)); } catch { }
            }
        }

        /// <summary>這個檔是之前改名來的，而且舊檔名裡有密碼 → 回傳那個密碼。</summary>
        public string PasswordFor(string currentPath)
        {
            lock (_lock)
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                    if (string.Equals(_entries[i].To, currentPath, StringComparison.OrdinalIgnoreCase))
                        return _entries[i].Password;
            }
            return null;
        }

        /// <summary>從最新的開始全部改回去。改不回去的（檔案不見、舊名已被占用）留在紀錄裡。</summary>
        public (int ok, int skipped) UndoAll(Action<LogLevel, string> log)
        {
            int ok = 0, skip = 0;
            lock (_lock)
            {
                var keep = new List<Entry>();
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    var e = _entries[i];
                    try
                    {
                        if (File.Exists(e.To) && !File.Exists(e.From))
                        {
                            File.Move(e.To, e.From);
                            ok++;
                            log?.Invoke(LogLevel.Rename, L.F("log.undo", e.To, Path.GetFileName(e.From)));
                            continue;
                        }
                    }
                    catch (Exception ex) { log?.Invoke(LogLevel.Fail, L.F("log.error", e.To, ex.Message)); }
                    skip++;
                    log?.Invoke(LogLevel.Skip, L.F("log.undoSkip", e.To));
                    if (File.Exists(e.To)) keep.Insert(0, e);   // 檔案還在才值得留著，不見了就丟掉
                }
                _entries.Clear();
                _entries.AddRange(keep);
                try { File.WriteAllLines(_file, _entries.Select(Line), new System.Text.UTF8Encoding(false)); } catch { }
            }
            return (ok, skip);
        }

        static string Line(Entry e) => e.Time + "\t" + e.From + "\t" + e.To + "\t" + (e.Password ?? "");
    }
}
