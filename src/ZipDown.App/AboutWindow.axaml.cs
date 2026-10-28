using Avalonia.Controls;
using ZipDown.Core;

namespace ZipDown.App
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            Title = L.T("about.title");
            NameText.Text = L.T("app.title");
            VersionText.Text = L.F("about.version", AppInfo.Version);
            RepoText.Text = AppInfo.RepoUrl;
            CreditText.Text = L.T("about.credit");
            ChangesLabel.Text = L.T("about.changes");
            ChangesBox.Text = L.T("about.changelog");
            CloseBtn.Content = L.T("about.close");
            CloseBtn.Click += (_, _) => Close();
        }
    }
}
