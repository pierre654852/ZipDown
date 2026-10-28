using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZipDown.Core;

namespace ZipDown.App
{
    public partial class SettingsWindow : Window
    {
        // 語言選單順序：跟系統、繁中、簡中、英文（名稱用該語言本身寫，切錯了也看得懂）
        static readonly string[] LangKeys = { "", "zh-TW", "zh-CN", "en" };

        readonly Settings _s;
        readonly PasswordBook _book;

        // 給 XAML 設計工具用
        public SettingsWindow() : this(new Settings(), null) { }

        public SettingsWindow(Settings s, PasswordBook book)
        {
            _s = s;
            _book = book;
            InitializeComponent();

            Title = L.T("set.title");
            BzLabel.Text = L.T("set.bzPath");
            WrLabel.Text = L.T("set.wrPath");
            BzBrowse.Content = L.T("set.browse");
            WrBrowse.Content = L.T("set.browse");
            PwLabel.Text = L.T("set.passwords");
            DepthLabel.Text = L.T("set.maxDepth");
            FlattenBox.Content = L.T("set.flatten");
            JunkLabel.Text = L.T("set.junk");
            LangLabel.Text = L.T("set.language");
            OkBtn.Content = L.T("set.ok");
            CancelBtn.Content = L.T("set.cancel");

            BzBox.Text = s.BandizipPath;
            WrBox.Text = s.WinRARPath;
            PwBox.Text = book == null ? "" : string.Join(Environment.NewLine, book.Passwords);
            DepthBox.Value = s.MaxDepth;
            FlattenBox.IsChecked = s.Flatten;
            JunkBox.Text = string.Join("|", s.JunkWords);
            LangBox.ItemsSource = new[] { L.T("lang.system"), "繁體中文", "简体中文", "English" };
            LangBox.SelectedIndex = Math.Max(0, Array.IndexOf(LangKeys, s.Language ?? ""));

            BzBrowse.Click += async (_, _) => { var p = await PickExe("bz.exe"); if (p != null) BzBox.Text = p; };
            WrBrowse.Click += async (_, _) => { var p = await PickExe("WinRAR.exe"); if (p != null) WrBox.Text = p; };
            CancelBtn.Click += (_, _) => Close(false);
            OkBtn.Click += (_, _) => { Apply(); Close(true); };
        }

        async Task<string> PickExe(string name)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = name,
                FileTypeFilter = new[] { new FilePickerFileType(name) { Patterns = new[] { name, "*.exe" } } },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        void Apply()
        {
            _s.BandizipPath = (BzBox.Text ?? "").Trim().Trim('"');
            _s.WinRARPath = (WrBox.Text ?? "").Trim().Trim('"');
            _s.MaxDepth = (int)Math.Max(1, Math.Min(10, DepthBox.Value ?? 3));
            _s.Flatten = FlattenBox.IsChecked == true;
            _s.JunkWords = (JunkBox.Text ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            _s.Language = LangKeys[Math.Max(0, LangBox.SelectedIndex)];
            _book?.SetPasswords((PwBox.Text ?? "").Split('\n'));
        }
    }
}
