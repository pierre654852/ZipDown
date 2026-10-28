using System.Diagnostics;
using System.Text;

namespace ZipDown.Core
{
    /// <summary>
    /// 解壓流程：改名 → 逐一試密碼（先測試、結束代碼 0 才算）→ 解到同名資料夾 →
    /// 省略多餘的一層資料夾 → 往下解內層。輸出資料夾已存在就跳過。
    /// </summary>
    public sealed class Unpacker
    {
        /// <summary>先用這個假密碼測試：能過代表沒加密。</summary>
        public const string NoPasswordProbe = "__NO_PASSWORD_CHECK__";

        readonly Settings _s;
        readonly PasswordBook _book;
        readonly IArchiveEngine _engine;
        readonly string _logFile;
        readonly object _logLock = new object();

        /// <summary>非 Detail 等級的紀錄會送到這裡（給畫面用）；全部紀錄都會寫進紀錄檔。</summary>
        public event Action<LogLevel, string> Log;
        /// <summary>更新 PlanItem 用（UI 要換到畫面執行緒）。</summary>
        public Action<Action> Post = a => a();

        volatile bool _stopRequested;
        public void RequestStop() { _stopRequested = true; Write(LogLevel.Skip, L.T("log.stopAfter")); }

        public int Renamed, Ok, Skipped, Failed;

        public Unpacker(Settings settings, PasswordBook book, IArchiveEngine engine, string logFile)
        {
            _s = settings; _book = book; _engine = engine; _logFile = logFile;
            if (_logFile != null) Directory.CreateDirectory(Path.GetDirectoryName(_logFile));
        }

        void Write(LogLevel level, string msg)
        {
            if (_logFile != null)
                lock (_logLock)
                    try { File.AppendAllText(_logFile, msg + "\r\n", new UTF8Encoding(false)); } catch { }
            if (level != LogLevel.Detail) Log?.Invoke(level, msg);
        }

        /// <summary>內層壓縮檔要顯示成子列：(父列, 子列)。會透過 Post 呼叫。</summary>
        public Action<PlanItem, PlanItem> ChildAdded;

        /// <summary>只處理最外層（Depth = 0）而且還沒完成的列。</summary>
        public static List<PlanItem> Todo(IEnumerable<PlanItem> items) =>
            items.Where(i => i.Depth == 0 && i.Status != ItemStatus.Done).ToList();

        public async Task RunAsync(IList<PlanItem> items, CancellationToken force)
        {
            var todo = Todo(items);
            if (todo.Count == 0) { Write(LogLevel.Skip, L.T("log.nothing")); return; }
            Write(LogLevel.Info, L.F("log.engine", _engine.Name, _engine.ExePath));
            Write(LogLevel.Info, L.F("log.found", todo.Count));
            Write(LogLevel.Info, "");
            int n = 0;
            foreach (var item in todo)
            {
                n++;
                if (_stopRequested || force.IsCancellationRequested) { Write(LogLevel.Skip, L.T("log.stopped")); break; }
                Post(() => { item.Status = ItemStatus.Testing; item.BeginTimer(); });
                ItemStatus final;
                try
                {
                    var arc = ApplyRenames(item);
                    if (arc == null) { Skipped++; final = ItemStatus.Skipped; }
                    else
                    {
                        var extra = new List<string> { item.WantedPassword, item.NamePassword, item.FolderPassword };
                        final = await Expand(arc, 1, "[" + n + "/" + todo.Count + "]", extra, item, item.RootDir, force);
                    }
                }
                catch (OperationCanceledException)
                {
                    Write(LogLevel.Fail, L.T("log.forceStop"));
                    Post(() => { item.Status = ItemStatus.Stopped; item.StopTimer(); });
                    break;
                }
                catch (Exception ex)
                {
                    Write(LogLevel.Fail, L.F("log.error", item.OriginalName, ex.Message));
                    Failed++;
                    final = ItemStatus.Failed;
                }
                Post(() => { item.Status = final; item.StopTimer(); });
            }
            Write(LogLevel.Info, "");
            Write(LogLevel.Info, L.F("log.summary", Renamed, Ok, Skipped, Failed));
            if (_logFile != null) Write(LogLevel.Info, L.F("log.logFile", _logFile));
        }

        /// <summary>照計畫改名（主卷＋分卷）。回傳主卷現在的路徑；主卷改不了名就回傳 null。</summary>
        string ApplyRenames(PlanItem item)
        {
            string primary = null;
            item.ArchiveFiles.Clear();
            for (int i = 0; i < item.Renames.Count; i++)
            {
                var r = item.Renames[i];
                string now = r.From;
                if (!string.Equals(r.From, r.To, StringComparison.Ordinal))
                {
                    if (!File.Exists(r.From) && File.Exists(r.To)) now = r.To;    // 已經改過了
                    else if (File.Exists(r.To))
                    {
                        Write(LogLevel.Skip, L.F("log.renameSkip", Path.GetFileName(r.To), Rel(item.RootDir, r.From)));
                        if (i == 0) return null;
                        continue;
                    }
                    else
                    {
                        try
                        {
                            File.Move(r.From, r.To);
                            now = r.To;
                            Renamed++;
                            Write(LogLevel.Rename, L.F("log.rename", Rel(item.RootDir, r.From), Path.GetFileName(r.To)));
                        }
                        catch (Exception ex)
                        {
                            Write(LogLevel.Fail, L.F("log.renameFail", Rel(item.RootDir, r.From), ex.Message));
                            if (i == 0) return null;
                            continue;
                        }
                    }
                }
                if (i == 0) primary = now;
                item.ArchiveFiles.Add(now);
            }
            return primary;
        }

        static string Rel(string root, string path)
        {
            if (string.IsNullOrEmpty(root)) return path;
            var parent = Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root;
            return Path.GetRelativePath(parent, path);
        }

        /// <summary>
        /// 解一個壓縮檔（row = 表格上對應的那一列），再往下解內層。
        /// 回傳這一層的結果；內層有失敗就回傳 PartialFail。
        /// </summary>
        async Task<ItemStatus> Expand(string arc, int depth, string tag, IEnumerable<string> extraPw,
                                      PlanItem row, string root, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Rel(root, arc);
            var leaf = Path.GetFileName(arc);
            var outDir = Path.Combine(Path.GetDirectoryName(arc), NameRules.OutFolderName(leaf));

            if (Directory.Exists(outDir) || File.Exists(outDir))
            {
                Write(LogLevel.Skip, L.F("log.skipExists", rel)); Skipped++;
                return ItemStatus.Skipped;
            }

            Write(LogLevel.Info, L.F("log.processing", tag, rel, Fmt.Size(FamilySize(arc))));
            var sw = Stopwatch.StartNew();

            var candidates = new List<string> { NoPasswordProbe };
            foreach (var p in extraPw.Concat(_book.Ordered()))
                if (!string.IsNullOrEmpty(p) && !candidates.Contains(p)) candidates.Add(p);

            string okPw = null;
            int lastOtherCode = 0;
            bool anyOther = false;
            Post(() => row.Status = ItemStatus.Testing);
            foreach (var pw in candidates)
            {
                ct.ThrowIfCancellationRequested();
                int code = await _engine.TestAsync(arc, pw, ct);
                Write(LogLevel.Detail, L.F("log.attempt", rel, pw == NoPasswordProbe ? L.T("log.noPw") : pw, code));
                if (code == 0) { okPw = pw; break; }
                if (!_engine.IsWrongPassword(code)) { anyOther = true; lastOtherCode = code; }
            }
            if (okPw == null)
            {
                Failed++;
                if (anyOther) { Write(LogLevel.Fail, L.F("log.damaged", lastOtherCode, rel)); return ItemStatus.Damaged; }
                Write(LogLevel.Fail, L.F("log.badPw", rel));
                return ItemStatus.WrongPassword;
            }

            Post(() => row.Status = ItemStatus.Extracting);
            Directory.CreateDirectory(outDir);
            int ec;
            try
            {
                ec = await _engine.ExtractAsync(arc, okPw, outDir, ct);
            }
            catch (OperationCanceledException)
            {
                // 強制停止：解一半的資料夾清掉，下次才會重新解
                try { Directory.Delete(outDir, true); Write(LogLevel.Skip, L.F("log.cleaned", Rel(root, outDir))); } catch { }
                throw;
            }
            if (ec != 0)
            {
                try { if (Directory.Exists(outDir) && !Directory.EnumerateFileSystemEntries(outDir).Any()) Directory.Delete(outDir); } catch { }
                Write(LogLevel.Fail, L.F("log.extractFail", ec, rel)); Failed++;
                return _engine.IsWrongPassword(ec) ? ItemStatus.WrongPassword : ItemStatus.Damaged;
            }

            Ok++;
            var used = okPw == NoPasswordProbe ? null : okPw;
            Post(() => row.SetResultPassword(used));
            var took = Fmt.Time(sw.Elapsed);
            if (used == null) Write(LogLevel.Success, L.F("log.okNoPw", rel, took));
            else
            {
                Write(LogLevel.Success, L.F("log.okPw", used, rel, took));
                _book.RecordSuccess(used);
                if (_book.AddIfMissing(used)) Write(LogLevel.Info, L.F("log.pwAdded", used));
            }
            CollapseSingleFolder(outDir, root);

            bool innerFailed = false;
            if (depth < _s.MaxDepth)
            {
                var scanner = new Scanner(_s.JunkWords);
                var inner = scanner.ScanFolder(outDir, outDir, inner: true);
                foreach (var sub in inner)
                {
                    ct.ThrowIfCancellationRequested();
                    sub.Depth = depth;
                    sub.Parent = row;
                    sub.RootDir = root;
                    Post(() => { ChildAdded?.Invoke(row, sub); sub.Status = ItemStatus.Testing; sub.BeginTimer(); });
                    ItemStatus st;
                    try
                    {
                        var subArc = ApplyRenames(sub);
                        if (subArc == null) st = ItemStatus.Skipped;
                        else
                        {
                            var pws = new List<string> { sub.NamePassword, sub.FolderPassword, used };
                            st = await Expand(subArc, depth + 1, L.T("log.inner"), pws, sub, root, ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Post(() => { sub.Status = ItemStatus.Stopped; sub.StopTimer(); });
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Write(LogLevel.Fail, L.F("log.error", sub.OriginalName, ex.Message));
                        Failed++;
                        st = ItemStatus.Failed;
                    }
                    var final = st;
                    Post(() => { sub.Status = final; sub.StopTimer(); });
                    if (st.IsFailure()) innerFailed = true;
                }
            }
            return innerFailed ? ItemStatus.PartialFail : ItemStatus.Done;
        }

        /// <summary>分卷的話把同組的大小加起來。</summary>
        static long FamilySize(string arc)
        {
            try
            {
                var info = NameRules.Analyze(Path.GetFileName(arc), null);
                long size = new FileInfo(arc).Length;
                if (!info.IsVolume) return size;
                var key = info.OutName.ToLowerInvariant() + "|" + info.TokenExt;
                foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(arc)))
                {
                    if (string.Equals(f, arc, StringComparison.OrdinalIgnoreCase)) continue;
                    var i = NameRules.Analyze(Path.GetFileName(f), null);
                    if (i.IsSecondary && i.OutName.ToLowerInvariant() + "|" + i.TokenExt == key) size += new FileInfo(f).Length;
                }
                return size;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 智慧解壓：輸出資料夾最上層只有 1 個資料夾、沒有檔案 → 把那個資料夾的內容往上搬一層，刪掉空資料夾。
        /// 只省一層。
        /// </summary>
        void CollapseSingleFolder(string outDir, string root)
        {
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(outDir).ToList();
                if (entries.Count != 1 || !Directory.Exists(entries[0])) return;
                var only = entries[0];
                var name = Path.GetFileName(only);
                // 先改成暫時名稱，裡面有同名子資料夾時才不會撞名
                var tmp = Path.Combine(outDir, "_zipdown_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.Move(only, tmp);
                foreach (var c in Directory.EnumerateFileSystemEntries(tmp).ToList())
                {
                    var target = Path.Combine(outDir, Path.GetFileName(c));
                    if (Directory.Exists(c)) Directory.Move(c, target); else File.Move(c, target);
                }
                Directory.Delete(tmp);
                Write(LogLevel.Info, L.F("log.collapsed", name));
            }
            catch (Exception ex) { Write(LogLevel.Fail, L.F("log.collapseFail", Rel(root, outDir), ex.Message)); }
        }

        /// <summary>
        /// 「刪除已解壓的原檔」要刪的檔：狀態為「完成」的最外層列，連同它底下狀態為「完成」的內層列，
        /// 每列的全部分卷。部分失敗／失敗的整組都不刪。
        /// </summary>
        public static List<string> FilesToDelete(IList<PlanItem> rows)
        {
            var files = new List<string>();
            foreach (var top in rows.Where(r => r.Depth == 0 && r.Status == ItemStatus.Done))
            {
                foreach (var r in rows.Where(r => ReferenceEquals(r, top) || (r.IsDescendantOf(top) && r.Status == ItemStatus.Done)))
                    foreach (var f in r.ArchiveFiles)
                        if (File.Exists(f) && !files.Contains(f, StringComparer.OrdinalIgnoreCase)) files.Add(f);
            }
            return files;
        }
    }
}
