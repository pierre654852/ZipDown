using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZipDown.Core;
using ZipDown.Engines;

namespace ZipDown.App
{
    public sealed class LogLine
    {
        public string Text { get; set; }
        public IBrush Brush { get; set; }
    }

    public partial class MainWindow : Window
    {
        static readonly string[] EngineKeys = { "auto", "bandizip", "winrar" };
        const int MaxLogLines = 5000;

        readonly ObservableCollection<PlanItem> _items = new ObservableCollection<PlanItem>();
        readonly ObservableCollection<LogLine> _log = new ObservableCollection<LogLine>();
        readonly HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly Settings _settings;
        readonly PasswordBook _book;

        bool _running, _busy;
        int _stopPresses;
        Unpacker _unpacker;
        CancellationTokenSource _cts;
        readonly DispatcherTimer _timer;

        DataGridTextColumn _cOrig, _cSize, _cPw, _cStatus, _cTime;
        DataGridTemplateColumn _cDir;

        public MainWindow()
        {
            InitializeComponent();

            _settings = Settings.Load();
            _book = new PasswordBook();
            L.Set(_settings.Language);

            BuildColumns();
            TaskGrid.ItemsSource = _items;
            LogList.ItemsSource = _log;
            _items.CollectionChanged += (_, _) => UpdateState();

            EngineBox.SelectionChanged += (_, _) =>
            {
                if (EngineBox.SelectedIndex < 0) return;
                var k = EngineKeys[EngineBox.SelectedIndex];
                if (k == _settings.Engine) return;
                _settings.Engine = k;
                TrySaveSettings();
            };

            AddFilesBtn.Click += OnAddFiles;
            AddFolderBtn.Click += OnAddFolder;
            RemoveBtn.Click += (_, _) =>
            {
                var sel = TaskGrid.SelectedItems.Cast<PlanItem>().ToList();
                foreach (var it in _items.Where(x => sel.Contains(x) || sel.Any(x.IsDescendantOf)).ToList()) RemoveRow(it);
            };
            ClearBtn.Click += (_, _) => { _items.Clear(); _known.Clear(); };
            DeleteBtn.Click += OnDeleteDone;
            StartBtn.Click += OnStart;
            StopBtn.Click += OnStop;
            SettingsBtn.Click += OnSettings;
            AboutBtn.Click += async (_, _) => await new AboutWindow().ShowDialog(this);

            TaskGrid.DoubleTapped += (_, _) =>
            {
                // 雙擊「位置」欄 → 檔案總管選取該檔；其他欄照舊進入編輯（密碼欄）
                if (ReferenceEquals(TaskGrid.CurrentColumn, _cDir)) { if (TaskGrid.SelectedItem is PlanItem it) ShowInExplorer(it); }
                else TaskGrid.BeginEdit();
            };

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => { foreach (var it in _items) if (it.IsRunning) it.Tick(); };

            Closing += (_, _) => { try { _cts?.Cancel(); } catch { } };

            ApplyLanguage();
            UpdateState();
        }

        // ---------------- 介面文字 ----------------

        void BuildColumns()
        {
            DataGridTextColumn Col(string path, double stars, bool readOnly = true, BindingMode mode = BindingMode.OneWay)
            {
                var c = new DataGridTextColumn
                {
                    Binding = new Binding(path) { Mode = mode },
                    IsReadOnly = readOnly,
                    Width = new DataGridLength(stars, DataGridLengthUnitType.Star),
                };
                TaskGrid.Columns.Add(c);
                return c;
            }
            _cOrig = Col(nameof(PlanItem.DisplayName), 3);
            _cSize = Col(nameof(PlanItem.SizeText), 0.8);
            _cPw = Col(nameof(PlanItem.Password), 1.2, readOnly: false, mode: BindingMode.TwoWay);
            _cStatus = Col(nameof(PlanItem.StatusText), 1.1);
            _cTime = Col(nameof(PlanItem.ElapsedText), 0.9);

            // 位置：完整路徑，太長被截掉時滑鼠停著看全文
            _cDir = new DataGridTemplateColumn
            {
                IsReadOnly = true,
                Width = new DataGridLength(3, DataGridLengthUnitType.Star),
                CellTemplate = new FuncDataTemplate<PlanItem>((_, _) => new TextBlock
                {
                    [!TextBlock.TextProperty] = new Binding(nameof(PlanItem.Location)),
                    [!ToolTip.TipProperty] = new Binding(nameof(PlanItem.Location)),
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(12, 0, 4, 0),
                }),
            };
            TaskGrid.Columns.Add(_cDir);
        }

        // ---------------- 列 ----------------

        static IEnumerable<string> Keys(PlanItem it)
        {
            yield return it.OriginalPath;
            yield return it.ArchivePath;
        }

        /// <summary>原檔名或改名後的檔名已經在表格裡 → 同一個檔。</summary>
        bool IsKnown(PlanItem it) => Keys(it).Any(_known.Contains);

        void AddRow(PlanItem it)
        {
            foreach (var k in Keys(it)) _known.Add(k);
            _items.Add(it);
        }

        void RemoveRow(PlanItem it)
        {
            _items.Remove(it);
            foreach (var k in Keys(it)) _known.Remove(k);
        }

        /// <summary>內層壓縮檔：插在父列（和父列已有的子列）後面。</summary>
        void InsertChild(PlanItem parent, PlanItem child)
        {
            int i = _items.IndexOf(parent);
            if (i < 0) { AddRow(child); return; }
            i++;
            while (i < _items.Count && _items[i].IsDescendantOf(parent)) i++;
            foreach (var k in Keys(child)) _known.Add(k);
            _items.Insert(i, child);
        }

        void ApplyLanguage()
        {
            Title = L.T("app.title") + "  v" + AppInfo.Version;
            EngineLabel.Text = L.T("ui.engine");
            EngineBox.ItemsSource = new[] { L.T("eng.auto"), "Bandizip", "WinRAR" };
            EngineBox.SelectedIndex = Math.Max(0, Array.IndexOf(EngineKeys, _settings.Engine));

            AddFilesBtn.Content = L.T("ui.addFiles");
            AddFolderBtn.Content = L.T("ui.addFolder");
            RemoveBtn.Content = L.T("ui.remove");
            ClearBtn.Content = L.T("ui.clear");
            DeleteBtn.Content = L.T("ui.deleteDone");
            StartBtn.Content = L.T("ui.start");
            StopBtn.Content = _stopPresses > 0 ? L.T("ui.forceStop") : L.T("ui.stop");
            SettingsBtn.Content = L.T("ui.settings");
            AboutBtn.Content = L.T("ui.about");
            ToolTip.SetTip(HelpIcon, BuildHelp());
            DropHint.Text = L.T("ui.dropHint");

            _cOrig.Header = L.T("col.original");
            _cSize.Header = L.T("col.size");
            _cPw.Header = L.T("col.password");
            _cStatus.Header = L.T("col.status");
            _cTime.Header = L.T("col.elapsed");
            _cDir.Header = L.T("col.folder");

            foreach (var it in _items) it.RefreshText();
        }

        void UpdateState()
        {
            bool idle = !_running && !_busy;
            EngineBox.IsEnabled = idle;
            AddFilesBtn.IsEnabled = idle;
            AddFolderBtn.IsEnabled = idle;
            RemoveBtn.IsEnabled = idle;
            ClearBtn.IsEnabled = idle;
            DeleteBtn.IsEnabled = idle && _items.Any(i => i.Status == ItemStatus.Done);
            SettingsBtn.IsEnabled = idle;
            StartBtn.IsEnabled = idle && _items.Count > 0;
            StopBtn.IsEnabled = _running && _stopPresses < 2;
            DropHint.IsVisible = _items.Count == 0;
        }

        // ---------------- 「？」使用說明 ----------------

        /// <summary>help.text：「# 」標題、「## 」段落標題，其他每行一段（自動換行）。</summary>
        static Control BuildHelp()
        {
            var panel = new StackPanel { MaxWidth = 440, Spacing = 3, Margin = new Avalonia.Thickness(4) };
            bool first = true;
            foreach (var raw in L.T("help.text").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
                if (line.StartsWith("## "))
                {
                    tb.Text = line.Substring(3);
                    tb.FontWeight = FontWeight.SemiBold;
                    tb.Margin = new Avalonia.Thickness(0, first ? 0 : 8, 0, 0);
                }
                else if (line.StartsWith("# "))
                {
                    tb.Text = line.Substring(2);
                    tb.FontSize = 18;
                    tb.FontWeight = FontWeight.Bold;
                }
                else
                {
                    tb.Text = line.StartsWith("- ") ? "• " + line.Substring(2) : line;
                    tb.Margin = new Avalonia.Thickness(8, 0, 0, 0);
                }
                first = false;
                panel.Children.Add(tb);
            }
            return panel;
        }

        // ---------------- 雙擊位置 → 檔案總管 ----------------

        void ShowInExplorer(PlanItem it)
        {
            // 優先指向實際的檔案：解壓時的主卷 → 改名後 → 原檔名；都不在就開資料夾
            var file = it.ArchiveFiles.FirstOrDefault(File.Exists)
                       ?? new[] { it.ArchivePath, it.OriginalPath }.FirstOrDefault(File.Exists);
            var dir = file == null && Directory.Exists(it.Dir) ? it.Dir : null;
            if (file == null && dir == null) { AddLog(LogLevel.Fail, L.F("log.notFound", it.Dir)); return; }
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                    psi.Arguments = file != null ? "/select,\"" + file + "\"" : "\"" + dir + "\"";
                    System.Diagnostics.Process.Start(psi);
                }
                else
                {
                    var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                    var psi = new System.Diagnostics.ProcessStartInfo(opener) { UseShellExecute = false };
                    if (OperatingSystem.IsMacOS() && file != null) { psi.ArgumentList.Add("-R"); psi.ArgumentList.Add(file); }
                    else psi.ArgumentList.Add(dir ?? Path.GetDirectoryName(file));
                    System.Diagnostics.Process.Start(psi);
                }
            }
            catch (Exception ex) { AddLog(LogLevel.Fail, L.F("log.openFail", file ?? dir, ex.Message)); }
        }

        // ---------------- 紀錄 ----------------

        static readonly IBrush BInfo = new SolidColorBrush(Color.Parse("#D4D4D4"));
        static readonly IBrush BOk = new SolidColorBrush(Color.Parse("#6CCB5F"));
        static readonly IBrush BFail = new SolidColorBrush(Color.Parse("#F06A6A"));
        static readonly IBrush BSkip = new SolidColorBrush(Color.Parse("#E5C07B"));
        static readonly IBrush BRen = new SolidColorBrush(Color.Parse("#56B6C2"));

        void AddLog(LogLevel level, string msg)
        {
            var brush = level switch
            {
                LogLevel.Success => BOk,
                LogLevel.Fail => BFail,
                LogLevel.Skip => BSkip,
                LogLevel.Rename => BRen,
                _ => BInfo,
            };
            var line = new LogLine { Text = msg, Brush = brush };
            _log.Add(line);
            while (_log.Count > MaxLogLines) _log.RemoveAt(0);
            LogList.ScrollIntoView(line);
        }

        void PostLog(LogLevel level, string msg) => Dispatcher.UIThread.Post(() => AddLog(level, msg));

        void TrySaveSettings()
        {
            try { _settings.Save(); } catch (Exception ex) { AddLog(LogLevel.Fail, L.F("log.error", _settings.FilePath, ex.Message)); }
        }

        // ---------------- 加入檔案 ----------------

        async void OnAddFiles(object sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = L.T("ui.addFiles"),
                SuggestedStartLocation = await StartFolder(),
            });
            await AddPaths(files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList());
        }

        async void OnAddFolder(object sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = true,
                Title = L.T("ui.addFolder"),
                SuggestedStartLocation = await StartFolder(),
            });
            await AddPaths(folders.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList());
        }

        async Task<IStorageFolder> StartFolder()
        {
            if (string.IsNullOrEmpty(_settings.LastFolder) || !Directory.Exists(_settings.LastFolder)) return null;
            try { return await StorageProvider.TryGetFolderFromPathAsync(_settings.LastFolder); } catch { return null; }
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = !_running && !_busy && e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        async void OnDrop(object sender, DragEventArgs e)
        {
            if (_running || _busy) return;
            var items = e.Data.GetFiles();
            if (items == null) return;
            await AddPaths(items.Select(i => i.TryGetLocalPath()).Where(p => p != null).ToList());
        }

        async Task AddPaths(IList<string> paths)
        {
            if (paths.Count == 0) return;
            _busy = true; UpdateState();
            try
            {
                var scanner = new Scanner(_settings.JunkWords);
                int added = 0;
                foreach (var p in paths)
                {
                    AddLog(LogLevel.Info, L.F("ui.scanning", p));
                    List<PlanItem> found;
                    try { found = await Task.Run(() => scanner.ScanPath(p)); }
                    catch (Exception ex) { AddLog(LogLevel.Fail, L.F("log.error", p, ex.Message)); continue; }
                    foreach (var it in found)
                        if (!IsKnown(it)) { AddRow(it); added++; }
                }
                AddLog(LogLevel.Info, L.F("ui.scanned", added));

                var last = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]);
                if (last != null && last != _settings.LastFolder) { _settings.LastFolder = last; TrySaveSettings(); }
            }
            finally { _busy = false; UpdateState(); }
        }

        // ---------------- 開始／停止 ----------------

        async void OnStart(object sender, RoutedEventArgs e)
        {
            if (_running || _items.Count == 0) return;
            TaskGrid.CommitEdit();
            var todo = Unpacker.Todo(_items);
            if (todo.Count == 0) { AddLog(LogLevel.Skip, L.T("log.nothing")); return; }
            var engine = EngineLocator.Create(_settings);
            if (engine == null) { AddLog(LogLevel.Fail, L.T("log.noEngine")); return; }

            _running = true;
            _stopPresses = 0;
            StopBtn.Content = L.T("ui.stop");
            _cts = new CancellationTokenSource();
            UpdateState();
            _timer.Start();

            var logFile = Path.Combine(Settings.AppDir, "logs", "ZipDown_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            _unpacker = new Unpacker(_settings, _book, engine, logFile)
            {
                Post = a => Dispatcher.UIThread.Post(a),
            };
            _unpacker.Log += PostLog;
            _unpacker.ChildAdded = InsertChild;   // Unpacker 會透過 Post 在畫面執行緒呼叫

            // 要重跑的列：先清掉上次留下的子列
            foreach (var old in _items.Where(x => todo.Any(x.IsDescendantOf)).ToList()) RemoveRow(old);
            var list = todo;
            var token = _cts.Token;
            try
            {
                await Task.Run(() => _unpacker.RunAsync(list, token));
            }
            catch (Exception ex) { AddLog(LogLevel.Fail, L.F("log.error", "", ex.Message)); }
            finally
            {
                _timer.Stop();
                _running = false;
                _stopPresses = 0;
                StopBtn.Content = L.T("ui.stop");
                _unpacker = null;
                _cts.Dispose();
                _cts = null;
                UpdateState();
            }
        }

        void OnStop(object sender, RoutedEventArgs e)
        {
            if (!_running) return;
            _stopPresses++;
            if (_stopPresses == 1)
            {
                _unpacker?.RequestStop();
                StopBtn.Content = L.T("ui.forceStop");
            }
            else
            {
                try { _cts?.Cancel(); } catch { }
            }
            UpdateState();
        }

        // ---------------- 刪除已解壓的原檔 ----------------

        async void OnDeleteDone(object sender, RoutedEventArgs e)
        {
            if (_running || _busy) return;
            var files = Unpacker.FilesToDelete(_items);
            if (files.Count == 0) { AddLog(LogLevel.Info, L.T("del.none")); return; }
            if (!OperatingSystem.IsWindows()) { AddLog(LogLevel.Fail, L.T("del.unsupported")); return; }

            // 連續確認兩次
            if (!await ConfirmWindow.Ask(this, L.F("del.confirm1", files.Count))) return;
            if (!await ConfirmWindow.Ask(this, L.F("del.confirm2", files.Count))) return;

            int ok = 0, fail = 0;
            foreach (var f in files)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    ok++;
                    AddLog(LogLevel.Rename, L.F("del.ok", f));
                }
                catch (Exception ex) { fail++; AddLog(LogLevel.Fail, L.F("del.fail", f, ex.Message)); }
            }
            foreach (var it in _items) it.ArchiveFiles.RemoveAll(f => !File.Exists(f));
            AddLog(LogLevel.Info, L.F("del.summary", ok, fail));
            UpdateState();
        }

        // ---------------- 設定 ----------------

        async void OnSettings(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_settings, _book);
            var saved = await w.ShowDialog<bool>(this);
            if (!saved) return;
            TrySaveSettings();
            L.Set(_settings.Language);
            ApplyLanguage();
        }
    }
}
