using System.Diagnostics;
using System.Text;

namespace ZipDown.Core
{
    /// <summary>
    /// 解壓流程（照 unpack V7）：改名 → 逐一試密碼（先測試、結束代碼 0 才算）→ 解到同名資料夾 →
    /// 往下解內層 → 內層內容搬到第一層資料夾。
    /// </summary>
    public sealed class Unpacker
    {
        /// <summary>先用這個假密碼測試：能過代表沒加密。</summary>
        public const string NoPasswordProbe = "__NO_PASSWORD_CHECK__";

        readonly Settings _s;
        readonly PasswordBook _book;
        readonly DoneList _done;
        readonly RenameJournal _journal;
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

        public Unpacker(Settings settings, PasswordBook book, DoneList done, RenameJournal journal, IArchiveEngine engine, string logFile)
        {
            _s = settings; _book = book; _done = done; _journal = journal; _engine = engine; _logFile = logFile;
            if (_logFile != null) Directory.CreateDirectory(Path.GetDirectoryName(_logFile));
        }

        void Write(LogLevel level, string msg)
        {
            if (_logFile != null)
                lock (_logLock)
                    try { File.AppendAllText(_logFile, msg + "\r\n", new UTF8Encoding(false)); } catch { }
            if (level != LogLevel.Detail) Log?.Invoke(level, msg);
        }

        public async Task RunAsync(IList<PlanItem> items, CancellationToken force)
        {
            Write(LogLevel.Info, L.F("log.engine", _engine.Name, _engine.ExePath));
            var todo = items.Where(i => i.Status != ItemStatus.Done).ToList();
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
                    final = await ProcessTop(item, "[" + n + "/" + todo.Count + "]", force);
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

        async Task<ItemStatus> ProcessTop(PlanItem item, string tag, CancellationToken ct)
        {
            var arc = ApplyRenames(item);
            if (arc == null) { Skipped++; return ItemStatus.Skipped; }
            var extra = new List<string> { item.Password, item.NamePassword, item.FolderPassword };
            var r = await Expand(arc, 1, tag, null, extra, item, item.RootDir, ct);
            return r;
        }

        /// <summary>照計畫改名（主卷＋分卷）。回傳主卷現在的路徑；主卷改不了名就回傳 null。</summary>
        string ApplyRenames(PlanItem item)
        {
            string primary = null;
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
                            _journal?.Add(r.From, r.To, item.NamePassword);
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
            }
            return primary;
        }

        static string Rel(string root, string path)
        {
            if (string.IsNullOrEmpty(root)) return path;
            var parent = Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root;
            return Path.GetRelativePath(parent, path);
        }

        async Task<ItemStatus> Expand(string arc, int depth, string tag, string topOut, IEnumerable<string> extraPw,
                                      PlanItem item, string root, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Rel(root, arc);
            var leaf = Path.GetFileName(arc);
            var outDir = Path.Combine(Path.GetDirectoryName(arc), NameRules.OutFolderName(leaf));

            if (Directory.Exists(outDir) || File.Exists(outDir)) { Write(LogLevel.Skip, L.F("log.skipExists", rel)); Skipped++; return ItemStatus.Skipped; }
            if (_done.Contains(DoneList.Key(arc))) { Write(LogLevel.Skip, L.F("log.skipDone", rel)); Skipped++; return ItemStatus.Skipped; }
            topOut ??= outDir;

            Write(LogLevel.Info, L.F("log.processing", tag, rel, Fmt.Size(FamilySize(arc))));
            var sw = Stopwatch.StartNew();

            var candidates = new List<string> { NoPasswordProbe };
            foreach (var p in extraPw.Concat(_book.Ordered()))
                if (!string.IsNullOrEmpty(p) && !candidates.Contains(p)) candidates.Add(p);

            string okPw = null;
            Post(() => item.Status = ItemStatus.Testing);
            foreach (var pw in candidates)
            {
                ct.ThrowIfCancellationRequested();
                int code = await _engine.TestAsync(arc, pw, ct);
                Write(LogLevel.Detail, L.F("log.attempt", rel, pw == NoPasswordProbe ? L.T("log.noPw") : pw, code));
                if (code == 0) { okPw = pw; break; }
            }
            if (okPw == null) { Write(LogLevel.Fail, L.F("log.badPw", rel)); Failed++; return ItemStatus.Failed; }

            Post(() => item.Status = ItemStatus.Extracting);
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
                Write(LogLevel.Fail, L.F("log.extractFail", ec, rel)); Failed++; return ItemStatus.Failed;
            }

            Ok++;
            var took = Fmt.Time(sw.Elapsed);
            if (okPw == NoPasswordProbe) Write(LogLevel.Success, L.F("log.okNoPw", rel, took));
            else
            {
                Write(LogLevel.Success, L.F("log.okPw", okPw, rel, took));
                _book.RecordSuccess(okPw);
                if (_book.AddIfMissing(okPw)) Write(LogLevel.Info, L.F("log.pwAdded", okPw));
            }
            _done.Mark(arc);

            if (depth < _s.MaxDepth)
            {
                var scanner = new Scanner(_s.JunkWords, _journal, null);
                var inner = scanner.ScanFolder(outDir, outDir, inner: true);
                foreach (var sub in inner)
                {
                    ct.ThrowIfCancellationRequested();
                    var subArc = ApplyRenames(sub);
                    if (subArc == null) continue;
                    var pws = new List<string> { sub.NamePassword, sub.FolderPassword, okPw == NoPasswordProbe ? null : okPw };
                    await Expand(subArc, depth + 1, L.T("log.inner"), topOut, pws, item, root, ct);
                }
            }
            if (depth > 1 && _s.Flatten) MoveUp(outDir, topOut, root);
            return ItemStatus.Done;
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

        /// <summary>撞名時加 (2)、(3)…</summary>
        public static string FreePath(string dir, string name, bool isDir)
        {
            var t = Path.Combine(dir, name);
            if (!File.Exists(t) && !Directory.Exists(t)) return t;
            string b = isDir ? name : Path.GetFileNameWithoutExtension(name);
            string e = isDir ? "" : Path.GetExtension(name);
            for (int k = 2; ; k++)
            {
                t = Path.Combine(dir, b + " (" + k + ")" + e);
                if (!File.Exists(t) && !Directory.Exists(t)) return t;
            }
        }

        /// <summary>把 src 裡的東西全部搬到 dst，然後移除 src。</summary>
        void MoveUp(string src, string dst, string root)
        {
            try
            {
                var tmp = Path.Combine(Path.GetDirectoryName(src), "_zipdown_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.Move(src, tmp);
                foreach (var c in Directory.EnumerateFileSystemEntries(tmp).ToList())
                {
                    bool isDir = Directory.Exists(c);
                    var name = Path.GetFileName(c);
                    var target = FreePath(dst, name, isDir);
                    if (isDir) Directory.Move(c, target); else File.Move(c, target);
                    if (Path.GetFileName(target) != name) Write(LogLevel.Skip, L.F("log.moveConflict", Rel(root, target)));
                }
                if (!Directory.EnumerateFileSystemEntries(tmp).Any()) Directory.Delete(tmp);
                Write(LogLevel.Info, L.F("log.moved", Rel(root, src)));
            }
            catch (Exception ex) { Write(LogLevel.Fail, L.F("log.moveFail", Rel(root, src), ex.Message)); }
        }
    }
}
