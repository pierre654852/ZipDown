using Avalonia.Controls;
using ZipDown.Core;

namespace ZipDown.App
{
    /// <summary>簡單的確定／取消對話框。預設按鈕是「取消」，按 Enter 不會誤刪。</summary>
    public partial class ConfirmWindow : Window
    {
        public ConfirmWindow() : this("") { }

        public ConfirmWindow(string message)
        {
            InitializeComponent();
            Title = L.T("app.title");
            MessageText.Text = message;
            OkBtn.Content = L.T("dlg.ok");
            CancelBtn.Content = L.T("dlg.cancel");
            OkBtn.Click += (_, _) => Close(true);
            CancelBtn.Click += (_, _) => Close(false);
        }

        public static async Task<bool> Ask(Window owner, string message) =>
            await new ConfirmWindow(message).ShowDialog<bool>(owner);
    }
}
