namespace ZipDown.Core
{
    /// <summary>
    /// 找出資料夾裡的壓縮檔並算出「應該改成什麼名字」，但不動檔案（改名在開始解壓時才做）。
    /// </summary>
    public sealed class Scanner
    {
        readonly IList<string> _junk;
        readonly RenameJournal _journal;
        readonly DoneList _done;

        static readonly EnumerationOptions Recursive = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        public Scanner(IList<string> junkWords, RenameJournal journal = null, DoneList done = null)
        {
            _junk = junkWords ?? new List<string>();
            _journal = journal;
            _done = done;
        }

        /// <summary>使用者加入的東西：檔案或資料夾。</summary>
        public List<PlanItem> ScanPath(string path)
        {
            if (Directory.Exists(path))
                return ScanFolder(path, path, inner: false);
            if (File.Exists(path))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                // 單獨加入的檔：同資料夾的其他分卷也要看到，才能一起改名
                var siblings = Directory.EnumerateFiles(dir).ToList();
                var items = ScanFiles(siblings, dir, inner: false);
                var name = Path.GetFileName(path);
                return items.Where(i => i.OriginalName == name).ToList();
            }
            return new List<PlanItem>();
        }

        public List<PlanItem> ScanFolder(string dir, string rootDir, bool inner)
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", Recursive).ToList(); }
            catch { files = new List<string>(); }
            return ScanFiles(files, rootDir, inner);
        }

        sealed class Entry
        {
            public string Path, Name;
            public NameInfo Info;
            public string Family;   // 同一組分卷的 key
        }

        public List<PlanItem> ScanFiles(IEnumerable<string> files, string rootDir, bool inner)
        {
            var result = new List<PlanItem>();
            foreach (var g in files.GroupBy(f => System.IO.Path.GetDirectoryName(f), StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entries = g.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f =>
                {
                    var name = System.IO.Path.GetFileName(f);
                    var info = NameRules.Analyze(name, _junk);
                    var e = new Entry { Path = f, Name = name, Info = info };
                    if (info.HasToken)
                        e.Family = info.OutName.ToLowerInvariant() + "|" + info.TokenExt + "|" + (info.VolumeNo > 0 ? "n" : "p");
                    return e;
                }).ToList();

                var families = entries.Where(e => e.Family != null).ToLookup(e => e.Family);

                foreach (var e in entries)
                {
                    var item = e.Info.HasToken ? FromToken(e, families) : FromContent(e, inner);
                    if (item == null) continue;
                    item.Dir = g.Key;
                    item.RootDir = rootDir;
                    item.FolderPassword = FolderPassword(g.Key, rootDir);
                    item.Password = item.NamePassword ?? item.FolderPassword;
                    if (_done != null && _done.Contains(DoneList.Key(item.Size, item.NewName))) item.Status = ItemStatus.AlreadyDone;
                    result.Add(item);
                }
            }
            return result;
        }

        /// <summary>檔名裡有 .zip/.rar/.7z/分卷記號的。</summary>
        PlanItem FromToken(Entry e, ILookup<string, Entry> families)
        {
            var info = e.Info;
            if (info.IsSecondary) return null;   // 第 2 卷以後跟著主卷走
            // 壓縮副檔名後面還有東西（偽裝副檔名或密碼）→ 一定要看內容確定真的是壓縮檔
            if (info.HasRemainder && Sniffer.Detect(e.Path) == ArcKind.None) return null;

            var item = new PlanItem
            {
                OriginalName = e.Name,
                NewName = info.Clean,
                Kind = Sniffer.KindFromExt(info.TokenExt),
                NamePassword = info.Password ?? _journal?.PasswordFor(e.Path),
            };
            item.Renames.Add(new FileRename(e.Path, Sibling(e.Path, info.Clean)));
            long size = Len(e.Path);

            foreach (var m in families[e.Family])
            {
                if (ReferenceEquals(m, e) || !m.Info.IsSecondary) continue;
                item.Renames.Add(new FileRename(m.Path, Sibling(m.Path, m.Info.Clean)));
                size += Len(m.Path);
            }
            item.Size = size;
            return item;
        }

        static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png" };

        /// <summary>檔名看不出是壓縮檔，用內容判斷（.mp4 偽裝；內層的圖片、沒副檔名的檔）。</summary>
        PlanItem FromContent(Entry e, bool inner)
        {
            var ext = System.IO.Path.GetExtension(e.Info.NoJunk).ToLowerInvariant();
            ArcKind kind = ArcKind.None;
            try
            {
                if (ext == ".mp4")
                {
                    if (Sniffer.Mp4(e.Path, out var k) == Sniffer.Mp4Result.Archive) kind = k;
                }
                else if (inner && ImageExts.Contains(ext))
                {
                    if (Sniffer.ZipTail(e.Path)) kind = ArcKind.Zip;
                }
                else if (inner && !NameRules.IsRealExt(ext))
                {
                    kind = Sniffer.Head(e.Path);
                }
            }
            catch { return null; }
            if (kind == ArcKind.None) return null;

            var newName = NameRules.WithArchiveExt(e.Info.NoJunk, kind);
            var item = new PlanItem
            {
                OriginalName = e.Name,
                NewName = newName,
                Kind = kind,
                Size = Len(e.Path),
                NamePassword = _journal?.PasswordFor(e.Path),
            };
            item.Renames.Add(new FileRename(e.Path, Sibling(e.Path, newName)));
            return item;
        }

        /// <summary>從壓縮檔所在資料夾往上找到 rootDir，資料夾名稱裡有「密碼／解壓碼／pw」就取出來（最近的優先）。</summary>
        public static string FolderPassword(string dir, string rootDir)
        {
            var root = (rootDir ?? dir).TrimEnd('\\', '/');
            var d = dir;
            for (int guard = 0; d != null && guard < 64; guard++)
            {
                var pw = NameRules.KeywordPassword(System.IO.Path.GetFileName(d.TrimEnd('\\', '/')));
                if (pw != null) return pw;
                if (string.Equals(d.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)) break;
                d = System.IO.Path.GetDirectoryName(d);
            }
            return null;
        }

        static string Sibling(string path, string newName) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), newName);

        static long Len(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    }
}
