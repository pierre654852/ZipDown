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

    public enum ItemStatus { Pending, Testing, Extracting, Done, PartialFail, Skipped, WrongPassword, Damaged, Failed, Stopped }

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
        /// <summary>解壓時實際的檔案路徑（主卷＋分卷，改名後）。刪除原檔用。</summary>
        public List<string> ArchiveFiles { get; } = new List<string>();
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
}
