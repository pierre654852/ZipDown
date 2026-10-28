using System.Collections.ObjectModel;
using Avalonia.Controls;
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
        readonly List<string> _sources = new List<string>();

        readonly Settings _settings;
        readonly PasswordBook _book;
        readonly DoneList _done;
        readonly RenameJournal _journal;

        bool _running, _busy;
        int _stopPresses;
        Unpacker _unpacker;
        CancellationTokenSource _cts;
        readonly DispatcherTimer _timer;

        DataGridTextColumn _cOrig, _cNew, _cSize, _cPw, _cStatus, _cTime, _cDir;

        public MainWindow()
        {
            InitializeComponent();

            _settings = Settings.Load();
            _book = new PasswordBook();
            _done = new DoneList(Settings.AppDir);
            _journal = new RenameJournal(Settings.AppDir);
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
                foreach (var it in TaskGrid.SelectedItems.Cast<PlanItem>().ToList()) { _items.Remove(it); _known.Remove(it.OriginalPath); }
            };
            ClearBtn.Click += (_, _) => { _items.Clear(); _known.Clear(); _sources.Clear(); };
            UndoBtn.Click += OnUndo;
            StartBtn.Click += OnStart;
            StopBtn.Click += OnStop;
            SettingsBtn.Click += OnSettings;
            AboutBtn.Click += async (_, _) => await new AboutWindow().ShowDialog(this);

            TaskGrid.DoubleTapped += (_, _) => TaskGrid.BeginEdit();

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
            _cOrig = Col(nameof(PlanItem.OriginalName), 3);
            _cNew = Col(nameof(PlanItem.RenamedDisplay), 2.2);
            _cSize = Col(nameof(PlanItem.SizeText), 0.8);
            _cPw = Col(nameof(PlanItem.Password), 1.2, readOnly: false, mode: BindingMode.TwoWay);
            _cStatus = Col(nameof(PlanItem.StatusText), 1);
            _cTime = Col(nameof(PlanItem.ElapsedText), 0.9);
            _cDir = Col(nameof(PlanItem.Location), 2);
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
            UndoBtn.Content = L.T("ui.undo");
            StartBtn.Content = L.T("ui.start");
            StopBtn.Content = _stopPresses > 0 ? L.T("ui.forceStop") : L.T("ui.stop");
            SettingsBtn.Content = L.T("ui.settings");
            AboutBtn.Content = L.T("ui.about");
            DropHint.Text = L.T("ui.dropHint");

            _cOrig.Header = L.T("col.original");
            _cNew.Header = L.T("col.renamed");
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
            UndoBtn.IsEnabled = idle;
            SettingsBtn.IsEnabled = idle;
            StartBtn.IsEnabled = idle && _items.Count > 0;
            StopBtn.IsEnabled = _running && _stopPresses < 2;
            DropHint.IsVisible = _items.Count == 0;
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

        async Task AddPaths(IList<string> paths, bool remember = true)
        {
            if (paths.Count == 0) return;
            _busy = true; UpdateState();
            try
            {
                var scanner = new Scanner(_settings.JunkWords, _journal, _done);
                int added = 0;
                foreach (var p in paths)
                {
                    AddLog(LogLevel.Info, L.F("ui.scanning", p));
                    List<PlanItem> found;
                    try { found = await Task.Run(() => scanner.ScanPath(p)); }
                    catch (Exception ex) { AddLog(LogLevel.Fail, L.F("log.error", p, ex.Message)); continue; }
                    foreach (var it in found)
                        if (_known.Add(it.OriginalPath)) { _items.Add(it); added++; }
                    if (remember && !_sources.Contains(p, StringComparer.OrdinalIgnoreCase)) _sources.Add(p);
                }
                AddLog(LogLevel.Info, L.F("ui.scanned", added));

                var last = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]);
                if (remember && last != null && last != _settings.LastFolder) { _settings.LastFolder = last; TrySaveSettings(); }
            }
            finally { _busy = false; UpdateState(); }
        }

        // ---------------- 開始／停止 ----------------

        async void OnStart(object sender, RoutedEventArgs e)
        {
            if (_running || _items.Count == 0) return;
            TaskGrid.CommitEdit();
            var engine = EngineLocator.Create(_settings);
            if (engine == null) { AddLog(LogLevel.Fail, L.T("log.noEngine")); return; }

            _running = true;
            _stopPresses = 0;
            StopBtn.Content = L.T("ui.stop");
            _cts = new CancellationTokenSource();
            UpdateState();
            _timer.Start();

            var logFile = Path.Combine(Settings.AppDir, "logs", "ZipDown_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            _unpacker = new Unpacker(_settings, _book, _done, _journal, engine, logFile)
            {
                Post = a => Dispatcher.UIThread.Post(a),
            };
            _unpacker.Log += PostLog;
            var list = _items.ToList();
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

        // ---------------- 復原改名 ----------------

        async void OnUndo(object sender, RoutedEventArgs e)
        {
            if (_journal.Count == 0) { AddLog(LogLevel.Info, L.T("log.undoNone")); return; }
            _busy = true; UpdateState();
            try
            {
                var (ok, skip) = await Task.Run(() => _journal.UndoAll(PostLog));
                AddLog(LogLevel.Info, L.F("log.undoSummary", ok, skip));
            }
            finally { _busy = false; UpdateState(); }

            // 檔名變回去了，重新掃描表格
            var src = _sources.ToList();
            _items.Clear(); _known.Clear();
            await AddPaths(src, remember: false);
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
